// Native side of Stykker.NanoCut.Gpu: a Z-map (one height per grid cell) lowered by ball-tool steps, plus a small
// C API for the managed wrapper. Built with nvcc, see build.ps1 / build.sh. This library is optional: the managed
// CPU backend is the reference and works everywhere, and CI has neither a GPU nor nvcc.
//
// The arithmetic mirrors ToolProfile.cs in src/Stykker.NanoCut.Gpu exactly: the same packed step layout (12 floats,
// mm relative to the grid origin), the same reject, the same clamped stationary point. Differences come only from
// the compiler contracting a*b+c into fma, so results agree to a few float units of the coordinates, not bit for bit.
//
// Every entry point returns an error code and never throws across the boundary; nc_last_error explains a failure.

#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <new>

#include <cuda_runtime.h>
#include <math_constants.h>   // CUDART_INF_F

#ifdef _WIN32
#define NC_API extern "C" __declspec(dllexport)
#else
#define NC_API extern "C" __attribute__((visibility("default")))
#endif

namespace {

// Floats per packed step: (x0, y0, z0, r), (wx, wy, wz, r^2), (w2, 1/w2, c, zLow); see ToolProfile.Pack.
constexpr int kStepFloats = 12;

constexpr int kErrorLength = 512;
thread_local char g_error[kErrorLength] = "";

void set_error(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(g_error, kErrorLength, format, args);
    va_end(args);
}

void set_cuda_error(const char* what, cudaError_t error)
{
    set_error("%s: %s", what, cudaGetErrorString(error));
}

/// <summary>One height field on the device.</summary>
struct ZMap
{
    int nx = 0;
    int ny = 0;
    float cellX = 0.f;
    float cellY = 0.f;
    float bottom = 0.f;
    float top = 0.f;
    float* heights = nullptr;
    float* steps = nullptr;
    int stepsCapacity = 0;
    float* queryIn = nullptr;
    int queryCapacity = 0;
    float* queryOut = nullptr;
    int queryOutCapacity = 0;
    double* volumePartials = nullptr;
    double* volumeOut = nullptr;
};

/// <summary>
/// A set of points that outlives the query that brought it in: two doubles (x, y) each, in absolute mm, exactly as
/// the caller handed them over. A viewer asks for the same pixels after every batch of steps, and the upload of a
/// million points is 1.7 ms of the 2.2 ms a query costs; paying it once turns the per-frame cost into the download.
/// The origin stays out of it, so one set can be asked about any map.
/// </summary>
struct PointSet
{
    double* xy = nullptr;
    int count = 0;
};

/// <summary>Blocks used by the volume reduction; a fixed count keeps the result independent of the grid size.</summary>
constexpr int kVolumeBlocks = 1024;

/// <summary>Threads per block of the reduction, and the size of the shared array that finishes it.</summary>
constexpr int kVolumeThreads = 256;

__global__ void fill_kernel(float* heights, size_t count, float value)
{
    size_t i = blockIdx.x * static_cast<size_t>(blockDim.x) + threadIdx.x;
    if (i < count) heights[i] = value;
}

/// <summary>
/// Lowest z of the ball swept along one step at the column (x, y), or +inf when the step cannot reach the column.
/// Measured from the point t* of the step line that is horizontally closest to the column, at horizontal distance e:
/// with a^2 = r^2 - e^2 the bottom of the ball at t is g(t) = z0 + wz t - sqrt(a^2 - w2 (t - t*)^2), which is convex,
/// so its minimum over the valid interval [max(0, t* - a/sqrt(w2)), min(1, t* + a/sqrt(w2))] is the stationary point
/// t* - c a clamped to that interval. a^2 and t - t* are formed directly, never as a difference of terms of order
/// L^2, which is what kept an earlier expansion around the step start from working on long steps in float.
/// </summary>
__device__ __forceinline__ float ball_bottom(float x, float y, const float* __restrict__ p)
{
    const float x0 = p[0], y0 = p[1], z0 = p[2], r2 = p[7];
    const float wx = p[4], wy = p[5], wz = p[6];
    const float w2 = p[8], invW2 = p[9], c = p[10], zLow = p[11];

    const float px = x - x0, py = y - y0;
    if (w2 == 0.f)
    {
        // Vertical step (or none): every ball position has the column at the same distance.
        const float p2 = px * px + py * py;
        return p2 > r2 ? CUDART_INF_F : zLow - sqrtf(r2 - p2);
    }

    const float ts = (px * wx + py * wy) * invW2;
    const float ex = px - ts * wx, ey = py - ts * wy;
    const float a2 = r2 - (ex * ex + ey * ey);
    if (a2 < 0.f) return CUDART_INF_F;

    const float a = sqrtf(a2);
    const float h = a * sqrtf(invW2);
    const float lo = fmaxf(0.f, ts - h), hi = fminf(1.f, ts + h);
    if (lo > hi) return CUDART_INF_F;

    const float t = fminf(fmaxf(ts - c * a, lo), hi);
    const float du = t - ts;
    return z0 + wz * t - sqrtf(fmaxf(a2 - w2 * du * du, 0.f));
}

/// <summary>One thread per cell, looping over every step of the batch.</summary>
__global__ void zmap_apply_kernel(float* __restrict__ heights, int nx, int ny, float cellX, float cellY,
                                  float bottom, const float* __restrict__ steps, int stepCount)
{
    const int i = blockIdx.x * blockDim.x + threadIdx.x;
    const int j = blockIdx.y * blockDim.y + threadIdx.y;
    if (i >= nx || j >= ny) return;

    const float x = (i + 0.5f) * cellX;
    const float y = (j + 0.5f) * cellY;
    const int index = j * nx + i;
    float current = heights[index];

    for (int s = 0; s < stepCount; ++s)
    {
        const float b = ball_bottom(x, y, steps + static_cast<size_t>(s) * kStepFloats);
        if (b < current) current = b > bottom ? b : bottom;
    }

    heights[index] = current;
}

/// <summary>
/// Height of the field at (x, y) by bilinear interpolation between the four surrounding cell centres. The
/// coordinate is first clamped to the field, so points outside see the border height instead of extrapolating.
/// CpuBackend.cs mirrors this operation; the arithmetic has to stay in the same order in both.
/// </summary>
__device__ __forceinline__ float sample_height(const float* __restrict__ heights, int nx, int ny, float cellX,
                                               float cellY, float x, float y)
{
    const float gx = fminf(fmaxf(x / cellX - 0.5f, 0.f), static_cast<float>(nx - 1));
    const float gy = fminf(fmaxf(y / cellY - 0.5f, 0.f), static_cast<float>(ny - 1));
    const int i0 = static_cast<int>(gx), j0 = static_cast<int>(gy);
    const int i1 = i0 + 1 < nx ? i0 + 1 : nx - 1;
    const int j1 = j0 + 1 < ny ? j0 + 1 : ny - 1;
    const float fx = gx - static_cast<float>(i0), fy = gy - static_cast<float>(j0);

    const float* row0 = heights + static_cast<size_t>(j0) * nx;
    const float* row1 = heights + static_cast<size_t>(j1) * nx;
    const float a = row0[i0] + (row0[i1] - row0[i0]) * fx;
    const float b = row1[i0] + (row1[i1] - row1[i0]) * fx;
    return a + (b - a) * fy;
}

/// <summary>
/// One thread per point, two doubles (x, y) per point, in absolute mm; the origin is subtracted and the coordinates
/// are narrowed to float here, on the device. Taking the points as they are is what lets a query skip the host-side
/// packing loop, which costs more than the extra bytes on the wire: 2.8 ms of packing against 0.9 ms of upload for a
/// million points on the bench scene. The sample itself runs in 0.06 ms, so the device has the time to spare.
/// </summary>
__global__ void sample_d_kernel(const float* __restrict__ heights, int nx, int ny, float cellX, float cellY,
                                const double* __restrict__ points, double originX, double originY, int count,
                                float* __restrict__ out)
{
    const int k = blockIdx.x * blockDim.x + threadIdx.x;
    if (k >= count) return;
    const double2 p = *reinterpret_cast<const double2*>(points + 2 * static_cast<size_t>(k));
    const float x = static_cast<float>(p.x - originX), y = static_cast<float>(p.y - originY);
    out[k] = sample_height(heights, nx, ny, cellX, cellY, x, y);
}

/// <summary>
/// One thread per tool pose, four floats (x, y, z, r) per pose. Writes how far the ball reaches into the
/// material: height minus the bottom of the ball, zero when the ball is in air.
/// </summary>
__global__ void probe_kernel(const float* __restrict__ heights, int nx, int ny, float cellX, float cellY,
                             const float* __restrict__ poses, int count, float* __restrict__ out)
{
    const int k = blockIdx.x * blockDim.x + threadIdx.x;
    if (k >= count) return;
    const float* p = poses + 4 * static_cast<size_t>(k);
    const float h = sample_height(heights, nx, ny, cellX, cellY, p[0], p[1]);
    const float depth = h - (p[2] - p[3]);
    out[k] = depth > 0.f ? depth : 0.f;
}

/// <summary>
/// One block per slot of the reduction, so no atomics are needed and the sum is the same on every run. The thread
/// sums are added in thread order, not in arrival order, so the result does not depend on the scheduling. The second
/// kernel adds the slots in a fixed order as well. The shared array has to be as large as the block, kVolumeThreads.
/// </summary>
__global__ void volume_partial_kernel(const float* __restrict__ heights, size_t count, float top,
                                       double* __restrict__ partials)
{
    __shared__ double parts[kVolumeThreads];
    double sum = 0.0;
    const size_t stride = static_cast<size_t>(gridDim.x) * blockDim.x;
    for (size_t i = static_cast<size_t>(blockIdx.x) * blockDim.x + threadIdx.x; i < count; i += stride)
        sum += static_cast<double>(top - heights[i]);
    parts[threadIdx.x] = sum;
    __syncthreads();
    if (threadIdx.x == 0)
    {
        double total = 0.0;
        for (int i = 0; i < blockDim.x; i++) total += parts[i];
        partials[blockIdx.x] = total;
    }
}

/// <summary>
/// Adds the block sums in a fixed order, so the result is the same on every run. One thread, because the number of
/// block sums is bounded by kVolumeBlocks and the whole call is supposed to cost microseconds: a serial pass over a
/// thousand doubles is nothing next to a kernel launch, and it cannot go wrong.
/// </summary>
__global__ void volume_finalize_kernel(const double* __restrict__ partials, int blocks, double cellArea,
                                       double* __restrict__ out)
{
    double sum = 0.0;
    for (int i = 0; i < blocks; i++) sum += partials[i];
    *out = sum * cellArea;
}

/// <summary>Creates three events for timing and reports whether that worked.</summary>
bool make_events(cudaEvent_t& t0, cudaEvent_t& t1, cudaEvent_t& t2)
{
    cudaError_t e = cudaEventCreate(&t0);
    if (e == cudaSuccess) e = cudaEventCreate(&t1);
    if (e == cudaSuccess) e = cudaEventCreate(&t2);
    if (e != cudaSuccess)
    {
        set_cuda_error("cudaEventCreate", e);
        cudaEventDestroy(t0);
        cudaEventDestroy(t1);
        cudaEventDestroy(t2);
        return false;
    }
    return true;
}

void destroy_events(cudaEvent_t t0, cudaEvent_t t1, cudaEvent_t t2)
{
    cudaEventDestroy(t0);
    cudaEventDestroy(t1);
    cudaEventDestroy(t2);
}

} // namespace

NC_API const char* nc_last_error()
{
    return g_error;
}

NC_API int nc_gpu_init(int device)
{
    cudaError_t e = cudaSetDevice(device);
    if (e == cudaSuccess) e = cudaFree(nullptr); // force context creation, so the first kernel is not the slow one
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_gpu_init", e);
        return static_cast<int>(e);
    }
    g_error[0] = '\0';
    return 0;
}

NC_API int nc_gpu_device_count()
{
    int count = 0;
    cudaError_t e = cudaGetDeviceCount(&count);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_gpu_device_count", e);
        return -1;
    }
    return count;
}

NC_API int nc_gpu_device_info(int device, char* name, int nameLength, int* ccMajor, int* ccMinor,
                              unsigned long long* memoryBytes)
{
    cudaDeviceProp properties;
    cudaError_t e = cudaGetDeviceProperties(&properties, device);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_gpu_device_info", e);
        return static_cast<int>(e);
    }
    if (name != nullptr && nameLength > 0)
    {
        std::strncpy(name, properties.name, static_cast<size_t>(nameLength) - 1);
        name[nameLength - 1] = '\0';
    }
    if (ccMajor != nullptr) *ccMajor = properties.major;
    if (ccMinor != nullptr) *ccMinor = properties.minor;
    if (memoryBytes != nullptr) *memoryBytes = properties.totalGlobalMem;
    return 0;
}

NC_API void* nc_zmap_create(int nx, int ny, float cellX, float cellY, float bottom, float top)
{
    if (nx <= 0 || ny <= 0 || !(cellX > 0.f) || !(cellY > 0.f))
    {
        set_error("nc_zmap_create: invalid grid %d x %d with cells %g x %g", nx, ny, cellX, cellY);
        return nullptr;
    }

    auto* z = new (std::nothrow) ZMap();
    if (z == nullptr)
    {
        set_error("nc_zmap_create: out of host memory");
        return nullptr;
    }
    z->nx = nx;
    z->ny = ny;
    z->cellX = cellX;
    z->cellY = cellY;
    z->bottom = bottom;
    z->top = top;

    const size_t count = static_cast<size_t>(nx) * static_cast<size_t>(ny);
    cudaError_t e = cudaMalloc(&z->heights, count * sizeof(float));
    if (e == cudaSuccess) e = cudaMalloc(&z->volumePartials, kVolumeBlocks * sizeof(double));
    if (e == cudaSuccess) e = cudaMalloc(&z->volumeOut, sizeof(double));
    if (e == cudaSuccess)
    {
        const int threads = 256;
        const size_t blocks = (count + threads - 1) / threads;
        fill_kernel<<<static_cast<unsigned int>(blocks), threads>>>(z->heights, count, top);
        e = cudaGetLastError();
        if (e == cudaSuccess) e = cudaDeviceSynchronize();
    }
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_create", e);
        if (z->heights != nullptr) cudaFree(z->heights);
        if (z->volumePartials != nullptr) cudaFree(z->volumePartials);
        if (z->volumeOut != nullptr) cudaFree(z->volumeOut);
        delete z;
        return nullptr;
    }
    return z;
}

NC_API int nc_zmap_apply_steps(void* zmap, const float* steps, int stepCount, double* kernelMs, double* uploadMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr || steps == nullptr || stepCount <= 0)
    {
        set_error("nc_zmap_apply_steps: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;
    if (uploadMs != nullptr) *uploadMs = 0;

    const size_t bytes = static_cast<size_t>(stepCount) * kStepFloats * sizeof(float);
    if (z->stepsCapacity < stepCount)
    {
        if (z->steps != nullptr) cudaFree(z->steps);
        z->steps = nullptr;
        z->stepsCapacity = 0;
        cudaError_t e = cudaMalloc(&z->steps, bytes);
        if (e != cudaSuccess)
        {
            set_cuda_error("nc_zmap_apply_steps: cudaMalloc", e);
            return static_cast<int>(e);
        }
        z->stepsCapacity = stepCount;
    }

    cudaEvent_t t0, t1, t2;
    if (!make_events(t0, t1, t2)) return static_cast<int>(cudaErrorUnknown);

    cudaEventRecord(t0);
    cudaError_t e = cudaMemcpy(z->steps, steps, bytes, cudaMemcpyHostToDevice);
    cudaEventRecord(t1);
    if (e == cudaSuccess)
    {
        dim3 block(16, 16);
        dim3 grid((static_cast<unsigned int>(z->nx) + block.x - 1) / block.x,
                  (static_cast<unsigned int>(z->ny) + block.y - 1) / block.y);
        zmap_apply_kernel<<<grid, block>>>(z->heights, z->nx, z->ny, z->cellX, z->cellY, z->bottom, z->steps,
                                           stepCount);
        e = cudaGetLastError();
    }
    cudaEventRecord(t2);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();

    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_apply_steps", e);
        destroy_events(t0, t1, t2);
        return static_cast<int>(e);
    }

    float upload = 0.f, kernel = 0.f;
    cudaEventElapsedTime(&upload, t0, t1);
    cudaEventElapsedTime(&kernel, t1, t2);
    destroy_events(t0, t1, t2);
    if (uploadMs != nullptr) *uploadMs = upload;
    if (kernelMs != nullptr) *kernelMs = kernel;
    return 0;
}

NC_API int nc_zmap_read(void* zmap, float* heights, double* downloadMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr || heights == nullptr)
    {
        set_error("nc_zmap_read: invalid argument");
        return 1;
    }
    if (downloadMs != nullptr) *downloadMs = 0;

    const size_t bytes = static_cast<size_t>(z->nx) * static_cast<size_t>(z->ny) * sizeof(float);
    cudaEvent_t t0, t1, t2;
    if (!make_events(t0, t1, t2)) return static_cast<int>(cudaErrorUnknown);

    cudaEventRecord(t0);
    cudaError_t e = cudaMemcpy(heights, z->heights, bytes, cudaMemcpyDeviceToHost);
    cudaEventRecord(t1);
    cudaEventRecord(t2);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_read", e);
        destroy_events(t0, t1, t2);
        return static_cast<int>(e);
    }

    float download = 0.f;
    cudaEventElapsedTime(&download, t0, t1);
    destroy_events(t0, t1, t2);
    if (downloadMs != nullptr) *downloadMs = download;
    return 0;
}

namespace {

/// <summary>Grows the device input buffer of a query to hold <paramref name="bytes"/> and copies the data in.</summary>
cudaError_t upload_bytes(ZMap* z, const void* data, size_t bytes)
{
    const int capacity = static_cast<int>((bytes + sizeof(float) - 1) / sizeof(float));
    if (z->queryCapacity < capacity)
    {
        if (z->queryIn != nullptr) cudaFree(z->queryIn);
        z->queryIn = nullptr;
        z->queryCapacity = 0;
        cudaError_t e = cudaMalloc(&z->queryIn, static_cast<size_t>(capacity) * sizeof(float));
        if (e != cudaSuccess) return e;
        z->queryCapacity = capacity;
    }
    return cudaMemcpy(z->queryIn, data, bytes, cudaMemcpyHostToDevice);
}

/// <summary>Grows the device input buffer of a query and copies the host data into it.</summary>
cudaError_t upload_query(ZMap* z, const float* data, int floats)
{
    return upload_bytes(z, data, static_cast<size_t>(floats) * sizeof(float));
}

/// <summary>Grows the device output buffer of a query.</summary>
cudaError_t reserve_query_out(ZMap* z, int floats)
{
    if (z->queryOutCapacity >= floats) return cudaSuccess;
    if (z->queryOut != nullptr) cudaFree(z->queryOut);
    z->queryOut = nullptr;
    z->queryOutCapacity = 0;
    cudaError_t e = cudaMalloc(&z->queryOut, static_cast<size_t>(floats) * sizeof(float));
    if (e != cudaSuccess) return e;
    z->queryOutCapacity = floats;
    return cudaSuccess;
}

/// A device-to-host copy timed on the host clock, in milliseconds. The copy back is synchronous from pageable
/// memory, so this is exactly what the caller waits for, and no stream event sees it.
/// </summary>
inline double copy_back(void* dst, const void* src, size_t bytes, cudaError_t& error)
{
    const auto start = std::chrono::steady_clock::now();
    error = cudaMemcpy(dst, src, bytes, cudaMemcpyDeviceToHost);
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
}

/// <summary>
/// Events for the phases of a query call: start, upload done, kernel done. The download is not in here, see
/// Report: a pageable device-to-host copy never enters the stream timeline, so an event pair around it would
/// always read zero.
/// </summary>
struct QueryTiming
{
    cudaEvent_t start, uploaded, computed;
    bool ok = make_events(start, uploaded, computed);

    ~QueryTiming()
    {
        if (ok)
        {
            cudaEventDestroy(start);
            cudaEventDestroy(uploaded);
            cudaEventDestroy(computed);
        }
    }

    void Report(double& uploadMs, double& kernelMs, double downloadMs)
    {
        // cudaEventElapsedTime only answers once both events have completed, so the stream has to be drained first.
        // Without this the times would be garbage, because the last event has not been reached yet.
        cudaDeviceSynchronize();
        float upload = 0.f, kernel = 0.f;
        cudaEventElapsedTime(&upload, start, uploaded);
        cudaEventElapsedTime(&kernel, uploaded, computed);
        uploadMs = upload;
        kernelMs = kernel;
        // The download is not measured here. A pageable device-to-host copy is synchronous and the driver is free to
        // run it outside the caller's stream, in which case an event pair around it carries the same timestamp twice
        // and the phase reads as zero -- which is exactly what it did before. The callers time the copy on the host
        // clock instead, which cannot be fooled.
    }
};

} // namespace

/// <summary>
/// Removed volume in mm³, reduced on the device. No height field comes back, so a caller that reports progress can
/// ask for the volume after every batch of steps without paying for the read-back of the whole field.
/// </summary>
NC_API int nc_zmap_volume(void* zmap, double* volumeMm3, double* kernelMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr || volumeMm3 == nullptr)
    {
        set_error("nc_zmap_volume: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;

    const size_t count = static_cast<size_t>(z->nx) * static_cast<size_t>(z->ny);
    const int threads = kVolumeThreads;
    const size_t needed = (count + threads - 1) / threads;
    const int blocks = static_cast<int>(needed < static_cast<size_t>(kVolumeBlocks) ? needed
                                                                                    : static_cast<size_t>(kVolumeBlocks));
    QueryTiming timing;
    if (!timing.ok) return static_cast<int>(cudaErrorUnknown);

    cudaEventRecord(timing.start);
    volume_partial_kernel<<<blocks, threads>>>(z->heights, count, z->top, z->volumePartials);
    cudaError_t e = cudaGetLastError();
    if (e == cudaSuccess)
    {
        const double cellArea = static_cast<double>(z->cellX) * z->cellY;
        volume_finalize_kernel<<<1, 1>>>(z->volumePartials, blocks, cellArea, z->volumeOut);
        e = cudaGetLastError();
    }
    cudaEventRecord(timing.computed);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    if (e == cudaSuccess) copy_back(volumeMm3, z->volumeOut, sizeof(double), e);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_volume", e);
        return static_cast<int>(e);
    }

    float kernel = 0.f;
    cudaEventElapsedTime(&kernel, timing.start, timing.computed);
    if (kernelMs != nullptr) *kernelMs = kernel;
    return 0;
}

/// <summary>
/// Height of the field at <paramref name="count"/> points, two doubles (x, y) each in absolute mm, interpolated
/// between cell centres and clamped to the field. Writes <paramref name="count"/> floats.
/// </summary>
NC_API int nc_zmap_sample(void* zmap, const double* points, int count, double originX, double originY,
                          float* outHeights, double* kernelMs, double* uploadMs, double* downloadMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr || points == nullptr || outHeights == nullptr || count <= 0)
    {
        set_error("nc_zmap_sample: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;
    if (uploadMs != nullptr) *uploadMs = 0;
    if (downloadMs != nullptr) *downloadMs = 0;

    QueryTiming timing;
    if (!timing.ok) return static_cast<int>(cudaErrorUnknown);

    cudaEventRecord(timing.start);
    cudaError_t e = upload_bytes(z, points, static_cast<size_t>(2 * count) * sizeof(double));
    cudaEventRecord(timing.uploaded);
    if (e == cudaSuccess) e = reserve_query_out(z, count);
    if (e == cudaSuccess)
    {
        const int threads = 256;
        const int blocks = (count + threads - 1) / threads;
        sample_d_kernel<<<blocks, threads>>>(z->heights, z->nx, z->ny, z->cellX, z->cellY,
                                             reinterpret_cast<const double*>(z->queryIn), originX, originY, count,
                                             z->queryOut);
        e = cudaGetLastError();
    }
    cudaEventRecord(timing.computed);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    double download = 0.0;
    if (e == cudaSuccess)
    {
        download = copy_back(outHeights, z->queryOut, static_cast<size_t>(count) * sizeof(float), e);
    }
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_sample", e);
        return static_cast<int>(e);
    }

    double upload = 0.0, kernel = 0.0;
    timing.Report(upload, kernel, download);
    if (uploadMs != nullptr) *uploadMs = upload;
    if (kernelMs != nullptr) *kernelMs = kernel;
    if (downloadMs != nullptr) *downloadMs = download;
    return 0;
}

/// <summary>
/// Copies <paramref name="count"/> points, two doubles (x, y) each in absolute mm, into a point set that stays on
/// the device until <c>nc_pointset_destroy</c>. Returns the set, or null on failure, and reports the copy on the host
/// clock in milliseconds.
/// </summary>
NC_API void* nc_pointset_create(const double* points, int count, double* uploadMs)
{
    if (uploadMs != nullptr) *uploadMs = 0;
    if (points == nullptr || count <= 0)
    {
        set_error("nc_pointset_create: invalid argument");
        return nullptr;
    }

    auto* set = new (std::nothrow) PointSet();
    if (set == nullptr)
    {
        set_error("nc_pointset_create: out of memory");
        return nullptr;
    }
    set->count = count;

    const size_t bytes = static_cast<size_t>(2 * count) * sizeof(double);
    cudaError_t e = cudaMalloc(&set->xy, bytes);
    const auto start = std::chrono::steady_clock::now();
    if (e == cudaSuccess) e = cudaMemcpy(set->xy, points, bytes, cudaMemcpyHostToDevice);
    const double upload =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    if (uploadMs != nullptr) *uploadMs = upload;
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_pointset_create", e);
        delete set;
        return nullptr;
    }
    return set;
}

/// <summary>Frees a point set and its device memory. A null set is ignored.</summary>
NC_API void nc_pointset_destroy(void* pointSet)
{
    auto* set = static_cast<PointSet*>(pointSet);
    if (set == nullptr) return;
    if (set->xy != nullptr) cudaFree(set->xy);
    delete set;
}

/// <summary>
/// Height of the field at every point of a set that is already on the device: the same kernel as
/// <c>nc_zmap_sample</c>, with nothing uploaded, so <paramref name="uploadMs"/> reads as the cost of the timing
/// events alone. Writes <c>count</c> floats, one per point of the set.
/// </summary>
NC_API int nc_zmap_sample_set(void* zmap, void* pointSet, double originX, double originY, float* outHeights,
                              double* kernelMs, double* uploadMs, double* downloadMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    auto* ps = static_cast<PointSet*>(pointSet);
    if (z == nullptr || ps == nullptr || ps->xy == nullptr || outHeights == nullptr || ps->count <= 0)
    {
        set_error("nc_zmap_sample_set: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;
    if (uploadMs != nullptr) *uploadMs = 0;
    if (downloadMs != nullptr) *downloadMs = 0;

    const int count = ps->count;
    QueryTiming timing;
    if (!timing.ok) return static_cast<int>(cudaErrorUnknown);

    // The two events are back to back because there is no upload to separate: what the pair reports is the launch
    // path, not a transfer.
    cudaEventRecord(timing.start);
    cudaEventRecord(timing.uploaded);
    cudaError_t e = reserve_query_out(z, count);
    if (e == cudaSuccess)
    {
        const int threads = 256;
        const int blocks = (count + threads - 1) / threads;
        sample_d_kernel<<<blocks, threads>>>(z->heights, z->nx, z->ny, z->cellX, z->cellY, ps->xy, originX, originY,
                                             count, z->queryOut);
        e = cudaGetLastError();
    }
    cudaEventRecord(timing.computed);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    double download = 0.0;
    if (e == cudaSuccess) download = copy_back(outHeights, z->queryOut, static_cast<size_t>(count) * sizeof(float), e);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_sample_set", e);
        return static_cast<int>(e);
    }

    double upload = 0.0, kernel = 0.0;
    timing.Report(upload, kernel, download);
    if (uploadMs != nullptr) *uploadMs = upload;
    if (kernelMs != nullptr) *kernelMs = kernel;
    if (downloadMs != nullptr) *downloadMs = download;
    return 0;
}

/// <summary>
/// How far a ball reaches into the material at each of <paramref name="count"/> poses, four floats (x, y, z, r)
/// each: the height of the field minus the bottom of the ball, zero where the ball is in air. Writes
/// <paramref name="count"/> floats, which is all the caller gets back -- thousands of poses are a few tens of
/// kilobytes, unlike the height field itself.
/// </summary>
NC_API int nc_zmap_probe(void* zmap, const float* poses, int count, float* outPenetrationMm,
                         double* kernelMs, double* uploadMs, double* downloadMs)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr || poses == nullptr || outPenetrationMm == nullptr || count <= 0)
    {
        set_error("nc_zmap_probe: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;
    if (uploadMs != nullptr) *uploadMs = 0;
    if (downloadMs != nullptr) *downloadMs = 0;

    QueryTiming timing;
    if (!timing.ok) return static_cast<int>(cudaErrorUnknown);

    cudaEventRecord(timing.start);
    cudaError_t e = upload_query(z, poses, 4 * count);
    cudaEventRecord(timing.uploaded);
    if (e == cudaSuccess) e = reserve_query_out(z, count);
    if (e == cudaSuccess)
    {
        const int threads = 256;
        const int blocks = (count + threads - 1) / threads;
        probe_kernel<<<blocks, threads>>>(z->heights, z->nx, z->ny, z->cellX, z->cellY, z->queryIn, count,
                                          z->queryOut);
        e = cudaGetLastError();
    }
    cudaEventRecord(timing.computed);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    double download = 0.0;
    if (e == cudaSuccess)
    {
        download = copy_back(outPenetrationMm, z->queryOut, static_cast<size_t>(count) * sizeof(float), e);
    }
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_zmap_probe", e);
        return static_cast<int>(e);
    }

    double upload = 0.0, kernel = 0.0;
    timing.Report(upload, kernel, download);
    if (uploadMs != nullptr) *uploadMs = upload;
    if (kernelMs != nullptr) *kernelMs = kernel;
    if (downloadMs != nullptr) *downloadMs = download;
    return 0;
}

NC_API void nc_zmap_destroy(void* zmap)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr) return;
    if (z->heights != nullptr) cudaFree(z->heights);
    if (z->steps != nullptr) cudaFree(z->steps);
    if (z->queryIn != nullptr) cudaFree(z->queryIn);
    if (z->queryOut != nullptr) cudaFree(z->queryOut);
    if (z->volumePartials != nullptr) cudaFree(z->volumePartials);
    if (z->volumeOut != nullptr) cudaFree(z->volumeOut);
    delete z;
}

// ---------------------------------------------------------------------------------------------------------------------
// Dexel map: up to k material intervals [z0, z1] per column instead of one height. Mirrors DexelMap.cs and
// ToolProfile.Span in src/Stykker.NanoCut.Gpu: the same packed steps, the same interval of a column within a swept
// ball, the same subtraction (a split in a full column cuts through to the interval top and is counted).

namespace {

/// <summary>Largest k the kernel supports; DexelMap allows 1 to 16.</summary>
constexpr int kMaxDexelIntervals = 16;

/// <summary>Columns per tile and rows per tile of the binned launch: one tile is one thread block.</summary>
constexpr int kDexelTile = 16;

/// <summary>Half-spaces the convex tool sweep accepts. Mirrors ConvexProfile.MaxPlanes.</summary>
constexpr int kConvexPlanes = 16;

/// <summary>Floats per half-space: the unit normal and the distance.</summary>
constexpr int kPlaneFloats = 4;

/// <summary>Floats per packed convex step. Mirrors ConvexProfile.StepFloats.</summary>
constexpr int kConvexStepFloats = 32;

/// <summary>Offsets into a packed convex step: T_A, the move w, and then the rotation. The half-space count and the
/// swept box that precede them are read by the host, not here.</summary>
constexpr int kConvexFrom = 4;
constexpr int kConvexMove = 7;
constexpr int kConvexRot = 11;

struct Dexel
{
    int nx = 0;
    int ny = 0;
    int k = 0;
    float cellX = 0.f;
    float cellY = 0.f;
    float top = 0.f;
    float* intervals = nullptr;          // nx * ny * k * 2
    unsigned char* counts = nullptr;     // nx * ny
    unsigned long long* overflows = nullptr;
    float* steps = nullptr;
    int stepsCapacity = 0;
    int stepsStride = 0;                 // floats per step, which differs between the two layouts
    float* planes = nullptr;             // planeCount * kPlaneFloats, shared by every step of a program
    int planesCapacity = 0;
    int* tileStart = nullptr;            // tiles + 1
    int tileCapacity = 0;
    int* tileSteps = nullptr;
    int tileStepsCapacity = 0;
    double* volumePartials = nullptr;
    double* volumeOut = nullptr;
};

/// <summary>The interval [low, high] where the column meets the swept ball, or false. Mirrors ToolProfile.Span.</summary>
__device__ __forceinline__ bool swept_span(float x, float y, const float* __restrict__ p, float& low, float& high)
{
    const float x0 = p[0], y0 = p[1], z0 = p[2], r2 = p[7];
    const float wx = p[4], wy = p[5], wz = p[6];
    const float w2 = p[8], invW2 = p[9], c = p[10], zLow = p[11];

    const float px = x - x0, py = y - y0;
    if (w2 == 0.f)
    {
        const float p2 = px * px + py * py;
        if (p2 > r2) return false;
        const float half = sqrtf(r2 - p2);
        low = zLow - half;
        high = zLow + fabsf(wz) + half;
        return true;
    }

    const float ts = (px * wx + py * wy) * invW2;
    const float ex = px - ts * wx, ey = py - ts * wy;
    const float a2 = r2 - (ex * ex + ey * ey);
    if (a2 < 0.f) return false;

    const float a = sqrtf(a2);
    const float h = a * sqrtf(invW2);
    const float lo = fmaxf(0.f, ts - h), hi = fminf(1.f, ts + h);
    if (lo > hi) return false;

    const float tb = fminf(fmaxf(ts - c * a, lo), hi), db = tb - ts;
    low = z0 + wz * tb - sqrtf(fmaxf(a2 - w2 * db * db, 0.f));
    const float tt = fminf(fmaxf(ts + c * a, lo), hi), dt = tt - ts;
    high = z0 + wz * tt + sqrtf(fmaxf(a2 - w2 * dt * dt, 0.f));
    return true;
}

/// <summary>
/// Narrows t's range to where the column is inside the sweep, in place. Mirrors ConvexProfile.Where.
/// </summary>
/// <remarks>A point on the column is in the body at t when its z is over the lower bound and under the upper one, so
/// every line from below has to sit under every line from above, and each of those pairs is one bound on t. This is
/// done as a bound on t rather than as a test per candidate on purpose: a candidate where the body opens or closes has
/// the two bounds equal there, which in float they are not -- a couple of ulps apart, and which way is the compiler's
/// business. Testing such a candidate drops half of them at random, and the end that was to come from the one that got
/// dropped is then taken from a different t, a whole slice of the sweep away. Narrowing t instead is a chain of min and
/// max, so a ulop moves an end of the range by an ulop and nothing else.</remarks>
__device__ __forceinline__ bool convex_where(const float* __restrict__ below, int nLo,
                                             const float* __restrict__ above, int nHi, float& tLo, float& tHi)
{
    for (int i = 0; i < nLo; i++)
    {
        const float ai = below[2 * i], bi = below[2 * i + 1];
        for (int j = 0; j < nHi; j++)
        {
            // The pair reads ai + bi·t ≤ aj + bj·t, that is (ai − aj) + (bi − bj)·t ≤ 0.
            const float k = ai - above[2 * j], s = bi - above[2 * j + 1];
            if (s > 0.f) tHi = fminf(tHi, -k / s);
            else if (s < 0.f) tLo = fmaxf(tLo, -k / s);
            else if (k > 0.f) return false;   // parallel, and the lower one sits above the upper one
        }
    }
    return tLo <= tHi;
}

/// <summary>
/// The envelope of a bound's lines at t: the highest of them for the lower bound, the lowest for the upper.
/// Mirrors ConvexProfile.ExtremumAt.
/// </summary>
__device__ __forceinline__ float convex_envelope(const float* __restrict__ g, int count, float t, bool low)
{
    float v = low ? -INFINITY : INFINITY;
    for (int k = 0; k < count; k++)
    {
        const float q = g[2 * k] + g[2 * k + 1] * t;
        v = low ? fmaxf(v, q) : fminf(v, q);
    }
    return v;
}

/// <summary>
/// The lower end of an envelope of lines (max of them) or the upper one (min), taken over t's range. Mirrors
/// ConvexProfile.Extremum, operation for operation, so both backends land on the same float.
/// </summary>
/// <remarks>Both walk the same candidates: the ends of t's range, then the place where two lines cross. Every
/// candidate is scored on the envelope, the two ends included -- a line's own value only bounds the envelope's,
/// and taking it at the ends would put an end below the body the tool actually sweeps.</remarks>
__device__ __forceinline__ float convex_extremum(const float* __restrict__ g, int count, float tLo, float tHi,
                                                  bool low)
{
    if (count == 0) return low ? -INFINITY : INFINITY;

    float best = convex_envelope(g, count, tLo, low);
    const float end = convex_envelope(g, count, tHi, low);
    best = low ? fminf(best, end) : fmaxf(best, end);

    for (int i = 0; i < count; i++)
    {
        const float ai = g[2 * i], bi = g[2 * i + 1];
        for (int j = i + 1; j < count; j++)
        {
            const float aj = g[2 * j], bj = g[2 * j + 1];
            if (bi == bj) continue;   // parallel or identical: there is no crossing to walk to
            const float t = (aj - ai) / (bi - bj);
            if (t < tLo || t > tHi) continue;
            const float v = convex_envelope(g, count, t, low);
            best = low ? fminf(best, v) : fmaxf(best, v);
        }
    }
    return best;
}

/// <summary>
/// The interval [low, high] where the column meets the convex tool swept by one step, or false. Mirrors
/// ConvexProfile.Span: where a vertical line meets a convex body is one interval, and both of its ends come out of
/// a small linear program in (z, t) over the tool's half-spaces.
/// <para>
/// For the half-spaces turned into world coordinates the constraint of a point on the column is
/// m·(p(z) − T_A) − t·(m·w) ≤ d, which with z the height is mz·z ≤ c + g·t, and the ones with mz &lt; 0 bound z from
/// below while the ones with mz &gt; 0 bound it from above. The ones with mz = 0 say nothing about z at all and only
/// narrow t.
/// <para>
/// The sign of m·T_A is the one that matters here: moving the column's p into the half-space gives
/// m·p ≤ d + m·T_A + t·(m·w), so c = d + m·T_A − (m_x·x + m_y·y) and not d − m·T_A. With the wrong sign every
/// interval comes out mirrored about the grid's own origin and the tool cuts below the stock instead of into it.
/// </para>
/// </summary>
__device__ __forceinline__ bool convex_span(float x, float y, const float* __restrict__ p,
                                            const float* __restrict__ planes, int planeCount, float& low, float& high)
{
    const float ax = p[kConvexFrom], ay = p[kConvexFrom + 1], az = p[kConvexFrom + 2];
    const float wx = p[kConvexMove], wy = p[kConvexMove + 1], wz = p[kConvexMove + 2];
    const float r00 = p[kConvexRot], r01 = p[kConvexRot + 1], r02 = p[kConvexRot + 2];
    const float r10 = p[kConvexRot + 3], r11 = p[kConvexRot + 4], r12 = p[kConvexRot + 5];
    const float r20 = p[kConvexRot + 6], r21 = p[kConvexRot + 7], r22 = p[kConvexRot + 8];

    // The bounds as lines, two floats each: the intercepts first, the slopes second. The planes with a negative
    // normal's z give the lower bound, the rest the upper. They go in separate lists because where a line lands
    // depends on how many of the other kind there are, and that count is only known at the end of the loop.
    float gLo[2 * kConvexPlanes], gHi[2 * kConvexPlanes];
    int nLo = 0, nHi = 0;
    float tLo = 0.f, tHi = 1.f;

    for (int i = 0; i < planeCount; i++)
    {
        const float nx = planes[kPlaneFloats * i], ny = planes[kPlaneFloats * i + 1];
        const float nz = planes[kPlaneFloats * i + 2], d = planes[kPlaneFloats * i + 3];
        const float mx = r00 * nx + r01 * ny + r02 * nz;
        const float my = r10 * nx + r11 * ny + r12 * nz;
        const float mz = r20 * nx + r21 * ny + r22 * nz;
        const float dot = mx * wx + my * wy + mz * wz;
        const float c = d + (mx * ax + my * ay + mz * az) - mx * x - my * y;

        if (mz == 0.f)
        {
            // A horizontal half-space bounds t alone: -dot·t ≤ c.
            if (dot > 0.f) tLo = fmaxf(tLo, -c / dot);
            else if (dot < 0.f) tHi = fminf(tHi, -c / dot);
            else if (c < 0.f) return false;
            continue;
        }
        const float inv = 1.f / mz;
        float* g = mz < 0.f ? gLo : gHi;
        const int at = 2 * (mz < 0.f ? nLo++ : nHi++);
        g[at] = c * inv;
        g[at + 1] = dot * inv;
    }

    if (tLo > tHi || !convex_where(gLo, nLo, gHi, nHi, tLo, tHi)) return false;
    low = convex_extremum(gLo, nLo, tLo, tHi, true);
    high = convex_extremum(gHi, nHi, tLo, tHi, false);
    return high > low;
}

/// <summary>Subtracts [lo, hi] from a column's sorted intervals. Mirrors DexelMap.Subtract.</summary>
__device__ __forceinline__ bool dexel_subtract(float* iv, int& n, int capacity, float lo, float hi)
{
    if (!(hi > lo)) return false;
    float out[2 * kMaxDexelIntervals + 2];
    bool overflow = false;
    int w = 0;
    for (int i = 0; i < n; i++)
    {
        const float a = iv[2 * i], b = iv[2 * i + 1];
        if (hi <= a || lo >= b)
        {
            out[2 * w] = a; out[2 * w + 1] = b; w++;
            continue;
        }
        const bool left = lo > a;
        bool right = hi < b;
        if (left && right && n + 1 > capacity)
        {
            overflow = true;   // no room for a split: keep the part below the cut, lose the roof above it
            right = false;
        }
        if (left) { out[2 * w] = a; out[2 * w + 1] = lo; w++; }
        if (right) { out[2 * w] = hi; out[2 * w + 1] = b; w++; }
    }
    for (int q = 0; q < 2 * w; q++) iv[q] = out[q];
    n = w;
    return overflow;
}

__global__ void dexel_init_kernel(float* intervals, unsigned char* counts, size_t count, int k, float top)
{
    const size_t i = blockIdx.x * static_cast<size_t>(blockDim.x) + threadIdx.x;
    if (i >= count) return;
    intervals[i * k * 2] = 0.f;
    intervals[i * k * 2 + 1] = top;
    counts[i] = 1;
}

/// <summary>Subtracts the steps [first, last) from one column and returns how many of them overflowed its intervals.</summary>
/// <param name="stepIndex">The list the half-open range counts into: null for the batch itself, the tile's CSR entries
/// for the binned launch. The two are the same steps, in the same order, so the column ends up where the unbinned
/// launch puts it.</param>
/// <param name="planes">The tool's half-spaces, or null for the sphere; <paramref name="planeCount"/> tells the two
/// apart, so the layout is the host's to state and not something the kernel guesses.</param>
/// <param name="stride">Floats per step: <c>kStepFloats</c> for the sphere, <c>kConvexStepFloats</c> for the polytope.</param>
/// <remarks>Both dexel kernels go through here, so a column sees the same steps in the same order whichever launch runs
/// it and the intervals and the overflow count cannot drift apart.</remarks>
__device__ __forceinline__ unsigned int dexel_apply_column(float* __restrict__ intervals,
                                                           unsigned char* __restrict__ counts, int i, int j, int nx,
                                                           int ny, int k, float cellX, float cellY,
                                                           const float* __restrict__ steps,
                                                           const int* __restrict__ stepIndex, int first, int last,
                                                           const float* __restrict__ planes, int planeCount, int stride)
{
    if (i >= nx || j >= ny) return 0;

    const float x = (i + 0.5f) * cellX;
    const float y = (j + 0.5f) * cellY;
    const size_t column = static_cast<size_t>(j) * nx + i;
    float* global = intervals + column * k * 2;
    float local[2 * kMaxDexelIntervals];
    int n = counts[column];
    for (int q = 0; q < 2 * n; q++) local[q] = global[q];

    unsigned int over = 0;
    for (int s = first; s < last && n > 0; ++s)
    {
        const int index = stepIndex == nullptr ? s : stepIndex[s];
        const float* p = steps + static_cast<size_t>(index) * stride;
        float lo, hi;
        const bool hit = planes == nullptr ? swept_span(x, y, p, lo, hi)
                                           : convex_span(x, y, p, planes, planeCount, lo, hi);
        if (!hit) continue;
        if (dexel_subtract(local, n, k, lo, hi)) over++;
    }

    for (int q = 0; q < 2 * n; q++) global[q] = local[q];
    counts[column] = static_cast<unsigned char>(n);
    return over;
}

/// <summary>One thread per column, its intervals in local memory while it loops over every step of the batch.</summary>
__global__ void dexel_apply_kernel(float* __restrict__ intervals, unsigned char* __restrict__ counts,
                                   unsigned long long* __restrict__ overflows, int nx, int ny, int k, float cellX,
                                   float cellY, const float* __restrict__ steps, int stepCount,
                                   const float* __restrict__ planes, int planeCount, int stride)
{
    const int i = blockIdx.x * blockDim.x + threadIdx.x;
    const int j = blockIdx.y * blockDim.y + threadIdx.y;
    const unsigned int over = dexel_apply_column(intervals, counts, i, j, nx, ny, k, cellX, cellY, steps, nullptr, 0,
                                                 stepCount, planes, planeCount, stride);
    if (over != 0) atomicAdd(overflows, static_cast<unsigned long long>(over));
}

/// <summary>One block per tile of columns, over only the steps that reach the tile (the CSR the host binned).</summary>
/// <remarks>Launch with a <see cref="kDexelTile"/> square block, so one block covers exactly one tile and the tile
/// index splits into column and row with <paramref name="tilesX"/>. The steps inside a tile are in ascending order,
/// which is why the result does not depend on the binning.</remarks>
__global__ void dexel_apply_binned_kernel(float* __restrict__ intervals, unsigned char* __restrict__ counts,
                                          unsigned long long* __restrict__ overflows, int nx, int ny, int k, float cellX,
                                          float cellY, const float* __restrict__ steps, const int* __restrict__ tileStart,
                                          const int* __restrict__ tileSteps, int tilesX,
                                          const float* __restrict__ planes, int planeCount, int stride)
{
    const int tile = static_cast<int>(blockIdx.x);
    const int i = (tile % tilesX) * blockDim.x + static_cast<int>(threadIdx.x);
    const int j = (tile / tilesX) * blockDim.y + static_cast<int>(threadIdx.y);
    const unsigned int over = dexel_apply_column(intervals, counts, i, j, nx, ny, k, cellX, cellY, steps, tileSteps,
                                                 tileStart[tile], tileStart[tile + 1], planes, planeCount, stride);
    if (over != 0) atomicAdd(overflows, static_cast<unsigned long long>(over));
}

/// <summary>Per block: the sum over its columns of (stock height - material left). Finished by volume_finalize_kernel.</summary>
__global__ void dexel_volume_partial_kernel(const float* __restrict__ intervals, const unsigned char* __restrict__ counts,
                                            size_t count, int k, float top, double* __restrict__ partials)
{
    __shared__ double parts[kVolumeThreads];
    double sum = 0.0;
    const size_t stride = static_cast<size_t>(gridDim.x) * blockDim.x;
    for (size_t c = static_cast<size_t>(blockIdx.x) * blockDim.x + threadIdx.x; c < count; c += stride)
    {
        double left = 0.0;
        const float* iv = intervals + c * k * 2;
        for (int q = 0; q < counts[c]; q++) left += static_cast<double>(iv[2 * q + 1] - iv[2 * q]);
        sum += static_cast<double>(top) - left;
    }
    parts[threadIdx.x] = sum;
    __syncthreads();
    if (threadIdx.x == 0)
    {
        double total = 0.0;
        for (int q = 0; q < blockDim.x; q++) total += parts[q];
        partials[blockIdx.x] = total;
    }
}

void free_dexel(Dexel* d)
{
    if (d == nullptr) return;
    if (d->intervals != nullptr) cudaFree(d->intervals);
    if (d->counts != nullptr) cudaFree(d->counts);
    if (d->overflows != nullptr) cudaFree(d->overflows);
    if (d->steps != nullptr) cudaFree(d->steps);
    if (d->planes != nullptr) cudaFree(d->planes);
    if (d->tileStart != nullptr) cudaFree(d->tileStart);
    if (d->tileSteps != nullptr) cudaFree(d->tileSteps);
    if (d->volumePartials != nullptr) cudaFree(d->volumePartials);
    if (d->volumeOut != nullptr) cudaFree(d->volumeOut);
    delete d;
}

} // namespace

NC_API void* nc_dexel_create(int nx, int ny, float cellX, float cellY, float top, int k)
{
    if (nx <= 0 || ny <= 0 || !(cellX > 0.f) || !(cellY > 0.f) || k < 1 || k > kMaxDexelIntervals)
    {
        set_error("nc_dexel_create: invalid map %d x %d, cells %g x %g, k %d", nx, ny, cellX, cellY, k);
        return nullptr;
    }
    auto* d = new (std::nothrow) Dexel();
    if (d == nullptr)
    {
        set_error("nc_dexel_create: out of host memory");
        return nullptr;
    }
    d->nx = nx;
    d->ny = ny;
    d->k = k;
    d->cellX = cellX;
    d->cellY = cellY;
    d->top = top;

    const size_t count = static_cast<size_t>(nx) * static_cast<size_t>(ny);
    cudaError_t e = cudaMalloc(&d->intervals, count * k * 2 * sizeof(float));
    if (e == cudaSuccess) e = cudaMalloc(&d->counts, count);
    if (e == cudaSuccess) e = cudaMalloc(&d->overflows, sizeof(unsigned long long));
    if (e == cudaSuccess) e = cudaMemset(d->overflows, 0, sizeof(unsigned long long));
    if (e == cudaSuccess) e = cudaMalloc(&d->volumePartials, kVolumeBlocks * sizeof(double));
    if (e == cudaSuccess) e = cudaMalloc(&d->volumeOut, sizeof(double));
    if (e == cudaSuccess)
    {
        const int threads = 256;
        const size_t blocks = (count + threads - 1) / threads;
        dexel_init_kernel<<<static_cast<unsigned int>(blocks), threads>>>(d->intervals, d->counts, count, k, top);
        e = cudaGetLastError();
        if (e == cudaSuccess) e = cudaDeviceSynchronize();
    }
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_dexel_create", e);
        free_dexel(d);
        return nullptr;
    }
    return d;
}

/// <summary>Makes the device buffer for <paramref name="count"/> packed steps big enough, growing it only when it must.
/// A batch in the other layout invalidates what is there, so the stride is part of what is being reserved.</summary>
cudaError_t reserve_steps(Dexel* d, int count, int stride)
{
    if (d->steps != nullptr && d->stepsStride != stride)
    {
        cudaFree(d->steps);
        d->steps = nullptr;
        d->stepsCapacity = 0;
    }
    if (d->stepsCapacity >= count) return cudaSuccess;
    if (d->steps != nullptr) cudaFree(d->steps);
    d->steps = nullptr;
    d->stepsCapacity = 0;
    const cudaError_t e = cudaMalloc(&d->steps, static_cast<size_t>(count) * stride * sizeof(float));
    if (e == cudaSuccess)
    {
        d->stepsCapacity = count;
        d->stepsStride = stride;
    }
    return e;
}

/// <summary>The same for the tool's half-spaces, which every step of a program reads and which are uploaded once.</summary>
cudaError_t reserve_planes(Dexel* d, int planeCount)
{
    if (planeCount <= 0) return cudaSuccess;
    if (d->planesCapacity >= planeCount) return cudaSuccess;
    if (d->planes != nullptr) cudaFree(d->planes);
    d->planes = nullptr;
    d->planesCapacity = 0;
    const cudaError_t e = cudaMalloc(&d->planes, static_cast<size_t>(planeCount) * kPlaneFloats * sizeof(float));
    if (e == cudaSuccess) d->planesCapacity = planeCount;
    return e;
}

/// <summary>The same for one of the int buffers the binning uploads.</summary>
cudaError_t reserve_ints(int** buffer, int* capacity, int count)
{
    if (count <= 0) return cudaSuccess;
    if (*capacity >= count) return cudaSuccess;
    if (*buffer != nullptr) cudaFree(*buffer);
    *buffer = nullptr;
    *capacity = 0;
    const cudaError_t e = cudaMalloc(reinterpret_cast<void**>(buffer), static_cast<size_t>(count) * sizeof(int));
    if (e == cudaSuccess) *capacity = count;
    return e;
}

/// <summary>
/// Uploads a batch and runs one of the two dexel launches, for either tool layout.
/// <para>
/// <paramref name="planeCount"/> of zero is the sphere, whose steps are <paramref name="kStepFloats"/> floats each;
/// anything else is the polytope, whose steps are <paramref name="kConvexStepFloats"/> and whose half-spaces
/// (<paramref name="planes"/>) are the same for every step of a program. <paramref name="tileStart"/> of null is the
/// unbinned launch, which walks every step of every column; otherwise the CSR is uploaded and a tile only sees the
/// steps that reach it.
/// </para>
/// </summary>
cudaError_t dexel_apply_common(Dexel* d, const float* steps, int stepCount, int stride, const float* planes,
                               int planeCount, const int* tileStart, int tileCount, const int* tileSteps,
                               int tileStepCount, double* kernelMs, double* uploadMs, const char* what)
{
    if (kernelMs != nullptr) *kernelMs = 0;
    if (uploadMs != nullptr) *uploadMs = 0;
    const bool binned = tileStart != nullptr;
    const int tilesX = (d->nx + kDexelTile - 1) / kDexelTile;
    const int tilesY = (d->ny + kDexelTile - 1) / kDexelTile;
    if (binned && tileCount != tilesX * tilesY)
    {
        set_error("%s: %d tiles for a %d x %d map, expected %d", what, tileCount, d->nx, d->ny, tilesX * tilesY);
        return cudaErrorInvalidValue;
    }

    cudaError_t e = reserve_steps(d, stepCount, stride);
    if (e == cudaSuccess) e = reserve_planes(d, planeCount);
    if (e == cudaSuccess && binned) e = reserve_ints(&d->tileStart, &d->tileCapacity, tileCount + 1);
    if (e == cudaSuccess && binned) e = reserve_ints(&d->tileSteps, &d->tileStepsCapacity, tileStepCount);
    if (e != cudaSuccess)
    {
        set_error("%s: cudaMalloc: %s", what, cudaGetErrorString(e));
        return e;
    }

    cudaEvent_t t0, t1, t2;
    if (!make_events(t0, t1, t2))
    {
        set_error("%s: cudaEventCreate: %s", what, cudaGetErrorString(cudaErrorUnknown));
        return cudaErrorUnknown;
    }
    cudaEventRecord(t0);
    e = cudaMemcpy(d->steps, steps, static_cast<size_t>(stepCount) * stride * sizeof(float), cudaMemcpyHostToDevice);
    if (e == cudaSuccess && planes != nullptr)
        e = cudaMemcpy(d->planes, planes, static_cast<size_t>(planeCount) * kPlaneFloats * sizeof(float),
                       cudaMemcpyHostToDevice);
    if (e == cudaSuccess && binned)
        e = cudaMemcpy(d->tileStart, tileStart, (tileCount + 1) * sizeof(int), cudaMemcpyHostToDevice);
    if (e == cudaSuccess && binned && tileStepCount > 0)
        e = cudaMemcpy(d->tileSteps, tileSteps, static_cast<size_t>(tileStepCount) * sizeof(int),
                       cudaMemcpyHostToDevice);
    cudaEventRecord(t1);
    if (e == cudaSuccess)
    {
        // The kernel tells the sphere from the polytope by this pointer alone, and reserve_planes leaves the
        // half-spaces of an earlier convex run in place, so a ball program on the same map would otherwise be cut
        // with planeCount 0 -- no tool at all, which takes the whole height of every column its bin reaches.
        const float* devicePlanes = planeCount > 0 ? d->planes : nullptr;
        if (!binned)
        {
            dim3 block(16, 16);
            dim3 grid((static_cast<unsigned int>(d->nx) + block.x - 1) / block.x,
                      (static_cast<unsigned int>(d->ny) + block.y - 1) / block.y);
            dexel_apply_kernel<<<grid, block>>>(d->intervals, d->counts, d->overflows, d->nx, d->ny, d->k, d->cellX,
                                                d->cellY, d->steps, stepCount, devicePlanes, planeCount, stride);
        }
        else
        {
            dexel_apply_binned_kernel<<<static_cast<unsigned int>(tileCount), dim3(kDexelTile, kDexelTile)>>>(
                d->intervals, d->counts, d->overflows, d->nx, d->ny, d->k, d->cellX, d->cellY, d->steps, d->tileStart,
                d->tileSteps, tilesX, devicePlanes, planeCount, stride);
        }
        e = cudaGetLastError();
    }
    cudaEventRecord(t2);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    if (e != cudaSuccess)
    {
        set_cuda_error(what, e);
        destroy_events(t0, t1, t2);
        return e;
    }
    float upload = 0.f, kernel = 0.f;
    cudaEventElapsedTime(&upload, t0, t1);
    cudaEventElapsedTime(&kernel, t1, t2);
    destroy_events(t0, t1, t2);
    if (uploadMs != nullptr) *uploadMs = upload;
    if (kernelMs != nullptr) *kernelMs = kernel;
    return cudaSuccess;
}

NC_API int nc_dexel_apply_steps(void* dexel, const float* steps, int stepCount, double* kernelMs, double* uploadMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || steps == nullptr || stepCount <= 0)
    {
        set_error("nc_dexel_apply_steps: invalid argument");
        return 1;
    }
    const cudaError_t e = dexel_apply_common(d, steps, stepCount, kStepFloats, nullptr, 0, nullptr, 0, nullptr, 0,
                                             kernelMs, uploadMs, "nc_dexel_apply_steps");
    if (e != cudaSuccess) return static_cast<int>(e);
    return 0;
}

/// <summary>Applies the same steps as <see cref="nc_dexel_apply_steps"/>, but a tile of columns only sees the steps that
/// reach it, so the work grows with what the tool touches instead of with columns times steps.</summary>
/// <param name="tileStart">The CSR: <paramref name="tileStart"/>[t] to <paramref name="tileStart"/>[t + 1] index
/// <paramref name="tileSteps"/>, with tileCount + 1 entries.</param>
/// <param name="tileSteps">The step indices per tile, ascending inside each tile — which is why the intervals and the
/// overflow count do not depend on the binning.</param>
NC_API int nc_dexel_apply_steps_binned(void* dexel, const float* steps, int stepCount, const int* tileStart,
                                        int tileCount, const int* tileSteps, int tileStepCount, double* kernelMs,
                                        double* uploadMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || steps == nullptr || stepCount <= 0 || tileStart == nullptr || tileSteps == nullptr ||
        tileCount <= 0)
    {
        set_error("nc_dexel_apply_steps_binned: invalid argument");
        return 1;
    }
    const cudaError_t e = dexel_apply_common(d, steps, stepCount, kStepFloats, nullptr, 0, tileStart, tileCount,
                                             tileSteps, tileStepCount, kernelMs, uploadMs,
                                             "nc_dexel_apply_steps_binned");
    if (e != cudaSuccess) return static_cast<int>(e);
    return 0;
}

/// <summary>
/// Applies a batch of convex-tool steps: the polytope swept between two poses is cut like the sphere is, and the
/// sweep's interval on a column comes out of a small linear program over <paramref name="planes"/>.
/// </summary>
/// <param name="planes">The tool's half-spaces, <paramref name="planeCount"/> of them, four floats each: the unit
/// normal and the distance. They are the same for every step of a program.</param>
/// <param name="stepCount">Steps of <paramref name="kConvexStepFloats"/> floats each, as ConvexProfile.Pack writes them.</param>
NC_API int nc_dexel_apply_convex_steps(void* dexel, const float* steps, int stepCount, const float* planes,
                                       int planeCount, double* kernelMs, double* uploadMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || steps == nullptr || stepCount <= 0 || planes == nullptr || planeCount <= 0 ||
        planeCount > kConvexPlanes)
    {
        set_error("nc_dexel_apply_convex_steps: invalid argument");
        return 1;
    }
    const cudaError_t e = dexel_apply_common(d, steps, stepCount, kConvexStepFloats, planes, planeCount, nullptr, 0,
                                             nullptr, 0, kernelMs, uploadMs, "nc_dexel_apply_convex_steps");
    if (e != cudaSuccess) return static_cast<int>(e);
    return 0;
}

/// <summary>The binned launch of <see cref="nc_dexel_apply_convex_steps"/>, with the CSR of the same meaning.</summary>
NC_API int nc_dexel_apply_convex_steps_binned(void* dexel, const float* steps, int stepCount, const float* planes,
                                              int planeCount, const int* tileStart, int tileCount, const int* tileSteps,
                                              int tileStepCount, double* kernelMs, double* uploadMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || steps == nullptr || stepCount <= 0 || planes == nullptr || planeCount <= 0 ||
        planeCount > kConvexPlanes || tileStart == nullptr || tileSteps == nullptr || tileCount <= 0)
    {
        set_error("nc_dexel_apply_convex_steps_binned: invalid argument");
        return 1;
    }
    const cudaError_t e = dexel_apply_common(d, steps, stepCount, kConvexStepFloats, planes, planeCount, tileStart,
                                             tileCount, tileSteps, tileStepCount, kernelMs, uploadMs,
                                             "nc_dexel_apply_convex_steps_binned");
    if (e != cudaSuccess) return static_cast<int>(e);
    return 0;
}

NC_API int nc_dexel_read(void* dexel, float* intervals, unsigned char* counts, long long* overflows, double* downloadMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || intervals == nullptr || counts == nullptr || overflows == nullptr)
    {
        set_error("nc_dexel_read: invalid argument");
        return 1;
    }
    const size_t count = static_cast<size_t>(d->nx) * static_cast<size_t>(d->ny);
    cudaError_t e = cudaSuccess;
    double ms = copy_back(intervals, d->intervals, count * d->k * 2 * sizeof(float), e);
    if (e == cudaSuccess) ms += copy_back(counts, d->counts, count, e);
    unsigned long long over = 0;
    if (e == cudaSuccess) ms += copy_back(&over, d->overflows, sizeof(over), e);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_dexel_read", e);
        return static_cast<int>(e);
    }
    *overflows = static_cast<long long>(over);
    if (downloadMs != nullptr) *downloadMs = ms;
    return 0;
}

NC_API int nc_dexel_volume(void* dexel, double* volumeMm3, long long* overflows, double* kernelMs)
{
    auto* d = static_cast<Dexel*>(dexel);
    if (d == nullptr || volumeMm3 == nullptr || overflows == nullptr)
    {
        set_error("nc_dexel_volume: invalid argument");
        return 1;
    }
    if (kernelMs != nullptr) *kernelMs = 0;
    const size_t count = static_cast<size_t>(d->nx) * static_cast<size_t>(d->ny);
    const int threads = kVolumeThreads;
    const size_t needed = (count + threads - 1) / threads;
    const int blocks = static_cast<int>(needed < static_cast<size_t>(kVolumeBlocks) ? needed
                                                                                    : static_cast<size_t>(kVolumeBlocks));
    QueryTiming timing;
    if (!timing.ok) return static_cast<int>(cudaErrorUnknown);
    cudaEventRecord(timing.start);
    dexel_volume_partial_kernel<<<blocks, threads>>>(d->intervals, d->counts, count, d->k, d->top, d->volumePartials);
    cudaError_t e = cudaGetLastError();
    if (e == cudaSuccess)
    {
        const double cellArea = static_cast<double>(d->cellX) * d->cellY;
        volume_finalize_kernel<<<1, 1>>>(d->volumePartials, blocks, cellArea, d->volumeOut);
        e = cudaGetLastError();
    }
    cudaEventRecord(timing.computed);
    if (e == cudaSuccess) e = cudaDeviceSynchronize();
    if (e == cudaSuccess) copy_back(volumeMm3, d->volumeOut, sizeof(double), e);
    unsigned long long over = 0;
    if (e == cudaSuccess) copy_back(&over, d->overflows, sizeof(over), e);
    if (e != cudaSuccess)
    {
        set_cuda_error("nc_dexel_volume", e);
        return static_cast<int>(e);
    }
    *overflows = static_cast<long long>(over);
    float kernel = 0.f;
    cudaEventElapsedTime(&kernel, timing.start, timing.computed);
    if (kernelMs != nullptr) *kernelMs = kernel;
    return 0;
}

NC_API void nc_dexel_destroy(void* dexel)
{
    free_dexel(static_cast<Dexel*>(dexel));
}

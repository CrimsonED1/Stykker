// Native side of Stykker.NanoCut.Gpu: a Z-map (one height per grid cell) lowered by ball-tool steps, plus a small
// C API for the managed wrapper. Built with nvcc, see build.ps1 / build.sh. This library is optional: the managed
// CPU backend is the reference and works everywhere, and CI has neither a GPU nor nvcc.
//
// The arithmetic mirrors ToolProfile.cs in src/Stykker.NanoCut.Gpu exactly: the same packed step layout (12 floats,
// mm relative to the grid origin), the same reject, the same candidate set for the minimum. Small differences come
// only from the compiler contracting a*b+c into fma, so results agree to about a float unit, not bit for bit.
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

// Floats per packed step: (x0, y0, z0, r), (wx, wy, wz, r^2), (w2, 1/w2, wz^2 + w2, wz^2).
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

__device__ __forceinline__ bool in_unit_interval(float t) { return t >= 0.f && t <= 1.f; }

/// <summary>Bottom of the ball at parameter t, or +inf when the column is outside that ball.</summary>
__device__ __forceinline__ float bottom_at(float t, float z0, float wz, float base, float twoD, float w2)
{
    float s = base + t * (twoD - w2 * t);
    return s < 0.f ? CUDART_INF_F : z0 + wz * t - sqrtf(s);
}

/// <summary>
/// Lowest z of the ball swept along one step at the column (x, y), or +inf when the column is farther than the
/// radius from the whole segment. g(t) = z0 + wz t - sqrt(S(t)) with S(t) = r^2 - p^2 + 2 d t - w2 t^2 is continuous
/// wherever S >= 0, so its minimum over the valid part of [0, 1] is at an endpoint of that interval (t = 0, t = 1 or
/// a root of S) or at a stationary point (wz sqrt(S) = d - w2 t, squared into a quadratic).
/// </summary>
__device__ __forceinline__ float ball_bottom(float x, float y, const float* __restrict__ p)
{
    const float x0 = p[0], y0 = p[1], z0 = p[2], r2 = p[7];
    const float wx = p[4], wy = p[5], wz = p[6];
    const float w2 = p[8], invW2 = p[9], k = p[10], wz2 = p[11];

    const float px = x - x0, py = y - y0;
    const float p2 = px * px + py * py;
    const float d = px * wx + py * wy;

    // Cheap reject: the horizontal distance from the column to the segment is the smallest one over all t.
    const float tc = w2 > 0.f ? fminf(fmaxf(d * invW2, 0.f), 1.f) : 0.f;
    const float ex = px - tc * wx, ey = py - tc * wy;
    if (ex * ex + ey * ey > r2) return CUDART_INF_F;

    const float base = r2 - p2, twoD = 2.f * d;
    float best = fminf(bottom_at(0.f, z0, wz, base, twoD, w2), bottom_at(1.f, z0, wz, base, twoD, w2));

    if (w2 > 0.f)
    {
        // The reject above guarantees that S(t) >= 0 for some t in [0, 1], so both discriminants are >= 0 in exact
        // arithmetic. In float they can come out slightly negative through cancellation -- above all the stationary
        // one for a horizontal step, where it is exactly zero in theory but is computed as the difference of two
        // equal products of size 4 d^2 w2^2. Clamping is safe: every candidate t with S(t) >= 0 is a real ball
        // position, so an extra one can only be too high, never too low.
        const float sq = sqrtf(fmaxf(d * d - w2 * (p2 - r2), 0.f));
        const float ta = (d - sq) * invW2, tb = (d + sq) * invW2;
        if (in_unit_interval(ta)) best = fminf(best, z0 + wz * ta);
        if (in_unit_interval(tb)) best = fminf(best, z0 + wz * tb);

        const float alpha = w2 * k;
        const float beta = -2.f * d * k;
        const float sq2 = sqrtf(fmaxf(beta * beta - 4.f * alpha * (d * d - wz2 * (r2 - p2)), 0.f));
        const float inv = 0.5f / alpha;
        const float t1 = (-beta - sq2) * inv, t2 = (-beta + sq2) * inv;
        if (in_unit_interval(t1)) best = fminf(best, bottom_at(t1, z0, wz, base, twoD, w2));
        if (in_unit_interval(t2)) best = fminf(best, bottom_at(t2, z0, wz, base, twoD, w2));
    }

    return best;
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
__global__ void volume_finalize_kernel(const double* __restrict__ partials, int blocks, float cellArea,
                                       double* __restrict__ out)
{
    double sum = 0.0;
    for (int i = 0; i < blocks; i++) sum += partials[i];
    *out = sum * static_cast<double>(cellArea);
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
        volume_finalize_kernel<<<1, 1>>>(z->volumePartials, blocks, z->cellX * z->cellY, z->volumeOut);
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

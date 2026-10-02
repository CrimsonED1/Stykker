// Native side of Stykker.NanoCut.Gpu: a Z-map (one height per grid cell) lowered by ball-tool steps, plus a small
// C API for the managed wrapper. Built with nvcc, see build.ps1 / build.sh. This library is optional: the managed
// CPU backend is the reference and works everywhere, and CI has neither a GPU nor nvcc.
//
// The arithmetic mirrors ToolProfile.cs in src/Stykker.NanoCut.Gpu exactly: the same packed step layout (12 floats,
// mm relative to the grid origin), the same reject, the same candidate set for the minimum. Small differences come
// only from the compiler contracting a*b+c into fma, so results agree to about a float unit, not bit for bit.
//
// Every entry point returns an error code and never throws across the boundary; nc_last_error explains a failure.

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
    float* heights = nullptr;
    float* steps = nullptr;
    int stepsCapacity = 0;
};

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

    const size_t count = static_cast<size_t>(nx) * static_cast<size_t>(ny);
    cudaError_t e = cudaMalloc(&z->heights, count * sizeof(float));
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

NC_API void nc_zmap_destroy(void* zmap)
{
    auto* z = static_cast<ZMap*>(zmap);
    if (z == nullptr) return;
    if (z->heights != nullptr) cudaFree(z->heights);
    if (z->steps != nullptr) cudaFree(z->steps);
    delete z;
}

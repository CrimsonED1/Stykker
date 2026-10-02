# Stykker.NanoCut.Gpu.Native

The native half of the GPU prototype: one CUDA file, a C boundary and two build scripts. It is **optional** — the
exact kernel never touches it, the managed CPU backend in `src/Stykker.NanoCut.Gpu` is the reference and works
everywhere, and CI has neither a GPU nor `nvcc`, so this directory is deliberately not part of `dotnet build` and not
in `Stykker.NanoCut.slnx` (same treatment as `bench/cgal`).

| File | What it is |
| --- | --- |
| `zmap.cu` | The kernels and the C API. One height per grid cell, lowered by ball-tool steps. |
| `build.ps1` | Windows: runs `vcvars64.bat` of Visual Studio 2022 and then `nvcc`. |
| `build.sh` | Linux / macOS: `nvcc` directly. |
| `bin/` | Build output, git-ignored. `Stykker.NanoCut.Gpu` copies it to its output when it exists. |

## Build

```powershell
# Windows (Visual Studio 2022 and the CUDA toolkit installed; cl.exe is not on the PATH by default)
powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1
powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1 -Architecture 89 -Configuration Debug
dotnet build src/Stykker.NanoCut.Gpu -c Release     # copies nanocut_gpu.dll next to the managed assembly
```

```bash
# Linux / macOS, nvcc on the PATH
./src/Stykker.NanoCut.Gpu.Native/build.sh           # sm_120 (Blackwell)
./src/Stykker.NanoCut.Gpu.Native/build.sh 89        # sm_89 (Ada)
dotnet build src/Stykker.NanoCut.Gpu -c Release
```

`-Architecture` / the first argument is the compute capability without the dot. SASS for that architecture is
embedded, plus PTX for `compute_75` so older cards and future ones still load the library. The CUDA runtime is linked
statically (`-cudart static`), so the result depends on the NVIDIA driver only, not on `cudart64_*.dll` being on the
PATH.

Verified on Windows 11 with an RTX 5070 Ti (sm_120), driver 617.14, CUDA 13.4 and Visual Studio 2022 Community.

## C API

Plain C types only, no exceptions across the boundary. Every function returns an error code (or a null pointer) and
`nc_last_error()` explains the failure.

| Function | Purpose |
| --- | --- |
| `nc_gpu_init(device)` | Selects the device and creates the context, so the first kernel launch is not the slow one. |
| `nc_gpu_device_count()` | Number of CUDA devices, negative on error. |
| `nc_gpu_device_info(...)` | Name, compute capability and memory of one device. |
| `nc_zmap_create(nx, ny, cellX, cellY, bottom, top)` | Allocates the height field on the device, filled with `top`. |
| `nc_zmap_apply_steps(zmap, steps, stepCount, &kernelMs, &uploadMs)` | Lowers the field by a whole batch of steps in one launch and reports the device times. |
| `nc_zmap_read(zmap, heights, &downloadMs)` | Copies the field back to the host. |
| `nc_zmap_destroy(zmap)` | Frees it. |
| `nc_last_error()` | Text of the last error on this thread. |

A step is 12 `float`s in millimetres relative to the grid origin, the layout of `ToolProfile.Pack` in
`src/Stykker.NanoCut.Gpu`: `(x0, y0, z0, r), (wx, wy, wz, r²), (w2, 1/w2, wz² + w2, wz²)`.

The kernel runs one thread per cell and loops over the whole batch, which is what the plan asks for: one launch for
many steps beats one launch per step, because the height field never has to leave the device in between.

## Keeping it in step with the managed side

`ball_bottom` in `zmap.cu` and `ToolProfile.Bottom` in `src/Stykker.NanoCut.Gpu/ToolProfile.cs` are the same
algorithm written twice, and both read the same packed layout. They agree to about a float unit, not bit for bit,
because `nvcc` contracts `a*b+c` into `fma`. `tests/Stykker.NanoCut.Tests/GpuZMapTests.cs` compares them cell by cell
and runs on the CPU backend alone when no GPU is present, so a change to one of the two that is not mirrored shows up
in CI on a GPU machine and in the analytic tests everywhere.

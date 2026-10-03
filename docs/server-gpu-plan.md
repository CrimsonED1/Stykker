# Plan: server mode and GPU prototype

Read `docs/server-gpu-notes.md` first: it lists the build rules, pitfalls and facts about this machine.

## Ground rules

- Repository `CrimsonED1/Stykker-NanoCut`, branch `feature/server-gpu` (already created from `main`). Never push to
  `main`. At the end, open a pull request or hand the branch over for review.
- Code, comments, docs and commit messages in English. Commit and push often.
- Do not change the exact kernel: `src/Stykker.NanoCut.Core`, `src/Stykker.NanoCut.Geometry2D`,
  `src/Stykker.NanoCut.Geometry3D`. Other agents are optimising it in parallel. New projects, the demo, the bench and
  the bench scripts may change.
- Before every push run `dotnet test -c Release`; everything must pass.

## Part 1: server mode for the demo

Goal: the geometry is computed natively in .NET on the server with all cores; the browser only displays.

1. New project `samples/Stykker.NanoCut.Server`, ASP.NET Core, .NET 10. Preferred: Blazor Server (interactive
   server) with the same Razor pages as `samples/Stykker.NanoCut.Demo`, moved into a Razor class library that both
   hosts use. Alternative: the WASM demo stays and gets a "compute on server" switch that calls `POST /api/cut`
   (scene, tool and path as JSON); the answer is `MeshBuffers` as `application/octet-stream`, matching `viewer.js`
   `addMesh` (positions, normals, indices).
2. The viewer (three.js / Babylon.js) stays in the browser. Meshes travel as bytes, never as JSON numbers.
3. Long computations report progress (the Blazor Server circuit, SignalR or server-sent events) and can be cancelled
   (`CancellationToken`, cancelled when the page is left).
4. `SolidBoolean.MaxParallelism` stays at its default (the core count). Several users at once go through a job
   queue with limited concurrency.
5. The WASM demo, including the AOT publish for GitHub Pages, must keep working.
6. Measure the same pages (Spinning disc, Grinding grains, Profiles, mill machine) in the browser (WASM with AOT)
   against the server. Add the times with the hardware to `bench/README.md` under "Server mode".

## Part 2: GPU prototype with an own CUDA wrapper (mandatory)

Goal: find out where a GPU really helps. The exact kernel stays on the CPU and gets no dependency on any GPU code.
No ILGPU: the GPU code is CUDA C, built with NVIDIA's own compiler `nvcc`, and called from C# through a thin wrapper
of our own.

1. Native part `src/Stykker.NanoCut.Gpu.Native/`:
   - `zmap.cu` with the kernels and a small C API (`extern "C"`, `__declspec(dllexport)` on Windows), for example:
     `nc_gpu_device_info`, `nc_zmap_create`, `nc_zmap_apply_steps`, `nc_zmap_read`, `nc_zmap_volume`,
     `nc_zmap_destroy`, plus `nc_last_error`. Plain C types only (pointers, ints, floats); every function returns an
     error code, never throws across the boundary.
   - Build script `build.ps1` (calls `vcvars64.bat` of Visual Studio 2022, then `nvcc -O3 -shared`) and `build.sh`
     for Linux. Targets: `-gencode arch=compute_120,code=sm_120` plus embedded PTX for older cards
     (`-gencode arch=compute_75,code=compute_75`). Output `nanocut_gpu.dll` / `libnanocut_gpu.so`.
   - Not part of the normal `dotnet build`: CI has neither a GPU nor `nvcc`. Treat it like `bench/cgal`.
2. Managed part `src/Stykker.NanoCut.Gpu/` (in the solution, so CI builds it):
   - Public API for the preview, independent of the backend, for example `ZMap` (grid over the workpiece, one height
     per cell) with `ApplySteps`, `ToMeshBuffers`, `RemovedVolume`.
   - Backend `Cpu`: the same algorithm in plain C# (`Parallel.For`, `float`). It is the reference for the GPU results
     and the only backend in CI.
   - Backend `Cuda`: P/Invoke into `nanocut_gpu` with `LibraryImport` (source generated, stays AOT compatible, so the
     `src/` build rules need no override). Load the library lazily; if it or a CUDA device is missing, report that
     cleanly (`IsAvailable`), do not crash.
   - The csproj copies the native library to the output only if it exists (`Condition="Exists(...)"`).
   - Public members need XML doc comments (warnings are errors).
3. First case: Z-map preview for the milling scenes. Fast, not exact, preview only.
   - Grid over the workpiece, for example 1024 × 768 cells, one height per cell (Z-map), `float` relative to the
     grid origin.
   - One tool step (convex hull of two ball positions, that is a capsule) lowers the heights, one thread per cell.
     For many steps prefer one launch for a batch of steps (each thread loops over the steps) over one launch per
     step.
   - Input: the same expanded scene as the bench (`bench/out/<scene>/expanded.json` from `bench/run.py`). First make
     `bench/run.py` run on Windows (`resource` is Unix only, see the notes).
   - Output: the height field as a mesh for the viewer, the removed volume, and the time per step.
4. Comparison on `bench/scenes/pocket-large.json`, measured on this machine (RTX 5070 Ti):
   - Time per step: CUDA wrapper, C# CPU backend, exact kernel (`py -3 bench/run.py bench/scenes/pocket-large.json
     --engines nanocut`, measured here, not taken from the cloud tables). Report kernel time, transfer time and first
     call (context creation) separately.
   - Volume deviation of the preview against the exact volume (84 860.612636583 mm³) for several grid sizes. Separate
     the grid error from the model error: the reference uses a polyhedral ball with 48 segments, not a true sphere
     (see the notes).
5. Tests in `tests/Stykker.NanoCut.Tests` (or a new test project in the solution): the CPU backend against analytic
   volumes (for example a single straight capsule cut into a flat block). CUDA tests compare against the CPU backend
   and skip themselves when the native library or a device is missing.
6. Second case, only if the first one pays off: batch queries, for example heights at millions of points for
   roughness maps, or collision checks over thousands of tool poses.
7. Result: `docs/gpu-findings.md` with the measurement tables, hardware, driver and CUDA versions, and a
   recommendation: worth it or not, and for what.

## Acceptance

- Branch pushed, tests pass, no changes to the exact kernel (`git diff main -- src/Stykker.NanoCut.Core
  src/Stykker.NanoCut.Geometry2D src/Stykker.NanoCut.Geometry3D` is empty).
- `bench/README.md` (server times) and `docs/gpu-findings.md` (GPU measurement) exist.
- A short summary of which pages run in server mode and how to start it:
  `dotnet run --project samples/Stykker.NanoCut.Server`, and how to build the native GPU library.

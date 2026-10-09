# Server mode and GPU prototype: notes before starting

Collected on 2026-10-02 from `main` at `b1b8b71`, before any work on `feature/server-gpu`. These are facts and
pitfalls found in the repository and on this machine; they complement the plan, they do not replace it.

## Machine

| Item | Value |
|---|---|
| GPU | NVIDIA GeForce RTX 5070 Ti, 16 GB (Blackwell, compute capability 12.0 / sm_120) |
| NVIDIA driver | 617.14 |
| OS | Windows 11 Home 10.0.26200 |
| .NET SDK | 10.0.401 (`global.json` pins 10.0.100 with `rollForward: latestFeature`) |
| Workloads | `wasm-tools` installed (needed for the AOT demo build) |
| Python | 3.12 via the launcher `py`; `python` / `python3` are only the Microsoft Store stubs and do not work |
| WSL | only `docker-desktop`, no Linux distribution |

## Build rules that affect new projects

- `Directory.Build.props` sets `TreatWarningsAsErrors=true` for every project.
- Every project whose directory contains `\src\` additionally gets `IsTrimmable=true`, `IsAotCompatible=true`,
  `GenerateDocumentationFile=true` and `IsPackable=true`. A new `src/Stykker.NanoCut.Gpu` therefore inherits the AOT
  and trimming analyzers. ILGPU compiles kernels at run time (Reflection.Emit), so the analyzers will report IL2026 /
  IL3050 style warnings, which become errors (one more reason for the own wrapper: `LibraryImport` stays AOT
  compatible). Every public member needs an XML doc comment, or the build fails (CS1591).
- The solution is `Stykker.NanoCut.slnx`. New projects must be added there, otherwise `dotnet test -c Release` at the
  root neither builds nor tests them. `bench/Stykker.NanoCut.Bench` is not in the solution; build it separately with
  `dotnet build bench/Stykker.NanoCut.Bench -c Release`.
- CI (`.github/workflows/ci.yml`) runs on ubuntu-latest without a GPU: restore, Release build of the whole solution,
  then `tests/Stykker.NanoCut.Tests` and `tests/Stykker.NanoCut.OracleTests`. Any GPU test must therefore work with
  the ILGPU CPU accelerator, or skip itself cleanly when no CUDA/OpenCL device exists.
- `.github/workflows/demo-pages.yml` publishes `samples/Stykker.NanoCut.Demo` with AOT to GitHub Pages and rewrites
  `<base href>` in `wwwroot/index.html`. If the pages move into a Razor class library, this workflow and the static
  asset paths must keep working (check with `dotnet publish samples/Stykker.NanoCut.Demo -c Release -p:Aot=true`).

## Part 1: what the demo looks like today

- `samples/Stykker.NanoCut.Demo` is a standalone Blazor WebAssembly app (`Microsoft.NET.Sdk.BlazorWebAssembly`,
  packages 10.0.12). It references `src/Stykker.NanoCut.Cutting` and compiles `tests/Shared/*.cs` into itself
  (namespace `Stykker.NanoCut.Testing`, used in `_Imports.razor`). A Razor class library must do the same.
- Pages and routes: `/` Home, `/scene/{Id}` ScenePage, `/cubes`, `/spinning`, `/grinding`, `/profiles`,
  `/machine/{Kind}` MachinePage (mill and lathe, `Machines/*.cs`), `/selftest`. The plan's "Fräsmaschine" is
  `/machine/{Kind}` with the mill machine.
- Viewer interop: `Services/Viewer.cs` imports `./js/viewer.js` with a relative path and already passes meshes as raw
  bytes (`MemoryMarshal.AsBytes(...)` of `MeshBuffers.Positions` / `Normals` (float) and `Indices` (uint)). In a Razor
  class library the module path becomes `./_content/<LibraryName>/js/viewer.js`; make the path configurable or keep
  the JS in each host.
- `wwwroot/index.html` contains an import map for three.js (`lib/three/...`) and `@babylonjs/core`
  (`js/babylon-shim.js`). `viewer.js` imports `./nanocut-three/index.js` and `./nanocut-babylon/index.js`, which the
  csproj links in from the repository's `js/` folder. A server host needs the same import map in its host page and
  the same linked files.
- Compute pattern in the pages: the work runs synchronously on the UI thread, with `StateHasChanged()` plus
  `await Task.Delay(1)` (or 20/40 ms) between steps so the browser can draw. There is no `CancellationToken` anywhere.
  In Blazor Server (variant a) the same code runs on the circuit's synchronisation context: progress reaches the
  browser through the existing SignalR circuit at each `Task.Delay`, but a long synchronous step blocks that user's
  circuit. Move each heavy step into `Task.Run` and pass a token that is cancelled when the component is disposed.
- In Blazor Server, byte arrays in JS interop calls are sent as binary over SignalR, so `Viewer.AddMesh` needs no
  change. Only browser to server messages are limited by `MaximumReceiveMessageSize` (32 KB by default); the
  meshes go server to browser.
- Parallelism: `SolidBoolean.MaxParallelism` (`src/Stykker.NanoCut.Geometry3D/SolidBoolean.cs:89`) is a static
  property, default `Environment.ProcessorCount`, used by `Parallel.For` in the core (for example `ConvexHull3.cs:56`).
  Because it is process wide, do not change it per request; limit concurrent jobs with a queue (for example a
  `SemaphoreSlim` or a bounded `Channel`) instead.

## Part 2: the bench and the exact reference

- `bench/run.py` does not run on Windows as it is: it imports the Unix-only module `resource` at the top and calls
  `resource.getrusage` (lines 102 and 111). Make that import optional (skip the CPU-time numbers when it is missing)
  or run the bench elsewhere. Call it with `py -3 bench/run.py ...`.
- Only the engines `nanocut` and `manifoldsharp` work here. `cgal` needs `bench/cgal/build/bench_cgal`, and `manifold`
  needs the Python package; neither is set up on this machine.
- `run.py` expands a scene into `bench/out/<scene>/expanded.json`: `box.min` / `box.max` and every step point in
  integer nanometres (`nm(mm) = floor(mm * 1e6 + 0.5)`), `steps` = a list of point lists (one ball before and one
  after the move), `save` = which states to write as STL.
- `bench/Stykker.NanoCut.Bench` reads that file:
  `dotnet bench/Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll <expanded.json> <out-dir> nanocut [--warm]`.
  It writes `stats.json` and STL files and times only the cutting. A GPU tool can read the same `expanded.json`.
- `bench/scenes/pocket-large.json`: block 80 × 60 × 20 mm, ball radius 3 mm with 48 segments, zig-zag path at
  z = 18 mm (5 mm deep), 7 passes 4 mm apart from x = -5 to 85 mm, step length 0.75 mm. The path leaves the block at
  both ends.
- The reference volume 84 860.612636583 mm³ belongs to the polyhedral ball (48 segments, 24 latitude bands, vertices
  on the sphere), not to a true sphere. The polyhedron lies inside the sphere, so a Z-map that models a true sphere
  converges to a slightly larger volume. A rough estimate (not measured) is a few tenths of a percent. To separate the
  grid error from this model error, either evaluate the hull of the same polyhedral points per cell, or also report
  the volume of the true sphere sweep.
- `bench/README.md` already holds results from a cloud container with 4 cores. Numbers from this machine are not
  comparable to them; measure the exact kernel again here and state the hardware next to every table.

## Part 2: ILGPU (background only; the plan uses an own CUDA wrapper instead)

- Latest release: ILGPU 1.5.3 (NuGet `ILGPU`, plus `ILGPU.Algorithms`), published 2025-07-12. Releases have become
  rare since then.
- 1.5.3 contains "Improved Cuda compatibility with future devices" (PR m4rs-mt/ILGPU#1347). Kernels on newer cards
  should therefore load.
- A fix for LibDevice on compute_100 and later, reported on an RTX 5090, was merged after 1.5.3 (PR
  m4rs-mt/ILGPU#1360) and is not in any release. So do not enable LibDevice (`Context.Create(b => b.LibDevice())`)
  on this card. Plain `MathF.Sqrt` and basic arithmetic do not need it.
- If the CUDA backend still fails on sm_120, fall back to the OpenCL accelerator (the NVIDIA driver ships OpenCL)
  and write the failure into `docs/gpu-findings.md`.
- Consumer GeForce cards run double precision at a small fraction of single precision speed. For the Z-map use
  `float` relative to the grid origin: at 100 mm the float spacing is about 8 nm, enough for a preview.
- Measure kernel time separately from compile and transfer time: the first launch compiles the kernel (hundreds of
  milliseconds); host to device copies of the height field are not part of the time per step.

## Part 2: NVIDIA side and alternatives to ILGPU

- Installed: CUDA Toolkit 13.4 (`nvcc` in `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4`, `CUDA_PATH`
  is set), driver CUDA version 13.4, `nvcuda.dll` and `OpenCL.dll` in System32. Blackwell (sm_120) is supported by
  nvcc since CUDA 12.8.
- NVIDIA ships no official C# / .NET binding (only CUDA Python, `cuda-bindings`). Hybridizer (Altimesh) is no longer
  maintained.
- ManagedCUDA (community, NuGet `ManagedCuda-13`, 13.0.64, repository active in 2026) wraps the whole CUDA driver
  API. Kernels are written in CUDA C, compiled with nvcc (or NVRTC at run time) and loaded as PTX/cubin. GitHub
  shows no standard licence for it; check the licence before adding it as a dependency.
- Smallest option: a thin native library of our own. The kernels go into one `.cu` file built with nvcc into a DLL
  with a few C functions (create grid, apply steps, read back, free); C# calls them through `LibraryImport`. This
  uses NVIDIA's current compiler directly, so sm_120 is no risk. Downsides: a native build step, and CI (no GPU, no
  nvcc) cannot build it, so it must stay optional, like `bench/cgal`.
- Decision (2026-10-02): the own thin wrapper is mandatory, ILGPU is not used. See `docs/server-gpu-plan.md`.
- Toolchain verified on this machine: `vcvars64.bat` of Visual Studio 2022 Community
  (`C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat`), then
  `nvcc -O3 -gencode arch=compute_120,code=sm_120` builds, and a test kernel runs correctly on the RTX 5070 Ti. `cl.exe`
  is not on the PATH by default, so the build script must call `vcvars64.bat` first. Visual Studio Build Tools 2022
  (`C:\BuildTools`) and CMake (`C:\Program Files\CMake`) are also installed.

## Baseline

`dotnet test -c Release` on an untouched clone of `main` (`b1b8b71`) on this machine passes in about 38 s:
`Stykker.NanoCut.Tests` 142 of 142, `Stykker.NanoCut.OracleTests` 4 of 4. `WasmSmoke` is built but runs only in CI
through node.

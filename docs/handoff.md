# Handoff: state of the work and what comes next

Kept up to date while the work goes on, so another agent (Qwen, OpenCode, Claude) can continue at any point.
Newest entries at the top of "Log". Code, comments and docs in English; the user reads German.

## Where things are

| What | Where |
| --- | --- |
| Repository | `CrimsonED1/Stykker-NanoCut`, local checkouts under `C:\_AI\StykkerNanoCut\` |
| `main` | everything below is merged: kernel round `perf-round-3` (PR #1, `968e1d6`) and `feature/server-gpu` (PR #2, `d111193`: Z-map and dexel preview on CPU and CUDA, server mode of the demo, verification); CI green |
| `feature/server-gpu`, `perf-round-3` | merged, kept for reference; start new work on a new branch from `main` |
| Checkouts | `StykkerNanoCut-GPU` (Claude), `StykkerNanoCutRepo` (Qwen), `StykkerNanoCut-OpenCode` (OpenCode); switch each to a fresh branch from `main` before new work |
| Rules | never push to `main` directly (open a PR); kernel changes (`src/Stykker.NanoCut.Core`, `Geometry2D`, `Geometry3D`) only on a kernel branch and with a review; `dotnet test -c Release` green before every push |

## How to build and verify (Windows machine with the RTX 5070 Ti)

```powershell
dotnet build -c Release
dotnet test -c Release                                   # 221 + 4 on d111193
powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1   # nanocut_gpu.dll (nvcc 13.4 + VS 2022)
dotnet build src/Stykker.NanoCut.Gpu -c Release          # copies the dll next to the managed assembly
py -3 bench/run.py bench/scenes/pocket-large.json --engines nanocut --repeat 3   # exact kernel; writes bench/out/<scene>/expanded.json
dotnet build bench/Stykker.NanoCut.GpuBench -c Release
dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll bench/out/pocket-large/expanded.json --grids 512,1024,4096 --backends cpu,cuda --repeat 5 --reference 84860.612636583 --diff
dotnet run -c Release --project samples/Stykker.NanoCut.Server  # server mode, http://localhost:5180
```

Reference values (must not change unless the kernel or the preview model changes):

- exact kernel, `pocket-large` and `pocket-large-g1`: remaining 84 860.612636583 mm³
- Z-map 1024 × 768 on both scenes: remaining 84 770.457226 mm³, CPU and CUDA identical (`max dh 0`)
- Z-map 512 × 384: 84 768.999004 mm³, 4096 × 3072: 84 770.036271 mm³

Python is `py -3` (the `python`/`python3` commands are Store stubs). `gh` is not installed and the GitHub connector is
not authorised: pull requests are opened by the user through the compare link
`https://github.com/CrimsonED1/Stykker-NanoCut/compare/main...<branch>`. CI status can be read without login from
`https://api.github.com/repos/CrimsonED1/Stykker-NanoCut/actions/runs?branch=<branch>`.

## Open items, in order

1. Nothing open from the GPU and server work; both are in `main`.
2. Ideas for later, not started: a mesh of the dexel cavities for the viewer (today only the top surface); a dexel
   preview page in the demo/server (live preview of a G-code program with a CPU/CUDA switch); half-precision
   read-back for pictures (see docs/gpu-findings.md, "Not done").

## Dexel preview: plan and status (done)

Why: the Z-map keeps one height per column, so it loses the roof of material above the tool where the ball's crown
stays below the stock top (−76.7 mm³ = −0.09 % on pocket-large, the largest part of its deviation) and cannot model a
tool buried in the stock at all. A dexel map keeps up to K material intervals [z0, z1] per column instead.

Model: the column's intersection with one swept ball (a capsule, convex) is a single interval [low, high]. low is the
existing `ToolProfile.Bottom`; high is the mirror image: the concave top curve z0 + wz·t + √(a² − w2·(t − t*)²) has its
maximum at the stationary point t* + c·a clamped to the valid interval (vertical step: max(z0, z0 + wz) + √(r² − p²)).
Each step subtracts [low, high] from the column's interval list (an interval can split in two). K fixed per map
(default 4); a subtraction that would split an interval in a full column cuts through to that interval's top instead (loses
the roof piece, as a Z-map would) and is counted in `DexelMap.Overflows`, never hidden.

Expected result: on pocket-large the dexel volume converges with the grid to the true-sphere value 84 846.749 mm³
(docs/gpu-findings.md, "Accuracy"), i.e. only the tool-model error of the 48-segment ball remains (−13.863 mm³).

Steps (tick when done):

- [x] `ToolProfile.Span` (C#) next to `Bottom`: low and high of the column's interval, same packed layout; tested
      against the double reference on long ramps (`DexelMapTests.SpanMatchesTheExactInterval`).
- [x] `DexelMap` (C#, CPU backend, `IDexelBackend` on `CpuBackend`; tests in `DexelMapTests`, 10 green; with K = 1 it is
      bit for bit the Z-map): intervals as float pairs, K per column, apply / volume / mesh (top surface only is
      fine for the viewer), overflow count. Tests: a ball buried in the stock removes 4/3·π·r³ (the case the Z-map
      cannot do); a roof stays when the crown is below the top; horizontal capsule; CPU thread count does not matter.
- [x] CUDA: `dexel_apply_kernel` + C API (`nc_dexel_create/apply_steps/read/volume/destroy`) in `zmap.cu`, `CudaBackend`
      implements `IDexelBackend`. Tests `DexelMapTests.Cuda*`: CUDA vs CPU (worst 6e-5 mm, no column with a different
      interval count, same overflows), chunks vs one batch bit for bit on the intervals in use. Note: slots behind a
      column's last interval hold stale values; never compare them.
- [x] GpuBench `--dexel K`. Result on pocket-large: 84 846.915579 mm³ at 4096 × 3072 (−0.016 %), prediction
      84 846.749 mm³; CPU = CUDA; K = 2 suffices (K = 1 = Z-map); CUDA 2.9 ms at 1024 × 768, 36 ms at 4096 × 3072.
- [x] Docs: docs/gpu-findings.md "Dexel preview", bench/README.md "Dexel preview", this file.

## Log

### 2026-10-03, Claude

- PR #2 merged `feature/server-gpu` into `main` (`d111193`), CI green. This file updated on its own branch.
- Dexel preview done (CPU + CUDA + bench + docs), 221 + 4 tests green. The representation error of the Z-map is
  gone: −0.016 % against the exact kernel instead of −0.107 %, as predicted from the tool-model error alone.
- Bench "discrepancy" settled: not a harness bug, not a kernel regression. pocket-large-g1 scatters 3-4x between single
  runs (38-170 ms, with any parallelism); the median of 30 runs is 42 ms for the exact kernel (ManifoldSharp 46 ms).
  The earlier 72-113 ms and 256 ms came from too few runs. Docs corrected (bench/README.md, gpu-findings.md): on long
  moves the preview is about 10x (CPU, 4.4 ms) and 50x (CUDA, 0.8 ms) faster than the exact kernel. Rule: scenes well
  under a second need `--repeat 30`, quote median and minimum.
- Started this file. Main contains perf-round-3; feature/server-gpu has main merged in, CI green.

# Handoff: state of the work and what comes next

Kept up to date while the work goes on, so another agent (Qwen, OpenCode, Claude) can continue at any point.
Newest entries at the top of "Log". Code, comments and docs in English; the user reads German.

## Where things are

| What | Where |
| --- | --- |
| Repository | `CrimsonED1/Stykker-NanoCut`, local checkouts under `C:\_AI\StykkerNanoCut\` |
| `main` | contains the reviewed kernel round `perf-round-3` (PR #1, merge `968e1d6`) |
| `feature/server-gpu` | GPU Z-map preview (CPU + CUDA backends), server mode of the demo, verification; `main` merged in (`cf6d09b`); not yet merged into `main` |
| Checkouts | `StykkerNanoCut-GPU` (feature/server-gpu, used by Claude), `StykkerNanoCutRepo` (feature/server-gpu, used by Qwen), `StykkerNanoCut-OpenCode` (perf-round-3, merged, idle) |
| Rules | never push to `main` directly (open a PR); never change `src/Stykker.NanoCut.Core`, `Geometry2D`, `Geometry3D` on the GPU branch; `dotnet test -c Release` green before every push |

## How to build and verify (Windows machine with the RTX 5070 Ti)

```powershell
dotnet build -c Release
dotnet test -c Release                                   # 209 + 4 on cf6d09b
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

1. **Dexel preview (in progress).** Plan and status in the section "Dexel preview" below.
2. Pull request for `feature/server-gpu` once 1 is done (or leave 1 for a later branch and open the PR now).

## Dexel preview: plan and status

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
- [ ] GpuBench `--dexel K`, measure pocket-large and pocket-large-g1 at 512 … 4096, compare with 84 846.749 mm³.
- [ ] Docs: section in docs/gpu-findings.md, bench/README.md, this file.

## Log

### 2026-10-03, Claude

- Bench "discrepancy" settled: not a harness bug, not a kernel regression. pocket-large-g1 scatters 3-4x between single
  runs (38-170 ms, with any parallelism); the median of 30 runs is 42 ms for the exact kernel (ManifoldSharp 46 ms).
  The earlier 72-113 ms and 256 ms came from too few runs. Docs corrected (bench/README.md, gpu-findings.md): on long
  moves the preview is about 10x (CPU, 4.4 ms) and 50x (CUDA, 0.8 ms) faster than the exact kernel. Rule: scenes well
  under a second need `--repeat 30`, quote median and minimum.
- Started this file. Main contains perf-round-3; feature/server-gpu has main merged in, CI green.

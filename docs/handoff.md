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

1. **Bench harness discrepancy** (in progress, see Log): `bench/run.py` reports a median of 256 ms (hull 177 ms) for
   `pocket-large-g1`, while a plain loop over the same steps takes about 90 ms on the old and the new kernel alike. Not
   a kernel regression; find out what the harness does differently.
2. **CUDA, next steps** (see `docs/gpu-findings.md`, "Not done", and the Log below).
3. Pull request for `feature/server-gpu` once 1 and 2 are settled.

## Log

### 2026-10-03, Claude

- Started this file. Main contains perf-round-3; feature/server-gpu has main merged in, CI green.

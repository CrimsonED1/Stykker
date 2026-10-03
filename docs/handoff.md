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
dotnet test -c Release                                   # 251 + 4 with step 3 of the long-program plan (25 of the 251 are new; 221 + 4 on d111193)
powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1   # nanocut_gpu.dll (nvcc 13.4 + VS 2022)
dotnet build src/Stykker.NanoCut.Gpu -c Release          # copies the dll next to the managed assembly
py -3 bench/run.py bench/scenes/pocket-large.json --engines nanocut --repeat 3   # exact kernel; writes bench/out/<scene>/expanded.json
dotnet build bench/Stykker.NanoCut.GpuBench -c Release
dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll bench/out/pocket-large/expanded.json --grids 512,1024,4096 --backends cpu,cuda --repeat 5 --reference 84860.612636583 --diff
dotnet build bench/Stykker.NanoCut.LongPrograms -c Release
dotnet bench/Stykker.NanoCut.LongPrograms/bin/Release/net10.0/Stykker.NanoCut.LongPrograms.dll all --teeth 10,20,40 --grains 60,240,960,1920 --out bench/out/long-programs   # ~4 min, grinding first
dotnet bench/Stykker.NanoCut.LongPrograms/bin/Release/net10.0/Stykker.NanoCut.LongPrograms.dll dexel --repeat 2 --out bench/out/long-programs-dexel   # step binning, binned against unbinned
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

1. **Long programs on the GPU** (main topic now): plan, steps and log in `docs/long-programs.md`. The work is on the
   branch `long-programs-step-1` (pushed), not on `main`. **Steps 1 to 3 are done.** Step 1,
   `bench/Stykker.NanoCut.LongPrograms`, measures both programs on the exact kernel with time and result (gear 34.7 s at
   z = 20 with 1231.252941 mm² and 5.4 nm flank, 147.6 s at z = 40; grinding 0.200 s at 60 grains up to 5.820 s at 1920),
   one page `bench/results-2026-10-03-long-programs.html`. Step 2 bins the steps of the ball dexel kernel into tiles of
   16 × 16 columns on the host and launches one block per tile: on a finishing pass of 793 600 steps over 160 000
   columns the kernel costs 2.7 ms against 640.7 ms unbinned (~240×, and the same removed volume in both), one page
   `bench/results-2026-10-04-long-programs-dexel.html`. Step 3 adds a convex tool on a pose sequence: a tool is
   half-spaces, a step is an orientation and two positions, and where a column meets the sweep is a linear program in
   (z, t) on both backends (`ConvexTool`, `ConvexStep`, `ConvexProfile`, `convex_span` in the kernel), binned like a
   ball program. Against the exact kernel on the same body: the octahedron to −0.001 %, a box turned 1.2 rad while
   travelling to +0.362 %, both inside the volume a column model can be off by at the grid it samples — the tests check
   that sampling bound instead of a convergence a column model cannot give (the box case at 1000 cells sits on it to the
   last digit). Next is step 4, the grinding preview, on the two blocks that are now in place: 16 half-spaces for a gear
   tooth, and a pose sequence of a whole wheel. Open and unchanged by step 3: the host-side binning is O(steps) and by
   then the larger half of the wall (18.9 ms of 36.8 ms at 793 600 steps, docs/todo.md), and the exact kernel samples a
   rotation linearly in the angle (~50 000 poses at the default 30 nm, 512 at the 4 µm the tests use). One open
   question the baseline opened: after the 2D gear kernel
   the same 3D grinding case is up to 4.4× slower in the same process (docs/todo.md, "Long programs – follow-ups") —
   the bench therefore measures grinding before gear.
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

### 2026-10-04, Qwen

- **Step 3 verified independently, and the verification earned its keep.** A read-only verifier was pointed at the
  half of the commit nobody had read — the CUDA kernel and the new tests — and found a way to empty a map that no test
  could see: the kernel tells a sphere from a polytope by the plane pointer it is handed, `reserve_planes` keeps the
  half-spaces of an earlier convex run, and the ball launch passed them on with a count of zero, so a ball program
  after a convex one became a tool that removes the whole height of every column its bin reaches (1476.83 mm³ where it
  should have removed 84.82 mm³, overflow counter 0). One line fixes it, and the test that holds it
  (`AConvexToolThenASphereOnTheSameDevice`, convex → sphere against the CPU — the order the earlier test did not have)
  was checked to fail without the fix. The two implementations of `convex_span` and `ConvexProfile.Span` came back
  statement-for-statement identical, which answers the question the briefing was most worried about, and the convex
  branch of `StepBins.Tiles` turned out to have a completeness test of its own with a non-zero precondition.
  251 + 4 tests green in Release.
- Three doc errors the same pass found, all now corrected against the source: `docs/performance.md` quoted 0.44 % for
  the box at 1000 cells where its own table says +1.224 %, `docs/long-programs.md` said 32 half-spaces against
  `kConvexPlanes = 16`, and it credited the convex bit-identity test with 301 cells where it runs 241. Still open and
  written down in `docs/todo.md`: the false order-independence claim at `docs/long-programs.md:83-84`, and the fact
  that `SamplingBound` takes the maximum over the steps rather than their union.
- Step 3 of [long-programs.md](long-programs.md) done on `long-programs-step-1`: `ConvexTool` (a tool is half-spaces,
  at most 16), `ConvexStep` (an orientation and two positions), `ConvexProfile` and `convex_span` in `zmap.cu`, reached
  through `DexelMap.ApplyConvexSteps` and binned by the same CSR as a ball program. A tool on a pose sequence is what
  steps 4 and 5 need: 16 half-spaces for a gear tooth, a whole wheel as poses. 250 + 4 tests green in Release, 24 of them
  new in `ConvexDexelTests`.
- Where a column meets the sweep is a 2D linear program in (z, t), and `F = {t : L(t) ≤ U(t)}` is exactly the
  conjunction of the pair conditions `ℓᵢ(t) ≤ uⱼ(t)`, so it is one interval per column from a min/max chain. The first
  version tested candidates for feasibility instead and dropped half of them at random at a root (a 9.10 mm error, thrown
  away); both backends now take the interval.
- Measured against the exact kernel on the same body: octahedron to −0.001 %, a box turned 1.2 rad while travelling to
  +0.362 %. The tests check the *sampling bound* and not a convergence, because a column model cannot shrink its error
  with the grid — a face exactly on the centre line of a grid takes a whole extra row of columns (closed rule), and the
  box case at 1000 cells sits on that bound to the last digit (0.4408 against 0.4408 mm³). Written up in
  docs/performance.md and the log of long-programs.md.
- CUDA agrees with the CPU reference (no differing columns; interval ends 4.3e-05…1.3e-04 mm at a cell-relative
  threshold of `cell / 100` ≈ 8.3e-04 mm), and the binned launch is bit-identical to the unbinned one. The rotating case
  needs 4 µm of sweep against the exact kernel: `Process3.Sample` holds `diameter · dθ / 2` under its sweep tolerance, so
  the default 30 nm asks for ~50 000 poses and the test runs at 512 exact intervals in 72 s — the slowest test in the
  suite, and on the critical path for steps 4 and 5.
- Not done, on purpose: no time measurement of the convex path. The plan puts that on steps 4 and 5, where a whole
  program is previewed; docs/performance.md and bench/README.md say so rather than quoting a number from a single case.
- Step 2 of [long-programs.md](long-programs.md) done on `long-programs-step-1`: `StepBins.cs` (host CSR of the steps
  per tile of 16 × 16 columns), `nc_dexel_apply_steps_binned` in `zmap.cu` (one block per tile), `CudaBackend.BinSteps`
  and a `BinMs` on `ZMapTiming` so the host cost is reported apart from the kernel. New bench mode `dexel` measures the
  binned against the unbinned launch on a finishing pass; 225 + 4 tests green.
- The first binned kernel gave a wrong result although the CSR was complete: `dexel_apply_column` used the CSR range as
  step indices where it indexes `tileSteps`. It takes an optional index list now (null for the unbinned launch), and
  `StepBinsTests` + `DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` hold the line.
- Measured: 793 600 steps over 160 000 columns cost 2.7 ms binned against 640.7 ms unbinned (~240×), same removed volume
  to the last digit. The host-side binning (18.9 ms) is now the larger half of the binned wall and belongs to step 3.

### 2026-10-03, Qwen

- Step 1 of [long-programs.md](long-programs.md) done, committed on `main` and pushed as `long-programs-step-1`: the new bench
  `bench/Stykker.NanoCut.LongPrograms`, one page `bench/results-2026-10-03-long-programs.html`, overview docs updated.
  The baseline reproduces the reference of `docs/processes.md` exactly (1231.252941 mm², 209 089 pieces, 5.4 nm at
  z = 20), so it can serve as the yardstick for the gear preview in step 5.
- Two measurement findings, both documented: the warm-up rule in `bench/README.md` did not match the code and made the
  grinding numbers 4.7× too slow (938 ms instead of 200 ms), and the 2D gear kernel leaves the 3D grinding kernel up to
  4.4× slower in the same process, cause still open. Both are written up in the log of `long-programs.md`.

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

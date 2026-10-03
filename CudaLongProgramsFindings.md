# Review: what is open, and what to do before long programs on the GPU

Read-only analysis. Nothing was built, nothing was benchmarked, nothing was committed. Written while another
session runs the tests, so every number below is either quoted from a document or read out of the source, never
measured here.

State at the time of writing: `origin/long-programs-step-1` = `69a1675`, two commits ahead of `origin/main`
(`bea756d`), no pull request open, CI run 131 green.

**Re-checked 2026-10-03, four times.** The branch has moved three times under this note. `origin/long-programs-step-1`
was at `63adb49`; step 3 then landed as `da26cfc`, was verified and fixed as `909d9f2`, and was measured as
`2f627e4`. **All three are pushed** — the branch is level with origin, 6 commits ahead of `origin/main`. The §7 and
§9 line numbers were re-verified at `63adb49` and hold. **§10 is the step-3 design, §11 the CUDA half and tests,
§12 what the verification and the measurement commits changed** — read §12 first if you read nothing else, because
it is the part that closes findings.

Closed since this note was written: §7.1 (`ApplyConvexSteps` no longer defaults to the expensive read-back),
§11.3 (`docs/long-programs.md:75` now says 16, not 32), the `ConvexTool.Ball` default, and the convex overflow
precondition. Carried into the repository by `da26cfc` and still open: §6's false sentence and `SamplingBound`,
both now written down in `docs/todo.md`. Never addressed anywhere: `PackMs`, the ball-side overflow precondition in
`DexelMapTests.cs`, the false "same float" comment in `zmap.cu`, and the missing tilted-rotation case that would
exercise the `1/m_z` conditioning.

## 1. What is open

| Where | Branch | Commit | State |
|---|---|---|---|
| `StykkerNanoCutRepo` | `long-programs-step-1` | `2f627e4` | clean, **level with origin** |
| `.qwen/worktrees/keen-leaf-f9cab1` | `worktree-keen-leaf-f9cab1` | `3981494` | clean |
| `.qwen/worktrees/eager-ray-993e76` | `worktree-eager-ray-993e76` | `7b83ecf` | one untracked file |

- The unmerged branch is the only real open change. It now has six commits: `3981494` (baseline), `69a1675`
  (documentation), `63adb49` (step 2), `da26cfc` (step 3), `909d9f2` (step-3 verification and fixes), `2f627e4`
  (step-3 measurement). **All pushed**; no pull request open yet.
- **A critical device-state bug was found and fixed in `909d9f2`.** `zmap.cu:1106` dispatched on the planes
  *pointer* being null rather than on the plane count, and `reserve_planes` (`:1259`) returned early on a count of
  zero without clearing it — so a ball program on a map a convex program had touched was cut by `convex_span` with
  `planeCount = 0`, which answers ±∞ and removes the whole height of every column its bin reached: 1476.83 mm³
  where the ball should have removed 84.82 mm³, 3072 of 16641 columns emptied, **overflow counter still at zero**,
  plus an out-of-bounds read. One-line fix plus `AConvexToolThenASphereOnTheSameDevice`. The lesson recorded in the
  commit is the one worth keeping: the existing test ran sphere-then-convex, the safe order, so nothing touched the
  dangerous one. §11's agent did not find this — it checked parity, the plane cap, `m_z`, the capacity guard,
  overflows, occupancy and binning, but not the lifetime of state on a device object. That was the gap.
- **Operational hazard, restated exactly.** Local `main` = `3981494` is **1 ahead** of `origin/main` (`bea756d`) and
  **5 behind** the local `long-programs-step-1`. The one unpushed-ahead commit is already published on the feature
  branch, so nothing is at risk of being lost; the cost is only that `main` and `origin/main` disagree, and that a
  commit on local `main` now forks the history off a different base. Merge forward, never rebase.
- No stashes, no open pull requests. Every `perf-round-*` is contained in `main`. Two further remote branches exist —
  `origin/claude/compassionate-edison-yfb38h` and `origin/agent/spinning-grinding`, both 2026-10-02 — and both are
  ancestors of `origin/main`, so they are leftovers, not open work. (Corrected 2026-10-03; the earlier wording here
  said "no other open branches", which was wrong in letter.)

## 2. `PerformanceFindings.md` is superseded

The untracked note in the `eager-ray-993e76` worktree was checked finding by finding against the current
source. Of its fifteen:

| Finding | Status in `main` |
|---|---|
| B1, float filter in `Point3.SideOf` | measured and rejected; the comment in `Point3.cs` documents the 62 % cost |
| B2, `Above` via `Orient3D` | done, `ConvexHull3.cs:331` |
| C2, C3, probe and fragment buffers | done, `SolidBoolean.cs:97,177,212` |
| C4, scratch pool in the hull | half: `_scratch` covers `EdgeTri` (`ConvexHull3.cs:37`), `parent`, `size`, `groups`, `single` are still per call (`:43,:69-73`) |
| A1, A2, `FaceMerge` | open, convexity still O(V·E), `FaceMerge.cs:122-135` |
| C1, C5, C6, D1 | open, `BooleanKernel.cs:357,535,554` and `SolidBoolean.cs:47-48` |
| B3/A3/B7, SIMD, `SkipLocalsInit`, `AggressiveInlining` | open; the whole of `src/**/*.cs` has exactly one hit and it is in `CudaNative.cs` |
| file path | wrong: `Int384.cs` is in `Core`, not `Geometry3D` |

Do not commit it as it stands. It is German where the repository documentation is English, it sits in the root
rather than in `docs/`, it carries a wrong path, and its still-open items duplicate `docs/performance.md` §10 and
`docs/todo.md` one for one. Its only unique content, the contradiction about B1, is already settled in the code
comment and in `docs/performance-audit.md` §6.

## 3. Where the GPU kernels actually stand

Both apply kernels launch `dim3 block(16,16)` over the whole grid (`zmap.cu:397`, `:1057`) and each thread loops
over all steps (`:142`, `:930`). With 1e6 steps and 3e6 columns that is 3e12 `swept_span` calls. Binning is not
an optimisation here, it is the precondition for any long-program number at all.

The lesson from `docs/performance-audit.md` §9 does not apply against this. The band index there was 3.5x
*slower* because an existing `break` already made the scan far sub-quadratic. There is no such break here; the
loop really is columns times steps.

## 4. Six proposals

### A. The linear program in step 3 is not needed

The plan finds the swept interval per column as a tiny LP in (z, t). For a convex polytope
K = {p : n_i·p <= d_i} and the vertical line p = (x, y, z) the interval is closed-form:

```
hi = min  { (d_i - n_ix*x - n_iy*y) / n_iz   |  n_iz > 0 }
lo = max  { (d_i - n_ix*x - n_iy*y) / n_iz   |  n_iz < 0 }
empty when some i with n_iz == 0 has  d_i < n_ix*x + n_iy*y
```

No iteration, about 32*5 flops, only `min` and `max`.

**It holds unchanged for the sweep.** The sweep between two poses is the Minkowski sum K + [0, w]. The facet
normals of a Minkowski sum come entirely from the facets of its summands, and support functions add, so
h_{K+[0,w]}(n_i) = d_i + max(0, n_i·w). Replacing only `d` by `d + max(0, n·w)` gives the exact swept interval
from the same loop over the same at most 32 planes. The easy direction is one line: s in [0,1] implies
s·n·w <= max(0, n·w).

Two consequences beyond cost:

- Step 5, the 2D gear preview, is the same kernel with x as the free axis instead of z. Templated over the free
  axis, it is half the work.
- `CpuBackend` is already the oracle and can run the same loop in `double`, so step 3 gets the existing
  `CudaAgreesWithTheCpuReference` shape from `DexelMapTests.cs` without new test infrastructure.

**Status, re-checked 2026-10-03 — half of this was taken, the half that matters was not.** Step 3 is in progress as
uncommitted work in the main checkout. `ConvexTool` does describe the tool as half-spaces `n·p <= d` with normalized
normals, and `ConvexProfile.PackPlanes` uploads them **once per tool** rather than once per step, which is the
representation this proposal assumed. The interval search, though, is the plan's LP: `ConvexProfile.cs:9,17,21-22`
documents both ends as coming out of a small LP in (z,t), `Extremum`/`ExtremumAt` (`:222-243`) bound the envelope
over `t ∈ [tLo, tHi]`, and the `ConvexTool` remarks describe the search as "cubic in the number of half-spaces".

So the closed form above is still **unclaimed**, and the implementation's own note about cost is the argument for it:
`ConvexProfile.MaxPlanes = 16` (`:80`) is half the 32 the plan assumed, and 16 is a number a human picks in
response to a per-step cost. Nothing in the code forces it to be 16 — the closed form is a single `min`/`max` loop
that scales the same way the packing does. See `Step2FollowUps.md`, last section.

**Answered in §10 — read that instead of this paragraph.** Step 3 shipped with the LP, and the log now explains why
in detail: three variants of the envelope walk were tried and two were numerically wrong. That reasoning is sound
and it is not about the closed form — every one of those bugs lives in the `t`-envelope, which the closed form does
not have. §10.2 re-derives it from the equation in their own documentation, §10.3 shows the sort in *their* queued
follow-up can be hoisted to the host-side packer, and §10.4 says what `MaxPlanes = 16` is costing step 4.

### B. Live-tile compaction is a different lever from binning, and for one case the stronger

For the gear, most of the area never meets the tool. Tracking `maxHeight > bottom`, or any column with a
non-zero count, per tile and launching only over live tiles removes most of the work *independently of the step
count*. Binning only attacks the step count. For grinding it is the other way round, because the wheel covers
everything.

Both are needed, with opposite weight. Do the compaction on the device (prefix sum plus a compaction kernel) so
that no synchronisation to the host is added; the server mode pays for every sync.

### C. Template the kernel on K, and measure it before the binning

`zmap.cu:925` puts `float local[2*kMaxDexelIntervals]` (128 B) in local memory per thread and `:877` adds
`float out[2*kMaxDexelIntervals + 2]` (136 B). At 256 threads per block that is roughly 68 KB of local memory per
block, and local memory is DRAM. At `MaxIntervals = 2` it would be 16 B plus 20 B, but while K is a runtime
value the compiler cannot put dynamically indexed arrays in registers. The file contains no `template<` and no
`__launch_bounds__` at all.

A `switch (k)` over 1, 2 and 4 with a generic fallback is a small, isolated change. Measure it **before** the
binning, or it contaminates the baseline and the two effects cannot be separated afterwards.

### D. Steps 2 and 3 belong in one commit

Step 2 on its own only moves the sphere baseline (0.200 s and 5.820 s in `docs/long-programs.md`) and touches
neither of the two cases the plan is actually about. Together they deliver the first realistic long program,
grinding with an octahedral grain, immediately.

### E. The 4.4x drop of the 3D kernel after the 2D kernel is missing one candidate

`docs/todo.md` has already ruled out server GC, `DOTNET_TieredCompilation=0`, the machine, and process length.
What it does not ask is whether this is memory or threading at all. Two cheap separating experiments:

- `GC.GetTotalAllocatedBytes()` and the gen2 collection counters before and after each case. More gen2
  collections in the second case makes it a memory question.
- The `ArrayPool` hypothesis. `Process2` fills large `Vec2[]` and `Region2` structures; if those still sit in
  the buckets they compete with the 3D kernel's face lists. Run
  `GC.Collect(2, blocking: true, compacting: true)` between the cases and see whether the 3D case returns to its
  fresh value. That separates "warm state" from "data".

### F. Two gaps that exist independently of the plan

1. CI never compiles `zmap.cu`. `.github/workflows/ci.yml` has only `net10` and `browser-wasm`, so a typo in
   the `.cu` file surfaces only on a machine with nvcc. Step 3 adds a few hundred lines there.
2. The CPU reference is not an independent oracle. It catches CPU/GPU divergence, not a shared mistake in the
   half-space loop of proposal A. What catches that: densely sampling points on the faces of the swept body (in
   t and barycentric) and testing `n_i·p <= d_i + max(0, n_i·w)` in `double`. The same pattern as
   `OptimizationVerification*Tests.cs` and the Clipper2 and Manifold oracles.

## 5. Two numbers to take before writing any kernel

Pure host bookkeeping in the bench, and they make every later number interpretable:

- **(step, tile) pairs divided by steps**, the duplication factor. Between 1 and 4 the win is limited, above 50
  it is large.
- **The distribution of steps per tile** (median, p95, max). It predicts load balance in advance. A max/median
  above 10 means a CSR alone is not enough and the work needs sorting plus dynamic scheduling.

## 6. Correction to the plan: the order of steps inside a tile *does* matter

`docs/long-programs.md` states: *"Removal is a union, so the order of steps inside a tile does not matter for the
result (only the overflow counting can differ)."* The first half is wrong, and it matters for step 2.

At `zmap.cu:887-890`, when a column would need a split and `n + 1 > capacity`, the code keeps the part below the
cut and **discards the roof above it**:

```c
if (left && right && n + 1 > capacity)
{
    overflow = true;   // no room for a split: keep the part below the cut, lose the roof above it
    right = false;
}
```

Which interval is sacrificed, and what the surviving set looks like afterwards, depends on the order the splits
arrive in. The removal union is order-independent; the **capacity guard is not**. The correct statement is:
order-independence holds only for columns that never reach capacity. `DexelMapTests.CudaChunksGiveTheSameBitsAsOneBatch`
pins it for time chunking at K = 4 on a 257² grid without overflow, and tiling is chunking with a different
grouping, so that test has to be re-pointed at tiles — and it will only pass on a scene that does not overflow.

Write this down before step 2 rather than discovering it through a failing comparison.

## 7. What a second, independent read of the GPU code found

A second agent worked read-only through `zmap.cu` and `src/Stykker.NanoCut.Gpu/` on ground not covered above.
The five claims below I verified myself in the source before repeating them; the rest are its findings, ranked,
and marked as inferred where it had to extrapolate.

### 7.1 `nc_dexel_read` moves the whole 2·K slot on the default path — verified, highest payoff

`zmap.cu:1091` copies `count * d->k * 2 * sizeof(float)`, the **entire slot per column and not the live
prefix**, then the counts, then the overflow counter, as three separate pageable copies. `DexelMap.ApplySteps`
defaults to `ZMapReadBack.Always` (`DexelMap.cs:130`), so this is what every batch pays. At 4096² and K = 16 that
is 2.15 GB per read, roughly 250–330 ms at the 6.5–8.7 GB/s the repo has measured for pageable transfers.

Note the asymmetry: `nc_zmap_read` copies 4 B per column, and the Z-map's volume reduction reads the same 4 B per
column on the device at roughly a hundred times the link speed. The dexel has the cheap path available and the
default takes the expensive one. Cheapest fix is to default `DexelMap` to `Never` and push the mid-cut picture
onto `TopHeights()`; the better one is to copy the counts first and then the slot array in row bands, skipping
bands where no column is live.

**Re-checked 2026-10-03: still open, and the new code repeats it.** `DexelMap.cs` is one of the files the step-3
work touches, and the new entry point carries the same default:

```csharp
public void ApplyConvexSteps(ConvexTool tool, ReadOnlySpan<ConvexStep> steps,
                             ZMapReadBack readBack = ZMapReadBack.Always)   // DexelMap.cs:146
```

So there are now two public entry points that default to the expensive path, and the convex one will read back a
2 GB class of array as soon as it has any caller. The same argument applies, and fixing it before the first caller
arrives is cheaper than fixing it after.

### 7.2 `ToolProfile.Pack` is likely the largest single cost once the kernel is fast — verified code, magnitude inferred

`ToolProfile.cs:26` allocates `new float[steps.Length * StepFloats]` per call: **48 MB at 1e6 steps, on the LOH,
zero-initialised by the runtime before a byte is written.** The loop itself does per step six double subtractions,
a double `Math.Sqrt` (line 39) and a double divide (line 42).

Estimated at 20–40 ms per apply against a kernel that after step 2 should run in single-digit milliseconds. The
only anchor available is `docs/gpu-findings.md`, where a much simpler packing loop cost 2.8 ms for 1e6 points.

Three ways out, in increasing order of value: reuse a buffer, exactly as `PackPoses` already does; pack into
pinned memory, because the `cudaMemcpy` at `zmap.cu:393` reads pageable today; or pack on the device. The device
route has one caveat that has to be handled explicitly — the host computes `c` in **double** (line 39), so a
float `sqrt` on the device shifts `c` for every step. That needs a change to the CPU reference in the same commit
and a re-measurement of the divergence that `CudaAgreesWithTheCpuReference` currently pins below 1e-4 mm.

Note this is a GPU-side problem: `CpuBackend` also packs, but 30 ms against a 5.1 s apply is noise.

### 7.3 The dexel apply kernel moves k/n times more bytes than the data — verified, arithmetic follows from the addressing

Intervals are addressed as `intervals + column * k * 2` with `column = j*nx + i` (`zmap.cu:923-925`). A 128-byte
line holds `16/k` lanes; each lane uses `8n` bytes of live data, where `n` is its count. Bytes useful per line
are `128n/k`, so the **amplification is k/n**. A K = 16 map whose columns hold one or two intervals moves up to
16 times more than the data, on both the load and the store.

This sets a floor the binning cannot beat, because every apply call reads and writes the whole slot array: the
cost scales with **columns × K**, not columns × steps. This is the wrong axis for a long-program work, and it is
a separate problem from templating on K, which shrinks the array but not the ratio and does nothing when n < k.

Related, `block(16,16)` means a warp covers 16 columns of row `j` and 16 of row `j+1`. For `heights` that costs
two 64-byte requests instead of one 128-byte one with identical DRAM traffic, and the Z-map kernel touches
`heights` exactly twice, so the gain there is near zero. For `counts` (one byte per column) 16-wide is a strict
2x sector amplification, though only once per call. The other six kernels in the file are flat 256 threads, so
they already assume 32 consecutive elements; the two apply kernels are the only ones that disagree.

### 7.4 The `dexel_subtract` buffer bound rests on an undocumented invariant — verified, write a test before step 3

`zmap.cu:877` declares `float out[2 * kMaxDexelIntervals + 2]`, 34 floats, and the guard at 889 uses
`n + 1 > capacity`. Both are correct only because **a single contiguous cut interval can strictly split at most
one column interval**: a split needs `lo > a_i` and `hi < b_i`, that is `[lo, hi] ⊂ (a_i, b_i)`, and column
intervals are disjoint, so at most one `i` can satisfy it. Hence `w ≤ n + 1 ≤ 17` and index 33 is the last one
that fits.

This is load-bearing and written down nowhere. `DexelMap.Subtract` has the **identical** bound
(`DexelMap.cs`, `stackalloc float[34]`), so a CPU-versus-CUDA test would agree on a corrupted answer and catch
nothing. It survives step 3 only if the new interval test really is the intersection of a convex set with a line,
i.e. exactly one interval. Drive a column to n = K-1, then apply a cut spanning all of its intervals, and assert
the exact interval set on both backends — before step 3, not after.

### 7.5 `MinHorizontalSquared` does not transfer to half-spaces — verified by reading both

`ToolProfile.cs:19` uses one scalar per step to decide one branch, and zeroing `wx,wy` is semantically what
"this step is vertical" means. A convex tool has up to 32 normals, each with its own `n_iz`, and "vertical" has
to be decided per half-space.

What does transfer is the shape the file already uses: `ball_bottom` and `swept_span` take the `w2 == 0.f` case
as an **exact branch with no division and no epsilon** (`:118-124`, `:845-853`). The half-space code must do the
same for `n_iz == 0`: a vertical face constrains only (x, y), so it either rejects the column outright or holds
for every t. **Never a 1/n_iz.** Do not clamp a small `n_iz` up to a threshold either — that rotates the plane and
moves the cut boundary; the repository's own history records 0.018–52 mm errors from perturbing the long-step
formulation, and the fix was to form the terms directly rather than as a difference of large ones. Normalise the
normals once per tool on the host so the threshold has a meaningful scale, and take an exact branch.

### 7.6 Smaller items, all verified in the source

- `zmap_apply_kernel` indexes with `const int index = j * nx + i` (`zmap.cu:139`) while every other kernel uses
  `size_t`. It is the only place a grid beyond 2³¹ columns wraps silently — an 8.6 GB field, reachable on a 32 GB
  card. One word, and the dexel kernel is the correct model to copy.
- Three `cudaEvent`s are created and destroyed per call, unconditionally, even when the caller passes null for
  both times (`zmap.cu:390/417`, `:435/452`, `:511-520`). `docs/gpu-findings.md` already measures 6.7 µs of
  per-call launch overhead, and this is a share of it. Create them once per map.
- `PinnedReadBack` still defaults to true (`CudaBackend.cs:44`) although `docs/todo.md` records the item closed
  as "off by default" and the repo measured 8.68 GB/s pinned against 8.70 pageable. Flip the default, keep the flag.
- The step buffer grows exact-fit and frees before allocating (`zmap.cu:375-386`), and both `cudaFree` and
  `cudaMalloc` synchronise. A batch size that creeps reallocates every call; at 1e6 steps that is a 48 MB pair.
- One global `atomicAdd` per thread on the overflow counter (`zmap.cu:939`), while the repo has measured 393 216
  overflows on pocket-large at K = 1. A `__shared__` reduction across the block makes it 256 times fewer without
  touching determinism, since integer addition is associative.
- `overflows` is memset once at create (`zmap.cu:1004`) and never again, and `CpuBackend` accumulates with `+=`
  where `CudaBackend` assigns. Re-running a program on the same map carries the first run's overflows.
- `CudaBackend.Apply` starts its wall clock before packing (`CudaBackend.cs:66-67`), `CpuBackend.Apply` packs
  before its stopwatch (`CpuBackend.cs:40` vs `:48`). The two `WallMs` columns do not measure the same span, and
  `ZMapTiming` has no `PackMs` field to separate them.
- `CudaBackend.Apply` and `ApplyDexels` do not take the per-map gate that the three query paths take.
- K = 16 is the declared maximum in three places and is exercised by no CUDA test; `DexelMapTests` uses 4 and 6.
  It is simultaneously the least measured and, per 7.3, the most expensive setting.
- Release builds with `-O3` only, no `-fmad=false` variant (`build.ps1:70-72`), while the CPU/CUDA divergence is
  attributed to fma contraction. Step 3's test has more multiply-adds than the ball's, so the one control that
  would separate a real bug from contraction does not exist.

### 7.7 On the 2D gear preview: the metric, not the method, is the blocker

Nothing 2D exists today — no `Profile2Map`, no 2-D kernel, no entry in the C API; all nine kernels are
(x, y)-column by z. But `dexel_subtract` is already axis-agnostic, it takes `[lo, hi]` and a sorted interval
list, and `Region2.ConvexParts()` returns convex polygons whose edges *are* the half-spaces. `Motion2` and
`Process2.SampleTimed` already produce the adaptively sampled rolling pose sequence. Step 5 is genuinely small
if step 3 is written with the free axis as a parameter.

The problem is the yardstick. Step 5 says: compare the area with the exact 1231.252941 mm² and the flank with the
ideal involute. Area from a row raster is quantised by the row height: one row contributes at most about 48 mm
times Δy, so resolving 0.0012 mm² needs Δy ≈ 2.6e-8 mm, about 1.8 million rows. At a realistic Δy = 1 µm the
quantisation alone is around 0.048 mm². And the flank signal is 5.4 nm against a cell of 0.1 µm, an eighteenth of
one cell.

So step 5's acceptance criterion has to be redefined as a band-limited comparison — area to something like 1e-4
relative, and the flank deviation reported as a function of row height against the exact result convolved to the
same resolution. The bench already writes the exact profile as CSV under `--out`, so the reference data exists;
the comparison step does not. This is arithmetic from the plan's own numbers, not a measurement, and it is
cheaper to settle now than after the kernel is written.

## 8. Additive contribution on step 2 (added while its verifier runs)

Added from my own reading of commit `63adb49`, on ground neither the second GPU agent nor the verifier covers.
This does **not** replace or predict the verifier's report; it complements it and is to be merged with it.

### 8.1 What the commit did not touch

Straight from `git show 63adb49 --stat`. None of the §7 findings are in the file list, so all of them are still
open after step 2:

| From §7 | File | In the commit's 16 files? |
| --- | --- | --- |
| 7.1 full 2·K slot read-back | `zmap.cu` | touched, but for the binning path |
| 7.2 `ToolProfile.Pack` per call | `ToolProfile.cs` | **absent** |
| 7.4 the `out[34]` invariant | `DexelMapTests.cs` | tests touched, invariant not addressed |
| 7.6 `int` vs `size_t` | `zmap.cu` | — |
| 7.6 `PinnedReadBack` default | `CudaBackend.cs` | **absent** |
| 7.6 overflow counter never reset | `zmap.cu` | — |
| 7.6 no `-fmad=false` variant | `build.ps1` | **absent** |
| 7.6 K = 16 untested on CUDA | `DexelMapTests.cs` | tests touched, no K = 16 |
| §6 the order claim | `docs/long-programs.md` | **absent** |

The last row matters most: step 2 is exactly the step the order-independence claim belongs to, and the commit
changed `docs/long-programs.md` by 59 lines without touching it.

### 8.2 The factor table is sampled more weakly than anything else in the repo

The commit quotes **best of 2**. `bench/README.md` documents, per scene, best of 30 for pocket-large-g1 ("a scene
this short scatters by a factor of three to four between single runs, so a median of three can land anywhere in
that range"), best of 5 for the query and point-set tables, best of 3 for the dexel preview. Best of 2 is below
the weakest documented sampling.

The exposure is concentrated in the small rows. At 24800 steps the claim is 0.1 ms against 13.6 ms, and
`docs/gpu-findings.md` measures 6.7 µs of per-call launch overhead, plus a first-touch effect on the CSR
destination that the same README already documents ("the first touch of a 4 MB destination costs more than the
copy"). A 0.1 ms figure with two samples is inside that noise. The large rows are safe; the 102x is the number
that would move.

This is not a rule violation so much as an inconsistency with the file's own discipline, and it costs nothing to
fix: `--repeat 30`, quote median and minimum, as the pocket-large table already does. Worth noting that the
commit immediately before this one, `69a1675`, corrected exactly this class of error — `bench/README.md` still
said the warm-up "runs once", which is what made the first grinding table read 4.7x too slow.

### 8.3 "The factor does not depend on the program length" does not match the table under it

The commit says the factor is independent of program length. The table under that sentence reads 102x, 212x, 247x,
240x. It more than doubles from the first row to the second. What is presumably meant is that the *binned* time
does not grow like the step count; but as written the sentence makes a claim about the factor that its own table
contradicts. It is a wording fix in `docs/long-programs.md`, and it is the kind of sentence that gets quoted back
later out of context.

### 8.4 `StepBins.Build` at 24 ns per step is slow for the work it does

18.9 ms over 793 600 steps is about 24 ns per step on the host. A step contributes a bounding-box test against a
tile list and one write into a counting sort, which is a handful of integer operations and a cache-friendly
scatter. That should be low single-digit nanoseconds per step.

This matters because it is now the reported bottleneck, and because it has the *same shape* as the problem step 2
was built to remove: work on the host that scales with steps. If the constant is what it looks like, the fix is
not a different algorithm but where the time goes, and it should be attributed before step 3 inherits it — the
CSR build is the thing step 3 will hit first, since a convex tool sweeps a box rather than a point and will
produce more (step, tile) pairs per step.

I did not measure this and could not: the machine is running another session's tests. It is arithmetic on the two
numbers the commit reports, and it should be confirmed with a profiler before anything is changed on the strength
of it.

## 9. Independent verification of step 2 (`63adb49`), merged

A separate read-only verifier walked commit `63adb49` against the plan it claims to implement, reading the code
immutably via `git show 63adb49:<path>`. What follows is its result, merged rather than replaced. Items marked
**[checked]** I confirmed myself at that commit afterwards.

### 9.1 The order claim — the core claim does hold, but for a different reason than given

**Holds, mechanically.** One block per tile, one thread per column; the thread walks `tileStart[t]..tileStart[t+1]`
(`zmap.cu:968-980`); `StepBins.Build` fills each slice with a forward cursor over ascending `s`
(`StepBins.cs:71-78`), so the slice is ascending **by construction**, not by observation. Both launches funnel
into the same `dexel_apply_column`. A column is therefore touched by exactly one thread which sees its steps in
the same order either way.

**But the commit message argues it wrongly.** *"because a column is subtracted through the same helper either way,
the binning cannot move the result"* — sharing the helper proves nothing, because the first, broken version of the
kernel shared it too. The real argument is ascending order plus completeness. The **code** gets this right and
says so (`StepBins.cs:15-18`); the commit message does not.

**[checked] The plan document still carries the false guarantee.** `docs/long-programs.md:83-84` at this commit:

> Removal is a union, so the order of steps inside a tile does not matter for the result (only the overflow
> counting can differ).

and six lines later, `:90`, the correct statement: *"writes every tile's step indices in ascending order … a column
is subtracted in the same order whichever launch runs it."* Both are in the document now. Under the capacity guard
at `zmap.cu:893-898` a different order keeps a different side of the cut, so the **intervals** move, not just the
count. This is §6, confirmed from the other direction: not a pre-implementation warning, but text that survived
the very commit that implemented the step it belongs to.

### 9.2 "overflows included" is not pinned — **[checked]**

`DexelMapTests.cs:234` asserts `Assert.Equal(b.Overflows, a.Overflows)` at 16, 32, 64 and 301 cells, with **no
precondition that either side is non-zero**. If `Overflows == 0` at all four sizes, the assertion is `0 == 0` and
demonstrates nothing about the guard — while the commit message and `docs/long-programs.md` both advertise
"overflows included".

The bench document separately states *"0 overflows in every case, since a finishing pass never fills a column"* —
that is about the bench scene, which is overflow-free by construction. The test scene is a different one.

Nuance worth keeping: `DexelMapTests.cs:86` **does** carry `Assert.True(dexel.Overflows > 0)`, in a pre-existing
test. So the mechanism is exercised somewhere. It is simply not exercised *in the comparison that step 2 rests
on*. One `Assert.True` closes the gap.

### 9.3 What holds, and is worth saying plainly

- **The completeness test is independent.** `StepBinsTests.cs:42-71` does not reuse `StepBins.Tiles`. It calls
  `ToolProfile.Span` per (step, column) itself and then reads the tile mask back out of the CSR. A bug in the
  bounding box would surface as `missed > 0` rather than cancelling out. This is the right construction.
- **The ascending-order test is a guarantee, not an observation.** `TileListsAreAscendingAndTheOffsetsAddUp`
  asserts strictly ascending within each tile, monotone offsets, and every index in range. That is what turns 9.1
  from an observation into a property.
- **The headline factor is like-for-like.** The `×` column is kernel-vs-kernel, CUDA events around the kernel only,
  no pack, no bin, no upload. The `Wall ×` column is also internally consistent (both sides include `Pack`), and
  the removed volumes come from a device reduction outside both walls. The table is not contaminated, and the
  102–240× figures stand.
- **All six documents agree.** Every table value, 18.9 / 36.8 / "seven times the kernel" (18.9 / 2.7 = 7.0 ✓),
  "225 + 4" and the Done markers are consistent across `README.md`, `bench/README.md`, `docs/handoff.md`,
  `docs/long-programs.md`, `docs/performance.md` and `docs/todo.md`. Step 2 is marked done in all of them, and the
  open item in `docs/todo.md` is the CSR build cost, correctly scoped to above ~1e6 steps per call.

### 9.4 `MarginMm = 1e-6f` is smaller than the rounding it claims to absorb — **[checked]**

`StepBins.cs:26` sets the margin to 1e-6 mm, that is **1 nm** (`[checked]`), and the comment at `:25` says it exists
"so a column exactly on the boundary cannot be lost to the rounding of a float". At the bench's own coordinates
(20 mm map, 0.05 mm cells) the chain `x0 → +wx → ±r → /cellX − 0.5f` accumulates roughly **2.5 nm** of float error,
about 2.5× the margin.

Second effect in the same place: `StepBins` tests the reach against `steps[o+3]` = `r` (`:90`), while the kernel
tests `steps[o+7]` = `r*r` (`zmap.cu:848,864,872`). The kernel's effective radius is `sqrt(r*r) ≈ r·(1 ± 3e-8)`,
worth up to ~7.5 nm of **extra** reach at r = 2.5 mm, which a 1 nm margin does not absorb.

Severity low: the estimated exposure is ≈0.17 expected occurrences across the completeness test's ~20 000 pairs,
and the test is seeded, so it is stable either way. But the comment's absolute wording is not earned by the
constants next to it, and the fix is a comment or a bigger margin, not a redesign.

### 9.5 Timing accounting is cleaner than §8 assumed, and the gap moves elsewhere

There is a new `ZMapTiming.BinMs` field with a doc comment stating correctly that it is *part of* `WallMs` and is
reported apart "so the saving … is not credited with work it did not save". That is the right instrument.

**But there is no field for `ToolProfile.Pack`.** `CudaBackend.ApplyDexels` starts its wall clock and then packs
(`CudaBackend.cs:304-305`), so packing is inside `WallMs` with no sub-timer, while `CpuBackend.ApplyDexels` packs
before its stopwatch (`CpuBackend.cs:184` vs `:192`). CPU and CUDA `WallMs` therefore still cover different spans —
pre-existing, inherited, and now more visible because a host-side build cost is being discussed in the docs.

This **strengthens §8.4**. At 793 600 steps the pack allocates and fills a 38 MB `float[]`. The claim "18.9 ms of
the 36.8 ms wall" leaves 17.9 ms of *everything else* unattributed, and the prose in `docs/long-programs.md` and
`docs/performance.md` reads as though the CSR build is the whole host cost. It is the biggest remaining host
component only if `Pack` is small, and nobody has measured that. A `PackMs` field, or one stopwatch around
`ToolProfile.Pack`, settles it and costs nothing.

### 9.6 The bit-for-bit test has no CPU arm — **[checked]**

`BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` is CUDA against CUDA, exact float equality over the live prefix.
A fault shared by both launches is invisible to it. The CPU cross-check
(`CudaAgreesWithTheCpuReference`, `:173-197`) is tolerance-based (1e-4 mm) — and, because `CudaBackend` now
defaults to binning, that pre-existing test **silently switched to the binned path** with this commit. So the
binned path does meet the reference, just not at that strictness. Worth one sentence in the test's comment.

### 9.7 CI compiles none of it — **[checked] the gap is wider than stated**

`Stykker.NanoCut.Gpu.Native` is **not in `Stykker.NanoCut.slnx`**, and `build.ps1` is not invoked by CI. So
`dotnet build` never touches `zmap.cu` at all: **the 175 changed lines of CUDA in this commit are compiled by
nothing except the author's local nvcc run.** No syntax or type error in the new
`nc_dexel_apply_steps_binned` would be caught anywhere else.

The new managed tests do run: `StepBinsTests` is pure managed and runs on CI. The CUDA one returns early with a
printed note when the backend is unavailable (`DexelMapTests.cs:207-211`), so it is explicitly skipped rather than
silently required — which is honest, but it also means a green CI says nothing about the kernel. Adding the
Native project to the solution, or a `nvcc -c` job, is the cheapest remaining improvement to the project's safety
net and it now protects 175 lines rather than none.

### 9.8 Smaller

- `extra` is counted and printed but **never asserted** (`StepBinsTests.cs:51,65,68`), so the comment's
  "deliberately generous, never in fewer" (`StepBins.cs:14-15`) is untested. Only the tight direction is.
  **[checked]**
- `StepBins.Tile = 16` (`StepBins.cs:23`, **[checked]**) and the native `kDexelTile = 16` are two independent
  constants with no cross-assertion. Mitigated: a mismatch trips the native `tileCount != tilesX*tilesY` guard with
  a clear message rather than corrupting silently.
- Index typing holds everywhere the verifier could reach: every column offset in the dexel half is `size_t`
  (`zmap.cu:930,912-917,984-1000,1221-1223,1041`). The `int` at `zmap.cu:139` is in the **Z-map** apply kernel,
  which this commit did not touch — so §7.6 stands, and the new code sets the right example.
- Unverified edge: if every step in a batch lies wholly outside the map, the CSR is empty; a zero-length
  `ReadOnlySpan<int>` may marshal as null and `nc_dexel_apply_steps_binned` rejects null. The kernel itself is
  safe (`first == last`). Cheap to settle with a test.

### 9.9 Where this leaves step 2

The implementation looks sound: the order argument is real, the completeness test is independent, the ascending
test makes it a property, and the benchmark comparison is like-for-like. Three things are genuinely open, none of
them a defect in the binning:

1. the false sentence in `docs/long-programs.md:83-84`, contradicted by `:90` in the same file,
2. the unpinned `Overflows` precondition that the commit message leans on,
3. `ToolProfile.Pack` sitting in the wall with no timer, which makes the "the CSR is the host cost" claim
   unverified.

And one structural gap that predates this commit and now protects much more code: **nothing outside the author's
machine compiles `zmap.cu`.**

## 10. Step 3: the design as described

Written 2026-10-03 against step 3, first as uncommitted work and then as commit **`da26cfc`**. Everything in this
section is checked against a *described* design — `docs/long-programs.md`, `docs/todo.md`, `docs/performance.md` —
not against the code. §11 is the code.

The documentation pass is substantial: `docs/long-programs.md` +98 lines with a "Convex tool + pose sequence
(step 3)" section, the step checkbox ticked, `docs/todo.md` a "Done" entry plus three new follow-ups,
`docs/performance.md` +60, `README.md` and `bench/README.md` adjusted.

### 10.1 The interval stayed an LP, and the rejection was of a different repair

`docs/long-programs.md` now tells the story, and it is a good one. The first version took `low` as the minimum of the
lower bound over all of `t` and `high` as the maximum of the upper bound over all of `t` separately — two different
`t`, so they cross on columns the sweep misses, and the octahedron reported cuts 3.9 mm outside its own silhouette.
The obvious repair (test each candidate for feasibility) was worse: where the body opens or closes on a column the
two bounds are *equal*, in float two or three ulops apart, so half the candidates get dropped at random and the octa
came out 9.10 mm off over 390 columns. What worked is narrowing `t` first, because `{t : z_min(t) ≤ z_max(t)}` *is*
the conjunction of the pairwise conditions.

None of that touches §4A. Every one of those bugs lives in an **envelope-over-`t`** algorithm; §4A removes `t`
entirely. So "the LP was tried and rejected" is not what happened — the envelope walk was tried and two of its three
variants were wrong, which says something about the envelope, not about the problem.

### 10.2 §4A, checked against the equation in their own documentation

Their doc states the constraint as `m·(p(z) − T_A) − t·(m·w) ≤ d`, i.e. with `m = R·n` and
`c = d + m·T_A − (m_x·x + m_y·y)`, `m_z·z ≤ c + t·(m·w)`. The swept body is

```
∃ t ∈ [0,1]  s.t.  m_z·z ≤ c + t·(m·w)   for every plane m
```

which is exactly `K + [0, w]`, the Minkowski sum of the tool at pose `A` with the segment `[0, w]`. A Minkowski sum
with a segment contributes no facet normals of its own — a segment has empty interior, so it has no facets — so
every facet normal of the sum is a facet normal of `K`, and its support function is

```
h_K+[0,w](m) = d + max(0, m·w)
```

Substituting that into their own `c` gives the interval in closed form, one `min` and one `max` over the planes:

```
hi = min { (c_i + max(0, m_i·w)) / m_iz   |  m_iz > 0 }
lo = max { (c_i + max(0, m_i·w)) / m_iz   |  m_iz < 0 }
miss when some m with m_iz == 0 has c + max(0, m·w) < 0
```

This assumes only what their model already guarantees: **one orientation per step**, so the sweep is a pure
translation (`ConvexStep` is orientation plus two positions), and a **full-dimensional** tool, which `Bounded()`
already refuses to let through. It needs no envelope, no sort, no tie-break and no candidate walk — it is the same
loop shape as the ball path, a `min`, a `max` and a division, O(m).

**The one thing that must not be got wrong is `max`, not `min`.** The tempting slip is to read "there exists a `t`"
per plane and take `∃t` inside the conjunction, which gives `m_z·z − min(0, m·w) ≤ c`. That is a strictly smaller set,
and it fails on exactly the cases that matter: for a unit square `0 ≤ z ≤ 1` swept by `w = +z` the union is
`0 ≤ z ≤ 2`, and the `min` form calls `z = 1.5` — inside the sweep — outside, because `1.5 ≤ 1 + min(0, 1) = 1` is
false. A 2-D square is the cheapest check to run before touching the kernel.

### 10.3 The sort in their queued follow-up does not have to be in the kernel

`docs/todo.md` now carries this as the first long-programs follow-up, and gives a reason it is not done: building the
envelope once is O(m), but "it needs a sort in local memory and a stable tie-break on parallel lines". The sort is
the solvable half. A line is `ℓ_i(t) = (c_i + g_i·t)/m_iz` with slope `g_i/m_iz`, and **`g_i` and `m_iz` come from the
plane and the translation `w` alone** — `c_i` is the only part that depends on the column `(x, y)`. The slope order
is therefore a property of the *step*, identical for every column that step touches, and it can be sorted once when
`ConvexProfile.Pack` writes the step, or once per step on the device into shared memory. Per-column work then stays
the monotone stack, O(m), with no sort in the kernel and no shared-memory sort buffer at all.

That does not make §10.2 redundant — both reach O(m), and §10.2 additionally removes the envelope — but it does mean
their follow-up is closer to done than it reads: the sort is a change to the host-side packer, not a kernel feature.

### 10.4 What the choice costs

`MaxPlanes = 16` is justified in the new docs by the O(m³) walk, which says plainly that twice the planes is eight
times the work and that a larger tool has to be split by the caller. The plane budget is a direct function of the
interval algorithm. Under O(m) the cap has no such reason — and it is the cap, not the walk, that will shape step 4: a
grinding wheel is many faces, and every tool over `MaxPlanes` is split before it ever reaches the kernel.

### 10.5 Status corrections to §1 and §6, forced by the doc changes

- **The false sentence is still there, and the file was heavily rewritten.** `docs/long-programs.md:83-84` still reads
  "Removal is a union, so the order of steps inside a tile does not matter for the result". This is no longer "the
  docs were untouched": the same file took **+98 lines** in this pass. The sentence survived a rewrite of the file it
  belongs to.
- **The contradicting line moved.** `Step2FollowUps.md` points at `:90` as the sentence that contradicts `:83-84`. It
  is now **`:92`** ("Both go through the same `dexel_apply_column`, so a column is subtracted in the same order
  whichever launch runs it"). Same content, shifted by two lines.
- **§1 said "no other open branches". There are two** — `origin/claude/compassionate-edison-yfb38h` (`2cc135a`) and
  `origin/agent/spinning-grinding` (`e19a24d`), both dated 2026-10-02. Harmless: both are ancestors of `origin/main`
  and neither adds anything. The inventory was wrong in letter, not in substance.
- **The `main` hazard is smaller than §1 says.** Local `main` is 1 **ahead** of `origin/main` (`3981494`) and 2
  behind `origin/long-programs-step-1`. The unpushed commit is already published on the feature branch, so nothing is
  at risk of being lost; the only cost is that local `main` and `origin/main` disagree.
- **The pack-timing follow-up got worse.** There are now **six** pack call sites — `ToolProfile.Pack` at
  `CpuBackend.cs:40,184` and `CudaBackend.cs:76,304`, plus `ConvexProfile.Pack` at `CpuBackend.cs:221` and
  `CudaBackend.cs:336` — and `PackMs` appears nowhere. The convex path adds two more untimed packs on the host side
  of the same wall-clock problem.
- **The overflow assertion is still unpinned.** `DexelMapTests.cs` is not among the modified files. The new
  `ConvexDexelTests` does compare the binned and unbinned launches bit-identically at 16, 32, 64 and 301 cells, which
  is the same shape as the old test and carries the same silence about overflows.

### 10.6 One thing the new tests changed that is worth watching

The CUDA-vs-CPU tolerance was **relaxed from a fixed 1e-4 mm to a hundredth of a cell (8.3e-04 mm on a 0.083 mm
grid)**, on the argument that the interval ends come out of a division by `m_z` and the CUDA side contracts fma where
C# does not. That argument is sound and the derived tolerance is better than the guessed one. But it is a real
loosening, and the ball path keeps 1e-4 mm — so the two backends now have different notions of "agrees", for reasons
written in a log rather than in the test. If a later step changes the division order in `convex_span`, the
tolerance absorbs it silently. Pinning the *conditioning* claim — an assertion on `1/m_z` alongside the one on the
ends — would make a change that breaks the conditioning fail for the reason it breaks, instead of moving quietly
inside a loose bound.

**Update from §11c: the conditioning is never exercised, so the tolerance is looser than it needs to be rather than
as tight as it claims.** Every test rotates about z only, so `mz` equals `n_z` exactly and the worst `1/mz`
anywhere in the suite is 12. `Orientation3.AboutAxis` exists (`ConvexStep.cs:34`) and has **no caller in the whole
repository**. The argument for 8.3e-04 mm is therefore untested, and the real hazard it describes — a plane near
horizontal moving an interval end by `1/mz` ulops — has never been reached.

## 11. Step 3, CUDA half and tests — reviewed

Reviewed by a separate agent against `da26cfc`; full report in `Step3KernelFindings.md` (413 lines). Four of its
claims were spot-checked here and all four held: `kConvexPlanes = 16` at `zmap.cu:825`, the 264 → 520 byte frame
arithmetic, the `Math.Max` over per-step extents in `SamplingBound`, and `docs/long-programs.md:75` still saying
"at most 32 in the kernel". What follows is the part that changes what should be done next.

### 11.1 The sampling bound is not a bound — this is the weakest link in step 3

`ConvexDexelTests.PreviewAgainstProcess3` asserts against `SamplingBound` at three grids, and both documentation
tables rest on it. It computes (`ConvexDexelTests.cs:349-352`)

```csharp
for (int s = 0; s < steps.Length; s++)
{
    width  = Math.Max(width,  packed[s * ConvexProfile.StepFloats + 2]);
    height = Math.Max(height, packed[s * ConvexProfile.StepFloats + 3]);
}
```

— the **maximum over steps of each per-step box's extent**, which leaves out the travel entirely. It is not the
bounding box of the sweep; it is the widest single step's box. In the rotating-box case the union spans 5.473 mm
against a per-step maximum of 2.746 mm, and the bound comes out 0.1253 where the bounding box would give 0.1907 —
**34 % low**. The test still passes, because the observed difference is 0.0147 mm³ and the polytope is inscribed well
inside its own box; it passes by that slack, not by construction.

The geometry confirms the direction: the rim volume is `P·h/2` over the silhouette's perimeter `P`, and by Crofton
a simple closed curve inside a `w × h` box has `P ≤ 2(w + h)` — so the **bounding box** of the sweep is a valid
upper bound, and what the code computes is smaller than that. Fixing it is four lines (union the per-step boxes:
`min` of `x0`, `max` of `x0 + width`, likewise in y) and can only make the bound larger, so it cannot break a test
that passes today.

This matters most for **step 4**, which is a grinding preview of 60–1920 grains each turning along a trochoid — the
shape where the union is farthest from any single step's box, and precisely the case the bound exists to police.

### 11.2 The ball kernel now carries the convex buffer, so every step-2 number is stale

`convex_span` is `__device__ __forceinline__` (`zmap.cu:948`) and is inlined into the same column routine as the ball
path, so its frame is part of every `dexel_apply_column` launch. Per thread:

| | bytes |
| --- | --- |
| `gLo[2 * kConvexPlanes]` + `gHi[...]` (`:1000`) | 256 |
| `out[2 * kMaxDexelIntervals + 2]` (`:1039`) | 136 |
| `local[2 * kMaxDexelIntervals]` (`:1096`) | 128 |
| **total** | **520** (was 264) |

Local memory frames are allocated per thread at entry, not per branch, so the **ball path pays 256 bytes it never
uses**. The byte counts are read off the declarations; what that costs in occupancy is inferred and needs
`cuobjdump -res-usage`. The consequence stands either way: the 240×/247× factors and the 2.7 ms kernel time in
`docs/long-programs.md` were measured on a binary with half this frame, so they are not numbers about the current
one. Re-run `bench LongPrograms dexel` before quoting them again.

### 11.3 `MaxPlanes` is mirrored, but nothing pins the two copies

`kConvexPlanes = 16` (`zmap.cu:825`) matches `ConvexProfile.MaxPlanes = 16` (`:88`), and both ends are enforced —
four managed caps, two native guards (`:1419`, `:1437`). My earlier worry about silent drift is smaller than it
looked. Three things remain: no test asserts the two numbers agree; the native guard is unreachable, because nothing
can build a tool over 16 planes in the first place; and on a real drift the failure is a generic
`GpuNativeException` thrown *after* the pack has already been paid, not the `ArgumentOutOfRangeException` the guard
message promises.

And `docs/long-programs.md:75` — the line my §4A and §10.4 took the "32" from — says "at most 32 in the kernel"
and is simply wrong. It contradicts `:135`, `:200`, `performance.md:250` and `todo.md:75`, which all say 16.

### 11.4 The overflow gap is half closed, and the guard is still never exercised on either tool

`ConvexDexelTests.WithOneIntervalPerColumn…` (`:478`) carries the `Assert.True(Overflows > 0)` precondition that
`DexelMapTests` lacks — §9.2's lesson learned, once. But `ConvexDexelTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne`
(`:626`) has the identical missing precondition, and the one test that does assert a non-zero overflow runs on
`CpuBackend` (`DexelMap.cs:60` is the default). So `dexel_subtract`'s guard at `zmap.cu:1052` is **never exercised
on either backend**, and §6's order-dependence is documented for a path the test suite does not reach.

### 11.5 Two comments assert something about the build that the build does not do

`zmap.cu:946` says the two backends "land on the same float", and `ConvexProfile.cs:86-87` says the same from the
other side. Neither is true as built: `build.ps1:72` passes no `--fmad=false`, so nvcc contracts FMA and RyuJIT does
not, and `fmaxf`/`fminf` swallow NaN where `MathF.Max`/`MathF.Min` propagate it (`zmap.cu:937` against
`ConvexProfile.cs:299`). The project's own test contradicts the comment and is right — it uses a tolerance precisely
because they do not land on the same float.

### 11.6 The logic parity that was worth checking, is fine

`convex_span` and `ConvexProfile.Span` are the same algorithm operation for operation — 21 steps tabulated with line
pairs, same sign conventions, same comparison directions, same candidate walk, same final `high > low`. The fear
that two hand-written copies would drift did not materialise, which is the main reason to be relaxed about §11.

## 12. What `909d9f2` and `2f627e4` changed — the scoreboard

Both pushed 2026-10-03, working tree clean, 251 + 4 tests green in Release according to their own commit bodies
(not reproduced here). `909d9f2` is the verification pass and its fixes; `2f627e4` is the first measurement of the
convex path.

### 12.1 Findings from this note that are now closed

| Was | Stand |
| --- | --- |
| §7.1 — `ApplyConvexSteps` defaulted to `ZMapReadBack.Always` | **fixed.** Default is `Never`; the tests that need host state ask for `Always` explicitly. The commit explains why it was safe: a stale host copy throws rather than answering wrongly |
| `ConvexTool.Ball(r)` threw on its own defaults | **fixed.** The default is `MaxPlanes` now, "so it cannot drift again" |
| §11.3 — `docs/long-programs.md:75` said "at most 32 in the kernel" | **fixed.** Now "at most 16 in the kernel: `kConvexPlanes`" |
| §11.4 — the convex bit-identity test compared overflows with no non-zero precondition | **fixed.** It now pins that it overflows at all: 0 / 0 / 3 / 34 over the four sizes. `DexelMapTests.cs:234` still has the identical gap |
| `docs/performance.md` quoted 0.44 % for the box at 1000 cells where its own table says +1.224 % | **fixed** |
| `docs/long-programs.md` credited the convex bit-identity test with 301 cells; it runs 241 | **fixed** |
| §11.5 — `zmap.cu:943` and `ConvexProfile.cs:86-87` claim both backends "land on the same float" | **still false, still unreworded.** `build.ps1:72` passes no `--fmad=false`, and `fmaxf`/`fminf` still swallow NaN where `MathF.Max`/`MathF.Min` propagate it |
| §11.6 — parity between `convex_span` and `ConvexProfile.Span` | **confirmed** by the same pass: statement for statement identical |
| §11g — convex `StepBins.Tiles` completeness test | **confirmed**: it exists, and it has a non-zero precondition |

### 12.2 Findings that are open and now written down in `docs/todo.md`

Both of mine, both carried into the repository by `da26cfc` and now tracked rather than merely noted:

- **`:83-84`, the false order-independence claim** — `docs/todo.md:77-79`, which names the guard, says step 3 puts
  the convex path through the same `dexel_subtract`, and is explicit that the intervals move with the cut order.
  This is §6 of this note, verbatim in substance, in the project's own words.
- **`SamplingBound` takes the maximum over the steps rather than their union** — `docs/todo.md:84-87`, with the
  independent figure "the union geometry gives about 0.19 mm³". §11.1 had reached 0.1907 by a different route; the two
  agree to three digits, from different files and different methods. The project's note adds a framing worth keeping:
  a smaller bound is the *stricter* test, so no verdict changes — it is the description in the prose that is wrong,
  not the comparison.

### 12.3 Findings that were never addressed anywhere

| Was | Stand |
| --- | --- |
| §3 — `PackMs` at six untimed pack call sites | **untouched.** `ToolProfile.Pack` at `CpuBackend.cs:40,184` and `CudaBackend.cs:76,304`, `ConvexProfile.Pack` at `CpuBackend.cs:221` and `CudaBackend.cs:336`, no timer anywhere |
| §9.2 — `DexelMapTests.cs:234` compares overflows with no non-zero precondition | **untouched.** `DexelMapTests.cs` is not in either commit's diff. The lesson was learned on the convex side and not carried back to the ball side it was found on |
| §10.6 — the `1/m_z` conditioning justifying the 8.3e-04 mm tolerance | **still never exercised.** `Orientation3.AboutAxis` (`ConvexStep.cs:34`) still has exactly one occurrence in the repository: its own definition. Every test still rotates about z only |

`PackMs` is the one that matters most of the three, and it just got more urgent rather than less: §12.4 shows the
host binning is now only 4.7 % of the convex wall, but the **pack** is not the binning, and nothing has ever measured
it on either backend. The commit that measured everything else says so itself — "the CPU arm is timed on the
caller's clock, because the CPU backend's `WallMs` leaves the pack outside it" — which is §3 restated in the
project's own words.

### 12.4 The measurement, and what it does to §10.2 — it strengthens it

`2f627e4` runs four arms per case on one map and one path: the binned and unbinned convex launch, the **ball**
program of the identical path, and the CPU backend. The numbers (from the commit body, not reproduced here):

| steps | convex kernel | bin | wall | unbinned | ball | binning | convex/ball | cpu wall |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 800 | 3.0 ms | 1.1 | 16.4 ms | 480.5 ms | 0.2 ms | 161.5× | 17.7× | 57 661.7 ms |
| 99 200 | 7.3 ms | 1.9 | 47.1 ms | 1935.4 ms | 0.5 ms | 263.4× | 15.7× | — |
| 396 800 | 32.9 ms | 8.7 | 187.6 ms | 7881.6 ms | 1.7 ms | 239.5× | 19.0× | — |
| 793 600 | 65.8 ms | 17.7 | 372.6 ms | 15 546.9 ms | 3.4 ms | 236.2× | 19.2× | — |

Three things I checked in it rather than took on faith. The binning factors are consistent with step 2's ball
figures, the convex/ball ratio is flat over all four lengths (17.7 / 15.7 / 19.0 / 19.2), so it is the
representation and not the length — that inference holds. And the host-share claim follows from the table: the same
17.7 ms of host work was 51 % of the ball's wall and is 4.7 % of the convex one. **The ball was re-measured on the
current binary**, which answers half of §11.2 — the step-2 numbers can be refreshed rather than treated as stale.

**The O(m³) sweep is the part that bears on §10.2, and it does not refute it.** At a fixed 99 200 steps, unbinned,
only the tool varying:

| half-spaces | 6 | 8 | 12 | 16 |
| --- | ---: | ---: | ---: | ---: |
| kernel | 704.1 | 1055.6 | 1935.4 | 3278.2 ms |
| removes vs ball | 18.227 | 17.131 | 11.511 | 8.789 % less |

3278.2 / 704.1 = 4.655×, while (16/6)³ = 18.96 — so the exponent is about 1.57, and a least-squares fit over all four
points gives 1.56. **Their arithmetic checks out**, and so does the framing: the O(m³) is the worst case, a ball is
not it, and they keep the bound with the measurement beside it rather than quietly dropping the argument. That is
the right way to hold both.

What it changes is *who* the worst case belongs to. The commit names it: the O(m³) case is a tool with **flat
normals**, "and a gear flank or a wheel rim is exactly that". That is step 4's grinding wheel and step 5's gear
flank — the two things the whole project exists to preview. So the measured 1.57 is the exponent on the *easy*
shape, and the authors have told us the shape they are about to build is the hard one. §10.2's closed form is O(m)
for every shape including that one, which is a stronger position after the measurement than before it.

**And the new observation the measurement makes sharper.** The commit attributes the turning-tool penalty — 8.1 /
9.5 / 9.5 / 9.6 ms against 7.3 ms for translation only at 99 200 steps — to the fact that "the kernel turns the
half-spaces per column rather than per step", and calls that "what step 5 pays for a tooth rolling over a wheel".
Under the closed form that cost disappears, and for a reason the envelope needs and the closed form does not: in
`m_z·z ≤ c + t·(m·w)` with `m = R·n`, **every quantity except `m_x·x + m_y·y` is a property of the step**, not the
column. `m`, `g = m·w`, `T_A`, and `d` come from the tool and the pose alone. So `ConvexProfile.Pack` can carry four
floats per plane per step — `m_x`, `m_y`, `m_z`, `c_step = d + m·T_A + max(0, m·w)` — and the per-column work becomes
`c = c_step − m_x·x − m_y·y`, one `min`, one `max`, one divide. The rotation is hoisted to pack time, the envelope
and its sort are gone, and the turning penalty goes with them. That is a third measured number the closed form
attacks, not just the plane-cap argument of §10.4.

### 12.5 What this session got wrong, recorded because it is the useful part

§11's agent reviewed the CUDA half thoroughly — 21-step parity table, the plane cap, `m_z`, the capacity guard,
overflows, per-thread occupancy, binning — and **did not find the critical bug**. Its questions a–g were all about
the *interval algorithm* and its *interaction with existing machinery*. The bug was in the lifetime of a pointer on
a device object across two different public entry points, which is none of those things. A brief built from "what
does this commit compute" does not reach "what does this commit leave behind"; a brief has to name that second
category explicitly. The other session's verifier, briefed on the same commit, found it.

## 13. Sweeping the bug class — one live instance left, one layer up

A second agent swept the whole class the critical bug belonged to, because the fix for that one was a single line
and the pattern it came from is not a thing you fix once. Report in `Step4StateLifetimeFindings.md` (444 lines).
`f5af7c2` is docs-only, so this ran against the same code as `2f627e4`.

**On the device side the class is now empty, and that is a useful negative rather than an absence.** Every field of
`Dexel` (`zmap.cu:839-861`), `ZMap` (`:49-66`) and `PointSet` (`:74-78`) was enumerated against every `reserve_*`
and upload helper, with which entry point writes each field and which reads it:

- `reserve_steps` (`:1235-1254`) is **correct, and is the model `reserve_planes` should have followed**: its
  stride-mismatch branch zeroes `stepsCapacity` at `:1237-1242`, so the capacity early-return at `:1243` is only
  reachable when the stride already matches. `d->stepsStride` is never read by any launch — the stride is a kernel
  argument — so it cannot go stale the way the planes pointer did. My suspicion before the sweep, that the two step
  layouts might share a cached stride, was wrong.
- The Z-map has no instance: one step layout, so no stride field, and every long-lived buffer is `cudaMemcpy`'d
  after a reserve that is allowed to early-return. `PointSet` is immutable.
- binned ↔ unbinned in any combination is safe — the unbinned launch passes `stepIndex = nullptr` and never
  dereferences `d->tileSteps`.
- `readBack` never crosses the native boundary at all; it is purely a managed decision to call `ReadDexelDevice`.
  My hypothesis that a read-back mode could leave device state behind was also wrong.

### 13.1 `DexelMap.Overflows` is the same bug one layer up, and it is live

`DexelMap.cs:108` is a plain auto-property:

```csharp
public long Overflows { get; internal set; }
```

while its neighbours `Intervals` and `Counts` are reached only through `RequireCurrent()` (`:215-219`), which
**throws** when `!IsCurrent` — "the intervals are on the backend; call ReadBack() or use
BackendRemovedVolumeMm3". And the doc comment at `:122` says outright that `IsCurrent` governs `Intervals`,
`Counts` **and `Overflows`**. The property does not honour that.

`IsCurrent` is set to `readBack == ZMapReadBack.Always || _backend.KeepsDexelsOnHost` (`:139`, `:158`). On CUDA the
convex path now defaults to `Never` and the ball path to `Always`. `map.Overflows` is written in exactly two
places — `CudaBackend.cs:378` and `:396` — both reachable only when that read-back is asked for. So the same two
programs applied to the same `DexelMap` report **different `Overflows` depending on which entry point applied
them**, and the CPU backend, which accumulates unconditionally (`CpuBackend.cs:196`, `:243`), disagrees with CUDA.

That is the fixed bug's own signature — *"with the overflow counter still at zero"* while 3072 columns were emptied.
Here the symptom is quieter, a stale number rather than a wrong geometry, but the shape is identical: silent, and
the neighbouring property proves a throw was available. No test covers it: both cross-tool tests pass `Always`
explicitly and neither reads `Overflows`.

**One line closes it, and which line is the interesting part.** Not a guard inside the property, but writing
`Overflows` on every apply regardless of read-back — it is one `long` out of a device volume reduction that already
runs — or failing `Overflows` the way `RequireCurrent()` fails the intervals. The first is cheaper and cannot be
forgotten by a caller.

### 13.2 Two latent twins of the fix, not reachable from the four entry points

- The upload at `zmap.cu:1325` still guards on the host **pointer** while the launch at `:1339` guards on the
  **count** — the fix closed one side of that expression and left the other. One word closes it. Unreachable today
  only because the managed layer never passes one without the other.
- `dexel_apply_common` still never validates `tileStart[tileCount] == tileStepCount`. Pre-existing since step 2,
  flagged in §8 of this note; still open.

### 13.3 Reachability, bounded honestly

No sample — **including `Stykker.NanoCut.Server`** — references `Stykker.NanoCut.Gpu` at all, and every bench case
builds a fresh `DexelMap`. So this was a live defect in a public API with **no in-repo trigger**. That cuts both
ways: the server is exactly where a `DexelMap` would be reused across programs, so the first caller to reuse one
lands on §13.1 rather than around it.

Eight order-dependent pairs with no test coverage are named in §6 of that report. The cheapest two are the
`BinSteps = false` twin of the fix's own regression test, and a tool swap on one map (16 planes → 4).

## 14. Can step 4 run? — feasibility, and one parameter that means something else

A third agent asked whether step 4 (a grinding preview: grains as convex tools on their trochoids) can run under the
limits step 3 left. Report in `Step4FeasibilityFindings.md`. Against `f5af7c2`, which is docs-only, so the same code
as §13.

**The plane cap is not the friction.** `GrainSpec` (`GrindingWheel.cs:29`) has no shape parameter; `Build`
hard-codes the octahedron at `:110` and hulls it at `:129`. Six vertices → eight triangular facets → **8
half-spaces, for every wheel the code can build**. `SizeMm` only scales `h`. So 8 against `MaxPlanes = 16`, with
room to spare — and because a simplicial polytope *is* the intersection of its facet half-spaces, the `ConvexTool`
would be the same body `Process3` cuts, with none of the inscribed-polyhedron error `ConvexTool.Ball` carries
(8.8–18 % loss). The `Solid → ConvexTool` converter needed to get there exists, but only inside the test file
(`ConvexDexelTests.cs:21-31`), is private, and does not re-origin the tool at the grain.

**The split path does not exist, and a comment in two source files and three docs says it does.** `ConvexTool.cs:37-38`
and `ConvexProfile.cs:84` both promise that "a larger tool is split, which is what `ToolShape` does anyway".
`ToolShape` splits for *convexity*, not for plane count (`ToolShape.cs:8`; `FromConvexParts` does none), and a
single convex `Solid` — a sphere, a cylinder, a bond — is far over 16. A grep for `HalfSpace|FromPlanes|MaxPlanes`
across `src/**/*.cs` returns `ConvexTool.cs`, `ConvexProfile.cs` and the test file. Both guards are **refusals, not
splits**. Moot for a grain; it is the reason there is no fallback the moment step 4 wants a bond or a rim.

**The step count is not the scaling story the plan assumes.** One pass per grain per revolution matches the measured
3793/1920 = 1.98; at 5 passes per rotation that is ~18 965 steps, and at the measured 23.8 ns/step the CSR build is
**0.45 ms** — roughly 40× below what step 2 already measured, and far under the ~10⁶-step threshold where
`docs/long-programs.md:114` puts the host in charge. `docs/long-programs.md` says "then scale to a covered wheel";
at the step count there is nothing to scale. **What does scale is the grid**: ~6 ms at 10 µm cells, ~148 ms at 2 µm,
~1.2 s at 0.5 µm.

**The exact reference is affordable at both counts, and is already measured** — 0.200 s for 60 grains and 5.820 s
for 1920, in `docs/long-programs.md:50`. Every input a preview needs is public: `Passes` is planned in the
constructor and `PoseAt` is public, so a preview can read the pass list and never call `Run()`. One trap worth
naming: do **not** build the reference by calling `Process3` per grain. That costs ~87 s instead of 5.8 s, because
`GrindingSimulation`'s 0.3 mm cell splitting is what makes the reference cheap.

### 14.1 The volume claim cannot discriminate at a fast grid

`SamplingBound` charges the rim against the grain's **full** swept height, which for a grain is 0.13 mm — not the
2 µm bite depth that is actually at risk. Summed over the cutting passes at 1920 grains, the bound comes out at
**6.6× the entire removed volume** at 10 µm cells, and does not get under 10 % until roughly 0.15 µm cells. That
reading is pessimistic — only the bite depth is exposed, and with random grain orientations the errors are
two-sided — but the mean bite depth is the agent's assumption, not a number in the source, and it is the number
that decides the question. The workable form of step 4's comparison is therefore **Ra, Rz and mean depth**, not
volume: the bench's `--out` already writes the profile CSV, and ~2 µm cells cost ~148 ms against the reference's
5.82 s.

### 14.2 `StepsForRotation`'s radius parameter means something other than what the call sites pass

Verified and worth fixing before anyone copies the pattern. `ConvexStep.cs:86-88` documents the parameter as an
**orbit** radius — "The outermost corner rides a circle of that radius, and a step rotating by d misses its chord
by `r·(1 − cos(d/2)) ≈ r·d²/8`, so `d ≤ √(8·tol/r)`" — and the implementation at `:95-102` is exactly that. The
grain's corner rides a circle of the *wheel* radius, ~9.98 mm, because `GrindingWheel.Place` (`:118-122`) rotates
about z at `r = centre + v.X`.

Both existing call sites pass the tool's own corner radius instead:

```csharp
int n      = ConvexStep.StepsForRotation(sweep, tool.RadiusMm, tolMm);      // ConvexDexelTests.cs:218
int stepsForTurn = ConvexStep.StepsForRotation(turn, tooth.RadiusMm, 0.002); // ConvexDexelTests.cs:413
```

Since `d ∝ 1/√r`, that under-samples by `√(r_orbit / r_tool)`. At the rotating-box call: with `r = 1.2 mm`,
`d = √(8·0.002/1.2) = 0.1155 rad`, giving 11 steps — which is exactly the "11 preview steps" the documentation
quotes. With the correct `r = 9.98 mm`, `d = 0.0400 rad` and 30 steps. The chord error actually incurred at 11
steps is `9.98 · (1 − cos(0.1091/2)) ≈ 14.8 µm`, against the 2 µm the call asks for.

The tests still pass because the exact-kernel comparison has a tolerance far wider than that. This is not a broken
test; it is a helper whose parameter reads like a tool radius, is documented as an orbit radius, and is called with
the tool radius in both places it is called. Renaming it `orbitRadiusMm` and fixing the two call sites would make
step 4's first caller get it right by accident.

### 14.3 The `m_z` hazard is real, but not for the reason reported — and it is worse

**Correction.** The report gives `m_z = −(n_x sinθ + n_y cosθ)/√3` and says two of every grain's eight planes pass
through horizontal four times per revolution, so ~9 % of each pass is near it. That formula describes a rotation
about an axis **in the x-y plane**, and `GrindingWheel.Place` does not do that — it rotates about **z** (`:118-122`).
A rotation about z leaves the z-component of a vector unchanged, so for this wheel

```
m_z = n_z  for every step of the grain's revolution, and for all θ
```

For the default orientation it is identically `±1/√3 ≈ 0.577` — never near zero, and the phase plays no role at
all. The transient does not exist.

**What does exist is worse, because it does not average out.** `GrainSpec.Orientation` is an arbitrary `Pose3?`
(`:29`, "Rotation of the octahedron, null: a vertex points radially outwards"). A caller who supplies one rotates
all eight normals, and one of them can land near the x-y plane — giving that grain a small `m_z` for its **entire**
revolution, at every step, rather than for a transient 9 % of one pass. `ConvexProfile.Span` (`:213`) and the
kernel's `convex_span` (`:1022`) both do a bare `1f / mz` with no epsilon and no early-out, and `Where` divides on
quantities that both blow up there. Every step-3 test rotates about z only, which is why this has never been
reached: `Orientation3.AboutAxis` still has no caller anywhere in the repository.

So the prerequisite for step 4 is the tilted-rotation test this note has asked for four times — and the form of the
hazard should be a **grain with a tilted `Orientation`**, driven against the CPU reference, not a grain rotating
about an arbitrary axis.

### 14.4 One more thing to pin from the start

The 413-of-3793 population that cuts interior intervals and creates roofs is exactly what overflows against `K`,
and `docs/todo.md` already records that the guard is untested on both backends (§13.1 above). Step 4 should carry an
assertion with a non-zero precondition from its first commit, or it repeats §9.2 a third time.

## 15. Not done here

No build, no test run, no nvcc build, no benchmark. No commit, no push, no pull request opened, no worktree
removed, no branch checked out or moved, no patch applied.

**This session only collects.** The four follow-ups against `63adb49` are written down and prepared as
`step2-followups.patch`, and nothing has been done about any of them. `f5af7c2` is local and unpushed. §4A, §7.1,
§10, §11 and §13 are observations about a described design and reviewed commits, not verdicts that block anything.
The fixes come later.

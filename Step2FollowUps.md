# Step 2 follow-ups: four things `63adb49` leaves open

Written after reading `63adb49` ("Long programs step 2: bin the steps by tile") against the plan it claims to
implement. Nothing was built and nothing was run; every line number is from `63adb49`. The analysis behind this
note is `CudaLongProgramsFindings.md` §9, and the changes below are prepared as `step2-followups.patch`
(32 insertions, 6 deletions over five files, `git apply --check --reverse` verified against `63adb49`).

The binning itself holds up. One block per tile, one thread per column, and the per-tile step slice is ascending **by
construction** because `StepBins.Build` fills it with a forward cursor over `s` (`StepBins.cs:71-78`). A column
therefore sees its steps in the same order whichever launch runs it. The completeness test is independent — it calls
`ToolProfile.Span` itself rather than reusing `StepBins.Tiles` (`StepBinsTests.cs:42-71`) — and the benchmark
comparison is kernel against kernel. None of the four items below is a defect in the binning.

## Status, re-checked against the working tree

Re-read on 2026-10-03 after `git fetch --all --prune`. `origin/long-programs-step-1` was at `63adb49`; step 3 has
since landed as `da26cfc`, been verified and fixed as `909d9f2`, and been measured as `2f627e4`. **All pushed**,
working tree clean, branch level with origin. Two of the four items below are now closed; the other two are written
down in `docs/todo.md` rather than left in this worktree.

| Item | State |
| --- | --- |
| 1. the false order claim | **still there** at `docs/long-programs.md:83-84` — **but now tracked**: `docs/todo.md:77-79` names the guard, states that step 3 puts the convex path through the same `dexel_subtract`, and says the intervals move with the cut order |
| 2. overflows not pinned | **half fixed.** The convex side now pins it (`ConvexDexelTests`, 0 / 0 / 3 / 34 over the four sizes). The ball side is untouched — `DexelMapTests.cs` is in neither commit's diff, so `:234` still compares `0 == 0` on a scene that may never reach the guard |
| 3. `PackMs` missing | **still true, and more urgent.** Six pack call sites, no timer. The measurement commit says out loud that "the CPU arm is timed on the caller's clock, because the CPU backend's `WallMs` leaves the pack outside it" |
| 4. CI does not compile `zmap.cu` | **still true** — no Native project in `Stykker.NanoCut.slnx`, and `zmap.cu` has now grown by 395 lines across two commits CI never saw |
| `MarginMm = 1e-6f` | still 1 nm at `StepBins.cs:27` |
| `extra` never asserted | still open |

Two other findings from this file are closed: `ConvexTool.Ball(r)` no longer throws on its own defaults (the default
is `MaxPlanes` now), and `DexelMap.ApplyConvexSteps` no longer defaults to `ZMapReadBack.Always` — it is `Never`.

The contradicting sentence that item 1 points at moved from `:90` to **`:92`**; `83-84` itself never moved.

**A critical bug was found and fixed in between.** `zmap.cu:1106` dispatched on the planes *pointer* rather than on
the plane count, and `reserve_planes` (`:1259`) returned early on a count of zero without clearing it — so a ball
program on a map a convex program had touched was cut by `convex_span` with `planeCount = 0`, which answers ±∞ and
removes the full height of every column its bin reached: 1476.83 mm³ where 84.82 mm³ was expected, 3072 of 16641
columns emptied, **overflow counter still at zero**, plus an out-of-bounds read. Fixed in `909d9f2` with the
regression test `AConvexToolThenASphereOnTheSameDevice`. The existing test ran sphere-then-convex — the safe
order — so nothing in the suite had touched the dangerous one.

## 1. `docs/long-programs.md:83-84` states a guarantee that does not hold

> Removal is a union, so the order of steps inside a tile does not matter for the result (only the overflow counting
> can differ).

It does not hold. The capacity guard in `dexel_subtract` (`zmap.cu:893-898`) keeps the part below a cut and discards
the roof above it when a column has no room to split:

```c
if (left && right && n + 1 > capacity)
{
    overflow = true;   // no room for a split: keep the part below the cut, lose the roof above it
    right = false;
}
```

Which side survives depends on the order the cuts arrive in, so under that guard the **intervals** move, not only the
count. Order-independence holds for columns that never reach capacity.

This text predates the commit and was not introduced by it, but it survived the commit that implemented the step it
belongs to — and the same file now contradicts it six lines later (`:90`, "a column is subtracted in the same order
whichever launch runs it"). The prepared patch replaces `:83-84` with the correct statement and points forward to
the section that gets it right.

## 2. "overflows included" is not pinned by the test it rests on

`DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` asserts `Assert.Equal(b.Overflows, a.Overflows)` at 16,
32, 64 and 301 cells (`DexelMapTests.cs:234`) with **no precondition that either side is non-zero**. If overflows are
zero at all four sizes the assertion is `0 == 0` and never reaches the guard from item 1, while the commit message and
`docs/long-programs.md` both advertise "overflows included".

Worth knowing: `DexelMapTests.cs:86` does carry `Assert.True(dexel.Overflows > 0)`, in a pre-existing test. The
mechanism is exercised somewhere, just not in the comparison step 2 stands on.

The prepared patch sums the overflows across the four sizes and asserts the total is above zero, with a message that
says which side is wrong if it fails: the scene does not overflow, so the claim in `docs/long-programs.md` is what
needs correcting, not the test. The test cannot go green while proving nothing.

## 3. `ToolProfile.Pack` is inside one `WallMs` and outside the other, with no timer for it

`CudaBackend.ApplyDexels` starts its wall clock and then packs (`CudaBackend.cs:304-305`), so packing is inside
`WallMs`. `CpuBackend.ApplyDexels` packs before its stopwatch (`CpuBackend.cs:184` against `:192`), so it is outside.
The two `WallMs` columns of any table therefore cover different spans and cannot be compared against each other. The
same asymmetry exists in `Apply` (`CudaBackend.cs:66` against `CpuBackend.cs:40`).

This is what makes the follow-up item in `docs/todo.md` unsafe to act on as written. "18.9 ms of the 36.8 ms wall"
leaves 17.9 ms of everything else unattributed, and at 793 600 steps the pack allocates and fills a 38 MB `float[]`
(`ToolProfile.cs:26`). The CSR build is the largest host component only if packing turns out to be small, and nobody
has measured it. `docs/long-programs.md` and `docs/performance.md` both read as though the CSR build *is* the host
cost.

The prepared patch adds `ZMapTiming.PackMs` and times the pack on both backends, deliberately **without** moving it
inside or outside either `WallMs`: the CPU wall has always meant the parallel loop only and every CPU timing already
recorded in the docs depends on that. The new field makes the two comparable and changes no existing number.

## 4. CI does not compile `zmap.cu` at all

`Stykker.NanoCut.Gpu.Native` is **not listed in `Stykker.NanoCut.slnx`**, and CI does not invoke `build.ps1`. So
`dotnet build` never touches the CUDA source. The 175 lines of `zmap.cu` changed by `63adb49` are compiled by nothing
except a local nvcc run on the author's machine — a syntax or type error in the new `nc_dexel_apply_steps_binned`
would not be caught anywhere else.

The new managed tests do run in CI: `StepBinsTests` is pure managed. The CUDA one returns early with a printed note
when the backend is unavailable (`DexelMapTests.cs:207-211`), so it is explicitly skipped rather than silently
required — honest, but it also means a green CI says nothing about the kernel.

Adding the Native project to the solution, or a `nvcc -c` job, is one line or one job and it is the cheapest remaining
improvement to the project's safety net. It now protects 175 lines rather than none.

## Also worth a line when these are addressed

- **`MarginMm = 1e-6f` is 1 nm** (`StepBins.cs:27`), and the comment says it exists "so a column exactly on the
  boundary cannot be lost to the rounding of a float". At the bench's own coordinates (20 mm map, 0.05 mm cells) the
  chain `x0 → +wx → ±r → /cellX − 0.5f` accumulates roughly 2.5 nm. And `StepBins` tests the reach against `r`
  (offset 3) while the kernel tests `r*r` (`zmap.cu:848`), which at r = 2.5 mm is worth up to ~7.5 nm of extra kernel
  reach. Low severity — the completeness test is seeded, so it is stable either way — but the absolute wording of the
  comment is not earned by the constants beside it. The step-3 work added a second branch to `Tiles` and did not
  make this worse: the convex branch widens both ends by `2 * MarginMm` instead of one, and the ball branch still
  computes the same chain (now with the segment vector at offset 4 rather than offset 1).
- **`extra` is counted and printed but never asserted** (`StepBinsTests.cs:51,65,68`), so the comment's "deliberately
  generous, never in fewer" (`StepBins.cs:14-15`) is untested. Only the tight direction is pinned.
- **The bit-for-bit test has no CPU arm.** A fault shared by both CUDA launches is invisible to it. The CPU cross-check
  `CudaAgreesWithTheCpuReference` is tolerance-based (1e-4 mm) and, because `CudaBackend` now defaults to binning, it
  silently moved onto the binned path with this commit. One sentence in its comment would record that.
- **The factor table is the weakest sampling in `bench/README.md`.** The commit quotes best of 2; the file documents
  best of 30 for pocket-large-g1, best of 5 for the query tables, best of 3 for the dexel preview. The large rows are
  safe; the 102x at 24800 steps rests on two samples of a 0.1 ms figure, against 6.7 µs of per-call launch overhead
  already recorded in `docs/gpu-findings.md`.

## Step 3, committed as `da26cfc` (record, 2026-10-03)

Not a follow-up to step 2 — a note so the four items above are not re-derived later. Step 3 is commit **`da26cfc`**,
18 files and +2125/−121, clean in the main checkout and **not pushed**. The full design read is
`CudaLongProgramsFindings.md` §10; the CUDA half and the new tests are §11, with the agent's own report in
`Step3KernelFindings.md`. Only the points that touch this file are here.

**The representation is taken, the closed-form interval is not.** `ConvexTool` describes the tool as half-spaces
`n·p ≤ d` with normalized normals, and `ConvexProfile` packs them once per tool (`PackPlanes`) rather than once per
step — that part matches proposal A in `CudaLongProgramsFindings.md` §4A. The kernel interval is the plan's linear
program: `ConvexProfile.cs:9,17,21-22` says both ends come out of a small LP in (z,t), `Extremum`/`ExtremumAt`
(`:222-243`) take the min and max of the envelope over `t ∈ [tLo, tHi]`, and the search is O(m³) in the half-spaces.
§10.2 re-derives the closed form against the equation in their own documentation; §10.3 notes that the sort in
*their* queued O(m) follow-up can be hoisted out of the kernel entirely.

**Two hazards are closed, in managed code.** `ConvexTool.Bounded()` refuses a half-space set in which every `n_iz` is
zero, and `Corners()` rejects a plane whose normal cross product is not parallel to the others, so a redundant or
duplicate half-space cannot silently become the whole column.

**Still open, and now confirmed against the commit rather than suspected:** `ConvexTool.Ball` has `planeCount = 32`
as the default (`ConvexTool.cs:164`) while the very next line throws when `planeCount > MaxPlanes` and `MaxPlanes`
is 16 (`:168`, `ConvexProfile.cs:88`). So `ConvexTool.Ball(r)` with defaults throws on the default argument, which
is a trap for the first caller rather than a hypothetical. The tests always pass `planeCount` explicitly (6, 8, 12),
so nothing trips it today. Fix is a one-word change to the default — or raise `MaxPlanes`, which §10.4 argues for on
cost grounds anyway, but note that 16 now exists in **two** places that must move together: `ConvexProfile.cs:88`
and `kConvexPlanes` at `zmap.cu:825`. `ConvexProfile.Pack` has a second guard at `:113-115` whose message
("split the tool") is the right instruction.

**`StepBins` now carries two layouts.** `Tiles` takes `s`, `stride` and a `convex` flag and reads either the ball
offsets or the first four fields of the convex pack, and `ConvexProfile.Pack` already puts the rotated-then-translated
axis-aligned box of the swept polytope in those four fields. `MarginMm` is unchanged at 1 nm.

## Not done here

No build, no test run, no nvcc build, no benchmark. No commit, no push, no branch checked out or moved, no patch
applied. `step2-followups.patch` is prepared against `63adb49` and applies with `git apply`.

This is a collection. The four items are written down and the three code-level ones are drafted, but the fix comes
later — so nothing above has been applied, and the re-check in the status section changed no conclusion, only the
date on it.
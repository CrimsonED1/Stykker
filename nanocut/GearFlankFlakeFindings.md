# The gear flank test is not flaky — the boolean kernel is not a function of its arguments

**Date:** 2026-10-04, on `worktree-quick-elm-a7a7ca` after `81ca808` (= `main`).
**Subject:** `ProcessTests.RackGeneratedGearHasInvoluteFlanks`, and behind it `BooleanKernel.Execute` in
`src/Stykker.NanoCut.Geometry2D/BooleanKernel.cs`.

**The finding in one line:** `Process2.Cut` returns different results for identical inputs when the machine is busy,
and the inputs are provably identical — so the 2D boolean kernel is not a pure function of its arguments. **The
mechanism is not identified.** This document says what is established, what is ruled out, and where to look next.

## 1. The symptom

`RackGeneratedGearHasInvoluteFlanks` compares the generated gear's flank against the involute with a 300 nm tolerance
(`Tolerance.Budget(totalUm: 0.5, chordNm: 50, sweepNm: 300)`). Observed values of `worst`:

| run context | flank | verdict |
| --- | ---: | --- |
| the test alone, 9 times across the investigation | 36.158 nm | pass, every time |
| inside `dotnet test` on the whole suite | 36.158 / 511 / 1596 / 2230 / 5327 nm | fails roughly one full-suite run in three |

One failure was **not** an assertion: it threw inside `Process2.Cut` itself (`ProcessTests.cs:94`), so there are two
symptoms, not one.

The rate is a property of load, not of the test: the same code passes every time it runs alone and fails
intermittently when it runs beside other tests.

## 2. What is established

A probe ran the gear case **twice inside one process** and compared the two results point for point — contour count,
point count, an order-sensitive digest, the exact twice-area, and the flank.

- **Alone: the two runs are identical.** Same digest `8D1D811BFDF64B6C`, 18 contours, 25 575 points, flank 36.158 nm.
- **Inside the full suite: the first run differs from the second**, and the second is *always* the canonical value
  above. First-run digests seen: `2D9B5B33…` (25 251 points), `D0D59231…` (25 235), `EA532403…` (25 109),
  `A6112C6F…` (25 333), `9D755495…` (25 127), `BCEE4685…` (25 089).

A varying flank between 36 nm and 1596 nm for the same input is not rounding. It is a different region.

## 3. The inputs are identical, so the kernel is not

Everything upstream of the boolean was hashed and compared between the two calls:

| input | result |
| --- | --- |
| the blank, `Region2.Circle` | identical |
| the rack, `GearProfile.Rack` | identical |
| the sweep, `Process2.SweepPieces` — every piece's every point | identical digest |
| `stats.Intervals` | 640 both |
| `stats.Pieces` | 52 744 both |

and the output is not:

| output | run 1 | run 2 |
| --- | ---: | ---: |
| contour points | 25 127 | 25 575 |
| twice-area (nm²) | 615 634 201 395 535 | 615 631 956 980 914 |

The area differs by about **1e-8 relative** and the discretisation by ~450 points. So the two regions are the same
shape to within a rounding-level perturbation, but they are not the same region, and the flank metric — which samples
*discretisation vertices*, not the flank — moves with it.

## 4. What is ruled out

- **The pipeline (`376f705`).** The probe repeats with `Process2.Pipeline = false`, where `Cut` issues no `Task.Run`
  at all and runs strictly sequentially. The difference persists: 25 219 against 25 575 points.
- **The renormalise cadence (`2a2ae73`).** Only `bench/Stykker.NanoCut.Bench/Program.cs:284` ever assigns
  `Process2.RenormalizeEvery`; nothing in the test suite does, so no test can be running under a different cadence.
- **The sweep.** Identical digest, identical interval and piece counts (§3).
- **Mutable static state in the 2D path.** The only static field is `Region2.Empty`, and `Region2` is immutable by
  construction: a private constructor, get-only properties, no mutators.
- **Hash-order instability.** `Vec2` is a `readonly record struct (long X, long Y)`, so its `GetHashCode` is
  compiler-generated from the fields and stable; the same canonical digest came out of every process.
- **`[ThreadStatic]` or pooled buffers.** They exist — `FaceMerge.cs:30` and `ConvexHull3.cs:210`, both in
  `Geometry3D` — but this path never calls into 3D.
- **Timing-dependent sampling.** `Process2.SampleTimed` is named for the motion *parameter*, not the clock: there is
  no `Stopwatch`, no `Random`, no `Environment` read anywhere in `Process2.cs`.

## 5. Where the mechanism has to be

`BooleanKernel.Execute` and what it calls. `Merge` is explicitly defended — it sorts the merged edges with a total
order and the comment *"Deterministic order independent of hashing"* — so the fault is downstream of that. The rest of
the file is unread: `ApplySplits` (`:104`), `SnapRound` (`:141`), `FindSplitPoints` (`:221`) and `BuildResult`.

The shape to look for: anything that lets the *order* of the output depend on the enumeration order of a
`HashSet<Vec2>` or a `Dictionary<int, List<Vec2>>` — both are used, in `FindSplitPoints` and `SnapRound` — or any sort
whose comparator is not a total order. `List<T>.Sort` is an unstable introsort, so a comparator that returns 0 for two
distinct elements makes the result depend on the pivoting sequence; that is deterministic for one input and
catastrophic for robustness, because a perturbation anywhere upstream rearranges everything downstream of it.

## 6. Rebuilding the probe

The probe is twenty lines and is not committed, because a test that fails whenever the suite runs in parallel is not a
test. To put it back: run the gear case of `RackGeneratedGearHasInvoluteFlanks` twice in one `[Fact]`, and compare
contour count, point count, an order-sensitive digest (`d = d * 31 + x * 2654435761 + y * 40503` over every point) and
`(double)gear.TwiceAreaNm2`. `Process2.Cut([blank], rack, motion, tol, out var stats)` is the five-argument overload
that also gives the interval and piece counts. Run it once alone (must pass) and once inside the suite (must fail
today). `Process2.Pipeline = false` around it is what makes the pipeline irrelevant to the finding.

## 7. What this means for the numbers already recorded

`docs/processes.md` and the commit messages carry gear figures — 26 216 ms, 12312.529413 mm³, 69 contours, 5.4 nm
flank — measured from **single runs in single processes**. Given §3, those are samples from a distribution, not
constants. Until the kernel is deterministic:

- the flank is a gate that can be passed or failed by machine load alone, so it should be read as "the worst of
  several runs" or not at all;
- the removed volume moves in the eighth decimal with the contour count (as `docs/todo.md` already warns), and here
  the contour *point count* moves too, so volume agreement is weaker evidence than it looks;
- any comparison of two variants of `Process2` that is smaller than the run-to-run spread is not a result.

## 8. Not done

- The mechanism. §5 says where to look; nobody has read those 450 lines.
- No fix, and no attempt at one: a determinism defect in an exact-arithmetic kernel wants an understanding of *why*,
  not a patch.
- No HTML result page. This is a diagnosis from the test suite, not a measurement out of `bench/`, so the results-page
  convention does not apply to it. The gear case does deserve one the next time it is measured properly.
- The parallel branch `origin/feature/box-early-out` (`e72ad33`, an unbuilt diff draft of the box early-out that
  `cc1fbc6` supersedes) is still open and should be closed rather than applied.
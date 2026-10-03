# Long programs on the GPU: plan and log

Goal (user, 2026-10-03): long programs are the main topic, for example the whole generated gear or a fully covered
grinding wheel. The exact kernel stays the reference; the GPU gives a fast preview of the same process that can be
checked against it. Every step is logged at the end of this file with what was measured.

## Where the time goes today (exact kernel, CPU)

Measured with `bench/Stykker.NanoCut.LongPrograms` on the machine of `docs/handoff.md` (Ryzen 7 5800X3D, 16 threads,
Windows 11, .NET 10), single run, grinding warmed up and measured before the gear cases (the order is part of the
measurement, see the log). Full tables: `bench/README.md`, "Long programs".

| Program | What it is | Exact time | Source |
| --- | --- | ---: | --- |
| Gear generation | `Process2.Cut`: 7-tooth rack rolls a 20-tooth gear (m = 2), 2560 roll steps, 209 089 swept convex pieces, sweep tolerance 30 nm | 34.7 s (`docs/processes.md` measured 41 s on an older build) | `docs/long-programs.md`, "Baseline" |
| Gear generation, z = 40 | the same, 5120 roll steps, 398 178 pieces | 147.6 s | idem |
| Grinding, 60-grain demo scene | `GrindingSimulation`: 60 octahedral grains (150 µm) on trochoids, 3000 rpm, 0.8 mm feed | 0.20 s (the demo's "Grinding grains" page measures 0.38 s for the same scene) | `bench/README.md`, "Server mode" |
| Grinding, covered wheel | the same with 1920 grains, 3793 passes | 5.8 s | idem |

## Baseline (step 1)

`bench/Stykker.NanoCut.LongPrograms` runs both programs on the exact kernel and writes time **and result**: the gear
area against the ideal involute gear of the same module and the flank deviation, the removed volume and the surface
profile (Ra, Rz) of the ground surface. With `--out` it also writes the profiles as CSV, which is what steps 4 and 5
compare a preview against. The bench measures grinding first and gear afterwards: the 2D gear kernel leaves the 3D
grinding kernel measurably slower in the same process (log, "Three things the baseline settled").

**Gear generation**, m = 2 mm, 7-tooth rack, `Tolerance.Default` (0.1 µm total, 50 nm chord, 30 nm sweep):

| z | Cut | Extrude | Area | Ideal involute | Δ | Contours | Profile vertices | Roll steps | Swept pieces | Flank |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 † | 28.8 s | 0.9 s | 296.905130 mm² | 297.949 mm² | −0.35 % | 76 | 81 653 | 2560 | 224 988 | – |
| **20** | **34.7 s** | 0.8 s | **1231.252941 mm²** | 1226.103 mm² | +0.42 % | 69 | 107 328 | 2560 | 209 089 | **5.4 nm** |
| 40 | 147.6 s | 2.1 s | 4985.752597 mm² | 4982.782 mm² | +0.06 % | 53 | 243 515 | 5120 | 398 178 | 2.2 nm |

† z = 10 is undercut (below z = 2/sin²α = 17.1 a generated gear's flank is not the involute), so the flank column
carries no meaning there — the area still matches the ideal gear to −0.35 %, so the shape itself is right.

z = 20 reproduces the reference of `docs/processes.md` exactly (1231.252941 mm², 209 089 pieces, 5.4 nm), which is what
makes these rows usable as a reference for a preview.

**Grinding**, workpiece 1.5 × 0.8 × 0.3 mm = 0.36 mm³, Ø20 × 1 mm wheel with 150 µm grains (protrusion 40 ± 15 µm),
3000 rpm, 20 mm/s over 0.8 mm, 20 µm depth of cut, seed 1, 2 revolutions:

| Grains | Wall | Removed | of stock | Active grains | Cutting passes | Passes | Hulls | Chip | Ra | Rz |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 0.200 s | 0.018713 mm³ | 5.2 % | 42/60 | 80 | 118 | 707 | 46.2 µm | 9.69 µm | 55.5 µm |
| 240 | 0.924 s | 0.029406 mm³ | 8.2 % | 113/240 | 204 | 476 | 2589 | 43.0 µm | 6.96 µm | 43.1 µm |
| 960 | 2.979 s | 0.040148 mm³ | 11.2 % | 186/960 | 337 | 1897 | 8033 | 42.3 µm | 4.72 µm | 29.2 µm |
| 1920 | 5.820 s | 0.044690 mm³ | 12.4 % | 240/1920 | 413 | 3793 | 13 841 | 54.1 µm | 5.06 µm | 25.2 µm |

Two things the numbers say, and they set the target for the preview:

- **The covered wheel costs more and converges.** From 60 to 1920 grains the time grows 29× (0.200 → 5.82 s) and the
  number of hulls the exact kernel sweeps grows 19.6×, while the removed volume grows only 2.4× and the roughness stops
  improving after 960 grains (Ra 4.72 → 5.06 µm). The exact kernel pays, step by step, for grains that meet a surface
  which earlier grains already ground away — 413 of 3793 passes still cut at 1920 grains.
- **A preview takes over a different kind of work.** What the exact kernel spends its time on is a boolean per swept
  convex piece — 398 178 pieces for z = 40, 13 841 hulls for the covered wheel. A preview replaces that with one
  interval subtraction per column, so the target is set by how many *poses* and *columns* meet, not by how many pieces
  the union of neighbouring poses happens to have. Steps 2 and 3 build that and measure it.

## Why the existing preview is not enough

The Z-map / dexel preview handles a **ball** moving on **straight** segments. The two long programs need:

1. **Any convex tool**: grains are octahedra (8 faces), the rack is a set of convex polygons.
2. **Motion with rotation**: grains run on trochoids (spindle rotation plus feed), the rack rolls (rotation plus
   translation of the gear frame).
3. **Many steps**: tens of thousands to millions of tool poses. Today every column tests every step, which is
   O(columns × steps); a column must only see the steps that can reach it.

## Model

A tool is a convex polytope given by its half-spaces (n·p ≤ d, at most 16 in the kernel: `kConvexPlanes`). A program is a sequence of
rigid poses; between two consecutive poses the tool is swept by the translation only (rotation handled by sampling the
poses densely enough: the chord error of a rotated vertex between poses must stay below the preview tolerance). For one
column (3D: a vertical line; 2D: a horizontal row) the swept region of a pose pair meets the line in one interval,
found as a tiny linear program in (z, t): minimise and maximise z subject to n_i·(p(z) − t·w) ≤ d_i and 0 ≤ t ≤ 1.
That interval is subtracted from the column's intervals exactly as in the dexel map.

Steps are binned by tiles of columns: each step's bounding box (tool box swept by its translation) lists the tiles it
touches (CSR on the host), and a GPU block works on one tile with only those steps. Removal is a union, so the order of
steps inside a tile does not matter for the result (only the overflow counting can differ).

## Binning (step 2)

Built: `src/Stykker.NanoCut.Gpu/StepBins.cs` builds the CSR on the host — for every step the swept box of the tool,
grown by the radius and a margin of 1 nm, is intersected with the tiles of 16 × 16 columns, the count-then-place pass
writes every tile's step indices in ascending order. `nc_dexel_apply_steps_binned` in `zmap.cu` uploads it and launches
one block per tile; `CudaBackend.BinSteps` (on by default) chooses between that and the old launch. Both go through the
same `dexel_apply_column`, so a column is subtracted in the same order whichever launch runs it.

Measured with `bench/Stykker.NanoCut.LongPrograms dexel` on the machine of `docs/gpu-findings.md` (RTX 5070 Ti,
sm_120): a serpentine finishing pass of a 0.2 mm ball over a 20 mm square map at 0.05 mm cells (400 × 400 = 160 000
columns), 4 intervals per column, steps 0.05 mm apart, best of 2. Both launches get the same program on the same map.

| Steps | Rows | Column steps (unbinned) | Binned kernel | Bin (host) | Upload | Binned wall | Unbinned kernel | Unbinned wall | Kernel × | Wall × | Removed (both) |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 800 | 62 | 9 920 000 | 0.1 ms | 0.6 ms | 0.3 ms | 2.3 ms | 13.6 ms | 14.9 ms | 102× | 6.5× | 90.183421 mm³ |
| 99 200 | 248 | 39 680 000 | 0.4 ms | 2.2 ms | 0.7 ms | 5.2 ms | 77.9 ms | 80.1 ms | 212× | 15.4× | 99.454883 mm³ |
| 396 800 | 992 | 158 720 000 | 1.3 ms | 9.4 ms | 2.4 ms | 18.0 ms | 320.4 ms | 327.9 ms | 247× | 18.2× | 99.965987 mm³ |
| 793 600 | 1984 | 317 440 000 | 2.7 ms | 18.9 ms | 5.1 ms | 36.8 ms | 640.7 ms | 655.0 ms | 240× | 17.8× | 99.991493 mm³ |

What the table says:

- **The result does not move.** Both launches report the *same* removed volume to the last digit at every size (0
  overflows in every case, since a finishing pass never fills a column), which is what `DexelMapTests
  .BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` asserts cell by cell on smaller maps, and what the ascending CSR is
  for.
- **The kernel follows what the tool touches, not the length of the program.** 793 600 steps cost 2.7 ms, against 640.7
  ms for the same program with every column looking at every step. The factor settles at ~240×; it is lower at the
  smallest case only because the kernel there is 0.1 ms of work.
- **The host-side binning is what is left to win.** It is O(steps) — 18.9 ms of the 36.8 ms wall at 793 600 steps, more
  than the kernel itself by 7×. Above about 10⁶ steps per call the CSR build, not the GPU, sets the pace; larger tiles,
  or a binning kernel on the device, are the ways out. Logged as a follow-up in `docs/todo.md`.

## Convex tool + pose sequence (step 3)

Built: `src/Stykker.NanoCut.Gpu/ConvexTool.cs` (a tool as half-spaces, plus the corners the binning rotates),
`ConvexStep.cs` (orientation plus the two positions) and `ConvexProfile.cs` (the packing and the interval search), with
the same in the CUDA kernel (`convex_span` in `zmap.cu`) reached through `nc_dexel_apply_convex_steps` and its binned
twin. `CudaBackend.ApplyConvexDexels` picks between the two launches, so a convex program is binned like a ball one.

The interval itself is a small linear program in (z, t). For a half-space turned into world coordinates the constraint
of a point on the column is `m·(p(z) − T_A) − t·(m·w) ≤ d`, which with z the height is `mz·z ≤ c + g·t`, where
`m = R·n`, `c = d + m·T_A − (m_x·x + m_y·y)`. The half-spaces with `mz < 0` bound z from below, the ones with `mz > 0`
from above, and the ones with `mz = 0` bound t alone. So:

    low  = min over t of z_min(t),  where z_min(t) = max over the lines from below
    high = max over t of z_max(t),  where z_max(t) = min over the lines from above

`z_min` is convex and piecewise linear, so its minimum is at an end of t's range or at a kink of the envelope; the walk
is over exactly those candidates, scoring each on the whole envelope rather than on the crossing pair, and that inner
loop over all lines is what makes it O(m³) in the half-spaces. `ConvexProfile.MaxPlanes` is 16 for the same reason —
twice the planes is eight times the work, and a larger tool is split instead, which is what `ToolShape` does anyway.

**Where the t's range comes from is the whole trick, and the first version got it wrong.** A point on the column is
inside the body at t only when z is over the lower bound *and* under the upper one, so every line from below has to sit
under every line from above, and each of those pairs is one bound on t. The first version did not narrow t and took the
minimum of the lower bound and the maximum of the upper bound over all of t separately. The two then come from two
different t, and they cross on a column the tool never went near: the octahedron in the mixed tool program reported
cuts on columns 3.9 mm outside its own silhouette. Narrowing t first is exact — `z_min ≤ z_max` *is* the conjunction of
the pairs — and it is a chain of min and max, so nothing about it rounds badly.

Against the exact kernel, `ConvexDexelTests` compares the preview against `Process3` on the very body the half-spaces
describe (a hull of `ConvexTool.CornersMm`), so the two cannot drift apart by construction. All of it on a 20 mm block,
the tool cut by the exact kernel over its own poses and the preview cut column by column:

| Case (grid) | Preview | Exact | Difference | Sampling bound |
| --- | ---: | ---: | ---: | ---: |
| Box 2 × 2 × 2 mm, swept 7 mm sideways (250 / 500 / 1000 cells) | 35.8400 / 36.0000 / 36.4408 mm³ | 36.0000 mm³ | −0.444 % / 0.000 % / +1.224 % | 1.7728 / 0.8832 / 0.4408 mm³ |
| Octahedron r = 2.5 mm, swept (6, 8, 9) → (13, 11, 12) (250 / 500 / 1000) | 108.3404 / 108.3321 / 108.3320 mm³ | 108.3333 mm³ | +0.007 % / −0.001 % / −0.001 % | 12.8512 / 6.4128 / 3.2032 mm³ |
| Box 2.4 × 0.6 × 0.6 mm, 1.2 rad turn about z and 3 mm of travel (500) | 4.0848 mm³ | 4.0701 mm³ | +0.362 % | 0.1253 mm³ |

And the timing, on the same finishing pass step 2 measured (`LongPrograms convex`, best of 2, 400 × 400 columns at
0.05 mm, a 12-half-space tool inscribed in the 0.2 mm ball; the ball row is the *same* path with the ball tool, so the
difference between the two kernels is only how the tool is described):

| Steps | Convex kernel | Bin (host) | Convex wall | Unbinned kernel | Ball kernel | Binning × | Convex/ball × | CPU wall |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 800 | 3.0 ms | 1.1 ms | 16.4 ms | 480.5 ms | 0.2 ms | 161.5× | 17.7× | 57 661.7 ms |
| 99 200 | 7.3 ms | 1.9 ms | 47.1 ms | 1935.4 ms | 0.5 ms | 263.4× | 15.7× | – |
| 396 800 | 32.9 ms | 8.7 ms | 187.6 ms | 7881.6 ms | 1.7 ms | 239.5× | 19.0× | – |
| 793 600 | 65.8 ms | 17.7 ms | 372.6 ms | 15 546.9 ms | 3.4 ms | 236.2× | 19.2× | – |

The device is 3515× the CPU backend on the identical program (the 793 600 steps extrapolate to about 31 minutes on the
host — an extrapolation, not a measurement), the convex tool costs ~19× the ball kernel, and the binning is untouched
by the representation. The half-space sweep at a fixed 99 200 steps gives the kernel's growth as **m^1.57** (704 /
1056 / 1935 / 3278 ms unbinned for 6 / 8 / 12 / 16 half-spaces) rather than the m³ the code remarks argue for. One
page: `bench/results-2026-10-04-long-programs-convex.html`.

**What the table checks, and what it does not.** A column is cut when its centre lies in the sweep, so the preview
samples the silhouette where the exact kernel integrates it: its volume can be off by the rim it samples, P·h/2 of
area over a perimeter P (times the height the tool sweeps, which is exact — nothing is sampled in z). That is the
*sampling bound* in the last column, and it is what the test asks for, because it is O(h) while a body cut wrong sits
at the tool's real volume at every grid. The octahedron and the rotating box stay orders of magnitude inside it
(0.001 % and 0.36 %). The box at 1000 cells does not, and it is worth saying why: the preview counts a centre that
sits exactly on the tool's edge (the rule is closed), and at 1000 cells the centres are on a 0.01 mm grid, where this
box's faces at 4.13, 13.13, 9.07 and 11.07 mm land on centres. It then takes one extra column row on each side —
451 × 101 instead of 450 × 100 — and the difference is 0.4408 mm³ against a bound of 0.4408 mm³, to the last digit.
No refinement of the grid takes that away, so "the error shrinks with every finer grid" is not a property this
model has, and the test asks for the bound instead. The same closed rule is why the 500-cell row of the same box
lands on the exact number to the digit.

CUDA against the CPU reference, four tools (6, 8, 12 and 6 planes), 220 steps, 241 × 241 columns, binned launch:
no column differs in its interval count anywhere, and the ends agree to the conditioning of `1/mz` (4.3e-05 … 1.3e-04
mm on a 0.083 mm grid, against a tolerance of a hundredth of a cell, 8.3e-04 mm — a fixed 1e-4 mm was too tight for
the 12-plane ball and says nothing the grid does not already say). The binned launch against the unbinned one is
bit-identical at 16, 32, 64 and 241 cells.

## Steps

- [x] **1. Baselines.** `bench/Stykker.NanoCut.LongPrograms` (new): the gear generation with a rack for z = 10, 20, 40
      and grinding with 60 … 1920 grains on the exact kernel, with time **and result** (area against the ideal
      involute gear, flank deviation, removed volume, Ra/Rz), plus the profiles as CSV under `--out`. Numbers above.
- [x] **2. Binning for the existing ball dexel/Z-map** (tiles + CSR), measured on a long synthetic ball program
      (e.g. a finishing pass with 0.05 mm steps, 10^5 to 10^6 steps): the speed-up that every later step relies on.
      Numbers in "Binning (step 2)" below.
- [x] **3. Convex tool + pose sequence** in the dexel kernel (CPU reference + CUDA), tests against the exact kernel on
      small cases (a box tool, an octahedron, a rotating tool). Section below.
- [ ] **4. Grinding preview**: grains of a `GrindingWheel` as convex tools on their trochoids; compare the removed
      volume and the surface profile with `GrindingSimulation` (exact) for 60 grains, then scale to a covered wheel.
- [ ] **5. Gear preview (2D)**: rows as columns, the rack's convex parts as tools on the rolling poses; compare the area
      with the exact 1231.252941 mm² and the flank with the ideal involute.
- [ ] **6. Into the server demo**: preview of the long program first, the exact result when it is ready.

## Log

Newest first.

### 2026-10-04, Qwen

- **Step 3 got its bench mode, and the timing answer is the number the plan had deferred.** `LongPrograms convex` runs
  the step-2 finishing pass as a pose sequence of a convex tool, four arms per case on the same path: binned and
  unbinned convex, the *ball* program of the identical path, and the CPU backend on the same convex program.
  Measured: the device is **3515×** the host on that program (57 661.7 ms against 16.4 ms at 24 800 steps), the convex
  tool costs **~19×** the ball kernel at 12 half-spaces and that ratio holds over all four lengths, and the binning is
  untouched — 161× to 305×, the ball's own order. Two consequences to carry into step 4: the host arm could only be run
  at 24 800 steps (793 600 extrapolate to ~31 minutes, which is the reason a preview exists at all), and the host-side
  binning drops to **4.7 %** of the wall on this path because the kernel got expensive enough to hide it — the reverse
  of the ball case, where it was 51 %.
- **The half-space sweep contradicts the complexity the code argues from.** Unbinned kernel at a fixed 99 200 steps:
  704.1 / 1055.6 / 1935.4 / 3278.2 ms for 6 / 8 / 12 / 16 half-spaces. That is a factor 4.65 where m³ would be 18.9,
  i.e. an exponent of 1.57 in a log-log fit. The O(m³) bound is not wrong — it is the worst case, and a ball is not the
  worst case, because only the few half-spaces whose normal faces the column bound it at all. The queued O(m) envelope
  is therefore worth less than the exponent suggested, and `MaxPlanes = 16` has more headroom than it looks. Both
  implementation remarks keep the bound; the follow-up in `docs/todo.md` now carries the measurement beside it.
- A turning tool costs about a third more: 8.1 / 9.5 / 9.5 / 9.6 ms against 7.3 ms for translation only at 99 200 steps
  (four independent runs, and the scatter at this size is itself ~15 %), because the kernel turns the half-spaces per
  column rather than per step. That is what step 5 pays for a tooth rolling over a wheel.
- One number that is *not* a defect: `ConvexTool.Ball` is inscribed in the sphere, so the polyhedron removes less
  material than the ball it approximates — 18.227 / 17.131 / 11.511 / 8.789 % less at 6 / 8 / 12 / 16 half-spaces. The
  share falls with the program length (20.5 % at 24 800 steps down to 8.7 % at 793 600) because a longer pass covers the
  same layer again. The bench uses a polyhedral ball only so that its ball row and its convex row share one path.
- Step 3 done: a tool is a list of half-spaces (`ConvexTool`, with the corners the binning rotates), a step is an
  orientation and two positions (`ConvexStep`), and where a column meets the sweep is a small linear program in (z, t)
  (`ConvexProfile.Span` on the CPU, `convex_span` in the kernel) reached through `nc_dexel_apply_convex_steps` and its
  binned twin, so a convex program is binned like a ball one. `MaxPlanes = 16` is the runtime talking: the walk over
  the crossings of two of the m lines and the envelope there over all m of them is O(m³) per column and step, and a
  larger tool is split instead, which is what `ToolShape` does anyway. Against `Process3` on the very body the
  half-spaces describe: the octahedron to −0.001 % (108.3320 against 108.3333 mm³), the rotating box to +0.362 %
  (4.0848 against 4.0701 mm³), the box inside its sampling bound at every grid. Table above.
- **The first version of the interval was wrong on columns the tool never went near.** It took `low` as the minimum of
  the lower bound over all of t and `high` as the maximum of the upper bound over all of t, separately. Those two ends
  come from two different t, and on a column the sweep misses they cross anyway: the octahedron in the mixed tool
  program reported cuts on columns 3.9 mm outside its own silhouette, and hand-work on step 42 says it misses (its
  edges need |vx| + |vy| ≥ 5.43 mm at every t, the radius is 2.2 mm).
- **The obvious repair was much worse, and the reason is worth keeping.** Testing each candidate for "is the column in
  the body" (`low ≤ high`) fixes the concept and ruins the numbers: at a candidate where the body opens or closes on
  the column the two bounds are *equal*, and in float they are two or three ulops apart, so half of them get dropped
  at random and the end that was to come from a dropped one is taken from a different t. The octahedron against
  `Process3` came out 9.10 mm off over 390 columns. What works is narrowing t first: the set
  `{t : z_min(t) ≤ z_max(t)}` *is* the conjunction of the pair conditions `ℓᵢ(t) ≤ uⱼ(t)`, the pairs are affine in t,
  so what they leave is one interval whose ends are where the body opens and closes on the column. A chain of min and
  max, in both the CPU reference and the kernel.
- **Two things the comparison against the exact kernel taught, both about the model and not about the interval.**
  First, "the difference shrinks with every finer grid" is not a property a column model has: a centre that sits
  exactly on the tool's edge counts as inside, so a face that lands on the centre line of a grid takes a whole extra
  row of columns there — the box at 1000 cells is off by exactly the rim it samples (0.4408 mm³ against a bound of
  0.4408 mm³), while at 500 cells the same box lands on the exact number to the digit. The test asks for the bound
  now, which is O(h) and therefore still separates sampling from a body cut wrong. Second, a rotation is the one
  motion the exact kernel samples *linearly* in the angle: it holds `diameter · dθ / 2` under its sweep tolerance on
  top of the chord, so the default 30 nm asks for ~50 000 poses for a 1.2 rad turn of a 2.4 mm tooth (the subdivision
  is binary, so it would be 65 536), one exact hull and one Boolean each. The comparison runs on 4 µm (the preview's
  own 2 µm chord with room for the hull to overcut, both far below the 40 µm cell), which is 512 exact intervals
  against 11 preview steps and 72 s for that one test — the slowest in the suite, and worth it as the reference.
- **An independent read of the CUDA half found a way to empty the whole map, and it was one line.** The kernel tells a
  sphere from a polytope by the plane pointer it is handed (`zmap.cu:1106`), and the device dexel is kept for as long
  as its `DexelMap` (`CudaBackend.cs:386`). `reserve_planes` returns early on a plane count of zero without clearing
  the pointer (`zmap.cu:1259`), and the launch passed `d->planes` on regardless of the caller. So a ball program that
  ran on a map a convex program had already touched was cut by `convex_span` with `planeCount = 0`: no planes, so
  `convex_extremum` answers −∞ and +∞ (`zmap.cu:951`), and the ball takes the **whole height of every column its bin
  reaches** — in the regression test 1476.83 mm³ where the ball removes 84.82 mm³, 3072 of 16641 columns emptied, with
  the overflow counter still at zero, plus an out-of-bounds read of `p[4..19]` out of a 12-float ball step. The launch
  now takes the pointer from the plane count it was given. The test that holds it,
  `AConvexToolThenASphereOnTheSameDevice`, runs convex → sphere against the same two programs on the CPU (84.8186
  against 84.8186 mm³, worst 9.5e-07 mm) and fails without the fix. The cheap lesson is in the direction: the
  existing test ran sphere → convex, which is the safe order, and the state was not in the test at all, it was in the
  device — so a verifier that walks the paths the tests already walk never sees it.
- **The CUDA comparison needed a tolerance that says something.** The ends of an interval come out of a division by
  `mz`, and the CUDA side contracts fma where C# does not, so a half-space whose normal lies near the horizontal moves
  its end by an ulop times 1/mz. On the 12-plane ball that is 1.3e-04 mm on a 0.083 mm grid — above the fixed 1e-4 mm
  the check carried from the ball tests. It is a hundredth of a cell now (8.3e-04 mm), which says what the check is
  for: no column anywhere differs in its interval count, and the ends move by what the conditioning of `1/mz` costs.
- Step 2 done: the steps are binned into tiles of 16 × 16 columns on the host (`StepBins.cs`, CSR) and the CUDA dexel
  kernel launches one block per tile with only the steps that reach it (`nc_dexel_apply_steps_binned`). Measured on a
  finishing pass of 24 800 … 793 600 steps over 160 000 columns: the kernel goes from 640.7 ms to 2.7 ms at the longest
  case, a factor of ~240 that does not change with the program, because the work follows what the ball touches instead
  of columns × steps. Both launches return the same removed volume to the last digit at every size. Table above, bench
  mode `dexel`, one page: `bench/results-2026-10-04-long-programs-dexel.html`.
- **The result is pinned, not hoped for.** `StepBinsTests.EveryStepThatReachesAColumnIsInThatColumnsTile` walks 400
  steps over a 96 × 96 map and checks every one of the 20 348 (step, column) pairs that `ToolProfile.Span` accepts
  against the CSR: none may be missing, or the launch silently leaves columns uncut and cannot report it.
  `DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` runs the same 300-step program at 16, 32, 64 and 301
  cells on both launches and compares every column's count and intervals bit for bit, overflows included.
- **The first version of the binned kernel was wrong, and the binning was not.** The CSR was complete (no step that
  reaches a column missing) while the result was not: at 301 cells, 83 336 of 90 601 columns differed and the removed
  volume read 9.29 mm³ against 3944.06 mm³. Two candidates, and the cheap experiment settled it: with the CSR slice
  ignored and the kernel looping all steps, the binned launch reproduced the unbinned result exactly — so the launch
  geometry and the tile → column mapping were fine. The fault was that `dexel_apply_column` read `first … last` as
  step indices while a CSR range is positions in `tileSteps`. It takes an optional index list now, null for the batch
  itself, and the two launches share the helper.
- **What the binning costs.** It is O(steps) on the host: 18.9 ms of the 36.8 ms wall at 793 600 steps, seven times the
  kernel itself. That is invisible at 25 000 steps and dominant at 10⁶, so it belongs on the list of what step 3 has to
  think about (coarser tiles, or binning on the device); `docs/todo.md` carries it.

### 2026-10-03, Qwen

- Step 1 done: `bench/Stykker.NanoCut.LongPrograms`, run on the machine of `docs/handoff.md`. Gear z = 20 comes out at
  34.7 s with 1231.252941 mm², 209 089 pieces and 5.4 nm flank — the reference of `docs/processes.md` reproduced, which
  is what makes it usable as the yardstick for step 5. z = 40 costs 147.6 s, 398 178 pieces. Grinding: 0.200 s for the
  demo's 60 grains up to 5.82 s for 1920, the removed volume converging (0.0187 → 0.0447 mm³) while the number of hulls
  grows 19.6×. One page: `bench/results-2026-10-03-long-programs.html`.
- Three things the baseline settled that the plan had assumed:
  - **The grinding cases have to be warmed up, and the rule in `bench/README.md` was wrong about how.** The table
    first read 0.95 s for 60 grains against 0.38 s for the same scene on the demo page. A single warm-up pass leaves the
    methods in tier-0 code: the case reads 938 ms after one pass and 200 ms after five. The rule the code actually uses
    (`bench/Stykker.NanoCut.Bench`, `--warm`) is "three consecutive runs within 10 %, at least five runs"; the README
    described it as "until 1.5 s have passed", and a cold pass of this scene takes 1.8 s, so that wording ends the loop
    after exactly one pass. README corrected, bench now uses the code's rule.
  - **The 2D gear kernel leaves the 3D grinding kernel slower in the same process**, by up to 4.4×: the 60-grain case
    reads 200 ms in a fresh process, 352 ms after one 30 s gear case and 885 ms after the three gear cases of the full
    run. Ruled out: server GC (355 ms), `DOTNET_TieredCompilation=0` (411 ms), the machine (a fresh process right after
    the same load reads 198 ms), the length of the process (5.8 s of 3D work beforehand: 185 ms). No cause yet, so the
    bench measures grinding before gear — the gear numbers do not suffer for it (28.8 s against 28.8 … 30.5 s over the
    runs). Open as a follow-up in `docs/todo.md`; it matters for the server, where a preview is measured in a process
    that has already run programs.
  - **The flank metric is not a gear check below z = 17.1.** At z = 10 it reports 1357 µm, which is not a broken profile:
    the generated area is 296.905 mm² against 297.949 mm² of the ideal involute gear (−0.35 %), and below
    z = 2/sin²α a generated gear is undercut, so its flank is not the involute at all. The bench therefore prints the
    ideal area next to the generated one and marks such a case.
- `--out` writes `long-programs-results.json`, `-results.md`, the gear profile per case (contour, index, x, y) and the
  ground surface per grain count (y, z at x = 0.40 mm) as CSV: the input steps 4 and 5 compare a preview against.

### 2026-10-03, Claude

- Plan written. Working directly on `main` (user's decision).

# To do

Open features and ideas, newest first. Each entry says what is wanted and which existing building blocks it can use.

## Done

- **Inspection: colour as the reading** (demo page *Inspection*, route `/inspect`, cases in `Pages/InspectCases.cs`,
  tests in `tests/Stykker.NanoCut.Tests/InspectCasesTests.cs`): a measuring microscope for ground and formed models.
  Every vertex is coloured by a measured quantity and drawn unlit, because a highlight would falsify a number; the
  scale is fitted to what the run found, so a 1 nm case and a 100 nm case are both readable. Three cases: the 1 nm
  staircase (axis-parallel, so the Boolean rounds nothing — measured 1,000000 … 100,000000 nm, deviation 0, 148
  vertices on the grid), a rim whose facets widen by a constant factor from a 1 nm chord error up to the coarse end
  (colour = the distance from the centre to the facet's chord, measured on the geometry — a per-vertex radial distance
  cannot show it, because the vertices of an inscribed polygon lie *on* the circle), and the gear flank against the
  involute. The viewer grows a scale bar in nm/µm/mm, the 1 nm lattice at a pitch that follows the zoom, the nominal
  form as a curve on the top face, a data bar on the picture, and a click that reports the exact integer nanometre
  from the server. Two bugs came out of driving the page on both engines: the Babylon scale bar was out by a factor of
  ~60 (three.js states the field of view in degrees, Babylon in radians) and Babylon's pick never hit anything
  (`scene.pick` returned a line mesh). A third came out of the gear case: **the exact 2D Boolean is not a pure
  function** — see `GearFlankFlakeFindings.md` — so the flank deviation is a sample, and the page says so instead of
  printing a constant. The case also refuses z < 18, because below z = 2/sin²α the flank is undercut and is not the
  involute at all. German one-pager with the same numbers: `docs/overview.html`.

- **Inspection: a ground surface, which is what the viewer is for** (fourth case in `Pages/InspectCases.cs`, tests
  `TheWheelCutsAndNothingIsAdded` and `TheGroundColourIsInNanometres`): a block with a flat top face, 60 abrasive grains
  over it, and the colour is the **form error** — how far each point of the ground surface sits from the plane the wheel
  left behind. Only the top vertex of a column carries a value; the block below it stays neutral, because a
  metrologist looks at the surface and not at the block. Measured at 60 grains: 28 584 nm peak to valley, Ra 5 937 nm,
  0,006639 mm³ removed from 0,48 mm³ stock, 4 842 hulls, 2,5 s native. The nominal is the **measured mean ground
  level**, not a theoretical plane — how deep a wheel of random grains ends up is a result, not a promise the kernel
  makes, so a flatness has to be stated against the level that came out. Form and roughness are printed side by side
  because they are different questions that differ by more than most readers expect. This made `Inspection` carry a
  **list** of bodies: grinding leaves the workpiece as several cells, and uniting them would mean running the exact
  Boolean over the result — slower, and the one kernel here that is not a pure function. Two bugs were on the way and
  are worth writing down: the wheel has to be *oriented*, not just placed (`Linear ∘ Fixed(Rotation(−π/2, 1, 0, 0))`,
  or it spins about Z, its tips never reach the workpiece and the simulation cheerfully reports zero active grains and a
  perfectly flat, entirely meaningless surface); and the colour value is a difference of two nanometre heights, so an
  extra `* 1e-6` reports the whole form error a million times too small. The first one passed its check, because a
  surface that came out flat with a deviation of zero looks exactly like a correct answer.

- **Demo overview** (`Pages/Home.razor`, `Pages/PageIndex.cs`): "/" was a redirect into the first scene, so a newcomer
  arrived mid-groove with no way to see what else existed. It is now an index — what this is, where to click, and what
  it cannot do yet. The scene pages come from `SceneCatalog` and cannot go missing; the hand-written pages became
  `PageIndex.All`, which the sidebar and the overview both read, so adding a page is one line in one file. Routes that
  take minutes in WebAssembly rather than seconds are marked as such.

- **Preview page: the exact kernel, a CPU preview and a GPU preview side by side** (`PreviewPage.razor`, the demo page
  *Preview: exact vs CPU vs GPU*, commits e2f4138 and 861c6eb): one program, cut two ways. The panel times both
  previews on the same height field and shows the exact cut once, as the reference they stand in for, with each
  preview's Δ against it. The CUDA context is warmed before the GPU arm is timed and reported apart, so the device
  number is work rather than start-up. Measured on the default finishing pass, 400 steps over 512 × 384 cells
  (196 608 columns): CPU preview 39 ms, GPU preview 0.93 ms on cuda:0 — 42× — exact kernel 3314 ms, 799.825 mm³
  against 799.820 mm³ remaining, Δ +0.001 % for both previews. The field download is timed out of the call and
  printed beside each arm (0.00 ms and 0.16 ms). Verified 2026-10-04 in the browser: all three arms present, the two
  field buttons correctly disabled until Compute has run, all three viewer buttons clicked through, and a clean
  console — two Blazor info lines, no errors and no warnings. Two things that verification settled. The height
  field does land visibly over the exact body, read off the canvas rather than off a picture: 167 434 body pixels
  beside 352 410 field pixels on the CPU arm, and on the GPU arm 240 520 blend pixels of which all 240 520 lie on
  the straight line between the body colour and the field colour, which is what a 0.85 cover over it looks like. And
  the three buttons are exclusive toggles, not additive — the page holds one engine state, so clicking "+ GPU field"
  removes the CPU field again, which the two plus signs do not promise. A screenshot of the page could not be taken
  into account here: the vision bridge on this machine times out on every image.

- **Long programs, step 3: a convex tool on a pose sequence** (`ConvexTool`, `ConvexStep`, `ConvexProfile`,
  `convex_span` in the kernel, plan in [long-programs.md](long-programs.md)): a tool is half-spaces, a step is an
  orientation and two positions, and where a column meets the sweep is a small linear program in (z, t) on both
  backends — binned like a ball program, so a long one costs steps instead of columns × steps. Against the exact
  kernel on the same body: the octahedron to −0.001 %, a box turned 1.2 rad while travelling to +0.362 %, all three
  cases inside the volume a column model can be off by at the grid it samples. The half that had to be got right
  first: where the body opens and closes on the column is one interval in t (`Where`), and testing candidates for
  feasibility instead drops half of them at random — the story is in the log.

- **Long programs, step 2: step binning for the ball dexel kernel** (`StepBins.cs`, `nc_dexel_apply_steps_binned`,
  plan in [long-programs.md](long-programs.md)): the steps are binned on the host into tiles of 16 × 16 columns and a
  block only sees the steps that reach its tile, so the dexel kernel no longer costs columns × steps. Measured on a
  finishing pass of 793 600 steps over 160 000 columns: 2.7 ms kernel against 640.7 ms unbinned, a factor of ~240 that
  holds across the program length. The binning is the host's O(steps) work and is itself the next limit above ~10⁶ steps
  per call (18.9 ms of a 36.8 ms wall). Both launches return the same removed volume to the last digit; one page:
  [results-2026-10-04-long-programs-dexel.html](../bench/results-2026-10-04-long-programs-dexel.html).

- **Long programs, step 1: the baseline** (`bench/Stykker.NanoCut.LongPrograms`, plan in [long-programs.md](long-programs.md)):
  the two long programs on the exact kernel with time **and** result, so a preview has something to be checked against.
  Gear: 34.7 s at z = 20 with 1231.252941 mm², 209 089 swept pieces and 5.4 nm flank — the reference of
  [processes.md](processes.md) reproduced to the digit — and 147.6 s at z = 40. Grinding: 0.200 s for the demo's 60
  grains up to 5.820 s for 1920, with the removed volume converging and Ra going back up after 960 grains. Two findings
  for the plan and one for the measurement, see the log in [long-programs.md](long-programs.md); one page:
  [results-2026-10-03-long-programs.html](../bench/results-2026-10-03-long-programs.html).

- **Profile extraction from 3D solids** (`Section3`, `SectionPlane`, `Profile2`; demo page "Profiles"):
  - plane sections with a u/v window;
  - axial r–z profiles with z and r ranges, and envelopes over several angles;
  - radial sections with polar radius, and line profiles with Ra/Rz;
  - deviation from a nominal profile;
  - CSV / SVG export.

- **Performance rounds 1–6** (round 3: separate agent): pocket-large 30.9 s → 4.9 s, faster than C++ Manifold on long
  tasks, exact, independently reviewed after every round; see [performance.md](performance.md).

- **GPU prototype: Z-map preview, round 3** (branch `feature/server-gpu`): a point set the backend keeps
  (`UploadPoints`, `PointSet`), so a caller that asks about the same points again does not send them again. A
  million-point query costs 0.51 ms against 2.20 ms per call and 5.87 ms for the same call on the CPU, with identical
  answers; what is left is the 4 MB answer coming back. The height query also takes the per-map lock now, which it
  should have taken all along. See [gpu-findings.md](gpu-findings.md).

- **GPU prototype: Z-map preview, round 2** (branch `feature/server-gpu`): the caller decides about the read-back
  (`ZMapReadBack`, `ReadHeights`, `BackendRemovedVolumeMm3`), batch queries (`SampleHeights`, `ProbeMaterial`) and the
  removed volume reduced on the device. Measured: pinning buys nothing on this machine (8.68 vs 8.70 GB/s), a
  million-point query is transfer-bound but still wins (2.19 ms against 3.47 ms on the CPU) once the host stops
  packing it, and a few hundred probed poses are faster on the CPU. See [gpu-findings.md](gpu-findings.md).

- **GPU prototype: Z-map preview** (branch `feature/server-gpu`): previews a whole toolpath as a height field, `Cpu`
  backend in C# as the reference and an optional `Cuda` backend; 2.5 ms against 4.66 s for the exact kernel on an RTX
  5070 Ti, at 0.106 % deviation that is mostly a property of the representation; see [gpu-findings.md](gpu-findings.md).

## Performance – follow-ups

- Fixed costs per cut for short tasks (C++ still 1.3–2.3× faster); ReadyToRun for short-lived processes.
- Renumber the optimisation rounds once round 3 (other agent) is merged; branches `perf-round-*`.
- Merge `feature/server-gpu` after review – never directly to main. The GPU half is done; the server mode (part 1 of
  [server-gpu-plan.md](server-gpu-plan.md)) is not built.

## Long programs – follow-ups

Plan and log: [long-programs.md](long-programs.md). Steps 1 (the baseline), 2 (the step binning) and 3 (a convex tool
on a pose sequence) are done; steps 4 to 6 are the preview itself.

- ~~**A convex program followed by a ball one emptied the map on the GPU.**~~ Fixed, and the fault was a pointer and
  not an algorithm: the kernel tells a sphere from a polytope by the plane pointer it is handed, `reserve_planes`
  keeps the half-spaces of an earlier convex run, and the ball launch passed them on with a count of zero — so the
  ball became a tool that removes the whole height of every column its bin reaches (1476.83 mm³ where it should have
  removed 84.82 mm³, with the overflow counter still at zero). The launch takes the pointer from the count it was
  given, and `ConvexDexelTests.AConvexToolThenASphereOnTheSameDevice` runs convex → sphere against the CPU, which is
  the order the earlier test did not have.
- ~~**"The order of steps inside a tile does not matter" is written down and is false.**~~ Corrected in
  `docs/long-programs.md` on 2026-10-04: removal is a union, but that alone does not make a tile order-independent.
  The counter-example is now measured as well, not only argued — the binned launch test asserts that overflows
  actually occur, because `Assert.Equal(b.Overflows, a.Overflows)` would also pass at `0 == 0` with the guard never
  reached. They occur at 32, 64 and 301 cells (1, 3 and 36) and not at 16, which is why that assertion sums over the
  four sizes rather than looking at one. The mechanism it corrects: the capacity guard in `dexel_subtract`
  (`if (left && right && n + 1 > capacity)`) keeps the part below a cut and drops the roof above it, so the
  intervals move with the order the cuts arrive in, not only the count. It holds for columns that never reach
  capacity, and the overflows above are what make the difference observable at all. Step 3 puts the convex path
  through the same `dexel_apply_column`, so the same guard and the same caveat apply there.
- **The sampling bound in `ConvexDexelTests.SamplingBound` is the maximum over the steps, not their union.** For a
  program that fans out — the rotating case — the box one step contributes is smaller than the area the whole program
  sweeps, so what the test asks for (0.1253 mm³ at 500 cells) is not "the volume a column model can be off by" as the
  prose says; the union geometry gives about 0.19 mm³. No verdict changes — a smaller bound is the stricter test, and
  the rotating case sits at 0.0147 mm³ inside either — but the bound is not yet the quantity the text claims, and
  that matters as soon as step 4 asks whether a ground surface is inside the rim it samples.
- **The interval search is O(m³) in the half-spaces, and the measurement says the bound is much worse than the common
  case.** Where a column meets a sweep, `ConvexProfile.Span` (and `convex_span`) walks every crossing of two of the m
  lines and evaluates the envelope there over all m of them. Building the envelope once instead — sort the slopes,
  stack, m operations — is O(m) and is what the remarks on both point at; it needs a sort in local memory and a stable
  tie-break on parallel lines, which is why the walk is what it is today. **Measured** (`LongPrograms convex`, unbinned
  kernel at a fixed 99 200 steps): 704.1 / 1055.6 / 1935.4 / 3278.2 ms at 6 / 8 / 12 / 16 half-spaces — a factor 4.65
  where m³ would give 18.9. **The 1.57 this item used to quote was the wrong model rather than a smaller exponent:**
  the data is `289.4 + 11.63·m²`, a constant plus a quadratic, which fits all four points within ±2.1 %, and a power
  law through the same numbers returns 1.558 in log-log, because a power curve under-reports its exponent when a
  constant share is in the data. That share is 41 % of the time at m = 6 and falls to 9 % at m = 16, so the exponent
  is 2, which is what `Where` costs in any case: `nLo + nHi = m` exactly and the term runs unconditionally
  ([ConvexKernelFindings.md](../ConvexKernelFindings.md) §10.1). The bound is the worst case and a ball is not
  the worst case: only the few half-spaces whose normal faces the column bound it at all. So the follow-up is worth less
  than the exponent suggested, and `MaxPlanes` (16) has more headroom than it looks — a tool with more half-spaces has
  to be split by the caller today, and whether it has to be at all is now a question with numbers behind it. The reason
  to do the work anyway: the O(m³) case is a tool with flat normals, and a gear flank or a wheel rim is exactly that.
- **A column model cannot shrink its error with the grid, and the tests now say so instead.** A centre that sits
  exactly on the tool's edge counts as inside (closed rule), so a face on the centre line of a grid takes a whole
  extra row of columns there and no refinement takes that away — the box case at 1000 cells is off by exactly the rim
  it samples, 0.4408 mm³ against a bound of 0.4408 mm³. `ConvexDexelTests` therefore checks the *sampling bound*
  (P·h/2 of area over the silhouette's perimeter, times the swept height) at every grid, which is O(h) and still
  separates sampling from a body cut wrong. Whether a preview should ever give a boundary column half its weight
  instead of all of it is a question for step 4: the ground surface of a grinding wheel is what the bound shows.
- **The exact kernel samples a rotation linearly in the angle.** `Process3.Sample` holds `diameter · dθ / 2` under
  its sweep tolerance on top of the chord, so the default 30 nm asks for ~50 000 poses for a 1.2 rad move of a 2.4 mm
  tooth (65 536 with the binary subdivision it uses), one exact hull and one Boolean each. The sagitta is quadratic
  and would need a tenth of them; the linear guard is there for a reason (the hull of both poses overcuts by the
  chord), but it is what makes any rotation on a small tool expensive.
  `ConvexDexelTests.ARotatingToolAgreesWithTheExactCut` runs on 4 µm — 512 exact intervals against 11 preview steps,
  72 s, the slowest test in the suite. Steps 4 and 5 both turn tools over a whole wheel, so this is on the critical
  path for anything measured against the exact kernel.
- **The host-side binning is now the limit above ~10⁶ steps per call.** `StepBins.Build` is O(steps) and costs 18.9 ms
  of a 36.8 ms wall at 793 600 steps — seven times the kernel it feeds. It is invisible at 25 000 steps and dominant
  above a million, and step 3 did not change it: the convex launch goes through the same CSR, so a program of
  rotating poses pays it too. The ways out are coarser tiles (fewer, larger CSR entries per step), binning on the
  device, or binning once per program instead of per call. Measuring it is `LongPrograms dexel`.
  **Measured on the convex path it stops being the limit** (`LongPrograms convex`): the same 17.7 ms of host work is
  4.7 % of a 372.6 ms wall at 793 600 steps, because the convex kernel is ~19× the ball kernel and hides it. So this
  is a ball-path item, and whether it ever bites depends on which tool the program uses.
- ~~**The binned launch gave a different result than the unbinned one.**~~ Fixed, and the fault was not where it looked:
  the CSR was complete (no step that reaches a column missing) while 83 336 of 90 601 columns at 301 cells differed, because
  `dexel_apply_column` read the CSR range as step indices where it is positions in `tileSteps`. It takes an optional
  index list now. `StepBinsTests` (20 348 (step, column) pairs) and
  `DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` (four map sizes, every column and interval compared bit
  for bit) keep it that way.
- **Why the 2D kernel slows the 3D kernel in the same process.** Measured, not explained: the 60-grain grinding case
  reads 200 ms in a fresh process, 352 ms after one 30 s gear case, 885 ms after the three gear cases of the full run.
  Ruled out: server GC (355 ms), `DOTNET_TieredCompilation=0` (411 ms), the machine (fresh process right after the same
  load: 198 ms), the length of the process (5.8 s of 3D work first: 185 ms). The results (volume, Ra/Rz, pass and hull
  counts) are identical either way — only the time moves. Next candidates: the address space the 2D cut leaves behind
  (225 000 swept pieces), `Solid`'s per-thread scratch and `ArrayPool` buckets, and the `OrientedCache` in `Process3`.
  It matters for the server, where a preview is timed in a process that has already cut something.
- ~~**The warm-up rule in `bench/README.md`.~~ Fixed: the README described it as "until 1.5 s have passed" while the
  code uses "three runs within 10 %, at least five". A cold pass of the 60-grain case takes 1.8 s, so the documented
  wording ended the loop after one pass and reported 938 ms instead of 200 ms. README corrected, `LongPrograms` uses the
  code's rule.
- Steps 4 to 6 (grinding preview, 2D gear preview, into the server) are listed in
  [long-programs.md](long-programs.md).

## GPU preview – follow-ups

From [gpu-findings.md](gpu-findings.md); nothing here blocks a preview, all of it makes one better.

- ~~**Let the caller decide about the read-back.**~~ Done: `ZMapReadBack.Never` keeps the field on the device,
  `ReadHeights()` brings it back on demand, `BackendRemovedVolumeMm3` asks the device for the progress number, and every
  host read of a stale field throws instead of answering wrongly. A per-step read-back now costs 0.16 ms of device
  reduction instead of 0.4 ms of copy, and a caller who wants a picture of the field mid-cut still gets one.
- ~~**Second GPU case: batch queries.**~~ Built (`SampleHeights`, `ProbeMaterial`, both backends). The first
  measurement said no — a million-point query ended up at 0.6× the CPU time — but that was a *cold* call on freshly
  allocated arrays, and half the wall time was a host-side packing loop. With the packing moved into the kernel
  (`sample_d_kernel` takes the points as they are) a million-point query costs 2.19 ms against 3.47 ms on the CPU. A
  few hundred probed poses stay faster on the CPU (0.039 ms against 0.116 ms): that query is all round-trip latency.
- ~~**Pinned host memory.**~~ Built and measured: 8.68 GB/s pinned against 8.70 GB/s pageable, measured back to back in
  one process, with the order flipping between processes. There is nothing to win, so it is off by default and the
  item is closed. (The transfer ceiling here is the driver's, not the bus'.)
- ~~**A query that lives on the device between calls.**~~ Done: `UploadPoints` hands a point set to the backend once
  (`nc_pointset_create`) and `nc_zmap_sample_set` samples it without a copy. A million points: 2.20 ms per call
  against 0.51 ms from a set that is uploaded once in 2.35 ms, so it pays for itself at the second query. The upload is
  gone; 0.42 of the remaining 0.51 ms is the 4 MB answer coming back.
- **A cheaper answer.** The download is now the whole cost of a point query. The next thing to try is fewer bytes:
  half precision for a picture that is only shaded, or a renderer that consumes the height field on the device instead
  of asking for it point by point.
- ~~**Warm up before measuring.**~~ Done: every query number needed a warm-up to mean anything — the first table was
  off by a factor of ten because the bench called each query once on freshly allocated arrays. The bench prints the
  cold call beside the warm one now, and `docs/gpu-findings.md` reports only warm numbers.
- ~~**Time the call as well as the work inside it.**~~ Decided and built on 2026-10-04. The CPU backend reported
  3.46 ms for a query the caller waited
  5.87 ms for, because the span-to-array copy sat outside its stopwatch. The bench now prints the caller-side time next
  to the backend's own figure. `ZMapTiming` now carries a `PackMs` that both backends fill, and the deliberate part
  is where it sits: the CPU stopwatch still covers the parallel loop only, because every CPU timing already recorded
  in the docs depends on that span, so packing is reported apart rather than folded in. It falls inside `WallMs` on
  CUDA and outside it on the CPU, which is exactly why the two `WallMs` columns of a table could not be compared
  against each other before.
- **A partial read-back** (a row band of the field) for a caller that wants a picture of a part of the stock while the
  cut runs. The whole-field copy is the only thing left that scales with the grid.
- **Server mode** (`samples/Stykker.NanoCut.Server`): geometry on the server, progress over SignalR, cancellable, the WASM
  demo stays as it is.
- **The viewer buttons on the preview page promise stacking and do not stack.** "Exact only", "+ CPU field" and
  "+ GPU field" read as additive, but the page holds one engine state, so clicking "+ GPU field" removes the CPU field
  again — measured on 2026-10-04, the CPU field's 352 410 pixels are gone once the GPU field is on. The two fields
  are the same program on two backends and agree to +0.001 %, so stacking them would show the upper one and hide the
  lower; renaming the buttons is the fix that keeps the page honest, and accumulating fields is the one that would
  have to be argued for rather than assumed.

## Gear generation – follow-ups

- **`Process2.Cut` is not deterministic under load, and the flank gate is measuring that.** `RackGeneratedGearHasInvoluteFlanks`
  fails roughly one full-suite run in three — flank 511 / 1596 / 2230 / 5327 nm against a 300 nm tolerance — and passes
  every time it runs alone. It is not a flaky test: a probe running the gear case twice in one process gets two
  different regions, while the blank, the rack and the swept pieces hash identically. **The 2D boolean kernel is not a
  pure function of its arguments**, with the pipeline on or off. Until that is fixed, every gear figure in this repo —
  the volumes in `docs/processes.md`, the flank in this item, the timings in the two commits above — is a sample from a
  distribution rather than a constant, and no comparison smaller than the run-to-run spread is a result. The mechanism
  is not identified and the fault is narrowed to `BooleanKernel.Execute`'s callees; see
  [GearFlankFlakeFindings.md](../GearFlankFlakeFindings.md). **This is now the first thing to fix in this section**, and
  it outranks all three items below it.
- ~~**`Process2.Cut` is dominated by the sequential subtracts, not by the sweep.**~~ Answered on 2026-10-04, and not in
  the direction this item expected: the fix it asked for does not work. The batch size had never been swept for the
  shipped order, and 256 is an optimum from both sides (128 costs 1.47×, 512 1.27×, 1024 2.95×), so the win could not
  have come from there. Grouping the pieces by the region of the workpiece they remove costs 1.40× to 2.63× — the
  finer the cell, the worse, because it makes each union cheaper and multiplies the subtract count at the same time.
  What paid was overlapping the union with the subtract (`Process2.Pipeline`, on by default): 31.6 s to 26.7 s on a
  bit-identical result, 5.0 s of the 7.5 s of union recovered and the rest lost to memory bandwidth between two
  allocating strands. The cost is that `Process2.Cut` is no longer implicitly single-threaded, which the server mode
  needs to know: *n* concurrent planar cuts take 2*n* threads. Second gear section in `docs/processes.md`.
- ~~**Renormalise the result every N batches.**~~ Measured and shipped on 2026-10-04, at every 32 batches
  (`Process2.RenormalizeEvery`): 26 216 ms against 26 516 ms for never, in the same build, where the spread inside
  one setting is 0.2 % — the worst run at 32 still beats the best control run. The window is narrow, and the shape
  says why: 128 and 512 are both *slower* than never, so renormalising less often does not merely stop paying, it
  costs. Exactness-neutral at every setting — same removed volume, same 69 contours, same 107 328 profile
  vertices. That closes the last of the three items in this block. And the warning on how to judge a batching
  variant stays: not by volume, which moves in the eighth decimal with the contour count, but by the flank.

## Profile extraction – follow-ups

- Pick the section interactively in the viewer (plane gizmo) instead of entering numbers.
- Rz as the ISO mean over five sampling lengths, with profile filters (λc) for roughness vs. waviness.
- Nominal profiles for gears (involute flank deviation per tooth) and for milled parts (nominal solid instead of a 2D contour).
- Sections of a grinding result taken directly from its workpiece cells.



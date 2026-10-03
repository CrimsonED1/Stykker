# To do

Open features and ideas, newest first. Each entry says what is wanted and which existing building blocks it can use.

## Done

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
- **"The order of steps inside a tile does not matter" is written down and is false.**
  `docs/long-programs.md:83-84` claims it, with the parenthetical "only the overflow counting can differ". The
  capacity guard in `dexel_subtract` (`if (left && right && n + 1 > capacity)`) keeps the part *below* a cut and drops
  the roof above it, so which side survives depends on the order the cuts arrive in — the intervals move, not only
  the count. It holds for columns that never reach capacity. Step 3 puts the convex path through the same
  `dexel_apply_column`, so the same guard and the same caveat apply there. Correcting the sentence needs no
  measurement; what would need one is a counter-example to put next to it.
- **The sampling bound in `ConvexDexelTests.SamplingBound` is the maximum over the steps, not their union.** For a
  program that fans out — the rotating case — the box one step contributes is smaller than the area the whole program
  sweeps, so what the test asks for (0.1253 mm³ at 500 cells) is not "the volume a column model can be off by" as the
  prose says; the union geometry gives about 0.19 mm³. No verdict changes — a smaller bound is the stricter test, and
  the rotating case sits at 0.0147 mm³ inside either — but the bound is not yet the quantity the text claims, and
  that matters as soon as step 4 asks whether a ground surface is inside the rim it samples.
- **The interval search is O(m³) in the half-spaces, and that is the next thing to make cheaper.** Where a column
  meets a sweep, `ConvexProfile.Span` (and `convex_span`) walks every crossing of two of the m lines and evaluates the
  envelope there over all m of them. Building the envelope once instead — sort the slopes, stack, m operations — is
  O(m) and is what the remarks on both point at; it needs a sort in local memory and a stable tie-break on parallel
  lines, which is why the walk is what it is today. It matters at 16 planes and above, i.e. for the ball (12) and
  anything bigger: a tool with more than `MaxPlanes` (16) half-spaces has to be split by the caller today.
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
- **Time the call as well as the work inside it.** The CPU backend reported 3.46 ms for a query the caller waited
  5.87 ms for, because the span-to-array copy sat outside its stopwatch. The bench now prints the caller-side time next
  to the backend's own figure; whether `CpuBackend` should report its staging in `WallMs` is undecided.
- **A partial read-back** (a row band of the field) for a caller that wants a picture of a part of the stock while the
  cut runs. The whole-field copy is the only thing left that scales with the grid.
- **Server mode** (`samples/Stykker.NanoCut.Server`): geometry on the server, progress over SignalR, cancellable, the WASM
  demo stays as it is.

## Gear generation – follow-ups

- **`Process2.Cut` is dominated by the sequential subtracts, not by the sweep.** Measured on the rack case (m = 2, z = 20,
  2560 intervals): 0.3 s to build the pieces, the rest is 621–1200 exact subtracts of a growing region. The obvious
  win — uniting neighbouring intervals in one batch instead of uniting 32 poses — makes the *union* 7× faster and the
  *subtract* 2× slower, because each batch's union is no longer one contiguous ribbon and the result degenerates into
  thousands of degenerate loops. The fix is to group the sweep by the region of the workpiece it removes, not by
  interval. See `docs/processes.md` for the full numbers.

## Profile extraction – follow-ups

- Pick the section interactively in the viewer (plane gizmo) instead of entering numbers.
- Rz as the ISO mean over five sampling lengths, with profile filters (λc) for roughness vs. waviness.
- Nominal profiles for gears (involute flank deviation per tooth) and for milled parts (nominal solid instead of a 2D contour).
- Sections of a grinding result taken directly from its workpiece cells.



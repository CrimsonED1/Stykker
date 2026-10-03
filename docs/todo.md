# To do

Open features and ideas, newest first. Each entry says what is wanted and which existing building blocks it can use.

## Done

- **Profile extraction from 3D solids** (`Section3`, `SectionPlane`, `Profile2`; demo page "Profiles"):
  - plane sections with a u/v window;
  - axial r–z profiles with z and r ranges, and envelopes over several angles;
  - radial sections with polar radius, and line profiles with Ra/Rz;
  - deviation from a nominal profile;
  - CSV / SVG export.

- **Performance rounds 1–6** (round 3: separate agent): pocket-large 30.9 s → 4.9 s, faster than C++ Manifold on long
  tasks, exact, independently reviewed after every round; see [performance.md](performance.md).

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
- **A query that lives on the device between calls.** The point query is now 95 % PCIe traffic, so the next win is to
  upload the point set once and sample it repeatedly, instead of paying 16 MB per call.
- **Warm up before measuring.** Every query number needed a warm-up to mean anything: the first table was off by a
  factor of ten because the bench called each query once on freshly allocated arrays. The bench prints the cold call
  beside the warm one now, and `docs/gpu-findings.md` reports only warm numbers.
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



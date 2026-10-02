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

- **Let the caller decide about the read-back.** `IZMapBackend.Apply` returns the height field on every call, so progress
  reporting per step costs 450× the batched call. Report the volume per batch and the heights on demand (or every 32
  steps).
- **Second GPU case, only if something needs it:** batch queries – heights at millions of points for a roughness map, or
  collision checks over thousands of tool poses. One launch over independent points with no read-back is more
  GPU-friendly than the Z-map, not less.
- **Pinned host memory** for the height field: the download runs at 6.5 GB/s pageable, roughly half of what the bus can
  do.
- **Server mode** (`samples/Stykker.NanoCut.Server`): geometry on the server, progress over SignalR, cancellable, the WASM
  demo stays as it is.

## Profile extraction – follow-ups

- Pick the section interactively in the viewer (plane gizmo) instead of entering numbers.
- Rz as the ISO mean over five sampling lengths, with profile filters (λc) for roughness vs. waviness.
- Nominal profiles for gears (involute flank deviation per tooth) and for milled parts (nominal solid instead of a 2D contour).
- Sections of a grinding result taken directly from its workpiece cells.



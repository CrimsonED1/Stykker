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

## Performance – follow-ups

- Fixed costs per cut for short tasks (C++ still 1.3–2.3× faster); ReadyToRun for short-lived processes.
- Renumber the optimisation rounds once round 3 (other agent) is merged; branches `perf-round-*`.
- Merge `feature/server-gpu` (server mode, GPU prototype) after review – never directly to main.

## Profile extraction – follow-ups

- Pick the section interactively in the viewer (plane gizmo) instead of entering numbers.
- Rz as the ISO mean over five sampling lengths, with profile filters (λc) for roughness vs. waviness.
- Nominal profiles for gears (involute flank deviation per tooth) and for milled parts (nominal solid instead of a 2D contour).
- Sections of a grinding result taken directly from its workpiece cells.



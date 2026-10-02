# Shape generator and processes

Every machining process is modelled the same way: **an acting shape moves relative to one or more workpieces and
removes everything it sweeps.** Only the relative motion matters, so a moving workpiece (lathe spindle, gear blank,
rotary table) is handled with `Motion2.Relative` / `Motion3.Relative` (workpiece(t)⁻¹ ∘ tool(t)).

## Shapes

| Generator | Result |
| --- | --- |
| `Region2.Rectangle/Circle/Polygon`, Booleans, `Offset` | any planar region (holes allowed) |
| `GearProfile.Involute(m, z, α)` | involute spur gear profile |
| `GearProfile.Rack(m, n, α)` | rack cutter for generating gears |
| `Region2.ConvexParts()` | exact convex decomposition (triangulation + Hertel–Mehlhorn) |
| `Solid.Box/Sphere/Cylinder/Cone/Capsule` | primitives with controlled chord error |
| `Solid.Extrude(region, z0, z1, placement)` | prism of any region |
| `Solid.Revolve(profile, tol, placement)` | solid of revolution of any r–z profile |
| `Solid.Transform(pose)` | rigid placement (rounded to the grid, ≤ 0.87 nm) |
| `ConvexHull3.Compute`, `Sweep3.Translate` | exact convex hull, exact translational sweep |
| `ToolShape.Revolved/BallNoseMill/Extruded/FromConvexParts` | acting 3D shapes as unions of convex parts |

## Motions

`Motion2` / `Motion3` are lists of continuous segments `t ∈ [0, 1] → Pose`. Built-ins: `Linear`, `Polyline`,
`Rotate`, `Custom`, `Sequence`, `Compose` (3D), `Relative`. A segment boundary may jump (e.g. a periodic rack shifted
back by one pitch); the sweep is never interpolated across it.

## Process engines

| Engine | Use | Method | Error |
| --- | --- | --- | --- |
| `Process2` | planar processes: gear generation/shaping, wire EDM, 2.5D | convex parts at every sampled pose + the area swept by each edge between poses | exact for the linearly interpolated motion; deviation of vertex paths from their chords ≤ `SweepNm` |
| `Lathe` | turning | `Process2` in the r–z half-plane, then `Solid.Revolve` | as `Process2` + chord error of the revolve; helical feed marks not modelled |
| `Process3` | arbitrary spatial motion | same orientation: exact Minkowski sum with the move; with rotation: convex hull of both poses with step ≤ 2·`SweepNm`/diameter | exact for translations; correct but slow for large rotations |

Sampling is adaptive: an interval is accepted when no tool point leaves the chord between the interval's poses by more
than `Tolerance.SweepNm` (checked at the extreme points of the tool, where the deviation of a rigid motion is largest).

## Measured (net10.0, one core)

| Process | Result | Time |
| --- | --- | --- |
| Gear generation with a rack, m = 2 mm, z = 20, sweep 30 nm | flank deviation from the ideal involute ≤ 5.6 nm, 2560 roll steps | 22–31 s |
| Same, sweep 300 nm | ≤ 20 nm | 10 s |
| Turning a Ø20 × 40 mm bar, 8 moves | 89 profile vertices | 1 s |
| Ball-nose pocket, 8 moves, chord 1 µm | 39 207 faces | 3 s |
| Same, chord 50 nm | 664 246 faces | 51 s |

![Gear generation](images/gear-generation.png)
![Turning](images/turning.png)
![Milling](images/milling.png)

## Performance notes (measured)

Cube 8 mm moving and turning through a 20 mm cube, path error 50 µm (128 steps per 45°):

| Step | before | now native | now browser (AOT) |
| --- | --- | --- | --- |
| first 45° rotation (A) | 12 s native, 32 s AOT | 0.46 s | 0.6 s |
| translation on the cut cube (Y) | 5.3 s native, 15 s AOT | 0.55 s | 2.6 s |
| second 45° rotation (C, around the tilted cube) | 45 s native, 114 s AOT | 5.6 s | 17.5 s |

What made the difference:

1. **Floating-point filters** for every side test and every ray–face test: the sign is taken from a double
   evaluation when it clearly exceeds a conservative rounding bound (1e-11 × Σ|terms|, about 10⁴ above the actual
   error), otherwise the Int384 / BigInteger evaluation decides. Results are unchanged (all tests, including the
   degenerate lattice fuzzing and the ManifoldSharp oracle, pass); the exact path now runs for < 1 % of the tests.
2. **Coplanar knowledge in the ray cast:** faces in the fragment's own plane are skipped without arithmetic, and the
   coplanar-overlap test no longer builds the exact interior point.
3. **Split tree and fragment merging:** pieces of a face are restored when they all end up on the same side, and
   coplanar neighbours sharing an edge are merged when their union is convex (exact edge planes are kept).
4. **Grouped sweeps:** neighbouring swept pieces are united in groups of eight before cutting the workpiece.

The remaining cost of long rotating sweeps is the face count (35 000 faces after the second rotation above): regions
bounded by a polygonal envelope need many convex pieces, and pieces meeting at T-junctions cannot be merged yet.

## Known limits / next steps

- **3D motions with rotation** (5-axis tool tilt, hobbing, skiving, rotating workpiece in 3D) use the convex hull of
  two poses, which needs very small steps. An exact face-sweep (analogous to the 2D edge sweep) is the next step.
- **Face count:** pieces are merged back (split tree, coplanar merging), but pieces meeting at T-junctions are not,
  so long rotating sweeps still accumulate many faces.
- **Turning feed marks:** the lathe model is a continuous cut; scallops of the feed per revolution are ignored.

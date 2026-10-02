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
| `ToolShape.SawBlade`, `ToolShape.SawBladeTeeth` | circular saw blade: core disc + one convex trapezoid prism per tooth |
| `SpinningTool.Disc/Symmetric/SawBlade/Prismatic/Toothed/Asymmetric` | tools spinning about their z-axis at a given rpm (see below) |

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

| `Process3.CutSpinning` | spinning tools (wheels, cut-off discs, saw blades) | body: feed only; prismatic teeth fed in their plane: `Process2` edge sweep of feed + spin, extruded; otherwise `Process3` on feed ∘ spin | body exact (+ chord error); teeth ≤ `SweepNm` |

Sampling is adaptive: an interval is accepted when no tool point leaves the chord between the interval's poses by more
than `Tolerance.SweepNm` (checked at the extreme points of the tool, where the deviation of a rigid motion is largest).

## Spinning tools

`SpinningTool` describes a tool that spins about its own z-axis at `Rpm` while its frame is moved along a feed
`Motion3`. Time is explicit: each feed segment gets a duration (`SpinningTool.Durations(feed, mm/s)` from a constant feed
speed, or given directly), the spindle angle is `2π · rpm / 60 · t`, and `Process3.CutSpinning(..., startSeconds)` keeps
the spindle phase across cuts that are split into several calls (e.g. frames for playback).

```csharp
var saw  = SpinningTool.SawBlade(radiusMm: 20, thicknessMm: 1.6, teeth: 12, toothHeightMm: 2.5, rpm: 3000, tol);
var feed = Motion3.Compose(Motion3.Linear(Vec3.Mm(-11, 0, 17), Vec3.Mm(-5, 0, 17)),      // feed along x …
                           Motion3.Fixed(Pose3.Rotation(-Math.PI / 2, 1, 0, 0)));         // … spindle axis along y
var rest = Process3.CutSpinning([block], saw, feed, feedMmPerS: 60, tol, out var stats)[0];
double fz = saw.FeedPerToothMm(60);                                                       // 60 / (50 · 12) = 0.1 mm
```

**Rotationally symmetric tools are spin-invariant.** Spinning maps a solid of revolution onto itself, so at every
instant it occupies the same space whatever its spindle angle; the swept volume is that of the non-spinning body
moved by the feed. A plain grinding wheel or cut-off disc (`SpinningTool.Disc`, `SpinningTool.Symmetric`) is therefore
never sampled in time: it is cut with `Process3.Cut` on the feed alone, which is one exact Minkowski sweep per straight
feed segment and independent of the rpm. The only deviation is the chord error of the inscribed polygon
(`Tolerance.ChordNm`); a rotating polygon would sweep its circumcircle instead. The test `SymmetricDiscIsSpinInvariant`
checks this against an explicitly sampled spin.

**Toothed tools** (`SpinningTool.SawBlade`, `Prismatic`, `Toothed`, `Asymmetric`) are split into a symmetric body
(e.g. the core disc, cut as above) and the cutters (teeth), whose motion is feed(t) ∘ spin(t). The material each tooth
removes, and the uncut sliver of about one feed per tooth f_z = v_f ÷ (n · z) at the end of a cut, follow from the
geometry. Two engines:

- *Planar* (default when it applies): prismatic cutters (`CutterPrism`, e.g. straight saw teeth) fed in their own plane
  (spindle axis fixed, no feed along it – sawing, slitting, cut-off). Then the swept volume is the swept *area* of the
  tooth profile extruded over the tooth thickness. The area comes from `Process2`'s exact edge sweep of feed + spin
  (pieces outside the workpiece bounds are dropped, the rest united in a tree of small groups), the prism is subtracted
  once per call. Steps per revolution: at least 63 (0.1 rad), more when the tooth tips' chord error √(8 · SweepNm / r)
  requires it. Error ≤ `SweepNm` (undercut of the tip trochoids).
- *Spatial* (any other cutters or feed, or `planar: false`): `Process3` on the combined motion, split into pieces of ≤ 45°
  of spindle rotation so that whole revolutions cannot cancel. Each step is the convex hull of a tooth at two poses with
  a step ≤ 2 · `SweepNm` ÷ tooth diameter; hulls outside the workpiece bounds are skipped and the rest united in groups
  of 64. This works for tilted blades, feeds along the axis and non-prismatic teeth, but it is 10–100× slower.

Measured (net10.0, one core; browser = Blazor interpreter without AOT; times vary ± 50 % with machine load):

| Case | Result | Time |
| --- | --- | --- |
| Ø40 × 1.6 mm wheel, 3 mm deep, 8 mm feed in 16 frames, chord 20 µm | spin-invariant, exact sweep | 0.3 s |
| Ø40 saw, 12 teeth, 3000 rpm, f_z 0.1 mm, 6 mm feed (5 rev) in 12 frames, sweep 50 µm (demo default) | planar, 396 steps | 1.2 s native, 5.5 s browser |
| same, f_z 0.05 mm, 8 mm feed (13 rev) in 16 frames, sweep 20 µm | planar, 1808 steps | 2–4 s native, 51 s browser |
| same, spatial engine, sweep 100 µm, 6 mm feed in 12 frames | 1 100 hulls | 11 s (77 s at f_z 0.05 mm) |
| Ø20 saw, 6 teeth, 3 mm feed: f_z 1 / 0.2 / 0.05 mm vs. plain disc | uncut sliver 0.256 / 0.063 / 0.014 mm³ (≈ 0.26 mm² · f_z) | 0.2 / 0.8 / 2.3 s |

Limits: the spindle speed is constant and the tool is rigid (no run-out, deflection or wear); the teeth are not set
(use `toothThicknessMm` for a wider kerf); the planar engine requires a feed exactly in the tool plane (checked at 17
points per segment, otherwise the spatial engine is used).

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
- **Spinning tools in 3D:** toothed tools fed out of their plane use convex hulls of the rotating teeth (slow); the
  planar edge sweep covers sawing and slitting in the blade plane.
- **Turning feed marks:** the lathe model is a continuous cut; scallops of the feed per revolution are ignored.

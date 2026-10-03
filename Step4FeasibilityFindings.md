# Step 4 feasibility: can a grinding preview run under the limits step 3 left?

Read-only study, 2026-10-03. Nothing built, nothing run, nothing committed. All source read from the main checkout
`C:\_AI\StykkerNanoCut\StykkerNanoCutRepo`, branch `long-programs-step-1` at `f5af7c2`, working tree clean.

Every claim is labelled:

- **[read]** — verified in the source, with `file:line`.
- **[calc]** — arithmetic from parameters that are in the source; the arithmetic is shown.
- **[infer]** — needs a measurement or a decision I could not make from the source. Not a result.

---

## 0. Verdict in one paragraph

The plane cap is **not** the friction. A grinding grain is an octahedron with **8 faces**, for every parameter
combination the code allows, so `MaxPlanes = 16` is half-used and the grain is represented *exactly* — no
inscribed-polyhedron error, no split. The step count is **not** the friction either: a covered wheel produces about
**19 000 steps**, 40× fewer than the longest program step 2 already measured and timed. The exact reference **is**
affordable: `GrindingSimulation` at 1920 grains is 5.820 s and is already in the plan's table. The three things that
*are* in the way are (1) `ConvexStep.StepsForRotation` is written for rotation about the tool's own origin and returns
**one step per grain pass** on a spindle-borne grain, with a **37 µm path error** against a 20 µm depth of cut;
(2) for the bench's wheel, two of every grain's eight half-spaces pass through **horizontal** (`m_z = 0`) about four
times per revolution, and nothing in `ConvexProfile.Span` guards that — the one hazard step 3's suite provably never
exercised becomes the normal case; and (3) the accuracy claim itself, a volume comparison, is only discriminating at
a grid that gives away most of the speed advantage. Details and arithmetic below.

---

## 1. (a) How many half-spaces does a grinding grain need? — **8, always, and that is all**

**[read]** The grain's shape is not a parameter. `GrainSpec` carries angle, axial position, protrusion, size and an
orientation — there is no shape field:

- `src/Stykker.NanoCut.Cutting/GrindingWheel.cs:29` —
  `public sealed record GrainSpec(double AngleRad, double AxialMm, double ProtrusionMm, double SizeMm, Pose3? Orientation = null);`

**[read]** `Build` writes the octahedron unconditionally, six vertices at ±h on the grain's own axes, then rotates
them by the grain's orientation:

- `GrindingWheel.cs:109` — `double h = g.SizeMm / 2 * Units.NmPerMm;`
- `GrindingWheel.cs:110` — `var local = new (double X, double Y, double Z)[] { (h,0,0), (-h,0,0), (0,h,0), (0,-h,0), (0,0,h), (0,0,-h) }`
  `.Select(v => o.Apply(v.X, v.Y, v.Z)).ToArray();`
- `GrindingWheel.cs:129` — `grains[i] = new Grain(..., ConvexHull3.Compute(pts), tip);`

**[read]** `ConvexHull3.Compute` is the quickhull entry that returns one `Face3` per triangle
(`src/Stykker.NanoCut.Geometry3D/ConvexHull3.cs:9`, `:208` `Face3.FromTriangle` per triangle). A hull of six points in
general position is a simplicial polytope with `2·6 − 4 = 8` facets.

**[read]** The kernel side already says this out loud — `ConvexTool.Octahedron` is documented as "the grain shape of
`GrindingWheel`" (`src/Stykker.NanoCut.Gpu/ConvexTool.cs:131-133`), and it builds exactly eight half-spaces with
normals `(±1,±1,±1)/√3` (`:143-149`).

**So: 8 half-spaces, against `ConvexProfile.MaxPlanes = 16` (`src/Stykker.NanoCut.Gpu/ConvexProfile.cs:88`,
mirrored as `kConvexPlanes = 16` at `src/Stykker.NanoCut.Gpu.Native/zmap.cu:825`).**

**[calc]** This is not "it fits for the benchmark's parameters". `SizeMm` only scales `h`; `AngleRad`, `AxialMm`,
`ProtrusionMm` and `Orientation` change the position, the radius and the rotation, none of which changes the face
count. `Random` constrains only `0 < grainSizeMm < widthMm` and `protrusionSigmaMm ≥ 0` (`GrindingWheel.cs:71-73`) and
draws a protrusion clamped to `[0, grainSizeMm]` (`:81`). **There is no parameter of `GrindingWheel.Random` or
`GrindingWheel.FromGrains` under which the grain is not an 8-face octahedron.**

Two consequences that matter:

**[calc] The nm rounding does not break it.** `Place` rounds every vertex to integer nm
(`GrindingWheel.cs:124-127`). The rounding is ≤ 0.5 nm against `h = 75 000 nm` for the bench's 150 µm grains, a
relative 6.7e-6, so the hull is still the hull of six points in convex position and still has eight triangular
faces. **It does not matter anyway**: a simplicial polytope *is* the intersection of its facet half-spaces, so eight
planes taken from `Solid.Faces[i].Support` reproduce the grain exactly, perturbed or not. The `ConvexTool` the
preview builds is the same body `GrindingSimulation` hands `Process3` — no approximation, unlike
`ConvexTool.Ball`, which is inscribed and removes 8.8–18 % less than the sphere (`docs/long-programs.md`, step-3 log).

**[read] The converter exists — in the test file, and it is not re-origined.**

- `tests/Stykker.NanoCut.Tests/ConvexDexelTests.cs:21-31` — `private static ConvexTool FromSolid(Solid solid)`, one
  half-space per `solid.Faces[i].Support`, `planes[i] = new HalfSpace(nx/n, ny/n, nz/n, -(double)p.D / n)`.

`HalfSpace` appears in exactly three files in the repository (`ConvexTool.cs`, `ConvexProfile.cs`,
`ConvexDexelTests.cs` — grepped). **There is no `Solid → ConvexTool` bridge in the product.** Step 4 needs one.

**[read] The test's converter leaves the tool at the world origin.** `Plane3` is stored as `n·x + d = 0`
(`src/Stykker.NanoCut.Core/Plane3.cs:8`), so `-p.D` is the signed distance of the face from the origin, and for a
grain on a Ø20 mm wheel that distance is about **10 mm**, not 0.043 mm. `ConvexTool.FromPlanes` accepts that
(`ConvexTool.cs:96` only requires a bounded set with a corner on every plane), but `CornersMm` then sits 10 mm from
the tool frame's origin, and `HalfSpace.D` is packed as a `float` (`ConvexProfile.cs:97`, `PackPlanes`) — 10 mm in
float32 is a 0.6 nm ulop. **[calc]** That costs about 0.6 nm of interval end per unit `m_z`, so it is harmless, but a
production `FromSolid` should take an origin and translate: the grain's own centroid is free (the six octahedral
vertices average to it), and the precision and the corner magnitudes both get better for nothing.

**Answer to (a): 8 planes, for every wheel the code can build, with half the budget left. The cap is not the limit
and the grain needs no approximation.**

---

## 2. (b) What happens over `MaxPlanes` today — **nothing, because nothing splits by plane count**

### The claim in the source is false as written

Three places say a tool over the cap is split, and name `ToolShape` as the thing that does it:

- `src/Stykker.NanoCut.Gpu/ConvexTool.cs:37-38` — "a larger tool is split, which is what `ToolShape` does for it too."
- `src/Stykker.NanoCut.Gpu/ConvexProfile.cs:84` — "A bigger tool is split instead, which is what `ToolShape` does anyway."
- `docs/long-programs.md` (step-3 section) — "a larger tool is split instead, which is what `ToolShape` does anyway".

**[read] `ToolShape` splits for convexity, not for plane count.** Its own doc says so: `ToolShape.cs:8` — "Any shape
can be used once it is split into **convex parts**; the factories below do this automatically." What the factories do
is `Revolved` → one convex frustum per outline segment, `Extruded` → one convex prism per `Region2.ConvexParts()`
piece, `SawBlade` → core disc plus one prism per tooth. And `FromConvexParts` — the call `GrindingSimulation` makes
for every grain at `GrindingSimulation.cs:107` — does no splitting whatsoever: it stores the parts you hand it
(`ToolShape.cs:26-30`).

**[read] A single convex `Solid` can be far over 16 planes.** `ToolShape.Ball(r)` is `Solid.Sphere`, whose faces are a
chord-tolerance polyhedron; `Solid.Cylinder` likewise. Those are the "split" cases the comment is pointing at, and
`ToolShape` does not touch them. A grep for `HalfSpace|FromPlanes|MaxPlanes` over `src/**/*.cs` returns **only**
`ConvexTool.cs`, `ConvexProfile.cs` and the test file. **[read] There is no plane-count splitter anywhere in the
repository.**

**[read] The two guards that do exist are both refusals, not splits:**

- `ConvexProfile.cs:112-116` — `Pack` throws `ArgumentOutOfRangeException` with the message "split the tool".
- `zmap.cu:1423` and `:1441` — the native entry points reject `planeCount > kConvexPlanes`.

Both are unreachable in practice (§11.3 of `CudaLongProgramsFindings.md` already noted the native one can never fire,
because nothing can build a tool over 16 planes in the first place). **The instruction "split the tool" is advice to
a caller about work that does not exist.**

### Does splitting even preserve the result?

**[calc]** Yes in the union, at a cost. If a convex body is decomposed into convex pieces `P₁…P_s` whose union is the
body, then `sweep(⋃Pᵢ) = ⋃ sweep(Pᵢ)` because the sweep is a Minkowski sum with the same segment. So the *removed
volume* is unchanged. But each piece is convex, so **each piece contributes its own interval** per column. A column
that crosses two disjoint pieces goes from one interval to two. So splitting multiplies the intervals per column
directly against `K`, and therefore the overflow pressure — and `docs/todo.md` already records that
`dexel_subtract`'s capacity guard (`zmap.cu:1052`, `if (left && right && n + 1 > capacity)`) is **order-dependent**:
it keeps the part below the cut and drops the roof, so which side survives depends on the order the cuts arrive in.

**[infer]** For grinding specifically the split path is moot for the plane cap and probably moot for `K` too, but
that needs a measurement. The argument that `K` is not a problem: a grain's cut interval always reaches the top of the
column while it is cutting fresh stock, so `n` stays at 1 and no roof is made. A grain that dips into a groove an
earlier grain left *does* cut an interior interval and makes one — that is precisely the "413 of 3793 passes still
cut" population at 1920 grains (`docs/long-programs.md:50`). How many roofs a column ends up holding at K = 4 is not
in the source and not measurable by reading; `docs/todo.md` already flags that the guard is exercised by no test on
either backend.

**Answer to (b): the split path does not exist and the code comments that promise it are wrong. It does not matter
for a grain (8 ≤ 16), but it is the reason the cap has no fallback the moment step 4 wants anything that is not a
grain — the bond, a wheel rim, a disc.**

---

## 3. (c) Steps for the covered wheel — **about 19 000, and the CSR is not the bottleneck**

### The geometry, from the source

**[read]** `bench/Stykker.NanoCut.LongPrograms/Program.cs:348-351` (the demo's grinding parameters):

```
WheelRadiusMm = 10, WheelWidthMm = 1, GrainSizeMm = 0.150
ProtrusionMeanMm = 0.040, ProtrusionSigmaMm = 0.015
Rpm = 3000, FeedMmPerS = 20, DepthUm = 20, SweepUm = 2
BlockLengthMm = 1.5, HalfWidthMm = 0.4, X0Mm = -0.3
```

**[read]** `Program.cs:357` — `zCenter = WheelRadiusMm + (ProtrusionMeanMm - DepthUm / 1000)` = **10.020 mm**, the
wheel axis height above the block top. `Program.cs:360-362` — the feed is
`Compose(Linear(…), Fixed(Rotation(-π/2, 1, 0, 0)))`, so the wheel axis is world **+y**: a vertical wheel on a
horizontal face. `Program.cs:365` — `Tolerance.Budget(totalUm: 2.051, chordNm: 50, sweepNm: 2000)`, i.e. **the exact
run is at a 2 µm sweep tolerance**. `Program.cs:853` — feed length default **0.8 mm**.

**[read]** `Pose3.Compose(inner)` is "first *inner*, then this" (`src/Stykker.NanoCut.Core/Pose2.cs:167`) and
`Motion3.Compose(outer, inner)` zips that (`src/Stykker.NanoCut.Cutting/Motion.cs:97`), and
`GrindingSimulation.PoseAt` is `FeedAt(t).Compose(SpinPose(t))` (`:176`). So the grain's world rotation is
**R = R_x(−π/2) · R_z(θ) · R_grain** with θ the spindle angle.

### The pass window — one pass per grain per revolution

**[calc] Grain-centre radius.** `GrindingWheel.cs:113-120` picks the vertex that lies furthest out:
`centre = sqrt(rTip² − v_y²) − v_x` minimised, with `rTip = (radiusMm + protrusion)·1e6`. With a *random*
orientation the largest `v_x` over the six rotated vertices is `h·max|R_1i|`, and `max|R_1i| ∈ [1/√3, 1]` (Parseval)
with a mean near 0.79. So

| orientation | max v_x | centre (mm) |
| --- | ---: | ---: |
| identity (worst case, smallest centre) | 0.075 | 9.965 |
| typical random (max|R_1i| ≈ 0.79) | 0.059 | 9.981 |
| max\|R_1i\| = 1/√3 (largest centre) | 0.0433 | 9.997 |

using the mean protrusion 0.040 mm so `rTip = 10.040`.

**[calc] Bounding-sphere radius.** `GrindingSimulation.cs:211-212` takes the largest vertex distance from the vertex
centroid and adds the sweep tolerance in nm. For a centred octahedron the centroid *is* the centre and the max
distance is exactly `h`, so **ρ = 75 000 + 2 000 + 1 = 77 001 nm = 0.077001 mm**, for every grain.

**[calc] Contact window.** The pass exists while the grain's bounding sphere meets the block (`GrindingSimulation.cs:
215-217`). At spindle angle φ from the bottom the grain centre is at height `10.020 − centre·cos φ`, so

```
cos φ ≥ (10.020 − 0.077001) / centre = 9.942999 / centre
```

| centre (mm) | cos φ | φ_half (rad) | window 2φ (rad) | in time | feed travel |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 9.965 | 0.997789 | 0.0665 | 0.133 rad = 7.6° | 0.423 ms | 8.5 µm |
| 9.981 (typical) | 0.996192 | 0.0875 | 0.175 rad = 10.0° | 0.557 ms | 11.1 µm |
| 9.997 | 0.994597 | 0.1040 | 0.208 rad = 11.9° | 0.662 ms | 13.2 µm |

ω = 3000·2π/60 = **314.159 rad/s**; duration = 0.8/20 = 0.04 s = **2 revolutions**; `PlanPasses`'s own time grid is
0.25° per sample (`:197`), so the window is quantised to ±0.0044 rad — a generous test, the extra samples simply
remove nothing.

**[read] Cross-check against the measured pass counts** (`docs/long-programs.md:47-50`): 118 passes for 60 grains and
3793 for 1920, i.e. 1.97 and 1.98 per grain = **one pass per grain per revolution**. The arithmetic above lands in
the same place from the geometry, so it is sound.

### Exact intervals per pass — 16

**[read]** `CutPass` splits the pass into `pieces = ceil(Δθ / (π/4))` = 1 (`GrindingSimulation.cs:236`) and hands it
to `Process3.Cut`, which calls `Sample` (`:39`). `Sample` (`src/Stykker.NanoCut.Cutting/Process3.cs:175-196`) accepts
an interval only when `depth ≥ 40` **or** both `diameter · angle / 2 ≤ maxDeviationNm` and
`MaxDeviation(...) ≤ maxDeviationNm`, subdividing binary.

**[calc]** `diameter` is the bbox diagonal of the grain's placed vertices in nm (`Process3.cs:31`, `:159-164`). The
width of an octahedron of circumradius `h` along a unit `u` is `h·Σ|u·eᵢ| ∈ [h, h√3] = [75 000, 129 904] nm`, so the
diagonal is in `[75 000, 225 000] nm`, typical ≈ 195 000 nm. With `tol.SweepNm = 2000`:

- guard 1: `dθ ≤ 4000 / diameter` ∈ **[0.0178, 0.0533] rad**, typical **0.0205**.
- guard 2: the dominant term is the trochoid — the outer vertices ride a circle of radius ≤ `centre + h` ≈ 10.06 mm,
  and the chord-to-arc deviation over `dθ` is `R·dθ²/8`, so `dθ ≤ √(8 × 0.002 / 10.06) = ` **0.0399 rad**.

Guard 1 is the tighter one for any but the smallest diameters. Binary subdivision over a 0.175 rad window at
0.0205 rad gives **2⁴ = 16 intervals**.

**[read] Cross-check:** the bench reports `hulls = 13 841` at 1920 grains, i.e. 3.65 per pass, because most of the 16
intervals miss every cell's bounds and are skipped before a hull is built (`Process3.cs:64`). Consistent with 16.

### Preview steps per pass — and the trap

**[read]** `ConvexStep.StepsForRotation(sweepRad, radiusMm, toleranceMm)` (`ConvexStep.cs:95-102`) returns
`ceil(sweep / √(8·tol/r))`, documented at `:87` as "the outermost corner rides a circle of that radius". That is true
**only when the tool rotates about its own origin** — which is the case in `ARotatingToolAgreesWithTheExactCut`, where
the box turns in place, and **not** the case for a grain, whose origin rides a 9.98 mm circle about the spindle.

| call | `radiusMm` passed | steps per pass | path chord error | tip chord error |
| --- | ---: | ---: | ---: | ---: |
| as documented | 0.075 (tool radius) | **1** | **36.9 µm** | 0.28 µm |
| orbital radius | 9.98 | **5** | 1.5 µm | 11 nm |
| to match the exact's own tip chord | — | **11** | 0.35 µm | 2.6 nm |

Arithmetic for row 1: `√(8·0.002/0.075) = 0.462 rad > 0.175 rad` → 1 step. Path error
`9.98 · (1 − cos(0.0875)) = 9.98 × 3.827e-3 = 38.2 µm` (I quoted 36.9 for the 0.133 rad window; the figure is
**37–38 µm either way**). Row 2: `√(0.016/9.98) = 0.04005`, `0.175/0.04005 = 4.37` → 5; path
`9.98 × (0.035)²/8 = 1.53 µm`, tip `0.075 × (0.0175)²/2 = 11.5 nm`.

**This is the first thing that has to change, and it is a one-line change: a 37 µm path error against a 20 µm depth of
cut is not a coarse preview, it is a wrong one.** `StepsForRotation`'s `radiusMm` must be the **orbital** radius —
the grain centroid's distance to the spindle axis — or the API has to say so in the parameter name and the doc.

### Total steps

**[calc]** steps = passes × steps-per-pass:

| grains | passes | 1/pass | 5/pass | 11/pass |
| ---: | ---: | ---: | ---: | ---: |
| 60 | 118 | 118 | 590 | 1 298 |
| 1920 | 3793 | 3 793 | **18 965** | 41 723 |

**[calc] This is 40× smaller than the longest program step 2 already measured** (793 600 steps,
`docs/long-programs.md:103`). The plan's "then scale to a covered wheel" reads as though there is a scaling story
here. **There is not.** 60 grains and 1920 grains differ by 32×, i.e. 0.6 ms against 19 ms, both invisible against
5.82 s.

**[calc] Robustness: it does not even depend on `PlanPasses`.** If a preview emitted every grain's whole two
revolutions, that is 1920 × 2 × (2π / 0.04005) = **602 600 steps**. But a grain at the top of the wheel is ~20 mm from
the map, so `StepBins.Tiles` returns `Touches = false` for those (`StepBins.cs:129`) and they cost nothing in the
kernel. The naive version costs 602 600 × 24 ns ≈ **14.5 ms of host binning** and no kernel at all. Using the pass
window is worth 32× on the binning and nothing else.

### CSR cost and span-tests

**[calc] Host binning at the measured 23.8 ns/step** (`docs/long-programs.md:103`: 18.9 ms at 793 600 steps;
`:114` and `docs/todo.md` both say the CSR becomes the limit only above ~10⁶ steps per call):

- 18 965 steps → **0.45 ms**
- 602 600 steps (no pass window) → **14.5 ms**

Either way the binning is well under `docs/long-programs.md`'s own 10⁶-step threshold, and on the convex path
`docs/todo.md` already records that the same 17.7 ms of host work is only 4.7 % of a 372.6 ms wall. **The host does
not become the bottleneck again.**

**[calc] Kernel, from step 3's own binned measurement.** At 99 200 steps, 12 planes, the binned convex kernel was
7.3 ms (`docs/long-programs.md`). Each step's ball box there is `(0.05 + 2·0.2)² = 0.45²` mm on 0.05 mm cells =
9 × 9 = 81 columns, so **8.03e6 (step, column) pairs in 7.3 ms = 0.91 ns per pair**. Rescaling 12 → 8 planes by the
measured unbinned ratio 1055.6 / 1935.4 = 0.545 gives **≈ 0.50 ns per (step, column-in-box) pair**.

A step at 5/pass moves the grain centre `9.98 × 0.035 = 0.349 mm` in x, and the octahedron's horizontal width is
`h·Σ|u·eᵢ| ∈ [0.075, 0.13] mm`. Taking 0.13: the swept box is ≈ **0.48 × 0.13 mm**.

| cell `h` | columns in the box | (step, column) pairs @ 19 k steps | kernel | map columns | exact | ratio |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.010 mm | 48 × 13 = 624 | 1.18e7 | **5.9 ms** | 150 × 80 = 12 000 | 5.82 s | ~980× |
| 0.005 mm | 96 × 26 = 2 496 | 4.73e7 | **24 ms** | 300 × 160 = 48 000 | 5.82 s | ~245× |
| 0.002 mm | 240 × 65 = 15 600 | 2.96e8 | **148 ms** | 750 × 400 = 300 000 | 5.82 s | ~39× |
| 0.001 mm | 480 × 130 = 62 400 | 1.18e9 | **592 ms** | 1 500 × 800 = 1 200 000 | 5.82 s | ~10× |
| 0.0005 mm | 1.99e6 | 2.37e9 | **1.19 s** | 3 000 × 1 600 = 4 800 000 | 5.82 s | ~5× |

**[infer]** These kernel figures extrapolate a measured per-pair rate to a geometry step 3 never ran, and they assume
the GPU is occupied — at 0.01 mm the map is only 12 000 columns = 47 thread blocks, so the launch will be
latency-bound and read *slower* than 5.9 ms, not faster. Treat the top two rows as "single-digit to low tens of
milliseconds" and the bottom rows as the honest extrapolation. **The shape of the table is the point: the cost is
driven by the grid, not by the wheel.**

**Answer to (c): about 19 000 steps at 1920 grains (5 per pass), 0.45 ms of host binning, single-digit to
hundreds of ms of device depending entirely on the cell size. The program is short; the grid is the variable.**

---

## 4. (d) What the exact reference costs at 1920 grains — **5.82 s, and that is the right way to get it**

**[read]** `GrindingSimulation` at 1920 grains is already measured: **5.820 s**, 13 841 hulls, 3793 passes,
0.044690 mm³ removed (`docs/long-programs.md:50`). **The comparison is affordable at 1920 grains, not only at 60.**
The plan's phrasing ("for 60 grains, then scale to a covered wheel") implies the reference is the thing that gets
expensive; it is not. Step 4's exact side is 5.8 s, the cheapest acceptance test in the plan.

**[calc]** Per pass that is 5.820 / 3793 = **1.53 ms**; per hull, 5.820 / 13 841 = **0.42 ms**. That is cheap because
the grinding program is only **0.04 s of process time** (0.8 mm at 20 mm/s). Step 5's gear reference is 34.7 s and
step 4's is 5.8 s — the plan's own numbers. **Step 4's acceptance test is 6× cheaper than step 5's; the pressure
step 5 will feel about its reference does not apply here.**

**[read] All the inputs a preview needs from the exact side are already public:**
`GrindingSimulation.Passes` (`:147`) — planned in the constructor (`:108`), so a preview can construct the
simulation, read the pass list and **never call `Run()`**; `Pose3 PoseAt(double seconds)` (`:176`) for the pose inside a
pass; and `Wheel.Grains[i].Shape` (`:18`) for the tool. Nothing needs `PlanPasses` made non-private.

**[calc] Do not build the reference by calling `Process3` per grain.** `ARotatingToolAgreesWithTheExactCut` runs at
0.14 s per exact interval (72 s / 512, `ConvexDexelTests.cs:432` and the comment above it) on a 20 mm block.
`GrindingSimulation` gets 1.53 ms per pass for 16 sampled intervals because it splits the workpiece into 15 cells
(`GrindingSimulation.cs:97`, default cell `2 × max grain size` = 0.3 mm) and each interval touches one or two of them.
A per-grain `Process3.Cut` on the whole block would touch every overlapping cell, roughly 15× more: **≈ 87 s** at
1920 grains. Affordable, but 15× worse for nothing.

**The right shape of the reference is: run `GrindingSimulation` once per grain count (it already writes the surface
profile as CSV under `--out`, `Program.cs:431-441`), and compare the preview's removed volume and profile to that.**
The bench's `--out` already produces `grinding-1920-grains-surface.csv` — the yardstick step 4 needs is on disk.

**Answer to (d): affordable at both 60 and 1920, at 0.200 s and 5.820 s. The claim being made at 1920 is
well-founded; it does not have to be extrapolated from 60.**

---

## 5. (e) Is the preview's own accuracy claim measurable for a grain? — **only at a grid that costs most of the win**

**[read] The rim.** A column is cut when its **centre** lies in the sweep; nothing is sampled in z. So the volume
error is `(P · h/2) · H` per pass — `P` the silhouette perimeter, `h` the cell, `H` the swept z-height — which is what
`ConvexDexelTests.SamplingBound` (`:335-366`) computes as
`(2·(width + height)·cell/2 + cell²) · (highZ − lowZ)`.

**[read] Two known properties of that function before applying it.**

1. It takes the **maximum over steps** of each per-step box extent (`:349-352`), not their union — the bug
   `docs/todo.md:84-87` records. **[calc] For a grain this is nearly harmless**: consecutive steps of one pass
   overlap almost completely (the move is 0.35 mm and the tool is 0.13 mm), so the union ≈ one step's box. For a
   rotating tool it was 34 % low. Step 4 is closer to the harmless end than the rotating case was.
2. It is a **private static method inside the test class**. There is no reusable version. Step 4 needs its own.

**[calc] The bound for one grain pass.** Swept box 0.48 × 0.13 mm with an octahedral taper → silhouette perimeter
`P ≈ 1.1 mm` (a 0.48 × 0.13 rectangular sweep is 1.22; the octahedron's corners pull it in). Swept z-height
`H ≈ 0.13 mm` (the octahedron's vertical extent plus 1.5 µm of trochoid rise — nothing is sampled in z, so this part
is exact). Rim per pass = `1.1 × (h/2) × 0.13 = 0.0715·h` mm³.

Summed over the passes that actually cut — **413 at 1920 grains, 80 at 60** (`docs/long-programs.md:47`, `:50`):

| cell `h` | bound at 1920 grains | × signal (0.044690) | bound at 60 grains | × signal (0.018713) |
| ---: | ---: | ---: | ---: | ---: |
| 0.010 mm | 0.295 mm³ | **6.6×** | 0.0572 mm³ | **3.1×** |
| 0.002 mm | 0.059 mm³ | 1.32× | 0.0114 mm³ | 0.61× |
| 0.001 mm | 0.0295 mm³ | 0.66× | 0.0057 mm³ | 0.31× |
| 0.0005 mm | 0.0148 mm³ | 0.33× | 0.0029 mm³ | 0.15× |
| 0.00015 mm | 0.0045 mm³ | 0.10× | 0.00086 mm³ | 0.046× |

**So: at a 10 µm grid the step-3 bound is six times the entire removed volume of the covered wheel, and at 60 grains
it is three times. It does not fall below the signal until about 2 µm cells, and it does not reach 10 % until
0.15 µm cells — a 52-million-column map.** That is the plain reading, and it is the answer: **a total-volume
comparison cannot discriminate a correct preview from a broken one at any grid where the preview is fast.**

**[infer] But that reading charges the rim against more height than is at risk, and the honest figure is much
better.** `SamplingBound` uses the *whole* swept height `H ≈ 0.13 mm`. Only the part of the grain **below the surface
it meets** can produce a wrong removal, and that is the undeformed chip thickness — 42–54 µm at its maximum over the
run (`docs/long-programs.md:47-50`), and far less for most of the 413 passes, since a ground column is already
grooved. Taking a representative material height of 2 µm for the typical pass:

`413 × 1.1 × (h/2) × 0.002 = 0.908·h` mm³ → at h = 0.01: 9.1e-3 mm³ = **20 % of the signal**; at h = 0.002: 4 %.
With the random grain orientations `GrindingWheel.Random` produces (`GrindingWheel.cs:85-87`, a uniform Shoemake
quaternion), the 8 faces land at essentially random sub-cell offsets, so the rim errors are two-sided and partially
cancel — unlike the step-3 box case, where the faces were *deliberately* placed on grid lines and the error landed on
the bound to the last digit (`docs/long-programs.md`, "What the table checks").

**That mean bite depth of 2 µm is my assumption, not a number in the source.** It needs a measurement, and it is the
single number that decides whether the volume test works. The cheap way to get it: run the existing bench with
`--out` at 1920 grains and read `hulls / passes` (3.65) and the per-pass `RemovedMm3` distribution out of
`long-programs-results.json` (`Program.cs:96-121`). Nothing new has to be built for that.

**Answer to (e): the bound as written cannot discriminate; the *realised* error probably can, at 5–20 µm cells, but
nobody has measured the quantity that says so, and `SamplingBound` cannot express it because it does not know how much
of the grain is under the surface.**

### The surface profile is the better metric

**[read]** The bench already writes the profile as CSV: `grinding-{grains}-grains-surface.csv`, 301 samples across
the 0.8 mm ground width (`Program.cs:410`, `:431-441`), i.e. **2.67 µm per sample**, with Ra/Rz computed by
`SurfaceProfile.Roughness`. At 1920 grains **Ra = 5.06 µm, Rz = 25.2 µm** (`docs/long-programs.md:50`).

**[calc] What a preview needs to resolve that.** Ra 5 µm means the surface varies over ~10 µm peak-to-peak at the
RMS scale. A profile sampled at 2.67 µm (the exact's own grid) over a ±25 µm excursion is fine. A preview at a 10 µm
cell samples at 10 µm over a ±25 µm excursion — Ra would be recoverable to roughly ±30 %, and Rz badly aliased. At
2 µm cells the profile is comparable to the reference's own sampling. **From §3's table, 2 µm cells cost ~148 ms
against 5.82 s — a 39× win that still produces a usable profile.**

So the version of step 4 that works is: **compare the profile (mean depth, Ra, Rz) at ~2 µm cells, and use the
removed volume only as a sanity figure at ~10 % rather than as the pass/fail criterion.**

---

## 6. What has to be true before step 4 is worth starting

**Not problems — these are settled and step 4 can proceed on them:**

| Question | Answer |
| --- | --- |
| Does a grain fit under `MaxPlanes = 16`? | 8 faces, for every parameter combination. Exactly the same body the exact kernel cuts. **[read]** |
| Does splitting come up? | No. And no splitter exists — see §2. Moot for a grain, load-bearing for anything else. |
| How many steps does the covered wheel produce? | ~19 000 at 5 steps/pass (3 793 at the literal `StepsForRotation` call). 40× below what step 2 already measured. **[calc]** |
| Does the host become the bottleneck? | No. 0.45 ms of CSR at 24 ns/step, against a ~10⁶-step threshold in `docs/long-programs.md:114`. **[calc]** |
| Is the exact reference affordable at 1920? | Yes — 5.820 s, already measured, and its inputs are already public. **[read]** |

**Four things must change first. Three are small; the fourth is a decision about the test.**

1. **`ConvexStep.StepsForRotation` must be called with the orbital radius, and the API should make that hard to get
   wrong** (`ConvexStep.cs:87`, `:95`). With the tool's corner radius it returns **1** step per grain pass and a
   **37–38 µm** path error against a 20 µm depth of cut. This is the difference between a preview and a wrong answer,
   and it is a rename plus a doc line. **[calc]**

2. **An `m_z` story, because this program exercises the hazard step 3 never touched.** **[calc]** For the bench's
   wheel the world z-component of a grain's half-space normal is

   ```
   m_z = (R · n)_z = −(n_x sin θ + n_y cos θ)      [n = (n_x,n_y,n_z) the grain's own octahedral normal, /√3]
   ```

   derived from `R = R_x(−π/2) · R_z(θ) · R_grain`, `Pose3.Compose` order (`Pose2.cs:167`, `Motion.cs:97`), and
   `R_x(−π/2) = [[1,0,0],[0,0,1],[0,−1,0]]`. `m_z` depends only on `(n_x, n_y)`, so it is **the same for the ±n_z
   pair** — two of the eight planes share every value — and it passes through **zero four times per revolution**. The
   four roots are π apart, so at any spindle angle one of the four sign-pairs is within 0.785 rad of horizontal, and
   over a 0.175 rad pass window **roughly 9 % of each pass has two planes within 0.01 of horizontal**, with the worst
   case 0 % or 24 % depending on where the window falls. **[read]** `ConvexProfile.Span` has an exact `mz == 0f`
   branch (`:205`) and a bare `float inv = 1f / mz` (`:213`); `Where` (`:242`) then divides `−k/s` on quantities that
   are both large when a plane is near-horizontal, which puts a **wrong bound on `t`** — and every `Extremum` is then
   evaluated over the wrong range. **[read]** Every step-3 test rotates about z only, where `m_z = n_z` exactly;
   `Orientation3.AboutAxis` (`ConvexStep.cs:34`) has no caller in the repository. `CudaLongProgramsFindings.md`
   §10.6 and §12.3 both record this as untested. **Step 4 is the first program that hits it, on every grain, for
   hours of device time.** A test that drives a grain's half-spaces through horizontal and compares CUDA against
   `ConvexProfile.Span` is a prerequisite, not a nice-to-have. **[read]**

3. **A `Solid → ConvexTool` bridge in the product** (`ConvexDexelTests.cs:21-31` is the only one, and it is private
   to the test, and it does not re-origin the tool at the grain). Small, and step 4 cannot start without it.

4. **Decide what the accuracy claim is made against**, because at 10 µm cells the volume test is inside a bound that
   is 6.6× the signal. The workable form, from §5: **profile (mean depth, Ra, Rz) at ~2 µm cells** as the
   pass/fail, removed volume as a sanity figure, and `SamplingBound` re-derived with a material height rather than
   the full swept height. The alternative — a total-volume test — needs a 0.15 µm grid and gives up the entire
   advantage of the preview. **This is a design decision, not a bug fix, and it should be made before the kernel work
   starts rather than discovered by a failing test.**

**Two smaller things worth carrying into the design:**

- **[infer]** Whether a covered wheel overflows `K`. A fresh column has `n = 1` and a grain cutting fresh stock always
  reaches the top, so no roof is made. A grain that dips into a groove an earlier grain left cuts an interior interval
  and does — that is the 413-of-3793 population. `docs/todo.md` already records that the capacity guard is exercised
  by no test on either backend. Step 4 at 1920 grains is the first scene that would reach it; assert on `Overflows`
  with a non-zero precondition from the start.
- **[read]** The convex path goes through the same `dexel_subtract` (`zmap.cu:1109`) and therefore inherits the
  order-dependent capacity guard at `zmap.cu:1052`, as `docs/todo.md:77-79` already records. With 19 000 steps the
  binned and unbinned launches must still agree bit for bit, and `StepBins`' ascending CSR (`StepBins.cs:71-78`) is
  what makes that true.

---

## 7. Method, and what was not done

**Read this study.** `GrindingWheel.cs`, `GrindingSimulation.cs`, `ToolShape.cs`, `SpinningTool.cs`, `Process3.cs`,
`Solid.cs`, `Face3.cs`, `Plane3.cs`, `ConvexHull3.cs`, `Pose2.cs`, `Motion.cs`, `Tolerance.cs`, `DexelMap.cs`,
`ConvexTool.cs`, `ConvexProfile.cs`, `ConvexStep.cs`, `StepBins.cs`, `zmap.cu` (the convex/dexel region),
`ConvexDexelTests.cs`, `bench/Stykker.NanoCut.LongPrograms/Program.cs`, `docs/long-programs.md`, `docs/todo.md`,
`CudaLongProgramsFindings.md` §10–§12, `Step3KernelHandoff.md`.

**Not done, by instruction:** no build, no `dotnet test`, no nvcc, no run, no benchmark, no commit, no push, no
branch movement, no worktree, no patch, and no edit of anything in the main checkout. This file is the only thing
written.

**The four numbers that are mine and not the repository's**, each of which a 20-minute run would replace with a
measurement:

1. the pass window, 0.133–0.208 rad depending on the grain's random orientation (§3) — computed from
   `GrindingWheel.cs:113-120` and `GrindingSimulation.cs:211-217`, and cross-checked against the measured 1.97 passes
   per grain per revolution;
2. the kernel cost per (step, column) pair, 0.50 ns at 8 planes (§3) — extrapolated from step 3's 0.91 ns at 12
   planes;
3. the mean bite depth of ~2 µm (§5) — an assumption, and the one that decides whether the volume test works;
4. the rim perimeter `P ≈ 1.1 mm` and swept height `H ≈ 0.13 mm` (§5) — computed from the grain's size and the move,
   not from the code.
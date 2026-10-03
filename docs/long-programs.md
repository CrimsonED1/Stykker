# Long programs on the GPU: plan and log

Goal (user, 2026-10-03): long programs are the main topic, for example the whole generated gear or a fully covered
grinding wheel. The exact kernel stays the reference; the GPU gives a fast preview of the same process that can be
checked against it. Every step is logged at the end of this file with what was measured.

## Where the time goes today (exact kernel, CPU)

| Program | What it is | Exact time | Source |
| --- | --- | ---: | --- |
| Gear generation | `Process2.Cut`: 7-tooth rack rolls a 20-tooth gear (m = 2), 2560 roll steps, 209 089 swept convex pieces, sweep tolerance 30 nm | 36.6 s | `docs/processes.md`, kbench 2026-10-03 |
| Grinding, demo default | `GrindingSimulation`: 60 octahedral grains (150 µm) on trochoids, 3000 rpm, 0.8 mm feed | 0.38 s server, 1.7 s browser | `bench/README.md`, "Server mode" |
| Grinding, covered wheel | the same with thousands of grains | to be measured (step 1) | |

## Why the existing preview is not enough

The Z-map / dexel preview handles a **ball** moving on **straight** segments. The two long programs need:

1. **Any convex tool**: grains are octahedra (8 faces), the rack is a set of convex polygons.
2. **Motion with rotation**: grains run on trochoids (spindle rotation plus feed), the rack rolls (rotation plus
   translation of the gear frame).
3. **Many steps**: tens of thousands to millions of tool poses. Today every column tests every step, which is
   O(columns × steps); a column must only see the steps that can reach it.

## Model

A tool is a convex polytope given by its half-spaces (n·p ≤ d, at most 32 in the kernel). A program is a sequence of
rigid poses; between two consecutive poses the tool is swept by the translation only (rotation handled by sampling the
poses densely enough: the chord error of a rotated vertex between poses must stay below the preview tolerance). For one
column (3D: a vertical line; 2D: a horizontal row) the swept region of a pose pair meets the line in one interval,
found as a tiny linear program in (z, t): minimise and maximise z subject to n_i·(p(z) − t·w) ≤ d_i and 0 ≤ t ≤ 1.
That interval is subtracted from the column's intervals exactly as in the dexel map.

Steps are binned by tiles of columns: each step's bounding box (tool box swept by its translation) lists the tiles it
touches (CSR on the host), and a GPU block works on one tile with only those steps. Removal is a union, so the order of
steps inside a tile does not matter for the result (only the overflow counting can differ).

## Steps

- [ ] **1. Baselines.** A bench (`bench/Stykker.NanoCut.LongPrograms`) that runs the gear generation and grinding with
      growing grain counts on the exact kernel and writes time, removed area/volume and, for the gear, the profile.
- [ ] **2. Binning for the existing ball dexel/Z-map** (tiles + CSR), measured on a long synthetic ball program
      (e.g. a finishing pass with 0.05 mm steps, 10^5 to 10^6 steps): the speed-up that every later step relies on.
- [ ] **3. Convex tool + pose sequence** in the dexel kernel (CPU reference + CUDA), tests against the exact kernel on
      small cases (a box tool, an octahedron, a rotating tool).
- [ ] **4. Grinding preview**: grains of a `GrindingWheel` as convex tools on their trochoids; compare the removed
      volume and the surface profile with `GrindingSimulation` (exact) for 60 grains, then scale to a covered wheel.
- [ ] **5. Gear preview (2D)**: rows as columns, the rack's convex parts as tools on the rolling poses; compare the area
      with the exact 1231.252941 mm² and the flank with the ideal involute.
- [ ] **6. Into the server demo**: preview of the long program first, the exact result when it is ready.

## Log

Newest first.

### 2026-10-03, Claude

- Plan written. Working directly on `main` (user's decision).

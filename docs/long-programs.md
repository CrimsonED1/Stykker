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

- [x] **1. Baselines.** `bench/Stykker.NanoCut.LongPrograms` (new): the gear generation with a rack for z = 10, 20, 40
      and grinding with 60 … 1920 grains on the exact kernel, with time **and result** (area against the ideal
      involute gear, flank deviation, removed volume, Ra/Rz), plus the profiles as CSV under `--out`. Numbers above.
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

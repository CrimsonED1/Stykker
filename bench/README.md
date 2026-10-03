# Bench: NanoCut against other engines

A small, fair race between geometry kernels on the same input.

- **Scene file:** `scenes/*.json`, in mm. It defines a box, a ball tool, a path ("from where to where") and the number of
  steps per path segment.
- **Expansion:** `run.py` expands the scene once into explicit points on the 1 nm grid, so every engine gets identical
  input.
- **Each step:** the convex hull of the ball at the step's start and end points is subtracted from the workpiece. The
  steps run in order, like a cut.
- **What is timed:** only the cutting. Writing results is excluded.
- **States saved:** "save" lists the states written as STL (step numbers, `0` = stock, `-1` = final). The default is only
  the final state, so a long run does not write every intermediate state.

| Engine | Language | Arithmetic | Runner |
| --- | --- | --- | --- |
| `nanocut` | C# (.NET 10) | exact (nm grid, Int128/Int384 with filters) | `Stykker.NanoCut.Bench` |
| `manifoldsharp` | C# (port of Manifold) | double | `Stykker.NanoCut.Bench` |
| `cgal` | C++ (CGAL 5.6, Epeck, corefinement) | exact (lazy rationals, GMP) | `cgal/` (CMake) |
| `manifold` | C++ (manifold3d 3.5.4 via Python; one call per step) | double | `manifold/run.py` |

`manifoldsharp` against `manifold` is the same algorithm in C# and in C++, which shows the cost of the language alone.
`cgal` against `nanocut` compares two exact kernels.

## Run

```bash
sudo apt-get install libcgal-dev nlohmann-json3-dev libgmp-dev libmpfr-dev
pip install manifold3d numpy
dotnet build bench/Stykker.NanoCut.Bench -c Release
cmake -S bench/cgal -B bench/cgal/build -DCMAKE_BUILD_TYPE=Release && cmake --build bench/cgal/build
python3 bench/run.py bench/scenes/ball-small.json --repeat 3      # results in bench/out/<scene>/results.md
```

By default C# is measured warm: the scene repeats in the same process until it is steady (the rule is spelled out
below, round 6). `--cold` includes the JIT compilation instead.

## First results (2026-10-02, cloud container, 4 cores, best of runs)

**ball-small:** 16 steps, ball with 24 segments, 20 × 20 × 10 mm block.

| Engine | Language | Exact | Time (ms) | per step (ms) | Volume (mm³) | ΔV vs NanoCut |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| nanocut | C# | yes | 285 | 17.8 | 3603.390950167 | 0 |
| manifoldsharp | C# | no | 206 | 12.9 | 3603.390950167 | −4.6e-13 |
| cgal | C++ | yes | 113 | 7.0 | 3603.390950167 | −4.6e-13 |
| manifold | C++ | no | 26 | 1.6 | 3603.390950167 | −4.6e-13 |

**ball-medium:** 96 steps, ball with 48 segments, four-segment zig-zag over a 40 × 30 × 10 mm block.

| Engine | Language | Exact | Time (ms) | per step (ms) | Volume (mm³) | ΔV vs NanoCut |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| nanocut | C# | yes | 2390 | 24.9 | 10188.091000565 | 0 |
| manifoldsharp | C# | no | 1025 | 10.7 | 10188.091000565 | −5.5e-12 |
| cgal | C++ | yes | 3290 | 34.3 | 10188.091000565 | −5.5e-12 |
| manifold | C++ | no | 527 | 5.5 | 10188.091000565 | −5.5e-12 |

**pocket-large:** 876 steps. A zig-zag pocket with 7 overlapping passes 4 mm apart, 5 mm deep, over an 80 × 60 × 20 mm
block, ball with 48 segments, 0.75 mm per step. Sized so that the fastest engine needs about 10 s.

| Engine | Language | Exact | Time (s) | per step (ms) | Volume (mm³) | ΔV vs NanoCut | Triangles |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| nanocut | C# | yes | 30.9 | 35.3 | 84860.612636583 | 0 | 690 |
| manifoldsharp | C# | no | 31.1 | 35.5 | 84860.612636583 | −4.4e-11 | 23 774 |
| cgal | C++ | yes | 347.1 | 396.2 | 84860.612636583 | −4.4e-11 | 88 872 |
| manifold | C++ | no | 9.8 | 11.1 | 84860.612636583 | −4.4e-11 | 23 774 |

The triangle counts differ because NanoCut merges coplanar fragments (FaceMerge). The facets of successive hulls along a
pass become a few long strips, so the result is 30× smaller than Manifold's mesh and stays small as the cut goes on.

**What this shows:**

- **Results:** all four engines agree on the volume to 1e-10 mm³ (relative 5e-16).
- **The language alone** (same Manifold algorithm in C# and C++): C++ is 3.2× faster on the long task, 2× on the medium
  one.
- **Exact against exact:** NanoCut is 11× faster than CGAL on the long task. CGAL's lazy rationals and its growing mesh
  (89 k triangles) make each step slower; NanoCut stays on the nm grid with a compact result.
- **Price of exactness:** on the long task NanoCut is exactly as fast as the C# port of Manifold, which is not exact.
  It is 3.2× slower than C++ Manifold, and that gap is the language/runtime factor measured above.

So on long tasks a native port could gain about 3× at most. This is the bar for the C# optimisations in
`docs/native-speed-plan.md`.

## After the first optimisation round (2026-10-02)

pocket-large, same machine:

| Engine | Language | Exact | Time (s) | per step (ms) | Triangles |
| --- | --- | --- | ---: | ---: | ---: |
| **nanocut** | C# | yes | **18.4** (was 30.9) | 21.0 | 678 |
| manifoldsharp | C# | no | 30.8 | 35.1 | 23 774 |
| manifold | C++ | no | 9.4 | 10.7 | 23 774 |

Volumes are unchanged, identical to 1e-10 mm³. The measurements were taken with `perf`, run with
`DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0`. The EventPipe thread-time sampler is misleading here: it only
samples at safe points, so it shows GC polls and copy loops.

What helped:

| # | Change | Effect |
| --- | --- | --- |
| 1 | Convex hull: filtered plane test instead of a full exact orient3d per test | |
| 1 | Binary GCD for `Plane3.Canonical` | |
| 1 | BVH sort with a key array | |
| 2 | Boolean: no splitting by the plane of a face that cannot meet the fragment (strictly outside one of its edge planes) | −15 % time, −28 % allocations |
| 3 | Hull: coplanar neighbours found by orient3d and walked into one polygon; edge keys a·n + b | `(a << 32) \| b` hashes to a ^ b in .NET and collided massively |
| 4 | BVH queries on plain arrays | |
| 5 | Three-plane intersection as cross products in fixed 256-bit arithmetic instead of Cramer in generic Int384 | bit-identical, checked against Cramer on 20 000 random cases |

What did not help, and was reverted or kept out:

- GC settings (gen0 budget, server GC).
- Classifying connected groups of unsplit faces with one ray test: fewer ray tests, but the edge checks cost more than
  they saved.

## After the second optimisation round (2026-10-02)

pocket-large, same machine (4 cores):

| Engine | Language | Exact | Time (s) | per step (ms) | CPU (s, whole process) | Triangles |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| **nanocut** | C# | yes | **10.4** (was 18.4, first 30.9) | 11.9 | 35.9 | 698 |
| manifoldsharp | C# | no | 29.7 | 33.9 | 78.8 | 23 774 |
| manifold | C++ | no | 10.1 | 11.6 | 29.3 | 23 774 |

Volumes are identical to 3e-11 mm³. The CPU column counts the whole process. For the C# engines that includes the warm-up
run (the scene twice), so NanoCut uses about 18 CPU-s per run. C++ Manifold (built with TBB) runs on about 2.9 cores; the
earlier "2× behind" compared single-threaded NanoCut against multi-threaded Manifold.

| # | Change |
| --- | --- |
| 6 | Hull: linked conflict lists over arrays, no stored exact planes, smaller buffers |
| 7 | Winding ray along the axis that leaves the other solid's box soonest. For axis-aligned toolpaths the old +x ray ran lengthwise through every groove strip and hit near-parallel faces, which needed exact BigInteger fallbacks. |
| 8 | Face split: two crossing points in locals, pieces in exactly sized arrays |
| 9 | Lighter ray probes, single-entry edge index in FaceMerge, BVH nodes in a pre-sized array |
| 10 | Hull working buffers reused per thread: large-object allocations caused page faults and kernel page zeroing |
| 11 | Face classification in parallel, one buffer set per thread with results stored by index, so the output is deterministic. `SolidBoolean.MaxParallelism` controls it; the browser runs it sequentially. |

## After the fourth optimisation round (2026-10-02)

Round 3 is a separate effort by another agent; the numbers here compare round 4 with round 2 (commit 4384975).

pocket-large, same machine (4 cores):

| Engine | Language | Exact | Time (s) | per step (ms) | CPU (s, whole process) |
| --- | --- | --- | ---: | ---: | ---: |
| **nanocut** | C# | yes | **8.2** (rounds: 30.9 → 18.4 → 10.4 → 8.2) | 9.4 | 41.3 (incl. warm-up, about 20 per run) |
| manifold | C++ | no | 9.3 | 10.6 | 34.8 |

This round went through an external list of findings (`PerformanceFindings.md`) step by step, measuring with counters
before each change:

| Finding | Measured | Result |
| --- | --- | --- |
| B1: no float filter for side tests of grid points | 92k exact Int128 side tests per step on grid points (19k on exact points, 5.7 % fallback) | Filter using the cached face-plane doubles: about 1.2k exact evaluations per step left, Boolean −17 % |
| C2/C3, FaceMerge index, BVH arrays: allocations | `Probe` 13 %, FaceMerge entries 9 %, BVH arrays 19 % of the Boolean's allocations | Reused per-thread probe, edge index and fragment lists; BVH arrays from `ArrayPool`. 2.5 → 1.5 MB per step. |
| Hull face construction | about a third of the hull | Built in parallel by index: 5.1 → 3.9 ms |
| B2: hull fallback via orient3d | rare path | Done (simpler, same sign) |
| A1/A2: `FaceMerge.Join` | about 1 % of run time | Skipped. The proposed local test checked each vertex against the plane of its own edge (always 0), so it would accept non-convex merges. |
| D1: cache the BVH in the solid | the workpiece is a new solid after every cut | No effect on cut chains; not done |
| GC settings | measured in round 1 | No effect |

## Round 5: cut chains overlap tool construction and cutting (2026-10-02)

`Solid.SubtractInOrder(workpiece, toolFactories)` builds the next tool (here: the convex hull of the next step) on another
core while the current one is subtracted. The result is identical to subtracting one after another. Engine
`nanocut-pipeline` in `run.py`.

| Scene | nanocut | **nanocut-pipeline** | manifold C++ |
| --- | ---: | ---: | ---: |
| ball-small (16 steps) | 151 ms | 114 ms | **21 ms** |
| ball-medium (96 steps) | 832 ms | 639 ms | **469 ms** |
| pocket-profile (372 steps) | 3.12 s | **2.17 s** | 2.91 s |
| pocket-large (876 steps) | 7.52 s | **5.23 s** | 8.37 s |

Same CPU time, identical volumes. On pocket-large NanoCut is now 1.6× faster than C++ Manifold. Short tasks remain C++'s
domain, where fixed per-cut costs dominate.

What did not help:

| Attempt | NanoCut (pocket-profile) | Manifold (pocket-profile) | Why |
| --- | --- | --- | --- |
| Batching: unite k consecutive hulls, then cut once (`--batch k`) | 3.2 s → 3.9–4.4 s | 3.0 → 2.2 s | NanoCut's cost grows with interacting faces, and uniting nearly congruent hulls splits almost every face |
| Overlap for Manifold (`manifold-pipeline`) | – | 2.9 → 3.1 s | Its hull is cheap, and it already parallelises internally |

## Round 6: fixed costs per cut, and the JIT warm-up (2026-10-02)

| Change | Effect |
| --- | --- |
| Skip a split when the single face spanning the plane cannot meet the fragment (exact separation test per fragment) | Small scene Boolean 5.2 → 4.4 ms per cut. Geometry unchanged: all 396 differential workload results have identical exact volumes. |
| BVH build partitions around the median (quickselect) instead of sorting every range | pocket-profile Boolean 5.0 → 4.3–4.5 ms per cut |
| **Bench warm-up repeats the scene until it is steady** (was: once) | Short scenes were measuring JIT warm-up |

The warm-up finding: a single pass of ball-small (about 100 ms) leaves hot methods in unoptimised tier-0 code. With
`DOTNET_TieredCompilation=0` the same run takes 32 ms instead of 105 ms. Full optimisation from the start costs PGO on long
runs, though (pocket-profile 2.05 → 2.51 s). The bench now measures steady state, as in a long-running service. For
short-lived processes, ReadyToRun or `TieredCompilation=false` are the levers.

What "steady" means in the code (`Stykker.NanoCut.Bench`, `--warm`): repeat the scene until **three consecutive runs
agree within 10 % and at least five have run**, and give up when a single run costs more than 3 s. Not a fixed time
budget: a case that is itself slower than the budget would end the loop after one run and leave exactly the tier-0
code the warm-up exists to remove — measured on the grinding baseline, where the first pass reads 1.8 s, the second
938 ms and the warm case 202 ms.

Results, steady state, best of 3 (4 cores):

| Scene | nanocut-pipeline | nanocut | manifoldsharp | manifold C++ |
| --- | ---: | ---: | ---: | ---: |
| ball-small (16 steps) | 52 ms | 55 ms | 39 ms | **23 ms** |
| ball-medium (96 steps) | 607 ms | 784 ms | 908 ms | **451 ms** |
| pocket-profile (372 steps) | **1.94 s** | – | – | 2.83 s |
| pocket-large (876 steps) | **4.86 s** | – | – | 8.21 s |

Short tasks: C++ ahead by 1.3–2.3× (was up to 5.5×). Long tasks: NanoCut ahead by 1.5–1.7×.

## GPU prototype: Z-map preview (2026-10-03)

A preview of the same scene, not an exact solid: a height field over the workpiece (one height per cell) that every
tool step lowers. `src/Stykker.NanoCut.Gpu` holds the managed API with two backends – `Cpu` (C#, the reference) and
`Cuda` (`LibraryImport` into `src/Stykker.NanoCut.Gpu.Native/zmap.cu`, built by `build.ps1`, not part of `dotnet build`).
`bench/Stykker.NanoCut.GpuBench` measures it.

pocket-large, 876 steps, on the machine from `docs/gpu-findings.md` (RTX 5070 Ti, Ryzen 7 5800X3D), **not** comparable
to the cloud tables above:

| | Time for all 876 steps | per step | Remaining volume (mm³) |
| --- | ---: | ---: | ---: |
| Exact kernel (`nanocut`) | 4 655 ms | 5,31 ms | 84 860,612636583 |
| Z-map, CPU backend, 16 threads | 286 ms | 0,327 ms | 84 770,457226 |
| Z-map, CUDA backend (kernel only) | 1,5 ms | 0,0017 ms | 84 770,457226 |
| Z-map, CUDA backend (wall, with transfers) | 2,5 ms | 0,0029 ms | 84 770,457226 |

The deviation of −0,106 % splits into three causes: tool model −0,016 %, representation −0,090 %, grid +0,0005 %. The
representation error is a property of the height field and does not shrink with a finer grid, so 512 × 384 is enough for a
preview. Measurements, error decomposition and the recommendation: `docs/gpu-findings.md`. One-page result:
[`results-2026-10-03-gpu.html`](results-2026-10-03-gpu.html).

## GPU prototype, round 2: read-back on request, batch queries, the volume on the device (2026-10-03)

Same scene and machine. `IZMapBackend.Apply` takes a `ZMapReadBack`, so a caller that only wants a progress number after
a batch of steps never pays for the copy of the field, and `ZMap.BackendRemovedVolumeMm3` reduces the removed volume on
the device instead. 1024 × 768, warm, best of 5:

| | Kernel | Upload | Download | Wall |
| --- | ---: | ---: | ---: | ---: |
| CUDA, read-back after every call | 1,5 ms | 0,05 ms | 0,42 ms | 2,4 ms |
| CUDA, `ZMapReadBack.Never` + volume on the device | 1,544 ms | 0,05 ms | – | 1,665 ms |
| CPU backend | 266,2 ms | – | – | 266,2 ms |

The volume reduction inside that second row costs 0,1416 ms and returns 11 229,542774 mm³, against the 0,42 ms of copy
it replaces.

Batch queries (`SampleHeights`, `ProbeMaterial`), warm, best of 5, cold first call in brackets:

| Query | CPU | CUDA kernel | CUDA upload | CUDA download | CUDA wall | Agreement |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| heights at 1 000 000 points (16 MB in, 4 MB out) | 3,474 ms (4,007) | 0,064 ms | 1,658 ms | 0,428 ms | **2,187 ms** (3,489) | max Δh 1,9e-6 mm |
| penetration at 876 tool poses (14 KB in) | **0,039 ms** (0,443) | 0,004 ms | 0,044 ms | 0,028 ms | 0,116 ms (0,542) | Δ = 0 |

Three things came out of it:

- **The point query is transfer-bound, and the packing on the host was what made it lose.** `CudaBackend` used to copy
  the query into a flat float array before uploading (2,8 ms of packing to save 0,9 ms of transfer, 4,135 ms wall).
  `nc_zmap_sample` now takes the points as they are and `sample_d_kernel` subtracts the origin on the device: 2,187 ms,
  1,6× faster than the CPU, same answers to the last bit.
- **The pose query is round-trip latency and cannot win at this size** — 0,116 ms against 0,039 ms for the same 876 poses
  on the CPU, with a 0,004 ms kernel. Probe a few hundred poses on the CPU.
- **Pinned host memory buys nothing here**: 0,362 ms / 8,68 GB/s pinned against 0,362 ms / 8,70 GB/s pageable, measured
  back to back in one process, with the order flipping between processes. It is off by default.

And one methodological finding worth more than the numbers: every query measurement had to be warmed up first. The
first table was off by a factor of ten because the bench called each query exactly once on freshly allocated arrays
(first touch of a 4 MB destination costs more than the copy). The bench now warms up like the preview phase and prints
the cold call beside the warm one.

Details and the full reasoning: `docs/gpu-findings.md`. One-page result:
[`results-2026-10-03-gpu-round2.html`](results-2026-10-03-gpu-round2.html).

## GPU prototype, round 3: a query that lives on the device (2026-10-03)

Round 2 ended with the point query spending 2.086 of its 2.187 ms on the wire and a clear instruction: a caller that
asks about the same points again should not send them again. `IZMapQueryBackend.UploadPoints` hands a point set to the
backend once (`nc_pointset_create`, its own device allocation outside the map), and `nc_zmap_sample_set` runs the same
`sample_d_kernel` over it with no copy before the launch. Same scene and machine, 1024 × 768, 1 000 000 points, warm,
best of 5:

| 1 000 000 points | kernel | upload | download | reported wall | caller waits |
| --- | ---: | ---: | ---: | ---: | ---: |
| CUDA, points per call | 0,064 ms | 1,677 ms | 0,430 ms | 2,204 ms | 2,205 ms |
| CUDA, points kept on the device | 0,061 ms | 0,001 ms | 0,421 ms | **0,514 ms** | 0,515 ms |
| CPU, points per call | 3,457 ms | – | – | 3,457 ms | 5,873 ms |
| CPU, points kept in an array | 3,332 ms | – | – | 3,332 ms | **3,522 ms** |

The set costs 2,349 ms to upload once and saves 1,69 ms per call, so it pays for itself at the second query. The
answers are identical to the span query to the bit (`max Δh` against the CPU reference is 1,9e-6 mm either way), and
0,421 of the remaining 0,514 ms is the 4 MB answer coming back — the query now sits at the floor of what this link can
do with four bytes per pixel.

Two findings beyond the number:

- **The CPU backend was hiding 2,4 ms per call.** A `ref` struct cannot be captured by a parallel loop, so
  `CpuBackend` copies the span into an array before the loop, and that copy sits outside its stopwatch: 3,457 ms
  reported, 5,873 ms waited. Keeping the points in an array the set owns takes the call to 3,522 ms. The bench now
  times every query from outside and prints that next to the backend's own figure, because a timing that excludes work
  is not a wall time.
- **The height query did not take the per-map lock** that `IZMapQueryBackend` promises, although it shares the map's
  input and output buffers with the pose query, which does. Two concurrent queries on one map could overwrite each
  other. Fixed, and the interface now says which queries lock and why.

Reproduce:

```
dotnet build bench/Stykker.NanoCut.GpuBench -c Release
dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll `
    bench/out/pocket-large/expanded.json --grids 1024 --repeat 5 --queries --reference 84860.612636583
```

Details: `docs/gpu-findings.md`. One-page result:
[`results-2026-10-03-gpu-round3.html`](results-2026-10-03-gpu-round3.html).

## A toolpath of long moves (2026-10-03)

`scenes/pocket-large-g1.json` is `pocket-large` with one straight move per path segment, as a CAM program would send
it: 13 moves of 4 to 90 mm instead of 876 steps of 0.75 mm. The swept region is the same.

| Engine | pocket-large (876 steps) | pocket-large-g1 (13 moves) | Volume, both |
| --- | ---: | ---: | ---: |
| `nanocut` (exact) | 4162–4217 ms | 42 ms (min 38) | 84 860.612636583 mm³ |
| `manifoldsharp` | | 46 ms (min 41) | 84 860.612636583 mm³ |
| Z-map CPU, 1024 × 768 | 255–268 ms | 4.4 ms | 84 770.457226 mm³ |
| Z-map CUDA, 1024 × 768, wall | 2.4 ms | 0.8 ms | 84 770.457226 mm³ |

RTX 5070 Ti, 16 logical processors, Windows 11, warm. The pocket-large-g1 times of the exact engines are the median
of 30 runs (`--repeat 30`): a scene this short scatters by a factor of three to four between single runs (38 to 170 ms
for NanoCut, with any parallelism, also with `--par 1`), so a median of three can land anywhere in that range; an
earlier version of this table gave 72–113 ms from too few runs. The Z-map times are the best of 30. The exact kernel
costs per cut, not per length, so long moves are what makes it fast; the Z-map numbers need the long-step fix of 2026-10-03 (`docs/gpu-findings.md`, "Independent
verification") to give the same volume for both scenes.

## Server mode

The same demo pages in two hosts: `samples/Stykker.NanoCut.Demo` (Blazor WebAssembly, published with AOT as for
GitHub Pages, geometry computed in the browser) and `samples/Stykker.NanoCut.Server` (Blazor Server, `-c Release`,
geometry computed natively on the server with all cores, the browser only displays). Same machine for both: AMD Ryzen 7
5800X3D (8 cores, 16 logical processors), 32 GB, Windows 11; browser: the Chromium-based browser pane of the Claude
desktop app, on the same machine as the server, so the network is loopback.

Wall time is measured in the page from the click to the last change of the page (the DOM quiet for 0.8 s), so it
includes the transfer of the meshes to the viewer; "compute" is the page's own figure where it shows one. Second run of
each (the first differs by JIT on the server and by little in the browser), default parameters unless noted.

| Page | Browser (WASM, AOT) wall | Server wall | Faster | Browser compute | Server compute |
| --- | ---: | ---: | ---: | ---: | ---: |
| Spinning disc, saw blade, feed length 30 mm (40 frames) | 20 449 ms | 2 546 ms | 8.0× | 20 354 ms | 2 497 ms |
| Grinding grains | 1 719 ms | 423 ms | 4.1× | 1 670 ms | 379 ms |
| Profiles, spur gear, axial section (part built on every click) | 242 ms | 47 ms | 5.1× | 42 ms¹ | 9 ms¹ |
| 3-axis mill, example G-code program (11 moves, fresh workpiece) | 1 674 ms | 288 ms | 5.8× | | |
| Milling (ball-nose pocket) scene | 1 306 ms | 199 ms | 6.6× | 1 205 ms | 135 ms |

¹ The section alone; the wall time also contains building the gear.

The server is 4 to 8 times faster on every page (up to 9× on the computation alone). Two things add up: native code
against WebAssembly, and the cores, since the browser build runs on one thread while the kernel's parallel parts use
all 16 on the server; which share is which was not separated. Wall minus compute is 40 to 65 ms per click on the server
(circuit round trip, meshes over the socket, drawing) against 50 to 100 ms in the browser (drawing alone), so the
transfer costs nothing that matters at these mesh sizes, but on a page that computes for only a few milliseconds there
is little to gain. The first run on the server is slower by the JIT (the milling scene 1165 ms cold against 135 ms
warm); the browser build is compiled ahead of time and does not have that.

Several users share one queue (`Compute:MaxConcurrentJobs`, default 2): every job already uses all cores, so more jobs
at once only make each slower. A page that is left cancels its waiting jobs, and Stop ends a running computation after
the current step (measured: 103 ms after the click on the long spinning cut). `/api/compute` reports the jobs, the
queue and the time spent waiting.

```bash
dotnet run -c Release --project samples/Stykker.NanoCut.Server        # http://localhost:5180
dotnet publish samples/Stykker.NanoCut.Demo -c Release -p:Aot=true -o out   # the browser build, serve out/wwwroot
```

## Dexel preview (2026-10-03)

`DexelMap` keeps up to K material intervals per column instead of one height, so the roof of material above a shallow
tool stays (details in `docs/gpu-findings.md`, "Dexel preview"). `pocket-large`, K = 4, RTX 5070 Ti, best of 3:

| Grid | Remaining (CPU = CUDA) | against exact | CUDA wall | CPU wall |
| --- | ---: | ---: | ---: | ---: |
| 1024 × 768 | 84 847.733837 mm³ | −0.015 % | 2.9 ms | 261 ms |
| 4096 × 3072 | 84 846.915579 mm³ | −0.016 % | 36.3 ms | 4 371 ms |

The Z-map leaves 84 770.457226 mm³ (−0.106 %) at 1024 × 768; what is left for the dexel map is the 48-segment ball
against a true sphere (−0.016 %).

```bash
dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll bench/out/pocket-large/expanded.json --dexel 4 --grids 1024,4096 --backends cpu,cuda --reference 84860.612636583
```

## Long programs: baselines on the exact kernel (2026-10-03)

`bench/Stykker.NanoCut.LongPrograms` is the reference the long-program preview is measured against (plan and log:
`docs/long-programs.md`). It runs the two long programs as a **program**, not as a step loop, and writes the result next
to the time: a preview is only as good as the number it is compared with.

AMD Ryzen 7 5800X3D (8 cores, 16 threads), 32 GB, Windows 11, .NET 10, single run. The grinding cases are warmed up
until three consecutive runs agree within 10 % (the rule of `Stykker.NanoCut.Bench --warm`; one pass is not enough —
the 60-grain case reads 938 ms after one pass and 200 ms after five) and are measured **before** the gear cases, see
the note under the tables.

**Gear generation**, `Process2.Cut` with a 7-tooth rack rolling on the blank, m = 2 mm, `Tolerance.Default`:

| z | Cut | Extrude | Area | Ideal involute | Δ | Contours | Vertices | Roll steps | Pieces | Flank |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 28.8 s | 0.9 s | 296.905130 mm² | 297.949 mm² | −0.35 % | 76 | 81 653 | 2560 | 224 988 | – † |
| 20 | 34.7 s | 0.8 s | 1231.252941 mm² | 1226.103 mm² | +0.42 % | 69 | 107 328 | 2560 | 209 089 | 5.4 nm |
| 40 | 147.6 s | 2.1 s | 4985.752597 mm² | 4982.782 mm² | +0.06 % | 53 | 243 515 | 5120 | 398 178 | 2.2 nm |

z = 20 is the case of `docs/processes.md` (same area, same 209 089 pieces, same 5.4 nm), so the two documents agree.
† below z = 2/sin²α = 17.1 a generated gear is undercut, so its flank is not the involute and the column says nothing;
the area still matches the ideal gear, so the profile itself is right.

**Grinding**, `GrindingSimulation` with the demo's parameters: workpiece 1.5 × 0.8 × 0.3 mm, Ø20 × 1 mm wheel,
150 µm grains, protrusion 40 ± 15 µm, 3000 rpm, 20 mm/s over 0.8 mm, 20 µm depth of cut, seed 1, 2 revolutions:

| Grains | Wall | Removed | of stock | Active grains | Cutting passes | Passes | Hulls | Chip | Ra | Rz |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 0.200 s | 0.018713 mm³ | 5.2 % | 42/60 | 80 | 118 | 707 | 46.2 µm | 9.69 µm | 55.5 µm |
| 240 | 0.924 s | 0.029406 mm³ | 8.2 % | 113/240 | 204 | 476 | 2589 | 43.0 µm | 6.96 µm | 43.1 µm |
| 960 | 2.979 s | 0.040148 mm³ | 11.2 % | 186/960 | 337 | 1897 | 8033 | 42.3 µm | 4.72 µm | 29.2 µm |
| 1920 | 5.820 s | 0.044690 mm³ | 12.4 % | 240/1920 | 413 | 3793 | 13 841 | 54.1 µm | 5.06 µm | 25.2 µm |

The 60-grain row is the same scene as the "Grinding grains" page of the "Server mode" table above, which measures
0.38 s there (server, compute only) against 0.200 s here: same work, no frames, no logging, no page around it.

**The order of the two programs is part of the measurement.** After the 2D gear kernel has run, the same 3D grinding
case is slower in that process: 200 ms in a fresh one, 352 ms after a single 30 s gear case, 885 ms after the three gear
cases above. Not the GC mode (server GC: 355 ms), not tiered compilation (`DOTNET_TieredCompilation=0`: 411 ms), not the
machine (a fresh process right after the same load: 198 ms) and not how long the process has been running (5.8 s of 3D
work beforehand: 185 ms). The cause is open (`docs/todo.md`); the bench therefore measures grinding first, and the gear
numbers are the same either way (28.8 s against 28.8 … 30.5 s across the runs).

What the two tables say for the preview that comes next: the exact kernel pays a boolean per swept convex piece
(398 178 pieces at z = 40, 13 841 hulls on a covered wheel), and it keeps paying as the grains pile up even though the
result converges — 29× more time and 19.6× more hulls from 60 to 1920 grains, against 2.4× more removed volume and a
roughness that stops improving after 960 grains (Ra 4.72 → 5.06 µm). At 1920 grains only 413 of 3793 passes still remove
anything.

```bash
dotnet build bench/Stykker.NanoCut.LongPrograms -c Release
dotnet bench/Stykker.NanoCut.LongPrograms/bin/Release/net10.0/Stykker.NanoCut.LongPrograms.dll all --teeth 10,20,40 --grains 60,240,960,1920 --out bench/out/long-programs
```

`--out` writes `long-programs-results.json`, `long-programs-results.md` and the references as CSV: the gear profile
per case (contour, index, x, y) and the ground surface across the width per grain count (y, z at x = 0.40 mm). Those
files are what a preview of the same program is compared against.

One-page result: [`results-2026-10-03-long-programs.html`](results-2026-10-03-long-programs.html).


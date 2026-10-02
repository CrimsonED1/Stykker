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

By default C# is measured warm: the whole scene runs once in the same process before the timed run. `--cold` includes
the JIT compilation instead.

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


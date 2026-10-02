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

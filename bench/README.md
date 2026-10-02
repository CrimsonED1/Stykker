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

**What this shows (first look, two small scenes only):**

- **Results:** all four engines agree on the volume to 1e-11 mm³.
- **The language alone** (same Manifold algorithm in C# and C++): C++ is about 2× faster on the medium scene, and about
  8× on the tiny scene, where fixed costs dominate.
- **Exact against exact:** NanoCut beats CGAL as the part gets more complex. CGAL is 2.5× faster on the small scene, but
  NanoCut is 1.4× faster on the medium one. CGAL's lazy rationals grow with every cut; NanoCut stays on the nm grid.
- **Price of exactness:** NanoCut is about 4.5× slower than C++ Manifold and about 2.3× slower than C# Manifold.

So the language is worth up to about 2× at this size. The rest is algorithm and arithmetic. Next: bigger scenes and a
profile (see `docs/native-speed-plan.md`).

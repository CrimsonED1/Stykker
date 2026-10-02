# Performance work: overview

This is a summary of the optimisation rounds, with how each was verified and what was learned. Measurements per round
are in [bench/README.md](../bench/README.md). The one-page results are
[bench/results-2026-10-02-round4.html](../bench/results-2026-10-02-round4.html) (cloud),
[bench/results-2026-10-02.html](../bench/results-2026-10-02.html) (local Ryzen run) and
[bench/results-2026-10-03-gpu.html](../bench/results-2026-10-03-gpu.html) (GPU preview on an RTX 5070 Ti). The question
"Rust or C?" is covered in [native-speed-plan.md](native-speed-plan.md).

## Benchmark

`bench/` races NanoCut against Manifold (C++ and its C# port) and CGAL (C++, exact) on identical input:

- **Scenes:** a block, a ball tool and a path. `run.py` expands every scene to points on the 1 nm grid.
- **Each step:** the convex hull of the ball at the step's start and end is subtracted from the block.
- **Measured:** only the cutting, best of 3 runs. C# is measured warm (steady state), and the CPU time of each engine is
  recorded.

pocket-large (876 steps, 4-core container):

| State | NanoCut | C++ Manifold |
| --- | ---: | ---: |
| Start | 30.9 s | 9.8 s |
| Round 1 | 18.4 s | |
| Round 2 | 10.4 s | 10.1 s |
| Round 4 | 8.2 s | 9.3 s |
| Round 5 | 5.2 s | 8.4 s |
| Round 6 | **4.9 s** | 8.2 s |

Short scenes (16–96 steps) remain faster with C++ by 1.3–2.3×. Volumes agree to 1e-10 mm³ in every run. NanoCut's
result keeps about 700 triangles against Manifold's 23 774.

## What changed (library)

Round 3 is a separate effort by another agent; the round names will be renumbered at the end. Snapshots of each round are
on the branches `perf-round-2`, `perf-round-4`, `perf-round-5` and `perf-round-6`.

| Round | Change | Where |
| --- | --- | --- |
| 1 | Filtered plane test in the hull; binary GCD for `Plane3.Canonical`; BVH sort with a key array | `ConvexHull3`, `Plane3`, `Bvh3` |
| 1 | No split by the plane of a face that cannot meet the face being split (strict separation by an edge plane) | `SolidBoolean` |
| 1 | Hull groups coplanar neighbours and walks their boundary; well-spread edge keys | `ConvexHull3` |
| 1 | Three-plane intersection as cross products in fixed 256-bit arithmetic (bit-identical to Cramer) | `Plane3.IntersectFast`, `Int256` |
| 2 | Hull: linked conflict lists, smaller buffers, per-thread scratch | `ConvexHull3` |
| 2 | Winding ray along the axis that leaves the other solid's box soonest (six directions) | `SolidBoolean.RayWinding` |
| 2 | Split with two crossing points in locals and exactly sized arrays; lighter ray probes | `Face3`, `SolidBoolean` |
| 2 | Parallel face classification: per-thread buffers, results stored by index, so the output is deterministic | `SolidBoolean.Process` |
| 4 | Float filter for side tests of grid points, using the cached face-plane doubles | `Point3.SideOf` |
| 4 | Reused probes, merge index and fragment lists; BVH arrays from `ArrayPool`; hull faces built in parallel | several |
| 5 | **Cut chains:** `Solid.SubtractInOrder` builds the next tool while the current one is subtracted | `Solid` |
| 5 | `Solid.UnionAll` (balanced tree, parallel pairs); public `Solid.MaxParallelism` | `Solid` |
| 6 | No split of a fragment by the plane of a single face that cannot meet that fragment | `SolidBoolean` |
| 6 | BVH median by introselect instead of sorting | `Bvh3` |

New public API:

```csharp
Solid.MaxParallelism = Environment.ProcessorCount;   // 1 = sequential; results never depend on it
var work = Solid.SubtractInOrder(block, toolFactories); // next tool is built while the current one is cut
var tool = Solid.UnionAll(pieces);                    // balanced union tree
```

## How it was verified

After every round, an independent review agent looked for correctness bugs. It wrote adversarial tests, which are now
part of the suite: `tests/Stykker.NanoCut.Tests/OptimizationVerification*Tests.cs`.

- **Differential tests against the commit before each round:**
  - exact rational volumes and first moments from two origins;
  - for round 6, also exact symmetric differences (A − R and R − A both empty) for 1 076 results;
  - an offline fuzz run of 160 000 Booleans, compared with the previous commit.
- **Filter bounds proven and fuzzed** against BigInteger, including coordinates near ±2^31 and nearly cancelling terms.
- **Determinism:** identical results at parallelism 1, 2, 4, 8 and 64, with concurrent Booleans on shared solids.
- **Edge cases fixed along the way:**
  - The hull's filter bound now uses the actual input range, and coordinates beyond 2^40 are rejected with an error.
  - `Plane3.Canonical` no longer flips the sign when the gcd is 2^127.
  - The same exception type is thrown sequentially and in parallel (`AggregateException` is unwrapped).
  - `Face3.Split` rejects non-convex input.
  - Introselect replaces plain quickselect.

All 177 tests and the 4 oracle tests are green.

## GPU prototype: Z-map preview

A separate effort on branch `feature/server-gpu`, and deliberately not part of the exact kernel: the preview is a height
field (one height per grid cell) that every ball step lowers, and the exact 3D kernel gets no GPU dependency at all. Two
interchangeable backends sit behind `IZMapBackend` – `Cpu` in plain C# as the reference, `Cuda` through `LibraryImport`
into an optional `nvcc`-built `nanocut_gpu`, which CI never builds.

pocket-large, 876 steps in one call, warm, best of 3, on an RTX 5070 Ti / Ryzen 7 5800X3D (local numbers, not comparable
to the 4-core container tables above):

| | Time for all 876 steps | per step | Remaining volume (mm³) |
| --- | ---: | ---: | ---: |
| Exact kernel | 4 655 ms | 5,31 ms | 84 860,612636583 |
| Z-map, CPU backend, 16 threads | 286 ms | 0,327 ms | 84 770,457226 |
| Z-map, CUDA, kernel only | 1,5 ms | 0,0017 ms | 84 770,457226 |
| Z-map, CUDA, wall with transfers | 2,5 ms | 0,0029 ms | 84 770,457226 |

**How it was verified:** the CPU backend against analytic volumes (a straight capsule into a flat block), and the CUDA
backend against the CPU backend cell by cell. That comparison found a real bug the CPU-only tests had missed – for a
horizontal step the discriminant of the stationary point is exactly zero in theory, but computed as the difference of two
float32 products of size 4·d²·w2² it came out slightly negative about half the time and the step was dropped for that
cell. Both backends had it; both discriminants are clamped at zero now, and
`GpuZMapTests.LongHorizontalStepIsNotDroppedByFloatCancellation` pins it.

The −0,106 % deviation splits into three independent causes: tool model −0,016 %, representation −0,090 %, grid
+0,0005 %. The representation error is a property of the height field (it cannot keep the thin roof of material above the
tool's crown) and does not shrink with a finer grid – the analytic integral gives 76,825 mm³ against 76,713 mm³ measured.
So 512 × 384 is enough for a preview, and a stock-remainder check must not be decided on one. Full measurements and the
recommendation (worth it for a preview a person watches, not for the exact result): [gpu-findings.md](gpu-findings.md).

## Lessons learned

- **Measure with a real CPU profiler.** The .NET EventPipe thread-time sampler only samples at safe points, so it showed
  GC polls and copy loops as hot spots. `perf` gives the real picture. Run it with
  `DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0`, the second setting being needed so that JIT code gets
  symbols.
- **Large-object allocations are expensive in a hidden way.** Buffers over 85 KB caused page faults and kernel page
  zeroing: up to a third of the hull time went to `clear_page`. Reuse (per-thread scratch, `ArrayPool`) fixed it; GC
  settings did not.
- **`Int64` hashing folds the two halves with XOR,** so an edge key `(a << 32) | b` collided massively. `a·n + b` does
  not.
- **The direction of the test ray matters for CNC geometry.** A ray along the feed direction runs lengthwise through
  every groove strip and meets near-parallel faces, each of which needs an exact fallback.
- **Batching tools is not free.** Uniting k nearly congruent hulls before cutting made NanoCut slower, because every face
  interacts. Manifold profits from it. Overlapping tool construction with cutting helped NanoCut by 30 % and Manifold
  not at all.
- **Short benchmarks measure the JIT.** One pass of a 100 ms scene still runs tier-0 code: 105 ms against 32 ms fully
  optimised. Disabling tiering costs PGO on long runs (+20 %). Bench in steady state, and for short-lived processes
  consider ReadyToRun.
- **Fair comparison:** C++ Manifold uses TBB (about 2.9 cores), so a single-threaded NanoCut against it compares
  different things.
- **A second implementation is a test.** Writing the same minimum over a segment in C# and in CUDA C and comparing cell
  by cell found a float-cancellation bug that the CPU-only tests could not see. Compare two implementations wherever the
  cost of the second one is low.
- **On a GPU, the read-back can dominate the kernel.** Per-step calls on the Z-map cost 450× the batched call, but only
  5.9 ms of that is launch overhead (about 5 µs per launch) – the rest is 876 copies of 3 MB because the call returns the
  height field. Who decides whether data comes back is an API decision, not a kernel one.
- **A fixed-size representation sets an accuracy floor.** One height per column cannot represent overhangs, so no grid
  size buys that error down. Measure the floor before buying resolution.

## Open

- Fixed costs per cut for short tasks: C++ is still 1.3–2.3× faster there.
- Exact face sweep for 3D rotations, which today use hulls of poses and small steps (see [processes.md](processes.md)).
- Server mode: the second half of the plan behind [server-gpu-plan.md](server-gpu-plan.md), still unbuilt. The GPU half is
  done and measured above; what is still missing there is the caller's choice about read-back, batch queries and pinned
  host memory. The exact kernel stays on the CPU either way.

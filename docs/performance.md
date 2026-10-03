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

All 185 tests and the 4 oracle tests are green, including the GPU tests, which run against the `nvcc`-built library when
it is present and against the CPU backend only when it is not.

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

### Round 2: the caller decides about the read-back, batch queries, the volume on the device

The three items the first round left open, all built and measured. `IZMapBackend.Apply(map, steps, readBack)` takes a
`ZMapReadBack`, so with `Never` the height field stays on the device, `ZMap.IsHeightsCurrent` goes false and every host
read of a stale field throws instead of answering wrongly; `ZMap.ReadHeights()` brings it back on demand, and
`ZMap.BackendRemovedVolumeMm3` reduces the removed volume *on the device* (0,1416 ms for 11 229,542774 mm³, agreeing with
the host sum to the last digit). 1024 × 768, warm, best of 5:

| 876 steps at 1024 × 768 | kernel | upload | download | wall |
| --- | ---: | ---: | ---: | ---: |
| CUDA, read-back after every call | 1,5 ms | 0,05 ms | 0,42 ms | 2,4 ms |
| CUDA, `ZMapReadBack.Never` + volume on the device | 1,544 ms | 0,05 ms | – | 1,665 ms |

`IZMapQueryBackend` answers batch queries on both backends — heights at N points, ball penetration at N poses — so the
GPU result stays checkable cell by cell:

| Query | CPU | CUDA kernel | CUDA upload | CUDA download | CUDA wall | Agreement |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| heights at 1 000 000 points (16 MB in, 4 MB out) | 3,474 ms | 0,064 ms | 1,658 ms | 0,428 ms | **2,187 ms** | max Δh 1,9e-6 mm |
| penetration at 876 tool poses (14 KB in) | **0,039 ms** | 0,004 ms | 0,044 ms | 0,028 ms | 0,116 ms | Δ = 0 |

The point query wins, but only after the host stopped preparing it: packing the query into a flat float array cost
2,8 ms to save 0,9 ms of transfer and put the wall at 4,135 ms — *slower than the CPU*. `nc_zmap_sample` now takes the
points as they are and the kernel does that arithmetic (0,064 ms), which is where the 2,187 ms come from. The CPU
figure in that table is its parallel loop alone; the call around it costs 5,873 ms, because `CpuBackend` copies the
span into an array before the loop and does not count that copy. Round 3 below measures both. The pose query is the
opposite and cannot be rescued at this size — 0,072 ms of round-trip latency against a 0,004 ms kernel, so a few
hundred poses go to the CPU. Pinned host memory, which round 1 predicted would roughly double the download, buys
nothing here: 8,68 GB/s pinned against 8,70 GB/s pageable, measured back to back in one process with the order
flipping between processes, so it is off by default.

One-page result: [results-2026-10-03-gpu-round2.html](../bench/results-2026-10-03-gpu-round2.html). The full reasoning,
including why the first version of that table was wrong by a factor of ten: [gpu-findings.md](gpu-findings.md).

### Round 3: a query that lives on the device

Round 2 ended with the point query spending 2,086 of its 2,187 ms on the wire and named the fix: a caller that asks
about the *same* points again should not send them again. `IZMapQueryBackend.UploadPoints` hands a point set to the
backend once (`nc_pointset_create`, its own device allocation, so it outlives the query and works with any map), and
`nc_zmap_sample_set` runs the same `sample_d_kernel` over it with no copy before the launch. The answers are the
answers of the span query, to the bit — a test asserts that on both backends.

1 000 000 points at 1024 × 768, warm, best of 5, pocket-large on the same RTX 5070 Ti:

| 1 000 000 points | kernel | upload | download | reported wall | what the caller waits for |
| --- | ---: | ---: | ---: | ---: | ---: |
| CUDA, points per call | 0,064 ms | 1,677 ms | 0,430 ms | 2,204 ms | 2,205 ms |
| CUDA, points kept on the device | 0,061 ms | 0,001 ms | 0,421 ms | **0,514 ms** | 0,515 ms |
| CPU, points per call | 3,457 ms | – | – | 3,457 ms | 5,873 ms |
| CPU, points kept in an array | 3,332 ms | – | – | 3,332 ms | **3,522 ms** |

**4,3× on the GPU and 11× against a CPU call**, and the set pays for itself at the second query: 2,349 ms once against
1,69 ms saved per call. What is left is the download — 0,421 of the 0,514 ms is the 4 MB answer over a link that tops
out at 8,7 GB/s — so the query now sits at the floor of what four bytes per pixel can cost on this hardware.

The CPU row is the one nobody predicted. A `ref` struct cannot be captured by a parallel loop, so `CpuBackend` copies
the span into an array on every call, and that copy sits *outside* its stopwatch: the query reported 3,457 ms and cost
the caller 5,873 ms. Keeping the points in an array the set owns takes the call to 3,522 ms — a 1,7× win that the
reported number never moved for. The bench now times every query from outside and prints that beside the backend's own
figure.

Two defects fell out of the same work: the height query shared the map's query buffers with the pose query but, unlike
the pose query, did not take the per-map lock that the interface promises, so two concurrent queries on one map could
overwrite each other; and the CPU backend reported a loop it had already finished copying for. One-page result:
[results-2026-10-03-gpu-round3.html](../bench/results-2026-10-03-gpu-round3.html). Details: [gpu-findings.md](gpu-findings.md).

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
- **A first call is not a measurement.** The first query table was wrong by a factor of ten: the bench called each query
  exactly once, on arrays the collector had never touched, and faulting in a fresh 4 MB destination cost more than the
  copy (6,7 ms of "download" that was really page faults). Warm up, then take the best of N, and print the cold number
  beside it.
- **Do not prepare a query on the host if a kernel can do it for free.** Packing a million points into a flat float
  array cost 2,8 ms to save 0,9 ms of transfer and made the GPU query slower than the CPU one. The kernel had 0,064 ms
  of headroom to do the same arithmetic. Bandwidth saved on the wire is not free — it is paid for in the loop that
  saves it.
- **A timing that excludes work is not a wall time.** `CpuBackend` reported 3,457 ms for a query the caller waited
  5,873 ms for, because the span-to-array copy sat outside its stopwatch. The same missing copy was worth 2,4 ms per
  call and no reported number ever showed it. Time the call from outside as well as inside, and print both.
- **Input that crosses the bus more than once should cross it once.** A point set the backend keeps took the million
  point query from 2,204 ms to 514 ms, and the answers were identical to the bit. When a workload repeats its input,
  the API question "who owns this buffer" is worth as much as the kernel.
- **Promising serialisation in an interface is a promise the code has to keep.** Two queries on one map shared their
  input and output buffers; only one of them took the lock. The race is invisible in a single-threaded test and shows
  up as an occasional wrong answer in production.
- **Small queries lose to round-trip latency, not to bandwidth.** 876 poses are 14 KB and 3,5 KB; the kernel takes
  0,004 ms, the two copies 0,072 ms, and the CPU does the same work in 0,039 ms. Know where the fixed cost of a device
  round trip puts the break-even point before moving work onto it.
- **On a planar sweep, keep the piece order the geometry wants.** Uniting the pieces interval by interval made the union
  7× cheaper and the subtract 2× more expensive, because each batch stopped being one contiguous ribbon and left
  degenerate loops in the result. And the pose parts are load-bearing under rotation: dropping them keeps the area
  exact to nine decimals and costs 206 µm of flank error. Measure the cost of both halves, not one
  ([processes.md](processes.md)).

## Open

- Fixed costs per cut for short tasks: C++ is still 1.3–2.3× faster there.
- Exact face sweep for 3D rotations, which today use hulls of poses and small steps (see [processes.md](processes.md)).
- Server mode: the second half of the plan behind [server-gpu-plan.md](server-gpu-plan.md), still unbuilt. The GPU half is
  done and measured above, including the caller's choice about read-back, the batch queries, the point set that stays
  on the device and the pinned-memory question. What is still missing on the GPU side is a cheaper answer — the
  download is 0,42 of the 0,51 ms the query costs, so a half-precision picture or a renderer that consumes the field
  on the device is the next thing to try — and a partial read-back for a caller that wants a picture of part of the
  stock while the cut runs. The exact kernel stays on the CPU either way.
- The gear case is analysed but not fixed: `Process2.Cut` takes 41.0 s on the rack, spending 17,7 s in the unions of
  the pose parts and 23 s in the subtracts, and the win has to come from grouping the pieces by the region they remove,
  not from the piece order or the batch size ([processes.md](processes.md)).

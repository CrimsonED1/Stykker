# GPU round 2 and the gear case: what was measured, what it means

Branch `feature/server-gpu`, 3 October 2026. This is the self-contained write-up of the work; the long versions live in
`docs/gpu-findings.md` (round 2 section) and `docs/processes.md` ("The gear case: where the 41 s go"), the one-page
result in `bench/results-2026-10-03-gpu-round2.html`.

Two questions were on the table: finish everything still open on CUDA/GPU, and analyse the gear case, which is the
slowest thing in the suite. Both are done. The headline results:

- **A batch query over a million points is now 1.6× faster on the GPU than on the CPU** (2.187 ms against 3.474 ms).
  It was slower before. The reason was not the GPU.
- **A batch query over a few hundred tool poses is 3× faster on the CPU** (0.039 ms against 0.116 ms) and cannot be
  rescued: it is round-trip latency, not work.
- **Pinned host memory is worth nothing on this machine** (8.68 against 8.70 GB/s, measured back to back).
- **The first query table in the docs was wrong by a factor of ten**, because the bench measured each query exactly
  once on freshly allocated arrays. Warming up first is now part of the bench.
- **The gear case is a local optimum.** Nine variants were built and timed; the shipped piece order wins, and the win
  has to come from a different grouping, not from a different order or batch size.

Machine: AMD Ryzen 7 5800X3D (8 cores, 16 threads), NVIDIA GeForce RTX 5070 Ti 16 275 MiB (sm_120), driver 617.14,
CUDA 13.4, Windows 11, .NET 10. Scene `pocket-large`, 876 steps, 1024 × 768 cells. All query numbers below are warm
and best of 5.

---

## Part 1: GPU round 2

### 1.1 What was built

| Piece | What it does |
| --- | --- |
| `ZMapReadBack` (`src/Stykker.NanoCut.Gpu/ZMapReadBack.cs`) | `IZMapBackend.Apply(map, steps, readBack)`; with `Never` the height field stays on the device, `ZMap.IsHeightsCurrent` goes false and every host read of `ZMap.Heights` throws instead of answering from a stale array |
| `ZMap.ReadHeights()` | brings the field back on demand and returns the copy time |
| `ZMap.BackendRemovedVolumeMm3` / `nc_zmap_volume` | sums (stock top − height) × cell area on the device with a two-stage reduction, so a progress number costs 0.1416 ms instead of a 0.42 ms copy of the field |
| `IZMapQueryBackend` / `IZMapQueryBackend` implementations | `SampleHeights` (height at N points) and `ProbeMaterial` (ball penetration at N poses) on both backends, so the GPU result is checkable against the CPU reference cell by cell |
| Host-clocked copy back in `zmap.cu` | a pageable D2H copy is synchronous and the driver may run it outside the caller's stream, so a stream-event pair around it records the same timestamp twice and the phase reads 0.000. `copy_back()` times it on the host clock instead. |

### 1.2 The measurement that was wrong, and why

The first version of the query table came out of a bench that called each query **once**, on arrays that had just been
allocated:

| | reported | actual (warm) |
| --- | ---: | ---: |
| CUDA download, 4 MB | 6.7 ms (0.6 GB/s) | 0.428 ms (9.3 GB/s) |
| CUDA sample wall | 6.851 ms | 2.187 ms |
| CPU probe, 876 poses | 0.444 ms | 0.039 ms |

The 0.6 GB/s looked like a PCIe mystery, because the very same machine reaches 8.7 GB/s on the field read-back. It was
not the copy: it was the **first touch of a fresh 4 MB destination array**, about a thousand pages the collector has
never faulted in. The CPU numbers had the same problem in the other direction (a factor of eleven).

The bench now warms up like the preview phase and reports the best of `--repeat` with the cold call printed beside it
(`ColdSuffix` in `bench/Stykker.NanoCut.GpuBench/Program.cs`). Every number in this document and in `docs/gpu-findings.md`
is a warm one. The general lesson is worth more than the numbers: **a first-call timing is a statement about the first
call.**

### 1.3 The change that made the point query pay: stop packing on the host

`CudaBackend.SampleHeights` used to copy the query into a flat `float[]` relative to the map origin before uploading,
to halve the bytes on the wire. For a million points that loop cost **2.8 ms** to save 0.9 ms of transfer, and the wall
came out at **4.135 ms — slower than the CPU backend's 3.474 ms**.

The same arithmetic is trivial on the device, where there is time to spare (the kernel itself is 0.064 ms):

- `zmap.cu`: `sample_kernel` (float points) replaced by `sample_d_kernel`, which takes two doubles in absolute mm and
  subtracts the origin and narrows to float per thread; `nc_zmap_sample` gained `originX`/`originY` and takes
  `const double*`; `upload_query` split into a byte-counting `upload_bytes`.
- `CudaNative.ZMapSample` passes `ReadOnlySpan<SamplePoint>` — blittable, so the source-generated marshaller pins it
  and there is no copy.
- `CudaBackend`: `PackPoints` is gone. `SamplePoint` documents the layout the kernel assumes.

Result: the transfer grows from 8 MB to 16 MB and the wall drops to **2.187 ms** — 1.9× better than the packed version,
1.6× better than the CPU, with the same answers to the last bit (`max Δh 1.9e-6 mm` before and after).

| 1 000 000 points, 1024 × 768 | kernel | upload | download | wall |
| --- | ---: | ---: | ---: | ---: |
| CUDA, packed on the host (old) | 0.062 ms | 0.859 ms | 0.428 ms | 4.135 ms |
| CUDA, packed in the kernel (new) | 0.064 ms | 1.658 ms | 0.428 ms | **2.187 ms** |
| CPU | 3.474 ms | – | – | 3.474 ms |

### 1.4 What a query actually costs

| Query | CPU | CUDA kernel | upload | download | CUDA wall | agreement |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| heights at 1 000 000 points (16 MB in, 4 MB out) | 3.474 ms | 0.064 ms | 1.658 ms | 0.428 ms | **2.187 ms** | max Δh 1.9e-6 mm |
| penetration at 876 tool poses (14 KB in) | **0.039 ms** | 0.004 ms | 0.044 ms | 0.028 ms | 0.116 ms | Δ = 0 |

- The point query is **transfer-bound**: 2.086 of its 2.187 ms is PCIe, 0.064 ms is the kernel. The GPU computes 54×
  faster than 16 CPU threads and still spends 95 % of its time waiting for the link. The next win is not a faster
  kernel, it is a point set that lives on the device between calls.
- The pose query is **round-trip latency**: 0.072 ms of copying a few kilobytes, with a 0.004 ms kernel. No kernel
  makes that back against a 0.039 ms CPU loop. So: a million points on the GPU, a few hundred poses on the CPU, and a
  pose query only becomes interesting in the millions of poses.

### 1.5 The read-back, the volume on the device, and pinning

| 876 steps at 1024 × 768 | kernel | upload | download | wall |
| --- | ---: | ---: | ---: | ---: |
| CUDA, read-back after every call | 1.5 ms | 0.05 ms | 0.42 ms | 2.4 ms |
| CUDA, `ZMapReadBack.Never` + volume on the device | 1.544 ms | 0.05 ms | – | 1.665 ms |
| CPU backend | 266.2 ms | – | – | 266.2 ms |

The volume reduction inside that second row costs **0.1416 ms** and returns 11 229.542774 mm³, and it agrees with the
host-side sum to the last digit.

Pinned host memory, measured in the same process on the same field, back to back, best of 8:

| Read-back of 3 MB | time | bandwidth |
| --- | ---: | ---: |
| pinned (`cudaHostAlloc`) | 0.362 ms | 8.68 GB/s |
| pageable | 0.362 ms | 8.70 GB/s |

Same to three digits, and the order flips between processes (0.361/0.360 in one run, 0.494/0.380 in another). 6.3–8.7
GB/s in both directions is this link's ceiling, so the pinned buffer is off by default. The prediction from round 1 that
pinning would roughly double the download was wrong on this machine.

### 1.6 Tests

`dotnet test -c Release`: 183 + 4 = 187 passing, 0 failures, with the rebuilt native library. The GPU tests
(`tests/Stykker.NanoCut.Tests/GpuZMapTests.cs`) compare the CUDA backend against the CPU backend cell by cell and query
by query, which is what makes "max Δh 1.9e-6 mm" and "Δ = 0" claims checkable.

---

## Part 2: the gear case

`Process2.Cut` on the rack case (m = 2 mm, z = 20, `Tolerance.Default`) is the slowest planar process in the repo. It was
measured phase by phase, then nine variants were built and timed.

### 2.1 Where the 41 s go

| Phase | Time | What it is |
| --- | ---: | --- |
| `SweepPieces` | 0.4 s | 209 089 convex pieces, 2560 roll steps |
| bounds filter | 0.01 s | `Overlaps` against the workpiece box |
| 80 `UnionAll` calls over the pose parts | 17.7 s | 32 near-congruent 3687 mm² copies of the 8-part rack per batch, 0.76 ms per piece |
| 621 subtracts of the growing result | 23 s | cost grows with the loop count of the result |
| **total** | **41.0 s** | 1231.252941475 mm², 69 contours, flank deviation 5.4 nm (test asserts ≤ 5.6 nm) |

The total is `Process2.Cut`'s own figure (40.98 s), and the phases close on it: that run reports no separate
subtract figure, so the subtract is the remainder after the measured sweep, bounds filter and unions, and
0.4 + 0.01 + 17.7 + 23 = 41.1 s is the call's own 40.98 s to the rounding of the parts. That is how the shipped row
is checked, and how an earlier draft was found to be wrong at 32.1 s. The interval-order row is built the other way
round — there union and subtract are timed separately (2.20 + 51.40 s) and leave about 4 s of a 57.87 s call
uncovered. Compare totals across rows, not parts within a row.

Two costs with opposite behaviour: `UnionAll` is superlinear in the number of *overlapping near-congruent* polygons
(0.76 ms per piece for the rack copies against 0.011 ms for the band batches), while the subtract costs what the
result's loop count costs.

### 2.2 The variants

| Variant | Union | Subtract | total | contours | flank |
| --- | ---: | ---: | ---: | ---: | ---: |
| **shipped order, batches of 256** | 17.7 s | 23 s | **41.0 s** | **69** | 5.4 nm |
| interval order, batches of 256 | 2.2–2.5 s | 47–52 s | 57.9 s | 1151–2013 | exact |
| interval order, batches of 1024 | 11.6 s | 22.9 s | 34.5 s | 3946 | exact |
| interval order, batches overlapping by 32 / 64 pieces | | | 44.2 / 32.7 s | 1554 / 3459 | exact |
| one union for all 209 089 pieces | does not finish in 9 min | | | | |
| coarser fold split | | | 49.5 s | | 33 067 nm |
| finer fold split (hoist the fold point) | | | 188 769 → 150 033 pieces, no win | | exact |
| pose parts dropped, bands only | | | | | 205 614 nm |

### 2.3 What it means

- **The shipped order is a local optimum.** Reordering the pieces interval by interval makes the union 7× cheaper and
  the subtract 2× more expensive, because a batch that is no longer one contiguous ribbon subtracts as a set of slivers
  and leaves degenerate loops in the result. The 16 % win at batch 1024 costs 57× more contours, permanently.
- **The pose parts are load-bearing, not redundancy.** The Minkowski identity (convex polygon + translation = union of
  edge trapezoids) holds for translations only; under a rotation the pose parts cover the concave side of the fold.
  Dropping them keeps the area right to nine decimals and costs 206 µm of flank error — 40 000× the tolerance. That is
  the dangerous kind of failure, because a check that compares areas passes.
- **The fold split cannot be tuned into a win.** Coarser loses the flank by five orders of magnitude; finer changes
  nothing, because the fold point is essentially stationary over a 30 nm step.
- **The fix is the grouping.** Group the swept pieces by the region of the workpiece they remove, so every batch is
  one contiguous ribbon and every subtract sees a result whose loop count stays near 69. That is a change to how
  `Process2.Cut` batches, not to the sweep, and it is the first entry under "Gear generation – follow-ups" in
  `docs/todo.md`.
- **One change survived:** the bounds filter now makes one pass over the vertices instead of four LINQ passes. It runs
  on every piece of every sweep; it costs 0.01 s of 41.0 s, so it is kept because it is strictly less work, not because
  it moved the needle. Everything else was reverted with `git checkout`.

---

## Not done

- A query that lives on the device between calls. The point query is 95 % PCIe traffic; uploading the point set once
  and sampling it repeatedly is the next real win.
- A partial read-back (a row band of the field) for a caller that wants a picture of part of the stock mid-cut.
- No streams, no multi-GPU, no overlap of transfer and compute. One kernel on the default stream is all this prototype
  needs.
- Part 1 of the plan, the server mode (`samples/Stykker.NanoCut.Server`, progress over SignalR, cancellable), is not
  built; only the GPU half is done. ILGPU was not used, as decided in `docs/server-gpu-plan.md`.
- The gear case is analysed, not fixed. The grouping change in 2.3 is a real piece of work and was not started, because
  the exact kernel is being optimised in parallel by other agents.

## Where the details live

| Topic | File |
| --- | --- |
| All GPU measurements, the accuracy decomposition, the reasoning | `docs/gpu-findings.md` |
| One-page result, round 2 | `bench/results-2026-10-03-gpu-round2.html` |
| One-page result, round 1 | `bench/results-2026-10-03-gpu.html` |
| Bench numbers in context | `bench/README.md` |
| The gear case in the process documentation | `docs/processes.md` |
| Open items | `docs/todo.md` |
| The CUDA source | `src/Stykker.NanoCut.Gpu.Native/zmap.cu` (build: `build.ps1` / `build.sh`, not part of `dotnet build`) |

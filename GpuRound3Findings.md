# GPU round 3: a query that lives on the device, and a stopwatch that was not running

Branch `feature/server-gpu`, 4 October 2026. This is the self-contained write-up of round 3; the long version lives in
`docs/gpu-findings.md` ("The point set that lives where it is asked from"), the one-page result in
`bench/results-2026-10-04-gpu-round3.html`, the rounds before it in `GpuAndGearFindings.md`.

Round 2 ended with a measurement and a consequence. The point query spent 2.086 of its 2.187 ms on the PCIe link, and
the conclusion was blunt: *the next win is not a faster kernel, it is a point set that lives on the device between
calls*. That is what this round built. The headline results:

- **A million-point query costs 0.514 ms instead of 2.204 ms** (4.3×), with answers that are bit-identical to the
  span query. It is 11× faster than what a CPU call really costs, and 0.421 of the 0.514 ms is the answer coming
  back.
- **The point set pays for itself at the second query**: 2.349 ms once, 1.69 ms saved per call. A caller that asks
  once should stay with the span call.
- **The CPU backend was hiding 2.4 ms per call in a stopwatch that was not running.** It reported 3.457 ms and the
  caller waited 5.873 ms. Every CPU/GPU table in this project was therefore too generous to the CPU.
- **A promise the interface made and the code did not keep.** The height query shared the map's input and output
  buffers with the pose query without taking the per-map lock, so two concurrent queries on one map could overwrite
  each other's answers. Fixed.
- **Pinned host memory still buys nothing**, now over four runs: 8.04 … 8.75 GB/s pinned against 8.62 … 8.72 GB/s
  pageable, with the order flipping.

Machine: AMD Ryzen 7 5800X3D (8 cores, 16 threads), NVIDIA GeForce RTX 5070 Ti 16 275 MiB (sm_120), driver 617.14,
CUDA 13.4, Windows 11, .NET 10. Scene `pocket-large`, 876 steps, 1024 × 768 cells, 1 000 000 query points. All query
numbers below are warm and best of 5; the bench ran three times and the kept-set query landed at 0.514 … 0.535 ms.

---

## 1.1 What was built

| Piece | What it does |
| --- | --- |
| `struct PointSet` / `nc_pointset_create` / `nc_pointset_destroy` in `src/Stykker.NanoCut.Gpu.Native/zmap.cu` | allocates the points once in their **own device allocation, outside the map**, so the set outlives the query and can be posed at any map; the upload is timed on the host clock and reported separately |
| `nc_zmap_sample_set` | runs the same `sample_d_kernel` over the resident set with no `cudaMemcpy` before the launch; it records `start` and `uploaded` back to back, so the upload phase reads the launch path (0.001 ms) rather than a number that pretends work happened |
| `PointSet` (`src/Stykker.NanoCut.Gpu/PointSet.cs`) | `IDisposable` owner of the native handle and, on the CPU backend, of the host array; finalizer plus idempotent `Dispose`, use-after-dispose throws |
| `IZMapQueryBackend.UploadPoints` + `ZMap.UploadPoints` | the caller hands a point set to the backend once and owns it from then on |
| `IZMapQueryBackend.SampleHeights(ZMap, PointSet, Span<float>)` + `ZMap.SampleHeights(PointSet, Span<float>)` | the query against the resident set, on both backends, with the same `+= dz` shift the span overload applies |
| The per-map lock on the height query | `SampleHeights(ZMap, ReadOnlySpan…)` now takes `device.Gate` like `ProbeMaterial` does, and the interface remarks say which query locks and why |
| `CpuBackend.UploadPoints` | the CPU backend implements the same API by keeping the array the `ref` struct forced it to build anyway |

## 1.2 The query without its upload

| 1 000 000 points, 1024 × 768 | kernel | upload | download | reported wall | caller waits |
| --- | ---: | ---: | ---: | ---: | ---: |
| CUDA, points per call | 0.064 ms | 1.677 ms | 0.430 ms | 2.204 ms | 2.205 ms |
| CUDA, points kept on the device | 0.061 ms | 0.001 ms | 0.421 ms | **0.514 ms** | 0.515 ms |
| CPU, points per call | 3.457 ms | – | – | 3,457 ms | 5.873 ms |
| CPU, points kept in an array | 3.332 ms | – | – | 3,332 ms | **3.522 ms** |

What the table says, phase by phase:

- **The upload disappears and with it 1.676 ms.** The 0.001 ms that remains is the pair of timing events recorded
  back to back — the honest report of a phase in which nothing is uploaded.
- **The download does not move** (0.421 against 0.430 ms): 4 MB over a link that reaches 8.7 GB/s. This is now the
  whole query. The floor for this hardware with four bytes per pixel is about 0.46 ms, and the query sits on it.
- **The kernel is 12 % of the wall** (0.061 ms). Nothing about the kernel changes; the win is entirely in not
  moving the input.
- **The CPU also wins**, from 5.873 to 3.522 ms, and for the same reason round 2's host-packing loop existed: the
  `ref` struct cannot be captured by a parallel loop, so the backend has to materialise the points anyway. Making
  that array first-class removes the copy from the query.

Agreement is unchanged and is checked by the tests: **Δ = 0** between the kept-set and the span query, `max Δh`
1.91e-6 mm against the CPU reference, exactly as in round 2.

## 1.3 It pays for itself at the second query

The set is not free. Uploading a million points once costs **2.349 ms** on the CUDA backend (2.195 ms for the CPU
array), and each query afterwards saves 1.69 ms:

| queries | points sent per call | set uploaded once |
| --- | ---: | ---: |
| 1 | 2.204 ms | 2.863 ms |
| 2 | 4.408 ms | **3.377 ms** |
| 10 | 22.040 ms | **7.489 ms** |

So the rule is a one-liner and it cuts both ways: **one query, span call; from the second on, the set.** The case
that pays is the one round 2 named — a viewer that asks about its pixel raster after every step batch, a stock check
that asks the same raster whenever the program changes. The caller owns the set and disposes it.

## 1.4 The stopwatch that was not running

This is the finding worth more than the number above, and it has nothing to do with the GPU.

`CpuBackend.SampleHeights` stopped its stopwatch around the `Parallel.For` only. The span → array staging copy in
front of it (16 MB in) and the `answer.CopyTo` behind it (4 MB out) were never inside the measurement:

| CPU, 1 000 000 points | reported | caller waited |
| --- | ---: | ---: |
| span call | 3.457 ms | 5.873 ms |
| point set | 3.332 ms | 3.522 ms |

Consequence: every CPU-against-CUDA table written so far compared a host number that excluded 2.4 ms of work with a
device number that includes all of its transfers. The comparison was not wrong in direction, but it flattered the CPU
by a fixed 2.4 ms per call.

The fix is in the measurement, not in the backend: the bench now times every query call from outside and prints that
next to the backend's own figure (`(call … ms)` in the query phase). Whether `CpuBackend` should fold its staging into
`WallMs` is left undecided in `docs/todo.md` — it is a reporting contract, not a performance question. The general
rule, which will outlive this file: **a timing that excludes work is not a wall time.**

## 1.5 A promise the code did not keep

`IZMapQueryBackend` documents that two queries on one map may be serialised. `ProbeMaterial` honoured that with
`lock (device.Gate)`. `SampleHeights` did not — and both stage their input through the map's `queryIn` and write
through `queryOut`. Two concurrent queries on the same map could therefore overwrite each other's input and answer.
No test could see it, and in a viewer it would look like an occasionally wrong height in an otherwise correct image.

The height query now takes the same lock, the point-set path locks too (it has no input buffer to share but still
writes through `queryOut`), and the interface remarks say which query locks and why. A `PointSet` also records the
device it was allocated on, and `CudaBackend` refuses a set from another device — device memory belongs to the device
it was allocated on, and the check is one comparison.

## 1.6 Pinned host memory, again

Re-measured in four runs, back to back, on the same field: pinned 8.04 … 8.75 GB/s against pageable 8.62 … 8.72 GB/s.
The ranges overlap and the order flips. One run in six showed pinned ahead by 0.556 ms against 0.361 ms, which is an
artefact of the pinned variant being measured first and best-of-N still catching a one-off — the reason it is noted
here rather than as a property. The pinned buffer stays off.

## 1.7 Tests

`dotnet test -c Release`: **185 + 4 oracle = 189 passing, 0 failures**, with the rebuilt native library (230 KiB, no
errors). Two facts were added:

- `APointSetAnswersWhatTheSpanAnswers` — CPU backend, always runs, no CUDA needed: the set answer equals the span
  answer bit for bit, `Count` and `IsDisposed` behave, double dispose is fine, and use after dispose throws.
- `CudaResidentPointsUploadOnceAndAnswerLikeTheSpan` — skips with a message when CUDA is unavailable; applies three
  steps with `ZMapReadBack.Never`, then asserts the set answer equals the span answer twice over and that
  `resident.UploadMs < 0.05`, which is the actual claim being made about the device.

## 1.8 What it means for a caller

- **Preview:** unchanged from round 1 — 2.4 ms wall for 876 steps against 4.66 s for the exact kernel, remaining
  volume 84 770.457226 mm³ (−0.106 %).
- **One image per step batch:** upload the pixel raster once, then `SampleHeights(pixels, frame)`. 0.514 ms instead
  of 2.204 ms, with identical heights. Combined with `ZMapReadBack.Never` and `BackendRemovedVolumeMm3`, a frame
  costs 1.7 ms of steps plus 0.51 ms of query.
- **Heights at many points:** the GPU, once there are enough of them — 0.514 ms against 5.873 ms for a CPU call.
- **One query only:** the span call. The set's 2.349 ms is not recovered by a single query.
- **Poses:** still the CPU, still by round-trip latency (0.039 against 0.116 ms for 876 poses). Unchanged, and this
  round does not change it: the set removes an upload, and that query has no upload worth removing.
- **Always warm up, and always time the call as well as the work inside it.**

---

## Not done

- **A cheaper answer.** The download is now the entire query. The next real win is fewer bytes: half precision for an
  image that is only being shaded, or a renderer that samples the height field on the device and never brings it
  back. Both are real work, neither was started.
- **A partial read-back** (a row band of the field) for a caller that wants a picture of part of the stock mid-cut.
  Only the full copy grows with the grid.
- **No streams, no multi-GPU, no overlap of transfer and compute.** One kernel on the default stream is all this
  prototype needs.
- **Part 1 of the plan, the server mode** (`samples/Stykker.NanoCut.Server`, progress over SignalR, cancellable), is
  not built; only the GPU half is done. ILGPU was not used, as decided in `docs/server-gpu-plan.md`.
- **The gear case is still analysed, not fixed.** The grouping change in `GpuAndGearFindings.md` §2.3 is a real piece
  of work and was not started, because the exact kernel is being optimised in parallel by other agents.

## Where the details live

| Topic | File |
| --- | --- |
| All GPU measurements, the accuracy decomposition, the reasoning | `docs/gpu-findings.md` |
| One-page result, round 3 | `bench/results-2026-10-04-gpu-round3.html` |
| Rounds 2 and the gear case | `GpuAndGearFindings.md` (`bench/results-2026-10-03-gpu-round2.html`) |
| Round 1 | `bench/results-2026-10-03-gpu.html` |
| Bench numbers in context | `bench/README.md` |
| Open items | `docs/todo.md` |
| The CUDA source | `src/Stykker.NanoCut.Gpu.Native/zmap.cu` (build: `build.ps1` / `build.sh`, not part of `dotnet build`) |

# GPU prototype: what a Z-map preview costs and what it is worth

Measured on 2026-10-03 on `feature/server-gpu` (commit `cce28b7`, `main` at `b9a5faf` merged in). Everything below is
from this machine, not from the cloud tables in `bench/README.md`, and the two are not comparable.

## Hardware and software

| Item | Value |
| --- | --- |
| CPU | AMD Ryzen 7 5800X3D, 8 cores / 16 threads |
| GPU | NVIDIA GeForce RTX 5070 Ti, 16 275 MiB, Blackwell, compute capability 12.0 (sm_120) |
| NVIDIA driver | 32.0.16.1714 (617.14) |
| CUDA toolkit | 13.4, `nvcc` V13.4.59 |
| OS | Windows 11 Home 10.0.26200 |
| .NET | SDK 10.0.401, runtime 10.0.12 |
| Host compiler for nvcc | Visual Studio 2022 Community, `vcvars64.bat` |
| Native build | `build.ps1`, `-O3 -shared -std=c++17 -cudart static`, SASS for sm_120 plus PTX for compute_75, 192 KiB |

## The reference: the exact kernel, measured here

`py -3 bench/run.py bench/scenes/pocket-large.json --engines nanocut --repeat 3`, warm, best of three:

| Scene | Steps | Time | per step | Remaining volume (mm³) | Triangles |
| --- | ---: | ---: | ---: | ---: | ---: |
| `pocket-large` (48 segment ball) | 876 | **4655 ms** | 5.31 ms | 84 860.612636583 | 698 |
| `pocket-large-96` (96 segment ball) | 876 | 22 903 ms | 26.1 ms | 84 850.215178686 | 1 254 |

The second scene exists only for the accuracy decomposition below: it is the same toolpath with a finer polyhedral
ball, so the difference between the two isolates the tool's tessellation error. On the same machine the round-4 code
needed 4910 ms, so round 5 of the kernel optimisation is about 5 % faster here.

## The preview: Z-map, CPU backend against CUDA backend

`dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll
bench/out/pocket-large/expanded.json --grids 128,256,512,1024,2048,4096 --backends cpu,cuda --repeat 3 --diff
--reference 84860.612636583 --out bench/out/pocket-large-zmap`

All 876 steps in **one** call, warm, best of three. "Kernel" is the device kernel or the parallel host loop; upload
and download are the PCIe copies; the CPU backend has neither.

| Cells | CPU kernel (ms) | CUDA kernel (ms) | kernel speed-up | CUDA upload (ms) | CUDA download (ms) | CUDA wall (ms) | Remaining (mm³) | max Δh CPU↔CUDA (mm) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 128 × 96 | 7.7 | 0.2 | 39× | 0.04 | 0.04 | 0.4 | 84 761.374903 | 0 |
| 256 × 192 | 23.7 | 0.2 | 119× | 0.06 | 0.10 | 0.5 | 84 774.000335 | 0 |
| 512 × 384 | 73.0 | 0.4 | 183× | 0.06 | 0.18 | 0.8 | 84 768.999004 | 0 |
| 1024 × 768 | 286.0 | 1.5 | 191× | 0.05 | 0.46 | 2.5 | 84 770.457226 | 0 |
| 2048 × 1536 | 1296.7 | 6.1 | 213× | 0.07 | 1.78 | 8.7 | 84 769.861853 | 0 |
| 4096 × 3072 | 5144.5 | 26.8 | 192× | 0.05 | 7.73 | 36.2 | 84 770.036271 | 1.9e-6 |

The two backends compute the same thing to the last bit up to 2048 × 1536; at 4096 × 3072 the largest height
difference is 1.9e-6 mm (two thousandths of a micrometre), which is where `nvcc` contracting `a*b+c` into `fma`
becomes visible. The remaining volumes agree to nine decimals at every resolution.

The CPU numbers move by up to ±20 % between runs of the same measurement (277.8 … 391.9 ms at 1024 × 768 across four
runs), the CUDA numbers by less than ±5 %. The table is one run; the speed-up is therefore good to about two
significant figures, not three.

### Against the exact kernel

At the plan's suggested 1024 × 768 grid, for the whole 876 step toolpath:

| | Time for all 876 steps | per step | against the exact kernel |
| --- | ---: | ---: | ---: |
| Exact kernel (`nanocut`) | 4655 ms | 5.31 ms | 1× |
| Z-map, CPU backend, 16 threads | 286 ms | 0.327 ms | 16× faster |
| Z-map, CUDA backend (kernel only) | 1.5 ms | 0.0017 ms | **3100× faster** |
| Z-map, CUDA backend (wall, with transfers) | 2.5 ms | 0.0029 ms | 1860× faster |

The comparison is not apples to apples and is not meant to be: the exact kernel returns an exact solid with a growing
boundary representation, the Z-map returns a fixed-size height field that cannot represent overhangs. What it says is
that a *preview* of the whole pocket costs milliseconds, and that the preview does not need a GPU to be interactive —
the CPU backend is already at 0.3 s for the full path.

### One-off costs, measured separately

| Cost | Measured |
| --- | --- |
| Loading `nanocut_gpu` and letting the CUDA runtime initialise (once per process, paid by the first device probe) | 34.3 – 39.7 ms |
| The same, when `nc_gpu_init` is the first CUDA call of the process instead of the probe | 93 – 129 ms |
| Allocating the height field on the device and filling it (once per grid) | 0.3 ms at 1024 × 768, 1.4 ms at 4096 × 3072 |
| First kernel launch | not separable from the above; the context already exists |
| Compiling a kernel at run time | none, the kernels are compiled by `nvcc` at build time |

So the price of the first preview after a process starts is roughly 40 – 130 ms of CUDA start-up plus the work itself.
That is fine for a server process, and it is the reason `CudaRuntime` probes once and caches.

### One launch for the batch, or one per step

`--chunk 1` applies the 876 steps in 876 separate calls, which is what a page does when it reports progress after
every step:

| | Wall (ms) | Kernel (ms) | Upload (ms) | Download (ms) |
| --- | ---: | ---: | ---: | ---: |
| CUDA, one call for all 876 steps | 2.5 | 1.5 | 0.05 | 0.46 |
| CUDA, one call per step | 1128.9 | 5.9 | 48.0 | 1004.7 |
| CPU, one call for all 876 steps | 286.0 | 286.0 | – | – |
| CPU, one call per step | 490.1 | 490.1 | – | – |

The plan was right to prefer the batch, but for a more interesting reason than launch overhead: the launch overhead
itself is only 5.9 ms against 1.5 ms, about 5 µs per launch. The 450× comes from the fact that `IZMapBackend.Apply`
reads the height field back to the host on every call, so per step means 876 copies of 3 MB. **A wrapper change worth
making before this is used for progress reporting: let the caller decide whether a call reads the heights back.**
Chunking by, say, 32 steps would keep the progress updates and cost a fiftieth of the transfers.

### Transfer bandwidth

The download moves the whole height field: 50.3 MB in 7.73 ms at 4096 × 3072, that is 6.5 GB/s. That is pageable-host
speed, not what PCIe 5.0 x16 can do, and the first round expected a pinned host buffer (`cudaHostAlloc`) to roughly
double it — it does not, see *Pinned host memory* below. The upload is
42 KB of packed steps and is noise (0.05 ms) at every resolution. At 1024 × 768 the download is 0.46 ms against
1.5 ms of kernel, so transfers are already a third of the wall time there and three quarters of it at 4096 × 3072.

## Round 2: the caller decides about the read-back, batch queries, the volume on the device

Three things the first round left open, all built and measured.

### The caller decides about the read-back

The wrapper change the first round recommended: `IZMapBackend.Apply(map, steps, readBack)` takes a `ZMapReadBack`, and
`ZMap.ApplySteps` passes it on. With `ZMapReadBack.Never` the field stays on the device, the copy back is skipped, and

- `ZMap.IsHeightsCurrent` goes false, and every host read of `ZMap.Heights` (`RemovedVolumeMm3`, `ToMesh`, the CPU query
  backend) throws `InvalidOperationException` instead of quietly answering from a stale array;
- `ZMap.ReadHeights()` copies the field back and returns the copy time in ms;
- `ZMap.BackendRemovedVolumeMm3` asks the backend, and on CUDA that is a reduction on the device that works while the
  field is still on the device.

So progress reporting after every batch of steps costs a reduction over the field (0.16 ms, below) instead of a copy of
the whole field (0.4 ms at 1024 × 768, 7.7 ms at 4096 × 3072), and the heights come back only when someone actually
wants to look at them. The whole 876 step toolpath, applied with `Never` at 1024 × 768: kernel 1.545 ms, wall 1.655 ms
against 2.5 ms with the copy.

### Batch queries

`ZMap.SampleHeights` (height at each point) and `ZMap.ProbeMaterial` (how far a ball reaches into the material at each
pose) are the second case from the plan, and they are the queries that make a preview worth having on the server: the
input and the output stay small for a whole tool path, so a program can be checked for air cuts without ever reading
the height field back. Both backends answer them (`IZMapQueryBackend`); the CUDA one uploads the query, runs one kernel
over it and copies the answers back.

At 1024 × 768, warm, best of 5, the cold first call in brackets. "CPU call" is what the caller waits for, measured
around the whole call, because the CPU backend reports only its parallel loop and leaves its staging copy out of that
number — the section on the point set below has the two figures side by side.

| Query | Input | CPU call | CUDA kernel | CUDA upload | CUDA download | CUDA wall | Agreement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| heights at 1 000 000 points | 16 MB | 5.873 ms | 0.064 ms | 1.677 ms | 0.430 ms | **2.204 ms** (cold 3.624) | max Δh 1.9e-6 mm |
| the same points from a kept set | – | 3.522 ms | 0.061 ms | 0.001 ms | 0.421 ms | **0.514 ms** | Δ = 0 against the span query |
| penetration at 876 tool poses | 14 KB | **0.039 ms** (0.443) | 0.004 ms | 0.044 ms | 0.028 ms | 0.116 ms (0.542) | Δ = 0 |

Three findings, in the order they turned up.

**The first version of this table was a single cold call, and it was wrong by an order of magnitude.** The bench
measured each query once, on arrays that had just been allocated, and the CUDA download came out at 6.7 ms for 4 MB —
0.6 GB/s, against the 8.7 GB/s the very same machine reaches on the field read-back. The cost was not the copy but the
first touch of the destination: a fresh 4 MB array is about a thousand pages that the collector has never seen, and
faulting them in costs more than the transfer. The CPU numbers had the same problem in reverse (0.443 ms cold against
0.039 ms warm, a factor of eleven). The bench now warms up like the preview phase and reports the best of `--repeat`
beside the cold call, and every query number in this file is a warm one. Lesson worth keeping: a first-call timing is a
statement about the first call.

**Half of the query's wall time was a host-side packing loop.** `CudaBackend` used to copy the query into a flat float
array relative to the map origin before uploading it, to halve the bytes on the wire. The loop cost 2.8 ms for a
million points against 0.9 ms of saved transfer, and the wall came out at 4.135 ms — *slower than the CPU*. The
kernel can do the same arithmetic for free: `nc_zmap_sample` now takes the points as they are (two doubles, absolute)
and `sample_d_kernel` subtracts the origin and narrows to float per thread. The transfer grows from 8 MB to 16 MB and
the wall drops to 2.187 ms, a 1.9× improvement over the packed version and 1.6× better than the CPU backend, with the
same answers to the last bit (`max Δh 1.9e-6 mm` before and after). The host side is now 0.1 ms of the 2.187 ms.

**A point query is transfer-bound, and the pose query cannot win at all.** The million-point query spends 2.086 of its
2.187 ms on the two copies and 0.064 ms in the kernel: at this size the PCIe link, not the device, is the limit, and
the answer still comes back in the same traffic the question went out in. The pose query is the opposite: 14 KB in and
3.5 KB out, a 0.004 ms kernel, and a wall of 0.116 ms — *three times slower than the 0.039 ms the CPU needs for the same
876 poses*. Two pageable copies of a few kilobytes cost 0.072 ms of nothing but round-trip latency, and no amount of
kernel makes that back. So the split is: sample a million points on the GPU, probe a few hundred poses on the CPU, and
do not send a pose query to the device until the pose count is in the millions, where the fixed 0.07 ms stops mattering
next to 14 MB of upload.

The copy back is timed on the host clock, not with a stream event: a pageable device-to-host copy is synchronous, and the
driver is free to run it outside the caller's stream, in which case an event pair around it carries the same timestamp
twice and the phase reads as zero. That is exactly what it did before, and it is why the first version of this table
had downloads of 0.000 ms.

### The point set that lives where it is asked from

The query above spends 2.10 of its 2.20 ms on the wire and 0.064 ms in the kernel, and the round above it named the
obvious fix: a caller that asks about the *same* points again should not send them again. A viewer samples its pixel
grid after every batch of steps, a stock check samples one grid every time a program changes, and in both cases the
16 MB of points is identical every time. So the points can stay where the kernel is:

```csharp
using PointSet pixels = map.UploadPoints(points);   // once: 2.349 ms on the device
map.ApplySteps(batch, ZMapReadBack.Never);          // 1.7 ms, field stays on the device
map.SampleHeights(pixels, frame);                   // every frame: 0.514 ms, nothing uploaded
```

`IZMapQueryBackend.UploadPoints` hands the points to the backend once — `nc_pointset_create` copies them into their
own device allocation, outside the map, so they outlive the query and can be asked about any map — and
`nc_zmap_sample_set` runs the *same* `sample_d_kernel` over them. The only difference to `nc_zmap_sample` is that
there is no `cudaMemcpy` before the launch. The answers are the answers of the span query, to the bit.

| 1 000 000 points at 1024 × 768 | kernel | upload | download | wall | what the caller waits for |
| --- | ---: | ---: | ---: | ---: | ---: |
| CUDA, points per call | 0.064 ms | 1.677 ms | 0.430 ms | 2.204 ms | 2.205 ms |
| CUDA, points kept on the device | 0.061 ms | 0.001 ms | 0.421 ms | **0.514 ms** | 0.515 ms |
| CPU, points per call | 3.457 ms | – | – | 3.457 ms | 5.873 ms |
| CPU, points kept in an array | 3.332 ms | – | – | 3.332 ms | **3.522 ms** |

**4.3× on the GPU, and 11× against what a CPU call costs.** The upload is gone (0.001 ms is the pair of timing events),
and what is left is the download: 0.421 of 0.514 ms is the 4 MB answer coming back over a link that tops out at
8.7 GB/s, so the query is now at the floor of what this hardware can do with four bytes per pixel. The kernel is 12 %
of the call.

It pays for itself at the second query: 2.349 ms to upload the set once against 1.69 ms saved per call
(2.204 → 0.514), so one span query plus one resident query is already cheaper than two span queries. A caller that
wants exactly one answer should keep using the span overload and skip the object; the set is only worth its allocation
when it will be asked more than once.

Two things came out of building it that were not expected.

**The CPU backend gains more than the GPU does, in relative terms, and nobody could see it before.** A `ref` struct
cannot be captured by the parallel loop, so `CpuBackend` copies the span into an array on every call: 16 MB in and
4 MB out around a loop that is 3.4 ms. That copy was *outside* the backend's stopwatch, so the reported time never
showed it — the query looked like 3.457 ms and cost the caller 5.873 ms. With a kept set the copy happens once and the
same call costs 3.522 ms, a 1.7× improvement that the reported number never moved for. The lesson is the general one: a
timing that excludes work is not a wall time, and comparing a host figure that hides a 2.4 ms copy against a device
figure that includes its transfers flatters the host. The bench now times the call from outside and prints it beside
the backend's own number, for every query.

**The sample query was not serialised against the other queries, although the interface promised it.** Both queries
stage their input in the map's `queryIn` buffer and write their answer through `queryOut`; the pose query took the
per-map lock and the height query did not, so two concurrent queries against one map could overwrite each other's
input and answer. It is the kind of bug that shows up as an occasional wrong height in a viewer and nowhere else. The
height query takes the same lock now, and `IZMapQueryBackend` says so: a query from a point set has no input buffer to
share but still writes through the map's output buffer, so it locks too.

### The removed volume on the device

`ZMap.BackendRemovedVolumeMm3` is the sum over all cells of (stock top − height) times the cell area, reduced on the
device: one partial-sum kernel with a shared-memory staging step and a final kernel over the blocks, which returns a
single double. For the whole 876 step toolpath at 1024 × 768 it costs **0.1416 ms** and reports 11 229.542774 mm³.

That is cheaper than the 0.4 ms read-back it avoids, so a caller that wants a progress number per batch should ask the
backend for the volume and leave the field where it is. The host-side `RemovedVolumeMm3` still exists and is the
reference (it sums `Heights` in double), so the two can be compared; they agree to the last digit.

### Pinned host memory: measured, and not worth it

The first round predicted that a pinned host buffer would roughly double the download and listed it as the obvious
next step. It was built (`--no-pin` switches it off) and the prediction is **wrong on this machine**:

| Read-back of 3 MB (1024 × 768) | Time | Bandwidth |
| --- | ---: | ---: |
| pinned (`cudaHostAlloc`), best of 8 | 0.362 ms | 8.68 GB/s |
| pageable, best of 8 | 0.362 ms | 8.70 GB/s |

Measured in the same process, on the same field, back to back, the two are the same number to three digits. Across
processes the order flips (0.361 pinned against 0.360 pageable in one run, 0.494 against 0.380 in another,
0.359 against 0.362 in a third, 0.391 against 0.365 in a fourth), which is what two equal measurements look like. The
large-transfer numbers above say why: 6.3–8.7 GB/s in both directions is this link's ceiling, and a copy that the
driver stages through its own buffer reaches the same place. So there is nothing to win here, and the pinned buffer is
off by default. On a machine where a copy really does hit the bus limit (a server with a passive root port and a GPU,
or a much larger grid) the pinned path stays available in the wrapper.

One methodological note for whoever repeats it: the pinned variant is measured first, and a best-of-N can still catch
one-off costs if the first run is the expensive one — one run in six came out at 0.556 ms pinned against 0.361 ms
pageable, which is a first-touch artefact and not a property of pinning. Two equal numbers should be shown as a range
across processes, not as the single run that happened to disagree.


## Accuracy: the deviation split into its three causes

The preview at 1024 × 768 leaves 84 770.457226 mm³, the exact kernel 84 860.612636583 mm³. The deviation is
−90.155 mm³, or −0.106 %. It splits exactly into three independent causes:

| Cause | mm³ | % of the remaining volume | How it was measured |
| --- | ---: | ---: | --- |
| Tool model: the exact kernel cuts a 48 segment polyhedral ball, the preview cuts a true sphere | −13.863 | −0.0163 % | Richardson extrapolation from the 48 and 96 segment runs, deficit ∝ 1/n² → 84 846.749 mm³ for a true sphere |
| Representation: a Z-map holds one height per column, so it cannot keep the thin roof of material that stays above the tool where the ball's crown does not reach the stock top | −76.713 | −0.0904 % | preview at 4096 × 3072 (grid error negligible) minus the extrapolated true-sphere value |
| Grid: cell-centre sampling at 1024 × 768 | +0.421 | +0.0005 % | 1024 × 768 against 4096 × 3072 |
| **Total** | **−90.155** | **−0.1062 %** | |

The three parts add up to the total to the last digit.

The representation error is the interesting one, and it is analytic, not empirical. The tool centre runs at z = 18 with
r = 3 in a stock whose top is z = 20, so the crown reaches z = 18 + √(9 − d²) and stays below 20 once
d > √5 = 2.236 mm. In that outer band the Z-map removes material up to z = 20 although the tool never got there, and
the exact kernel keeps a roof of 2 − √(9 − d²) mm. Only the two outermost passes have such a band (the inner ones are
cleared by their neighbours 4 mm away), each 0.764 mm wide and 80 mm long:

```
2 · 80 · ∫[√5..3] (2 − √(9 − d²)) dd = 2 · 80 · 0.480158 = 76.825 mm³
```

against 76.713 mm³ measured, 0.15 % apart. Two consequences:

- The error does **not** shrink with a finer grid. It is a property of the height-field representation, and at
  0.09 % of the volume it is the floor for this kind of preview. Anyone who needs better has to model the tool
  reaching the column from above (compare the crown height with the current height before lowering), which costs a
  second extremum per step, or use the exact kernel.
- For this scene the sign is known: the preview always shows slightly **more** material removed than the exact result.
  A stock-remainder check must not be decided on a preview.

The grid error is what a resolution choice buys: +0.42 mm³ at 1024 × 768, +0.6 mm³ at 512 × 384, and 13.6 mm³ of
scatter at 128 × 96. From 512 cells upwards it is between one and two orders of magnitude below the representation
error, so **512 × 384 is enough for a preview of this part, and anything finer only costs time.**

## Reproducing this

```powershell
dotnet build bench/Stykker.NanoCut.Bench -c Release
py -3 bench/run.py bench/scenes/pocket-large.json --engines nanocut --repeat 3
py -3 bench/run.py bench/scenes/pocket-large-96.json --engines nanocut --repeat 1

powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1
dotnet build bench/Stykker.NanoCut.GpuBench -c Release
dotnet bench/Stykker.NanoCut.GpuBench/bin/Release/net10.0/Stykker.NanoCut.GpuBench.dll `
    bench/out/pocket-large/expanded.json --grids 128,256,512,1024,2048,4096 `
    --backends cpu,cuda --repeat 3 --diff --reference 84860.612636583 --out bench/out/pocket-large-zmap
```

`bench/out/` is git-ignored; `zmap-results.json`, `zmap-results.md` and `preview-1024.stl` (78 MB, 1 569 282
triangles) land there.

## Recommendation

**Worth it, but not for the reason the plan guessed.**

- **Yes for a preview that a person watches.** 876 steps at 1024 × 768 in 2.5 ms wall means the whole toolpath can be
  recomputed on every parameter change — tool radius, step length, depth — instead of once per save. At 4096 × 3072,
  where a CPU preview starts to feel like a pause (5.1 s), the GPU is at 36 ms.
  The factor against the exact kernel depends on how the path is cut up: for the same pocket as 13 long moves the
  exact kernel needs only 42 ms, and the preview is about 10× (CPU) and 50× (CUDA) faster, not 1860×; see
  "The same pocket as a CAM program would send it".
- **No for the exact result.** The exact kernel stays on the CPU and has no GPU dependency, which is deliberate: the
  deviation above is 0.1 %, dominated by a representation limit that no resolution fixes.
- **The CPU backend is not a fallback, it is a good answer.** 286 ms for the whole pocket at 1024 × 768 on 16 threads
  is already interactive, and it runs in CI, in the browser and on a machine without a GPU. The CUDA backend buys
  190×, which matters for high resolutions, for many parts or poses at once, and for recomputing on every keystroke —
  not for a single preview of a single part.
- **The wrapper works and should be kept.** `nvcc` on sm_120 was no risk, `LibraryImport` stayed AOT compatible, the
  optional native build did not disturb `dotnet build` or CI, and writing the algorithm twice (C# and CUDA C) against
  a shared packed layout caught a real bug — see below.

### What the comparison caught

Writing the same minimum-over-the-segment twice, once in C# and once in CUDA C, and comparing the two cell by cell,
found a bug that the CPU-only tests had missed: for a horizontal step the discriminant of the stationary point is
exactly zero in theory but is computed as the difference of two float32 products of size 4·d²·w2², so it came out
slightly negative about half the time and the whole step was dropped for that cell. Both backends had it; the CUDA one
just tripped over it first. Both discriminants were then clamped at zero.
`tests/Stykker.NanoCut.Tests/GpuZMapTests.cs::LongHorizontalStepIsNotDroppedByFloatCancellation` pins it.

That clamp treated a symptom. The cancellation was in the formulation itself, and the next section is what an
independent check found in it.

### Independent verification: long steps (2026-10-03)

A separate harness (not part of the repository) ran both backends against a double-precision reference that finds
the minimum by a different method (golden-section search on the convex bottom curve, itself checked against dense
sampling with 2 million positions per step): 300 random scenes with dwells, plunges, jumps below the stock bottom,
moves of 10⁻⁴ mm and of 500 mm, balls from 0.001 to 15 mm, boxes up to 5 m from the origin and 1-cell strips, about a
million cells in all; then single straight steps by length (1 to 1000 mm), radius (0.01 to 10 mm) and slope.

It found that the bottom of a long step was wrong in both backends alike. The old form expanded
S(t) = r² − p² + 2·d·t − w2·t² around the start of the step, so on a step of length L every term was of order L² while
S is of order r², and float kept little of it. Counting only columns that are more than a few float units from the rim
(closer than that the column may count as inside or outside, which is a lateral question, not an error of the
formula):

| Single step | Old form, worst error | New form, worst error |
| --- | ---: | ---: |
| L = 100 mm, r = 1 mm, horizontal | 0.018 mm | 4·10⁻⁷ mm |
| L = 200 mm, r = 1 mm, horizontal | 0.070 mm | 4·10⁻⁷ mm |
| L = 50 mm, r = 0.1 mm, slope 0.3 | 15 mm left standing | 8·10⁻⁷ mm |
| L = 1000 mm, r = 1 mm, slope 0.05 | 52 mm left standing, 0.31 mm cut below the ball | < 10⁻⁶ mm |
| any of L ≤ 1000 mm, r = 0.01…10 mm, slope 0…0.3 | up to 300 mm | ≤ 1.2·10⁻⁵ mm |

The cut below the ball came from the tangent candidates, which were taken without checking that the ball reaches the
column at that parameter; the old comment that an extra candidate "can only be too high" did not hold for them.

The bench scenes did not show it because their steps are 0.75 mm long; a toolpath straight from a CAM program, with
one G1 move per line, would have. The new form (`ToolProfile.Bottom`, `ball_bottom` in `zmap.cu`) measures from the
point of the step line closest to the column: with a² = r² − e² the bottom curve is
g(t) = z0 + wz·t − √(a² − w2·(t − t*)²), convex, and its minimum is the stationary point t* − c·a clamped to the
valid interval. No intermediate is a difference of terms of order L². It is shorter than the old one, the packed
layout keeps its 12 floats, and `pocket-large` gives the same volumes to the last digit at every grid size and the
same times (2.4 ms wall, 1.5 ms kernel at 1024 × 768).

What else the harness checked, and found in order: CUDA and CPU agree within the rim-dependent bound of the fma
contraction; the same steps applied in one call or in random chunks with the field left on the device give the same
bits, and so do two runs; a missing device and a grid too large to allocate end in an exception, not a crash; 300
maps of 16 MB created and dropped leave the device memory where it was. One host inconsistency was fixed with it: the
host volume sum used the top in double while the field starts at the top in float, so an untouched stock reported
the rounding of the top times its area as removed (−0.003 mm³ on an awkward box); it now uses the float top, as the
device does.

`LongStepsMatchTheExactBottom` (eight cases, CPU) and `CudaLongStepsAgreeWithTheCpuReference` pin the long steps,
`UntouchedStockRemovesNothingOnAnAwkwardBox` the volume.

### The same pocket as a CAM program would send it

`bench/scenes/pocket-large-g1.json` is the `pocket-large` path with one straight move per path segment: 13 moves of
4 to 90 mm instead of 876 steps of 0.75 mm. The swept region is the same set, so it is a check on both kernels at once.
Same machine, warm, best of runs:

| | pocket-large (876 steps) | pocket-large-g1 (13 moves) |
| --- | ---: | ---: |
| Exact kernel, remaining volume | 84 860.612636583 mm³ | 84 860.612636583 mm³ |
| Exact kernel, time | 4162–4217 ms | 42 ms (median of 30, min 38) |
| Z-map 1024 × 768, remaining, new form (CPU = CUDA) | 84 770.457226 mm³ | 84 770.457226 mm³ |
| Z-map 1024 × 768, remaining, old form, CPU / CUDA | 84 770.457226 mm³ | 84 770.448744 / 84 770.450848 mm³ |
| Z-map 1024 × 768, largest CUDA − CPU height, old form | 0 | 1.25·10⁻³ mm (3.8·10⁻³ mm at 4096 × 3072) |
| Z-map 1024 × 768, CPU / CUDA wall, new form | 255–268 / 2.4 ms | 4.4 / 0.8 ms |

The exact kernel gives the same volume to the last digit, which is what an exact kernel should do with the same set
cut in a different number of pieces. With the new form the preview does the same at every grid size from 128 × 96 to
4096 × 3072; with the old form it did not, and its two backends disagreed by up to 3.8 µm. This scene is horizontal
with r = 3 mm, the mildest case of the table above; a ramp or a small ball would have been off by far more.

The timings change the picture of the comparison more than the accuracy does. The exact kernel costs per *cut*, not
per millimetre: 13 long cuts take 42 ms, 876 short ones 4.2 s, for the same result. The Z-map costs per cell and
step, so it gains only the 67-fold drop in steps. Against a toolpath of long moves the preview is therefore about 10×
(CPU, 4.4 ms) and 50× (CUDA, 0.8 ms wall) faster than the exact result, not the 16× and 1860× of the finely stepped
scene; the large factors measure how expensive many tiny exact cuts are, not how cheap the preview is.

The 42 ms are a median of 30 runs. A scene this short scatters by a factor of three to four between single runs (38
to 170 ms, with any parallelism, also with `--par 1`); an earlier version of this section gave 72–113 ms from too few
runs. For any scene that runs well under a second, use `--repeat 30` and quote the median with the minimum.

### Not done

- **A batch query is transfer-bound, and the fix was to stop preparing it on the host — and then to stop sending it.**
  Sampling a million points costs 0.514 ms against 5.873 ms for the same query on the CPU, but 0.421 of that 0.514
  is the 4 MB answer coming back; the upload is gone (`PointSet`, uploaded once) and the kernel is 0.061 ms, so what
  is left is the link. A cheaper answer would mean fewer bytes: half precision for a picture that is only shaded, or
  a renderer that consumes the field on the device instead of asking for it point by point. Neither is built. The pose
  query is the opposite and cannot be rescued at this size — 0.116 ms against 0.039 ms on the CPU, all of it
  round-trip latency — so it should stay on the CPU until a program asks about millions of poses.
- **A first call is not a measurement.** Every query number in this file needed a warm-up to mean anything; the first
  table was off by a factor of ten because the bench called each query exactly once on freshly allocated arrays. The
  bench now warms up and prints the cold call beside the warm one. A second, quieter version of the same mistake is
  in the CPU backend, which reported a loop it had already finished copying for; the bench now also times the call
  from outside, because a number that excludes work is not a wall time.
- **No multi-GPU, no streams, no overlap of transfer and compute.** One kernel on the default stream is all this
  prototype needs.
- **The Z-map cannot represent overhangs or a tool buried in the stock**, and does not try. That is the standard
  height-field convention, it is documented on `ZMap` and
  `GpuZMapTests.AToolBuriedInTheStockIsTheKnownZMapLimit` asserts the behaviour instead of hiding it.
- **The read-back still copies the whole field.** A caller that only needs the volume after every batch never pays it
  (`ZMapReadBack.Never` plus `BackendRemovedVolumeMm3`), but a caller that wants a *picture* of the field while it is
  being cut still has to take the whole copy, and a partial copy (a row band, say) is not built.
- **Part 1 of the plan, the server mode** (`samples/Stykker.NanoCut.Server`, geometry on the server, progress over
  SignalR, cancellable) is still not built; only the GPU half is done.
- **ILGPU was not used at all**, as decided in `docs/server-gpu-plan.md`. Nothing here depends on it.

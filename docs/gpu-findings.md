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

At 1024 × 768, warm, best of 5, the cold first call in brackets:

| Query | Input | CPU wall | CUDA kernel | CUDA upload | CUDA download | CUDA wall | Agreement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| heights at 1 000 000 points | 16 MB | 3.474 ms (4.007) | 0.064 ms | 1.658 ms | 0.428 ms | **2.187 ms** (3.489) | max Δh 1.9e-6 mm |
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
processes the order flips (0.361 pinned against 0.360 pageable in one run, 0.494 against 0.380 in another), which is
what two equal measurements look like. The large-transfer numbers above say why: 6.3–8.7 GB/s in both directions is
this link's ceiling, and a copy that the driver stages through its own buffer reaches the same place. So there is
nothing to win here, and the pinned buffer is off by default. On a machine where a copy really does hit the bus limit (a
server with a passive root port and a GPU, or a much larger grid) the pinned path stays available in the wrapper.


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
just tripped over it first. Both discriminants are now clamped at zero, which is safe because every candidate
parameter with S(t) ≥ 0 describes a real ball position and can only be too high.
`tests/Stykker.NanoCut.Tests/GpuZMapTests.cs::LongHorizontalStepIsNotDroppedByFloatCancellation` pins it.

### Not done

- **A batch query is transfer-bound, and the fix that made it pay was to stop preparing it on the host.** Sampling a
  million points now costs 2.187 ms against 3.474 ms on the CPU, but 2.086 ms of that is PCIe traffic; the kernel is
  0.064 ms. The pose query is the opposite and cannot be rescued at this size — 0.116 ms against 0.039 ms on the CPU,
  all of it round-trip latency — so it should stay on the CPU until a program asks about millions of poses. What would
  help both is a query that lives on the device between calls (upload the point set once, then sample it repeatedly),
  which is the obvious next step and is not built.
- **A first call is not a measurement.** Every query number in this file needed a warm-up to mean anything; the first
  table was off by a factor of ten because the bench called each query exactly once on freshly allocated arrays. The
  bench now warms up and prints the cold call beside the warm one.
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

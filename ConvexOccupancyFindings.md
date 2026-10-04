# What the convex kernel actually spends its time on: lanes and L2, not arithmetic

**Date:** 2026-10-04
**Branch:** `worktree-quick-elm-a7a7ca`, after `012c8c1`.
**Machine:** AMD Ryzen 7 5800X3D, NVIDIA GeForce RTX 5070 Ti (sm_120), driver 617.14, CUDA 13.4, Nsight Compute
2026.3.0, Windows 11, .NET 10.
**Configuration:** `LongPrograms convex --steps 99200`, 400 × 400 columns over 20.0 mm at 0.05 mm cells, a ball of
r 0.20 mm as a polytope of 12 half-spaces, steps 0.050 mm apart. Profiled launch: grid (625,1,1) × (16,16,1), 160 000
threads, 5000 warps. `--launch-skip 1` skips the 8-step warm-up and lands on the measured convex launch; `--launch-skip
2` lands on the ball launch of the same kernel and the same program.

This document exists because the two levers built so far were chosen from instruction counts, and the profiler says
instruction counts are the wrong thing to count. The companion `ConvexEarlyOutFindings.md` §6 measured the envelope
lever losing the wall clock; this is the measurement that says why, and what to do instead.

---

## 1. The two arms of one kernel

`dexel_apply_binned_kernel` is launched three times per bench run with the same grid and the same program: once for the
warm-up, once for the convex tool, once for the sphere. The sphere path is the same code with `planes == nullptr`, the
same tile, the same step payload, and a span test roughly an order of magnitude cheaper. It is therefore the control
this kernel never had — same structure, different per-column work.

| | convex, 12 planes | ball, same kernel and program |
| --- | ---: | ---: |
| Duration | 4.60 ms | 0.470 ms |
| **Avg. active threads per warp** | **8.09** of 32 | **21.34** of 32 |
| Avg. not predicated off per warp | 7.64 | 20.40 |
| Warp cycles per issued instruction | 9.84 | 10.93 |
| L1/TEX cache throughput | 35.60 % | 60.64 % |
| L2 cache throughput | 17.83 % | 30.06 % |
| **Compute (SM) throughput** | **62.70 %** | 52.66 % |
| DRAM throughput | 0.82 % | 2.88 % |

Three things fall out of this, and they are not the same thing.

**The instructions are not slow.** Warp cycles per issued instruction is 9.84 against the sphere's 10.93 — within ten
percent, and the convex path is the *faster* of the two here. Whatever the linear program costs per instruction, it does
not cost what a stall would.

**The lanes are not there.** 8.09 of 32 threads are active per warp, against 21.34 for the sphere on the same data. The
convex path issues each instruction with a quarter of its warp alive. Nsight Compute's own estimate for fixing that is
**40.3 %**, the largest single number it offers on this kernel.

**The kernel is issue-bound, and only mildly stalled.** Compute at 62.70 % against memory at 32.74 %, and of the 9.84
cycles between two issued instructions 3.03 are a fixed-latency wait and 2.01 a long-scoreboard stall on L1TEX — 20 %
rather than a third. So the earlier reading of this kernel as "L2-bound" was wrong; it was taken from a build that was
not this one. **The waste is lanes, and lanes are an occupancy problem, not a bandwidth one.**

## 2. What the three levers have in common

`ConvexEarlyOutFindings.md` §6 has the numbers and, more usefully, the correction. The short version: the O(m)
envelope is correct, it cuts the special-function pipe by 38 %, and it is worth 2.4 % at twelve half-spaces and 4.5 % at
sixteen — against a 10 % loss at eight. Not taken.

Its first version ordered the lines with an insertion sort, which moves pairs through `gLo`/`gHi` in local memory, and
that version really was catastrophic: local **stores** went 8 883 619 → 43 152 403, l1tex throughput to 49.8 %, and it
looked like the lever had proved the kernel memory-bound. Ordering by repeated selection instead reads the same lines
about as often and writes none, and it recovered most of that. Two lessons, and the second is the one that generalises:
**a store on this kernel costs far more than a load**, so a change that trades arithmetic for local traffic has to be
judged on `op_local_st` and not on `op_local_ld` — the metric that was missing when the first verdict was written.

That is the general shape of this branch, and it has appeared three times:

| lever | what it optimised | what it cost | result |
| --- | --- | --- | --- |
| box early-out (`cc1fbc6`) | work per pair | four comparisons | **1.5–1.9×** |
| sign test in `Where` (`f950cee`) | 36 divisions | 1 compare | flat on the wall clock |
| stack envelope (built, not taken) | 38 % of the special-function pipe | 3× local stores, +4.9 % instructions | 2.4–4.5 % at m ≥ 12, −10 % at m = 8 |

The first lever cut work and that is why it is the only one that paid. The other two moved instructions around without
cutting work. **What this kernel needs next is less work per surviving column and fewer lanes left idle — not fewer
instructions.**

## 3. The levers this opens, in the order the evidence ranks them

### 3.1 Decouple the thread from the column, so the surviving columns form full warps

At 8.09 active lanes there are roughly 8 columns of a 32-column warp doing the linear program while 24 wait. Over the
whole step list of a tile the set of columns any step reaches is much larger than any single step's, so a block that
**decided once** which of its 256 columns are ever touched, compacted them into dense warps, and then ran every step over
those dense warps would fill them. The per-step early-out of `cc1fbc6` would still be there — it is what decides "ever
touched" — but it would be evaluated against the tile's whole step list rather than against one step at a time.

Cost and risk, honestly: this changes what a thread *is*, which is a structural change rather than an arithmetic one.
It also cannot be done by compaction alone if the tool footprint per step is small relative to the tile — the union
has to be large enough to pay, and that depends on the tool and the step spacing. **The measurement that decides it is
cheap**: count, per tile, how many of the 256 columns are touched by at least one of its steps. That number is the
speedup ceiling, and it costs one instrumented build before any of the restructuring.

### 3.2 Get the lines out of local memory

`gLo`/`gHi` are 2·m floats per thread, per column, rebuilt for every step. Their *slopes* do not depend on the column
(`ConvexEarlyOutFindings.md` §6.4), so per step and per tile they could live in shared memory, built once by the block
and read by all 256 columns. 256 bytes at m = 16, both dexel kernels report `SHARED:0` today, and the L1TEX stall this
attacks is 20 % of the cycles between instructions.

**The barrier is the catch, and it is a structural one.** `dexel_apply_column`'s loop condition is `s < last && n > 0`,
and `n > 0` is per thread, so a `__syncthreads()` inside that loop is a divergent barrier until that test is hoisted
out of it — which changes the loop's own cost. And the lever is for the binned launch only: in `dexel_apply_kernel`
every block walks all 99 200 steps for a couple of thousand useful pairs, so "one barrier per step" there is 99 200
barriers to amortise over almost nothing.

This is the lever the envelope experiment was reaching for and could not reach from inside the column loop. On its own
it is worth less than 3.1 — it makes the *existing* walk cheaper in traffic rather than making the kernel do less work
— but it composes: 3.1 without 3.2 still pays local-memory latency on a denser warp.

### 3.3 The per-pair cost that survives the early-out

The payload read, the branch, the `n > 0` test — the difference between 8.3 and 4.4 ms not being the 88 % the geometry
allows. With 3.1 and 3.2 this largely dissolves: both change what a pair costs rather than how many pairs there are.

## 4. What is not settled

- **The 40.3 % is Nsight Compute's estimate, not a measurement.** It is a rule of thumb over active-thread counts. The
  kernel's own numbers behind it are solid — 8.09 lanes, 9.84 cycles per issued instruction, compute 62.70 % — but the
  estimate of what fixing it is worth is the profiler's.
- **No occupancy measurement exists for a kernel that has the local arrays gone.** `launch__registers_per_thread` is
  56 with 256-thread blocks, which on this card is a comfortable occupancy; what is missing is the achieved one and
  what it would be if the frame shrank. `cuobjdump -res-usage` puts the frame at 520 B of the 1024 B post-Volta limit —
  and the frame is mostly *not* the lines: 256 B is `gLo` + `gHi`, 128 B the column's own intervals and 136 B
  `dexel_subtract`'s `out[]`.
- **The lane figure is for this bench's tool.** A larger tool, or coarser cells, or steps that move further, change the
  fraction of a tile that is touched and therefore the occupancy. The 8.09 belongs to a 0.4 mm tool on 0.05 mm cells,
  and §3.1's ceiling number would have to be measured per regime.
- **An earlier version of §1 in this document reported these figures for the wrong build.** They were taken while the
  bench still held the *envelope's* `nanocut_gpu.dll`, because the bench copies the native library into its own `bin`
  and `dotnet build` of the solution does not build the bench. That is trap four in
  `ConvexEarlyOutFindings.md` §9, walked into by the person who wrote it. The numbers above are from the reverted tree.
- **Nothing here has been built.** This document is a profile and a ranking.

## 5. Reproducing

```
"C:\Program Files\NVIDIA Corporation\Nsight Compute 2026.3.0\ncu.bat" --kernel-name regex:dexel_apply_binned_kernel --launch-skip 1 --launch-count 1 --section WarpStateStats --section SpeedOfLight dotnet bench/Stykker.NanoCut.LongPrograms/bin/Release/net10.0/Stykker.NanoCut.LongPrograms.dll convex --steps 99200 --repeat 1
```

`--launch-skip 2` instead of 1 lands on the ball launch, which is the control in §1. The bench's own printed kernel
times are distorted under the profiler (the kernel reads 716 ms there) — the Duration in the Speed Of Light table is the
number to use, 4.60 ms against the 4.2 ms the bench reports unprofiled at `--repeat 50`.

**Rebuild the bench before profiling it.** `dotnet build src/Stykker.NanoCut.Gpu.Native/build.ps1` is not enough: the
bench copies `nanocut_gpu.dll` into its own `bin`, and `dotnet build Stykker.NanoCut.slnx` does not build the bench, so
a native rebuild is invisible there until the bench itself is rebuilt. Getting that wrong is how §1 of this document
first reported the envelope's numbers as the walk's.
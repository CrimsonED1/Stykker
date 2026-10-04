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
| Duration | 4.40 ms | 0.470 ms |
| **Avg. active threads per warp** | **9.35** of 32 | **21.34** of 32 |
| Avg. not predicated off per warp | 8.77 | 20.40 |
| Warp cycles per issued instruction | 11.10 | 10.93 |
| L1/TEX cache throughput | 49.63 % | 60.64 % |
| **L2 cache throughput** | **66.36 %** | 30.06 % |
| Compute (SM) throughput | 55.55 % | 52.66 % |
| DRAM throughput | 3.82 % | 2.88 % |

Two things fall out of this, and they are not the same thing.

**The instructions are not slow.** Warp cycles per issued instruction is 11.10 against the sphere's 10.93 — within two
percent. Whatever the convex path costs per instruction, it costs about what the sphere path costs.

**The lanes are not there.** 9.35 of 32 threads are active per warp, against 21.34 for the sphere on the same data. The
convex path issues each instruction with 29 % of its warp alive. Nsight Compute's own estimate for fixing that is
**40.3 %**, the largest single number it offers on this kernel.

And the second row of the pair: the convex path drives L2 at 66.4 % while the sphere drives it at 30.1 %. L2 is the
busiest unit in the kernel; DRAM is at 3.8 % and irrelevant. What fills L2 is local memory — `gLo` and `gHi` are
per-thread arrays, and local memory is global memory behind an L1 hit. The stall statistics agree:
**3.9 of the 11.1 cycles between two issued instructions are a long-scoreboard stall on L1TEX**, 35 % of the total, and
Nsight Compute's estimate for removing it is 35.0 %.

So the convex kernel is neither arithmetic-bound nor DRAM-bound. It is **lane-occupied at 29 % and pushing two thirds of
its traffic through L2**, and those two are the same problem seen twice: one thread per column means the surviving
columns are scattered across the warp and each of them carries its own copy of the lines.

## 2. This is why the envelope lost

`ConvexEarlyOutFindings.md` §6 has the numbers: the O(m) envelope cut instructions by 15.3 % and the special-function
pipe by 37.9 %, and the kernel got 17 % slower. The stall statistics above are the mechanism.

The envelope replaces arithmetic with a sort, and the sort moves pairs through `gLo`/`gHi` in local memory with
data-dependent indices. Local loads went 73 140 173 → 86 666 986, **+18.5 %**, into a unit already at 66 % of peak, and
instruction throughput fell from 64.4 % to 54.4 % — the kernel stopped being issue-bound and became memory-latency
bound instead. It bought instructions with the one resource the kernel had least of.

That is the general lesson of this branch, and it is the third time the same shape has appeared:

| lever | what it optimised | what it cost | result |
| --- | --- | --- | --- |
| box early-out (`cc1fbc6`) | work per pair | four comparisons | **1.5–1.9×** |
| sign test in `Where` (`f950cee`) | 36 divisions | 1 compare | flat on the wall clock |
| stack envelope (built, reverted) | 15 % of instructions | 18.5 % more local traffic | **slower** |

The first lever cut work. The second and third moved instructions around without cutting work, and paid for it in
traffic. **What this kernel needs next is less work per surviving column and fewer lanes left idle — not fewer
instructions.**

## 3. The levers this opens, in the order the evidence ranks them

### 3.1 Decouple the thread from the column, so the surviving columns form full warps

At 9.35 active lanes there are roughly 9 columns of a 32-column warp doing the linear program while 23 wait. Over the
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
(`ConvexEarlyOutFindings.md` §6.2), so per step and per tile they could live in shared memory, built once by the block
and read by all 256 columns. 256 bytes at m = 16, both dexel kernels report `SHARED:0` today, and the L1TEX stall this
attacks is 35 % of the cycles between instructions.

This is the lever the envelope experiment was reaching for and could not reach from inside the column loop. On its own
it is worth less than 3.1 — it makes the *existing* walk cheaper in traffic rather than making the kernel do less work
— but it composes: 3.1 without 3.2 still pays local-memory latency on a denser warp.

### 3.3 The per-pair cost that survives the early-out

The payload read, the branch, the `n > 0` test — the difference between 8.3 and 4.4 ms not being the 88 % the geometry
allows. With 3.1 and 3.2 this largely dissolves: both change what a pair costs rather than how many pairs there are.

## 4. What is not settled

- **The 40.3 % is Nsight Compute's estimate, not a measurement.** It is a rule of thumb over active-thread counts, and
  the two figures it quotes (35.0 % from the stall, 40.3 % from occupancy) do not compose — they are not additive and
  the kernel has only one to give.
- **No occupancy measurement exists for a kernel that has the local arrays gone.** `launch__registers_per_thread` is
  56 with 256-thread blocks, which on this card is a comfortable occupancy; what is missing is the achieved one and
  what it would be if the frame shrank. `cuobjdump -res-usage` puts the frame at 520 B of the 1024 B post-Volta limit.
- **The lane figure is for this bench's tool.** A larger tool, or coarser cells, or steps that move further, change the
  fraction of a tile that is touched and therefore the occupancy. The 9.35 belongs to a 0.4 mm tool on 0.05 mm cells,
  and §3.1's ceiling number would have to be measured per regime.
- **Nothing here has been built.** This document is a profile and a ranking, written after the envelope revert and
  before anything was tried on the strength of it.

## 5. Reproducing

```
"C:\Program Files\NVIDIA Corporation\Nsight Compute 2026.3.0\ncu.bat" --kernel-name regex:dexel_apply_binned_kernel --launch-skip 1 --launch-count 1 --section WarpStateStats --section SpeedOfLight dotnet bench/Stykker.NanoCut.LongPrograms/bin/Release/net10.0/Stykker.NanoCut.LongPrograms.dll convex --steps 99200 --repeat 1
```

`--launch-skip 2` instead of 1 lands on the ball launch, which is the control in §1. The bench's own printed kernel
times are distorted under the profiler (the kernel reads 716 ms there) — the Duration in the Speed Of Light table is the
number to use, 4.40 ms against the 4.2 ms the bench reports unprofiled.
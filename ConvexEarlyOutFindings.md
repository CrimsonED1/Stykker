# Convex early-out and the division in `Where`: two levers, measured

**Date:** 2026-10-04
**Branch / commits:** `worktree-quick-elm-a7a7ca`, `cc1fbc6` (the box early-out) and `f950cee` (`Where` without the
division), both on top of `f39a8ed`.
**Machine:** AMD Ryzen 7 5800X3D, NVIDIA GeForce RTX 5070 Ti (sm_120), driver 617.14, CUDA 13.4, Windows 11, .NET 10.
**Bench configuration** unless a table says otherwise: `LongPrograms convex --steps 99200`, 400 × 400 columns over
20.0 mm at 0.05 mm cells, a ball of r 0.20 mm as a polytope of `--planes` half-spaces, steps 0.050 mm apart, no turn,
best of 3.

These are the first two of the four levers in `ConvexKernelFindings.md` §0, and the measurements were taken **before**
deciding whether to go on. One of the four claims that analysis rests on turned out to be wrong by a factor of three,
and it is the wrong one that decided the order. The detail is in §3; the short version is that the divisions were never
a third of the instruction stream, they were 3.7 % of it.

---

## 1. What was built

### 1.1 `cc1fbc6` — the early-out against the box the step already carries

The binning hands a step to every 16 × 16 tile it touches and there was no per-column pre-test, so a column inside an
assigned tile but outside the swept box ran the whole linear program to be told "no". The box is the first four floats
of the packed step and the kernel never read them. Four comparisons now settle it, at the top of `convex_span`
(`zmap.cu:994`) and mirrored at the top of `ConvexProfile.Span` (`ConvexProfile.cs:180`).

The window is the one `StepBins.Tiles` already tiles with, `MarginMm` included, and that is the load-bearing detail.
The early-out must never be stricter than the binning that put this thread here, or a column the binning counted would
go uncut. `StepBins.MarginMm` is `internal` now and both sides read it, so the two windows cannot drift apart.

### 1.2 `f950cee` — `Where` asks whether the bound moves before it divides

`convex_where` narrowed t's range with one division per pair. Whether `-k/s` lies under `tHi` is the question
`k + s·tHi >= 0` when `s > 0`, and whether it lies over `tLo` is the same form when `s < 0`; neither needs the
quotient. The comparison is `>=` and not `>` so that a pair whose crossing sits exactly on the end is narrowed as it
was before.

### 1.3 The two tests, and what they are for

`EveryColumnAStepReachesIsInsideItsOwnBox` walks exactly the (step, column) pairs the binned launch hands the kernel —
`StepBins` decides the tiles — and asserts that every column a step reaches lies inside that step's own box. That is
the **premise** the early-out rests on, and it is a property of the geometry rather than of the code using it. It also
asserts the box rejects something, so it cannot pass vacuously: an octahedron grain on 0.1 mm cells gives 6400 binned
pairs of which 768 reach the tool, **88.0 % rejected**. That share belongs to that tool on that grid.

`ATiltedToolThatTravelsAgreesWithTheExactCut` gives the pair loop the geometry it needs. `Where` only has a slope to
ask about when the tool travels, and a tilt about x with travel along x leaves every dot at zero and the new branch
unreached, so this case tilts 0.3 rad about x and travels 4 mm along y: `nLo = nHi = 2`, four pairs with `s` non-zero
on both sides. Against the exact kernel on three grids: 26.0999 / 25.0970 / 25.0994 against 25.0993 mm³.

**It does not, however, pin the fire condition, and this document said it did.** §7 records the correction: both ends of
the interval come out at `t = tLo = 0` in that geometry, so narrowing `tHi` changes nothing that is read, and four
wrong implementations of the rewrite produce zero differing columns. The only test that separates them is
`CudaAgreesWithTheCpuReference`, which needs a device.

The suite does check the sign rewrite rather than merely accompany it: inverting the comparison in
`ConvexProfile.Where` alone, with the CUDA side untouched, makes `CudaAgreesWithTheCpuReference` fail at 8 planes with
intervals 10.6 mm apart, and pass at 6.

---

## 2. What it bought

| | baseline `f39a8ed` | `cc1fbc6` | `f950cee` |
| --- | ---: | ---: | ---: |
| binned convex kernel, 12 planes | 8.0–8.3 ms | 4.4–5.2 ms | **4.2 ms** |
| unbinned convex kernel, 12 planes | 1735.7–1790.9 ms | 76.0–81.2 ms | 78.0 ms |
| removed volume | 88.006673 mm³ | 88.006673 mm³ | 88.006673 mm³ |

The early-out is worth 1.5–1.9× on the binned launch and about 22× on the unbinned one, where it replaces 39 680 000
linear programs with 39 680 000 comparisons. The removed volume is the same to the last digit in every configuration
and at every plane count, which is the evidence that it removes nothing.

The binned arm gains less than the rejected share suggests, because a rejected pair still reads the step payload and
takes the branch. What survives the early-out — the fixed per-pair cost — is the next thing to look at.

`Where` without the division is not visible in the wall clock, and §3 says why.

---

## 3. The measurement that changed the order

`cuobjdump -sass` on the binned kernel, and Nsight Compute 2026.3.0 on the full-grid launch
(grid (625,1,1) × (16,16,1), 160 000 threads), the metric names verified against `--query-metrics` on this card:

| metric | `cc1fbc6` | `f950cee` | per warp |
| --- | ---: | ---: | ---: |
| `smsp__inst_executed.sum` | 2 092 634 735 | 2 002 763 793 | 400 553 |
| `smsp__inst_executed_pipe_alu.sum` | 518 961 026 | 521 211 291 | 103 792 |
| `smsp__inst_executed_pipe_xu.sum` | 78 260 184 | **42 396 362** | 15 652 → 8 479 |
| `smsp__inst_executed_op_local_ld.sum` | 73 140 173 | 73 140 173 | 14 628 |
| `sm__instruction_throughput` | 64.38 % | | |
| `launch__registers_per_thread` | 56 | | |
| `launch__shared_mem_per_block` | 1.02 KB | | |

Three things follow, and the first two are corrections to `ConvexKernelFindings.md`:

1. **The divisions were 3.7 % of the instruction stream, not "a third to more of it".** §2.1 of that document put
   `m(m+1)/2` IEEE divisions at roughly 1100–1600 instructions against ~3000 for the whole envelope arithmetic. The
   division is one XU instruction plus a Newton-Raphson tail on the ALU pipe; it is not ten instructions of XU. The
   analysis weighted a sequence as though it were a single op. `f950cee` removes 45.8 % of the XU work and 4.3 % of the
   instructions, and the wall clock does not move: 4.4 → 4.2 ms, inside the run-to-run spread of this bench on
   identical code (4.2 … 5.2 ms across processes).

2. **The kernel is issue-bound, not memory-bound.** 64.4 % of peak instruction throughput with 56 registers and no
   spill. So the lever to pull is the instruction count, and the largest single block of it is not the divisions but
   the O(m³) envelope walk in `convex_extremum` — every crossing of two of the m lines evaluated over all m of them.
   That is lever C in the old ordering, and the measurements promote it.

3. **SASS cannot see the `Where` lever at all.** 17 MUFU sites before and after, 2106 → 2118 instructions: the division
   is still compiled in and is now behind a test. Static counting would have reported this change as nothing, which is
   the argument for having run the dynamic counters rather than trusting the instruction count.

The local-memory figure is the other thing worth keeping: 14 628 local loads per warp against 103 792 ALU
instructions, which is the `gLo`/`gHi` arrays in local memory. `smsp__inst_executed_op_local_ld` is the metric to watch
if the envelope is ever moved into shared memory, because that is what would move this number.

---

## 4. The m-sweep, re-measured on both sides of the early-out

The handoff asked for a fifth half-space count to test the `289.4 + 11.63·m²` model against
`ConvexKernelFindings.md` §10.1. Measured, unbinned kernel at 99 200 steps, baseline code:

| m | 6 | 8 | 10 | 12 | 16 |
| --- | ---: | ---: | ---: | ---: | ---: |
| baseline unbinned | 637.8 | 973.6 | 1328.4 | 1790.9 | 2980.0 |
| baseline binned | 2.3 | 3.5 | 5.4 | 8.3 | 12.9 |
| `cc1fbc6` binned | 1.4 | 2.0 | 3.1 | 4.4 | 7.1 |
| `cc1fbc6` unbinned | 76.0 | 75.4 | 78.2 | 76.9 | 81.2 |

The model holds and the fifth point lands on it: a least-squares fit of `c + a·m²` to the five baseline numbers gives
**273.6 + 10.57·m²** and reproduces all five within ±2.5 %. The constant share is 43 % at m = 6 and 9 % at m = 16,
which is what the document already claimed from four points. The exponent is 2, not 1.57, and the reason is the
constant that a power law hides.

**But that model describes the code as it was before `cc1fbc6`, and after it the unbinned arm no longer scales with m
at all** — 76.0 ms at six half-spaces against 81.2 ms at sixteen is 7 %, where the quadratic term alone would be 2.8×.
What is left there is reading the step payload and comparing. So:

- the binned kernel with the early-out is **0.45 + 0.0263·m²** ms, constant plus quadratic as before, the constant
  32 % of the runtime at m = 6 and 6 % at m = 16;
- the lever that matters from here is the **quadratic** term, because that is where `Where` and `convex_extremum` live;
- the per-step constant is 0.45 ms of a 4.4 ms kernel, so hoisting the per-step preparation into shared memory
  (lever D) is bounded by about a tenth of this kernel at best, and less at the plane counts a real tool uses.

---

## 5. What this leaves open, in the order it should be taken

1. **The O(m³) envelope walk (`convex_extremum`) is now the largest identified block of instructions.** At a hull vertex
   the envelope is *one* line rather than the maximum over m, so the inner loop disappears. The trap is documented in
   `ConvexKernelFindings.md` §7.1 and repeated here because it is the one that bites: the minimum must **not** be
   accumulated while the stack is being built, because a line popped later makes the accumulated value the envelope of
   the lines seen so far, which is below the final one — a `low` that is too low removes material the tool never cut.
   Two passes, build then accumulate. This is now first.
2. **The per-pair cost that survives the early-out** — the payload read, the branch, the `n > 0` test. It is the whole
   difference between 8.3 and 4.4 ms not being the 88 % the geometry allows.
3. **Lever D, the per-step preparation in shared memory.** Bounded by the 0.45 ms constant above. 256 bytes at m = 16,
   and the block already has 1.02 KB, so shared memory is not the constraint; the `__syncthreads()` per step is.
4. **`CpuBackend`'s convex path is still columns × steps.** The early-out gives it the same shape of win the kernel got —
  a miss costs four comparisons and a branch instead of the whole O(m³) program — but its asymptotics are unchanged and
  **no measurement of the CPU-side win exists**. `LongPrograms convex` measures the device. Before anyone writes one:
  `CpuBackend.ApplyConvexDexels` (`CpuBackend.cs:227-259`) has no pack stopwatch and returns a `ZMapTiming` whose
  `PackMs` is 0, while `ApplyDexels` times its pack — so on the convex path the two `WallMs` columns would still cover
  different spans, which is the whole reason `PackMs` exists.

## 6. Not done, and one thing that is not settled

- **An intermittent failure in the CPU planar path, observed once in six full-suite runs.**
  `ProcessTests.RackGeneratedGearHasInvoluteFlanks` failed with a flank deviation of 5327 nm against that test's 300 nm
  tolerance. It does not reproduce in isolation (five solo runs green, 3 s each) and did not appear in four baseline
  runs of `f39a8ed`. It is `Process2.Cut`, which neither commit touches — but this branch's own `376f705` made that
  path run the union and the subtract on two strands at once, and a result that depends on scheduling would look
  exactly like this. Not established and not dismissed. It belongs next to that commit, not to these two.
- The sphere path (`swept_span`) has no box early-out: its payload carries a position and a radius, not a box. It is
  19× cheaper than the convex path and was not worth the change.
- No `ncu` section other than the metrics above, no roofline, no occupancy work. The launch configuration is 56
  registers and a 16 × 16 block, which is fixed by the tile.

---

## 7. What the verifier found, and the one defect it was right about

A read-only verifier agent went over `cc1fbc6` and `f950cee` afterwards. Most of it confirmed what §2 and §3 say, and
three things in it were wrong on my side. They are recorded here rather than folded away.

### 7.1 The margin was too small, and that is a defect `cc1fbc6` introduced

`StepBins.MarginMm` was 1e-6 mm, and that number has to outlast the coordinate's own float resolution, because that is
what eats it: `fl(c - margin) == c` as soon as `ulp(c)` exceeds twice the margin. For 1e-6 mm that is already the case
at **32 mm** from the grid origin — and because the rounding accumulates over the corner, the width and the two ends,
the far edge of the grown box falls *inside* the body already at 17 mm.

`TheGrownPackedBoxAlwaysContainsTheExactOne` walks it out and fails on a **20 mm map**, the size this document's own
bench runs at:

```
grown float box [17.349998474, 18.649999619] x [17.401308060, 18.598690033]
exact       box [17.350000000, 18.650000000] x [17.401310354, 18.598689646]
```

The far x edge is 3.81e-7 mm short of the body. A column centre landing in that sliver would have been cut before
`cc1fbc6` and is not cut with it, so the early-out could lose material. Fixed by raising the margin to **1e-4 mm** on
both sides, which holds to about a metre and is a fifth of a 0.05 mm cell; the bench is unchanged at 4.2 ms and
88.006673 mm³. The test recomputes the box in double independently, on purpose: a test that repeated `Pack`'s own
arithmetic could only confirm that the packing agrees with itself.

Note what was already exposed before `cc1fbc6`: `StepBins` had tiled with the same too-small margin since it was
written, so a step whose box rounded inward could already have been left out of a tile. The early-out only made the
box authoritative for correctness as well as for routing.

### 7.2 Three claims of mine that do not hold

- **The `>=` justification was a non-reason.** "A pair whose crossing sits exactly on the end is narrowed as it was
  before" — at exact equality `min(tHi, tHi) = tHi`, so `>` and `>=` cannot differ there. They differ only where the
  *computed* sum lands on exactly `0.0f` while the exact sum has not, and `>=` is then marginally the more faithful
  choice. Measured: **0 of 1 312 500 columns** differ between the two. The choice is right, the reason was not, and
  both comments now say the right reason.
- **The tilted test does not pin the `Where` rewrite**, and §1.3 said it did. Four wrong implementations produce zero
  differing columns on it and on both other `Process3` cases, for the reason in §1.3. The suite's actual witness is
  `CudaAgreesWithTheCpuReference`, which is a CUDA-versus-CPU comparison and cannot run without a device — so on the
  CPU alone the rewrite is unverified, and this document should not have implied otherwise.
- **"Both sides read the margin, so the two windows cannot drift" is only true of the two C# sites.** The CUDA side has
  its own literal, `kConvexBoxMarginMm`, because C++ and C# share no header here. The comment now says so plainly
  instead of claiming a guarantee the build does not provide.

### 7.3 What the verifier confirmed

The binning window and the early-out window are **bit-identical** float expressions — same tree, same left
association, and `2 * margin` is exact on both compilers — so they cannot round apart. The algebra of both `Where`
branches is right, and it fires on a superset of the exact firing set, differing only at exact zero. The two backends
still mirror each other statement for statement, including the early return; the two cosmetic asymmetries (the C# zeroes
`low`/`high` where the kernel leaves them uninitialised) are harmless because the caller branches on the bool.

### 7.4 One new exposure the verifier named

`f950cee` makes `k + s·tHi` a **branch predicate**, and that is the first place in `Where` where nvcc's `-fmad=true`
contraction can change an *outcome* rather than a last bit: `fma(s, tHi, k)` rounds once, RyuJIT's `k + s*tHi` twice,
and when the exact sum sits within about half an ulop of zero they can take opposite branches — and the branch
decides whether the range narrows. Before this commit the only float operation in `Where` was the division, which is
correctly rounded and therefore identical on both sides. Measured rate: **0 of 107 252** pair evaluations differ on
the new geometry; the general order is ~1e-9 per pair. Rare, new, and covered by nothing — it is the one CPU/CUDA
disagreement class in the convex path that has no test behind it.

---

## 8. Reproducing

```
powershell -ExecutionPolicy Bypass -File src\Stykker.NanoCut.Gpu.Native\build.ps1
dotnet build bench\Stykker.NanoCut.LongPrograms -c Release
dotnet bench\Stykker.NanoCut.LongPrograms\bin\Release\net10.0\Stykker.NanoCut.LongPrograms.dll convex --steps 99200 --planes 12 --repeat 3
"C:\Program Files\NVIDIA Corporation\Nsight Compute 2026.3.0\ncu.bat" --kernel-name regex:dexel_apply_binned_kernel --launch-skip 1 --launch-count 1 --metrics smsp__inst_executed.sum,smsp__inst_executed_pipe_alu.sum,smsp__inst_executed_pipe_xu.sum,smsp__inst_executed_op_local_ld.sum,sm__instruction_throughput.avg.pct_of_peak_sustained_elapsed,launch__registers_per_thread,launch__shared_mem_per_block dotnet bench\Stykker.NanoCut.LongPrograms\bin\Release\net10.0\Stykker.NanoCut.LongPrograms.dll convex --steps 99200 --repeat 1
```

Four traps, all of which cost a wrong number before they were found:

- **The bench keeps its own copy of `nanocut_gpu.dll`, and `dotnet build` of the solution does not build the bench.**
  A native rebuild is invisible to the bench until the bench itself is rebuilt. The m-sweep in §4 was first run against
  the baseline binary and had to be thrown away.
- **`ncu --launch-count 1` profiles the warm-up, not the case.** The bench warms the context with an 8-step program
  whose launch has grid (1,1,1); `--launch-skip 1` is needed to reach the measured one. The first profile run reported
  480 ALU instructions and a 0.06 % instruction throughput, both of which were the warm-up's.
- **`dotnet test` changes its summary format when the output is redirected**, so a `findstr` on the interactive summary
  matches nothing on a logged run.
- The shell here is cmd.exe: no heredocs, and `|` inside a `for (...)` block has to be escaped.
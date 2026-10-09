# Research handoff — worktree `keen-elm-b95ffa`

**Written:** 2026-10-04
**Branch / commit:** `worktree-keen-elm-b95ffa` @ `437612f`, based on `861c6eb` (= `origin/main` at the time)
**State:** working tree clean, **nothing pushed**, no source file touched, nothing built, no test or bench run.
**Purpose of this session:** research new approaches for the current GPU work and hand usable results to whoever
implements next. Read-only everywhere else.

> **Language note.** `ConvexKernelFindings.md`, produced by this session, is in German. The project's own convention
> (`docs/handoff.md`, header) is „Code, comments and docs in English; the user reads German", and every other findings
> file in the repo (`Step3KernelFindings.md`, `Step4FeasibilityFindings.md`, `CudaLongProgramsFindings.md`) follows it.
> This file follows the convention. Converting the findings document is a deliberate decision, not an oversight —
> whoever owns the branch should decide.

---

## 1. What is committed

Four commits, all documentation, all on this branch only:

| Commit | Content |
| --- | --- |
| `b5eddfd` | Where the convex kernel's cost is: the `m(m+1)/2` IEEE-division count, per-step column independence, the free early-out |
| `a352a01` | Two corrections to `b5eddfd` (see §4), and the regime question |
| `3e195dc` | The envelope research, plus the CUDA documentation loaded locally and checked against the source |
| `437612f` | The `m^1.57` was the wrong model; `-fmad=false` is F7; the early-out numbers corrected |

The detail is in **`ConvexKernelFindings.md`** (§0 summary, §1 the regime, §2 divisions, §3 column independence,
§4 early-out, §5 per-step preparation, §7 the two research agents, §8 open question, §9 ordering, §10 the three
answers, §11 what was not done). Every claim there is tagged **[geprüft]** (read from source, with file:line),
**[rechnet]** / **[abgeleitet]** / **[zu messen]**.

The CUDA Programming Guide (11 pages, ~2.1 MB) is on disk under `.qwen/refs/cuda-programming-guide/` in this
worktree. **Not committed** — it is there so it can be grepped instead of re-fetched. It is untracked, not ignored;
delete it freely.

## 2. The four things worth knowing

1. **`convex_span` performs exactly `m(m+1)/2` IEEE divisions per (column, step)** — 136 at m = 16, independent of
   how the half-spaces split between the two bounds. The build carries nvcc's defaults (`build.ps1:81` sets no
   `--prec-div=false`), and the vendor documentation confirms `--prec-div=true` is IEEE round-to-nearest. The
   count is a closed form: `m + C(nLo,2) + nLo·nHi + C(nHi,2) = m + C(m,2)`. Two independent derivations reached it.

2. **The measured `m^1.57` is the wrong model.** The data is `constant + quadratic`, not a power law:
   `289.4 + 11.63·m²` fits all four measured points within 2.1 %, and a log-log least squares through the same
   points gives 1.558 — which reproduces the documented 1.57. **The constant is 41 % of the runtime at m = 6** and
   9 % at m = 16. That constant is the per-step rotation, the two dot products and the `1/mz` division, none of
   which depend on the column or on m. The exponent question had been hiding it.

3. **The step-4 regime is ~19 000 steps, 40× shorter than the bench**, so the 372.6 ms wall in
   `docs/performance.md:275` is a bench artefact — 289 ms of it is host work. The kernel work is still aimed at the
   right regime, because in the step-4 geometry the cost is driven by the grid (`Step4FeasibilityFindings.md:290-312`):
   5.9 ms at 0.01 mm cells, 592 ms at 0.001 mm.

4. **The cheapest lever is four comparisons.** Each step's swept bounding box is already in the payload
   (`ConvexProfile.cs:126-136`), the binning assigns it by **tile**, and there is no per-column pre-test — so columns
   inside an assigned tile but outside the box pay the full cost for a `false`. Rejection rate: **43 % on average**
   (19–70 % by alignment), computed from the tile geometry. Independent of the envelope, no change to the mathematics.

## 3. The one thing that is not mine to decide — a stranded patch

**`step2-followups.patch` has been sitting unapplied in the repo root since 2026-10-03 23:42**, committed by `34a06e4`
(„Docs: a read-only review of the long-programs GPU work"). Verified: it is **not applied** — `PackMs` appears nowhere
in `src/Stykker.NanoCut.Gpu`, and `docs/long-programs.md:83-84` still carries the false order-independence claim
verbatim. And verified: **`git apply --check` returns 0 for all five files**, so it still applies cleanly today.

It closes two documented open items:

| File | Change |
| --- | --- |
| `docs/long-programs.md` | Corrects „the order of steps inside a tile does not matter" — which `docs/todo.md` lists as open |
| `tests/…/DexelMapTests.cs` | Adds a **non-zero precondition** to the binned-vs-unbinned test, so `0 == 0` cannot pass without the capacity guard firing |
| `src/…/ZMapTiming.cs` | New `PackMs` field + docs + `operator +` |
| `src/…/CpuBackend.cs`, `CudaBackend.cs` | A pack stopwatch, deliberately outside the CPU one, so both `WallMs` columns cover the same span |

The second item is `CudaLongProgramsFindings.md:321-322`; the third is `docs/todo.md`'s „Time the call as well as the
work inside it" (GPU preview follow-ups), which records the open question „whether `CpuBackend` should report its
staging in `WallMs` is undecided".

**The defect is not the patch, it is the missing pointer.** Nothing in `docs/todo.md` references the file, so a later
session has no way to find it — and the implementer working in parallel is plausibly about to redo both items.

**Suggested, not done:** one line in `docs/todo.md` at each of the two entries pointing at the patch file.

## 4. Corrections this session made to its own earlier claims

Recorded because the project's culture is to document them, and because two of them were repeated to the user before
they were caught.

- **`MaxPlanes = 16` is not bounded by local memory.** The first version of the findings argued this from
  `ConvexProfile.cs:112-121`. It was wrong, and the point was **already settled**: `Step3KernelFindings.md:275-278`
  and `:321` state that the „under 512 bytes a thread may use" comment is wrong twice — the arithmetic omits
  `dexel_subtract`'s `out[34]` (520 B real, not 384 B), and 512 B is the pre-Volta limit while `build.ps1` targets
  `sm_120` and `compute_75`, both ≥ 7.0, where it is 1024 B. What actually bounds the 16 is the instruction count,
  which is open.
- **The 289 ms host share applies to the bench only.** The first version conflated columns of
  `docs/long-programs.md:161-165` because the header was missing from the excerpt.
- **`-fmad=false` is not new.** It is **F7**, documented four times: `Step3Verification.md:190-194` and `:275-276`,
  `Step3KernelFindings.md:70-71` and `:320`, `CudaLongProgramsFindings.md:756-757` and `:783`. The actual gap is
  stated verbatim at `CudaLongProgramsFindings.md:327-329` — there is no `-fmad=false` build, so **„the one control
  that would separate a real bug from contraction does not exist"**, listed as absent at `:369`. The recommendation
  is therefore to **build the A/B control, not to flip the flag**. The flag would not fix **F8** (`fmaxf`/`fminf`
  swallow NaN, `MathF.Max`/`MathF.Min` propagate it), and it is global, so it also hits `swept_span` and the
  step-2 ball baseline.
- **The early-out numbers were too high** (58 % → **43 %**), and the silhouette pre-test was over-sold: the swept
  octahedron fills its own box to **86 %** (`w²/2 + L·w` against `w(L+w)`, with L = 0.349 mm and w = 0.13 mm), so it
  rejects 14 %, and the fill ratio goes to 1 as the step gets longer — which is what both the step-4 geometry and the
  bench are. Dropped from the lever table.

## 5. Two proposals waiting for a decision

Neither is written; neither is committed.

1. **A pointer in `docs/todo.md`** to `step2-followups.patch` at the two open entries (§3). One line each, no code.
2. **A diff draft for the box early-out** in `convex_span` (`zmap.cu`) and `ConvexProfile.Span` — four comparisons
   against `p[0]..p[3]`, symmetric across both backends. Kept separate from §3 so nothing collides.

## 6. If you continue this line

**The one measurement worth doing first.** The fit in `ConvexKernelFindings.md §10.1` is arithmetic on four points and
is the only claim in the document that needs its own run before the rest can be trusted. The sweep already exists:
`LongPrograms convex --planes` with a fifth plane count. If the point lands on `289 + 11.63·m²`, the constant/quadratic
reading is settled and the per-step preparation (§5 of the findings) is justified as the largest structural lever.
If it does not, the model is too coarse and the constant must be split.

**The counters to add once, if you add any:** `columnsSeen` / `columnsInBox` (turns the 43 % into a fact), and a
`nLo`/`nHi` histogram (settles the §8 question about whether the growth is pairs or crossings). Both are cheap and both
are independent of whatever optimisation you then choose.

**Two settled facts not to re-derive:**

- `Orientation3.AboutZ(0)` is exactly the identity (`ConvexStep.cs:27-31`), so the bench's translation-only arm has
  `R = I` and therefore `mz = nz`. Since `ConvexTool.Ball`'s Fibonacci normals are at ±0.917 / ±0.583 / ±0.25 for
  12 planes, **no half-space ever takes the `mz == 0` branch**, so `nLo + nHi = m` holds exactly — which is what
  makes the exponent of §10.1 a puzzle at all.
- Per-thread arrays with a **uniform** index across the warp are fully coalesced (NVIDIA, `writing-cuda-kernels.html`
  §2.3.3.4): local memory has global-memory latency, not register latency. Forcing `gLo`/`gHi` into registers would be
  a **regression** (≈130 registers/thread → ~17 % occupancy). Only `g[at]` in the build loop diverges.

**Still unverified:** the literature in §7.1 of the findings document — both URLs the research agent named now return
404. And all hardware figures in §7.2 are an agent's inference without a profiler; the compute-capability 12.x
column in NVIDIA's own table is empty, so figures quoted for this card are documented for compute capability 9.0.

## 7. Not done

> This section describes the session that wrote the document. A later session built and measured several of the things
> it lists as open; §8 says which, and §8 is the current state. Everything below is as of `437612f`.

- No `dotnet build`, no `dotnet test`, no bench run, no `cuobjdump`, no `ncu`, no `nvcc`. Builds and tests run in
  other sessions.
- No source file changed. No commit on any branch but this one. **Nothing pushed.**
- The two background research agents completed; their results are in `3e195dc`. Their literature could not be
  fetched — they had no network access, which this session did have.

---

# 8. Later session: what was built, what was measured, what is next

**Written 2026-10-04 on `worktree-quick-elm-a7a7ca`, after `f39a8ed`.** Nine commits on top, and this document is the
ninth. Two findings documents carry the detail: **`ConvexEarlyOutFindings.md`** (levers A, B and the measurements) and
**`ConvexOccupancyFindings.md`** (what the kernel actually spends its time on). This section is the map: what is done,
what is refused and why, and the single next measurement.

## 8.1 Done and kept

| commit | what | measured |
| --- | --- | --- |
| `cc1fbc6` | the box early-out — four comparisons against the step's own swept box, at the top of `convex_span` and mirrored in `ConvexProfile.Span` | binned kernel 8.0–8.3 → 4.4–5.2 ms, unbinned 1736 → 76 ms, removed volume unchanged to the last digit |
| `f950cee` | `Where` asks whether the bound moves before it divides (`k + s·tHi >= 0`) | `smsp__inst_executed_pipe_xu` 78 260 184 → 42 396 362; **no wall-clock change**, and §3 of the findings says why the analysis in `ConvexKernelFindings.md` §2.1 was wrong by a factor of three |
| `310487b` | the box margin was 1e-6 mm and had to outlast the coordinate's own float resolution; now 1e-4 mm | this one is a **correctness fix**, not a speed change: at 1e-6 the grown box ended 3.81e-7 mm *inside* the body already on a 20 mm map |

## 8.2 Built, measured, and refused

**The O(m) envelope walk.** Correct — a differential test over 30 configurations and 432 000 columns found **zero**
hit/miss disagreements against the crossing walk it replaces, worst end difference 4.77e-06 mm. And worth 2.4 % at twelve
half-spaces, 4.5 % at sixteen, **−10 % at eight**. Refused: 4.5 % at the top of the range is not worth a hundred lines
of subtle code in a kernel whose correctness rests on a documented ulop argument. `ConvexEarlyOutFindings.md` §6 has the
numbers, the mutation checks, and the correction of an earlier verdict that had it backwards.

**Lever D on its own**, the per-step rotation into shared memory: not started. Its bound was mis-drawn. The 0.45 ms
"constant" in `ConvexKernelFindings.md` §10.1 is the *fitted intercept* of measured times, and the per-step preparation
is O(m) — so it lives in the **m²** term, 89 % of the kernel at m = 12, not in the 10 % constant. Roughly 27 of the ~40
instructions per plane are block-uniform. Those are counts, not measurements.

## 8.3 The next measurement — it decides the next lever

`ConvexOccupancyFindings.md` §3 is the argument, in one line: **the binned launch keeps 8.09 of 32 threads active per
warp** (Nsight Compute's estimate for fixing it: 40.3 %), against 21.34 for the sphere arm of the same kernel and the
same program. The kernel is issue-bound at 62.70 % compute and only 20 % long-scoreboard stall, so the waste is lanes,
not bandwidth and not arithmetic.

**What to measure first, before any restructuring:** per tile, how many of the 256 columns are touched by at least one
of the steps assigned to that tile. That number is the speedup ceiling for compacting the surviving columns into dense
warps, and it costs one instrumented build. If it is close to 256, the lever is worth building; if it is close to the
per-step footprint, it is not, and nothing else on the list should be tried in its place.

## 8.4 How to measure anything here — four rules that cost wrong numbers first

1. **`--repeat 50`, not `--repeat 3`, for anything under about 10 %.** The same code in different processes reads
   anywhere from 4.1 to 5.2 ms on this machine: a 24 % spread against effects of 2–4 %. Every verdict in §6 of the
   findings rests on best-of-50.
2. **`--tilt-deg` for anything about how `nLo` and `nHi` split** (`8b03f79`). `--turn-deg` rotates about z and leaves
   every m_z sign untouched, so the crossing walk — which costs `nLo³ + nHi³` — was only ever measured at its cheapest,
   with the split balanced 6/6 by `ConvexTool.Ball`'s construction.
3. **`smsp__inst_executed_op_local_st.sum` next to `op_local_ld`,** and pair every instruction count with the
   throughput **from the same run**: `cycles ∝ instructions / throughput`, so a count from one build beside a throughput
   from another invents a cycle ratio. Take `gpc__cycles_elapsed` directly. On this kernel a store costs several
   times a load, and an insertion sort is store-dominated — that is the whole reason the first envelope attempt looked
   catastrophic.
4. **Rebuild the bench before profiling it.** `build.ps1` is not enough: the bench copies `nanocut_gpu.dll` into its
   own `bin`, and `dotnet build Stykker.NanoCut.slnx` does not build the bench. Getting that wrong put the envelope's
   profile numbers into `ConvexOccupancyFindings.md` as the walk's for one revision. And `ncu --launch-count 1`
   profiles the 8-step warm-up; `--launch-skip 1` reaches the measured launch.

## 8.5 Two things that are still not settled

- **An intermittent failure in the CPU planar path**, `ProcessTests.RackGeneratedGearHasInvoluteFlanks`, once in six
  full-suite runs, flank 5327 nm against that test's 300 nm tolerance. It does not reproduce in isolation (five solo
  runs green) and did not appear in four baseline runs of `f39a8ed`. It is `Process2.Cut`, untouched by anything here —
  but this branch's `376f705` made that path run the union and the subtract on two strands at once, and a result that
  depends on scheduling would look exactly like this. **Not established, not dismissed.** It belongs next to that
  commit.
- **`k + s·tHi` is now a branch predicate**, which is the first place in `Where` where nvcc's `-fmad=true` contraction
  can change an *outcome* rather than a last bit. Measured: 0 of 107 252 pair evaluations differ on the tested
  geometry, general order ~1e-9 per pair. Rare, new, and covered by nothing.

## 8.6 One branch to close

`origin/feature/box-early-out` (`e72ad33`, worktree `worktree-keen-sky-991dba`) holds `box-earlyout.patch` — a diff
draft of the **same** box early-out, verified to apply and never built. `cc1fbc6` implements it instead, built and
measured. Applying that patch on top of this branch would conflict on the same lines. **Close the branch rather than
apply the patch**; the two ideas are otherwise the same, down to the 1e-6 mm margin that both got wrong and that
`310487b` fixed.

## 8.7 Not done

- Nothing of the lane-occupancy lever: no instrumented build, no restructuring.
- No `CpuBackend` measurement of what the early-out did for the CPU convex path — and before anyone writes one:
  `CpuBackend.ApplyConvexDexels` returns a `ZMapTiming` whose `PackMs` is 0, so the two `WallMs` columns would still
  cover different spans, which is the reason `PackMs` exists.
- The sphere path (`swept_span`) has no box early-out; its payload carries a position and a radius, not a box.
- The literature in `ConvexKernelFindings.md` §7.1 is still unverified — both URLs the research agent named return 404.
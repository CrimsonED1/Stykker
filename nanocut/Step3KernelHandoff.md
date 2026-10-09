# Step 3, kernel side: a briefing for the next reader

Written 2026-10-03 in `worktree-keen-leaf-f9cab1`. This is a handover note, not a review. It exists because the
managed half of step 3 has been read twice and the **CUDA half and the new tests have not been read at all** — and
that is exactly where the remaining risk sits.

The two background agents that worked this area before are completed and cannot be resumed from this worktree, so
this note is what a new agent needs in order to start cold without re-deriving everything.

## The situation

- Repo `Stykker-NanoCut` (`CrimsonED1/Stykker-NanoCut`), an exact-geometry CAM kernel in .NET 10 with an optional
  CUDA preview backend. Managed side `src/Stykker.NanoCut.Gpu/`, kernel `src/Stykker.NanoCut.Gpu.Native/zmap.cu`,
  built by hand through `build.ps1`.
- Branch `long-programs-step-1` = `63adb49` (step 2, CSR step binning). Three commits ahead of `origin/main`.
- **Step 3 is finished, documented and still uncommitted**, in the working tree of the main checkout at
  `C:\_AI\StykkerNanoCut\StykkerNanoCutRepo`, on top of `63adb49`. It adds a convex tool on a pose sequence:
  a tool is a list of half-spaces `n·p ≤ d`, a step is one orientation plus two positions, and where a column meets
  the sweep is computed as a small linear program in (z, t).
- Uncommitted files: 8 modified source (`zmap.cu`, `CpuBackend.cs`, `CudaBackend.cs`, `CudaNative.cs`, `DexelMap.cs`,
  `IDexelBackend.cs`, `StepBins.cs`, `StepBinsTests.cs`), 4 new (`ConvexTool.cs`, `ConvexProfile.cs`, `ConvexStep.cs`,
  `ConvexDexelTests.cs`), plus a documentation pass over `README.md`, `bench/README.md`, `docs/long-programs.md`
  (+98), `docs/performance.md` (+60), `docs/todo.md` (+45).

## Constraints — read these before touching anything

- **Read-only.** Another session is building and testing. Do not build, do not run `dotnet test`, do not run nvcc,
  do not start the app, do not benchmark. Reading files and `git diff` is fine.
- **Do not commit, push, apply a patch, check out or move a branch, create a worktree, or open a PR.**
- The main checkout holds someone else's in-flight work. Treat it as read-only even where a fix seems obvious.
- Write findings to a Markdown file in **this** worktree root, do not edit the repo's own docs.

## Already analysed — do not duplicate this ground

Everything below was established by reading the source in this session or by two earlier agents whose findings are
already folded into `CudaLongProgramsFindings.md`. Repeating it wastes the run.

**Ball / step-2 path, closed:**
- Step 2's binning is correct. One block per tile, per-tile step slice ascending by construction
  (`StepBins.cs:71-78`), independent completeness test (`StepBinsTests.cs:42-71`).
- The capacity guard in `dexel_subtract` (`zmap.cu:887-890`) discards the roof when `n + 1 > capacity`, so which
  side survives depends on cut order. `docs/long-programs.md:83-84` states the opposite and is wrong; `:92`
  contradicts it. Open.
- `nc_dexel_read` (`zmap.cu:1091`) copies the whole `2·K` slot per column, not the live prefix. `DexelMap.ApplySteps`
  and the new `ApplyConvexSteps` (`DexelMap.cs:146`) both default to `ZMapReadBack.Always`, so every batch pays it.
  Open.
- `ToolProfile.Pack` sits inside the CUDA `WallMs` and outside the CPU one, with no timer. There are now **six**
  untimed pack call sites: `ToolProfile.Pack` at `CpuBackend.cs:40,184` and `CudaBackend.cs:76,304`, plus
  `ConvexProfile.Pack` at `CpuBackend.cs:221` and `CudaBackend.cs:336`. `PackMs` exists nowhere.
- `DexelMapTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` compares overflow counts with no non-zero
  precondition, so it proves nothing if the scene never overflows. `DexelMapTests.cs:86` does assert `> 0` in a
  different, pre-existing test.
- CI never compiles `zmap.cu`: `Stykker.NanoCut.Gpu.Native` is not in `Stykker.NanoCut.slnx` and CI does not call
  `build.ps1`.
- `MarginMm = 1e-6f` is 1 nm (`StepBins.cs:27`) while the rounding chain it guards is worth ~2.5 nm at bench
  coordinates. Low severity, wording of the comment not earned.

**Convex / step-3 managed side, closed:**
- `ConvexTool.Bounded()` refuses a half-space set whose every `n_iz` is zero, and `Corners()` rejects redundant
  planes. Both hazards closed.
- `ConvexTool.Ball(r)` with defaults **throws**: default `planeCount = 32` (`ConvexTool.cs:164`) against
  `ThrowIfGreaterThan(planeCount, MaxPlanes)` (`:168`) with `MaxPlanes = 16` (`ConvexProfile.cs:88`). Tests always
  pass `planeCount` explicitly, so nothing trips it today.
- `StepBins.Tiles` carries a `convex` flag and two layouts; `MarginMm` unchanged.
- `DexelMap.ApplyConvexSteps` repeats the `ZMapReadBack.Always` default rather than correcting it.

**The design question, and its answer (do not re-argue it):**
The interval is an envelope over `t ∈ [tLo, tHi]`, O(m³) in the half-spaces, and `MaxPlanes = 16` follows from that.
`docs/long-programs.md` explains why: an earlier version took the min of the lower bound and the max of the upper
bound over all of `t` separately, and those come from different `t`, so they cross on columns the sweep misses (the
octahedron reported cuts 3.9 mm outside its own silhouette). Testing candidates for feasibility instead is worse —
where the body opens or closes on a column the two bounds are equal, in float two or three ulops apart, so half the
candidates get dropped at random (9.10 mm off over 390 columns). Narrowing `t` first is what works, because
`{t : z_min(t) ≤ z_max(t)}` *is* the conjunction of the pairwise conditions.

A closed form exists that removes `t` entirely: the sweep is `K + [0,w]`, a Minkowski sum with a segment, which
contributes no facet normals of its own, so `h = d + max(0, n·w)` and the interval is one `min` and one `max` over the
planes, O(m), no sort, no tie-break. It assumes only what the model already guarantees (one orientation per step, so
the sweep is a pure translation; full-dimensional tool). **The trap is `max`, not `min`** — taking the existential
per plane inside the conjunction gives a strictly smaller set. Reasoning is in `CudaLongProgramsFindings.md` §10.2.

Two related observations, also closed: the sort in their own queued O(m) follow-up can be hoisted out of the kernel
because the slope `g/m_z` depends only on the plane and `w`, not on the column (§10.3); and the CUDA-vs-CPU tolerance
was relaxed from 1e-4 mm to a hundredth of a cell, argued from the conditioning of `1/m_z` but written in the log
rather than asserted in the test (§10.6).

## What to look at — the surface nobody has read

This is the actual assignment. All of it is in the main checkout, all of it read-only.

1. **`convex_span` in the kernel** — `zmap.cu:988`, and its use at `:1107`. This is the one piece of step 3 that has
   never been looked at by anyone on this side.
2. **`nc_dexel_apply_convex_steps`** (`zmap.cu:1414`) and **`nc_dexel_apply_convex_steps_binned`** (`:1431`).
3. **`tests/Stykker.NanoCut.Tests/ConvexDexelTests.cs`** — the whole file.
4. **The deltas in `CudaNative.cs`** and `IDexelBackend.cs`, which route the new entry points.
5. **`docs/performance.md`** (+60 lines, none of it read yet).

### Questions worth answering, in rough order of value

a) **Does `convex_span` implement the same `t`-narrowing as `ConvexProfile.Span`?** The CPU reference and the kernel
   are two hand-written implementations of the same algorithm, and the log records that two of three variants of it
   were wrong. Divergence between them is the single most likely defect here. Compare the two line by line.
b) **Is `MaxPlanes = 16` mirrored in the kernel?** It is a C# constant (`ConvexProfile.cs:88`); the kernel cannot read
   it, and the guard message at `ConvexProfile.cs:113-115` says the kernel "keeps two lines per half-space per thread
   and reads MaxPlanes". There is no `kMaxPlanes` in `zmap.cu`. Find what the kernel actually uses, and whether the
   two numbers can drift apart silently.
c) **What does the kernel do for `m_z` near zero?** The documented float failure mode is that the interval ends come
   out of a division by `m_z`, so the conditioning is `1/m_z`. Is there an epsilon guard, an early out, or a bare
   division? A plane near-horizontal has no `z` bound to give, and the error is unbounded rather than small.
d) **Does the convex path go through `dexel_subtract`, and therefore inherit the capacity guard** at
   `zmap.cu:887-890`? If it does, the order-dependence already documented for the ball path applies here too, and
   `docs/long-programs.md` is now wrong about convex programs as well as ball ones.
e) **Does the convex path report overflows at all,** and does `ConvexDexelTests` have a non-zero precondition before
   asserting on them? This is the exact failure mode already found once in `DexelMapTests`.
f) **What is the per-thread register or stack cost of the two-lines-per-half-space buffer**, given `MaxPlanes = 16`?
   The dexel path already keeps `float out[2 * kMaxDexelIntervals + 2]` (`:877`) and `float local[2 * kMaxDexelIntervals]`
   (`:377`) per thread. Does adding the convex buffers push occupancy down, and does the binned twin share them?
g) **Does the binned convex launch share the CSR and the tile computation with the ball path,** and is the convex
   branch of `StepBins.Tiles` covered by a completeness test of its own, or only by the ball one?

### Output contract

- Separate **verified by reading** (with `file:line`) from **inferred, needs a measurement**. Do not present a
  magnitude you did not measure as if you had.
- If you quote a number that this repo has already measured elsewhere, check it against the source before repeating
  it — an earlier agent estimated `ToolProfile.Pack` at 20–40 ms and the real figure was about half that.
- Write the findings to a Markdown file in this worktree root. Do not edit `docs/` in the main checkout, do not
  commit, do not push.
- If you find that a step-3 decision contradicts something written in `docs/long-programs.md` or `docs/todo.md`, say
  so explicitly with both line references — that is the most useful thing you can hand back.
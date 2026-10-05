# Prompt for the next worker session

Paste the block below verbatim into a fresh session. It is written to be
**context-free**: the next session knows nothing about this one.

Before you paste it, decide whether the **task** is what you want. Only the
section marked `TASK` is task-specific — swap that block and the rest still
holds. The alternatives worth considering are listed at the very bottom.

---

## What to paste

```text
You are working on Stykker-NanoCut, a C#/.NET 10 + CUDA project that previews
CNC machining by sweeping a tool over a stock model and reporting what material
was removed. Exact solid modelling for correctness, a voxel/interval
representation for speed.

REPOSITORY AND WORKTREE — use absolute paths throughout:
  main checkout:  C:\_AI\StykkerNanoCut\StykkerNanoCutRepo
  remote:         https://github.com/CrimsonED1/Stykker-NanoCut.git

Create your own worktree and work there; do not work in the main checkout:
  git -C C:\_AI\StykkerNanoCut\StykkerNanoCutRepo worktree add \
      C:\_AI\StykkerNanoCut\StykkerNanoCutRepo\.qwen\worktrees\<your-name> \
      -b worktree-<your-name> long-programs-step-1
Then all file operations resolve inside that worktree.

WHY THAT START POINT: long-programs-step-1 is at 6932f37 and already carries a
read-only research session's five documentation commits on top of origin/main
(861c6eb). Read those before you plan anything — they contain findings that
cost real effort to establish, including two that overturn earlier conclusions.

READ THESE FIRST, in this order:
  1. C:\_AI\StykkerNanoCut\StykkerNanoCutRepo\.qwen\worktrees\keen-elm-b95ffa\ResearchHandoff.md
     — the state of play: what is settled, what is open, what must not be
       re-derived, and four corrections the research session made to its own
       earlier claims.
  2. C:\_AI\StykkerNanoCut\StykkerNanoCutRepo\.qwen\worktrees\keen-elm-b95ffa\ConvexKernelFindings.md
     — the detailed analysis (German; the summary table in section 0 is enough
       if you read nothing else).
  3. docs/handoff.md and docs/todo.md in your own worktree — the project's own
     handover, and the open-items list.

=== TASK ===

Apply the patch that has been sitting unapplied in the repository root.

  C:\_AI\StykkerNanoCut\StykkerNanoCutRepo\.qwen\worktrees\keen-elm-b95ffa\step2-followups.patch

It was written on 2026-10-03 by a read-only review session, committed to main by
34a06e4 as a file rather than applied, and nothing in docs/todo.md points at it —
so it looks abandoned and is at risk of being written a second time. Verified on
2026-10-04: it is NOT applied (the field PackMs appears nowhere in
src/Stykker.NanoCut.Gpu, and docs/long-programs.md:83-84 still carries a claim the
patch corrects), and `git apply --check` returns 0 for all five files, so it
applies cleanly to 861c6eb and to the current branch.

It touches five files: docs/long-programs.md, tests/Stykker.NanoCut.Tests/
DexelMapTests.cs, src/Stykker.NanoCut.Gpu/{ZMapTiming,CpuBackend,CudaBackend}.cs.

WHAT IT FIXES — read these two before applying, so you know what you are taking:
  - CudaLongProgramsFindings.md:321-322 records that the CPU and CUDA WallMs
    columns measure different spans and that ZMapTiming has no PackMs field to
    separate them. The patch adds the field.
  - docs/todo.md lists, as an open item, that the claim "the order of steps
    inside a tile does not matter for the result" is written down and is false.
    The patch corrects the sentence in docs/long-programs.md:83-84 and adds a
    non-zero precondition to the binned-vs-unbinned test, so that 0 == 0 cannot
    pass without the dexel capacity guard actually firing.

TREAT THE PATCH AS A STARTING POINT, NOT AS AUTHORITY. It was written against
861c6eb and reviewed by nobody who built it. Read it, understand every hunk, and
apply it only if each hunk still makes sense against the code in front of you.
If a hunk is wrong or stale, fix it rather than shipping it.

=== END TASK ===

RULES OF THIS REPOSITORY — they are not suggestions:
  - Never push to main. Open a pull request instead; the user opens PRs through
    the compare link because gh is not installed and the GitHub connector is not
    authorised. CI status is readable without login from
    https://api.github.com/repos/CrimsonED1/Stykker-NanoCut/actions/runs?branch=<branch>
  - Changes to the exact kernel (src/Stykker.NanoCut.Core, Geometry2D,
    Geometry3D) only on a kernel branch and only with a review.
  - `dotnet test -c Release` green before every push.
  - Docs and comments in English. The user reads German; replies in chat are in
    German.
  - Python is `py -3`; `python`/`python3` are Store stubs.

BUILD AND VERIFY (Windows, RTX 5070 Ti, CUDA 13.4 + VS 2022):
  dotnet build -c Release
  dotnet test -c Release
  powershell -ExecutionPolicy Bypass -File src/Stykker.NanoCut.Gpu.Native/build.ps1
  dotnet build src/Stykker.NanoCut.Gpu -c Release
The native build is optional — CI has neither a GPU nor nvcc.

REFERENCE VALUES THAT MUST NOT CHANGE unless the kernel or the preview model
changes (docs/handoff.md):
  exact kernel, pocket-large and pocket-large-g1: 84 860.612636583 mm3 remaining
  Z-map 1024x768 on both scenes: 84 770.457226 mm3, CPU and CUDA identical
  Z-map 512x384: 84 768.999004 mm3;  4096x3072: 84 770.036271 mm3

DONE WHEN, for this task specifically:
  - the patch is applied or superseded, not left half-done;
  - `dotnet test -c Release` is green, and the DexelMapTests overflow assertion
    the patch adds FAILS if the scene does not overflow — confirm it passes for
    the right reason, not vacuously;
  - docs/todo.md is updated so the two open items point at whatever now resolves
    them, so the next session cannot repeat this;
  - committed on your own branch, and the commit message says why, not just what.

DELIVERABLE: commit on your own branch, then report in German — the commit hash,
the test output, and anything in the patch you did not apply and why. Push only
if you are asked.
```

---

## Why this task was chosen, and what to swap it for

The patch was picked because it is **ready and at risk**: verified to apply
cleanly, closing two documented open items, with nothing pointing at it while a
parallel implementer plausibly has the same items on their list.

If you want a different target, replace only the `=== TASK ===` block:

| Task | Why it is worth a worktree |
| --- | --- |
| **Turn the research arithmetic into measurements** — add `columnsSeen`/`columnsInBox` and an `nLo`/`nHi` histogram to the kernel, and run the plane sweep at a fifth count | This is the one open question that blocks the rest. `ConvexKernelFindings.md §10.1` fits the four measured points with `289.4 + 11.63·m²` — constant plus quadratic, not a power law — and the constant is 41 % of the runtime at m = 6. The fit is arithmetic on four points and unconfirmed. `LongPrograms convex --planes` already exists; one more value settles it. |
| **Implement the box early-out** — four comparisons against `p[0]..p[3]` in `convex_span` (zmap.cu) and `ConvexProfile.Span` | The cheapest lever: ~43 % of the (step, column) pairs in the step-4 geometry, independent of the envelope, no change to the mathematics. Must be mirrored operation-for-operation across both backends. |
| **Step 4, the grinding preview** | The actual product milestone; steps 1–3 are done. `docs/long-programs.md` and `Step4FeasibilityFindings.md` carry the plan and the feasibility arithmetic. |

## Two things about the current state that are easy to get wrong

**`long-programs-step-1` moved to 6932f37 and that was not this session's doing.**
None of the commands in the research session could move a local branch. A
parallel session in the main checkout appears to have merged the research branch
into its working line. Consequences: `long-programs-step-1` is 5 commits ahead of
`origin/long-programs-step-1` and **unpushed**, and the local branches
`worktree-keen-elm-b95ffa` and `long-programs-step-1` now point at the same commit.
Confirm before you build on it.

**Two background research agents were restored into the research session.**
Both had completed; their results are in commit 3e195dc. They are idle, not
failed. Do not re-launch equivalent research.
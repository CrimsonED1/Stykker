# Box early-out: what landed, what it cost, and what is still unmeasured

**Written:** 2026-10-04
**Branch / commit:** `feature/box-early-out` — draft `e72ad33`, implementation `f019ad2`
**Subject:** four comparisons against the swept body's bounding box, before the half-spaces are walked
**State at writing:** built, 252 + 4 tests green, `zmap.cu` compiled with nvcc 13.4, CUDA compared against the CPU
reference on a real card. **The speed-up is not measured** — see §7.

Every claim below is tagged **[read]** (read from source, with file:line), **[derived]** (arithmetic), **[measured]**
(this machine, numbers given), **[flagged]** (raised by a verifier pass and *not* reproduced here) or **[unmeasured]**.

---

## 0. Short version

`ConvexProfile.Pack` already writes each step's swept bounding box into the first four floats of the step.
`StepBins` uses it on the host to bin steps into 16×16 tiles, so a block only sees steps that can reach its columns.
Inside the block, though, *every* column of an assigned tile calls the full solver, even the ones whose centre is
nowhere near the body. Four comparisons fix that, and they cannot change a result.

The interesting part is not the change. It is that the change **would have silently disarmed one of this project's
best tests** (§5), and that the draft's own comments claimed more than the constants support (§4). Both are fixed.
What nobody has done is measure whether it is faster.

---

## 1. What landed

| File | Change |
| --- | --- |
| `src/Stykker.NanoCut.Gpu/ConvexProfile.cs` | the test in `Span`, `BoxMarginMm`, `BoxOffset` now `internal` |
| `src/Stykker.NanoCut.Gpu.Native/zmap.cu` | the same test in `convex_span`, `kConvexBox`, `kConvexBoxMarginMm` |
| `src/Stykker.NanoCut.Gpu/StepBins.cs` | `MarginMm` now *is* `ConvexProfile.BoxMarginMm` instead of a third literal |
| `tests/Stykker.NanoCut.Tests/BluntedSteps.cs` | new — the blunted copy, shared by two tests |
| `tests/Stykker.NanoCut.Tests/ConvexDexelTests.cs` | the new differential test |
| `tests/Stykker.NanoCut.Tests/StepBinsTests.cs` | its oracle moved onto the blunted copy (§5) |

**[read]** The test sits ahead of the plane loop and of the twelve payload reads it would otherwise touch:

```csharp
float boxX = steps[o + BoxOffset] - BoxMarginMm, boxY = steps[o + BoxOffset + 1] - BoxMarginMm;
float boxW = steps[o + BoxOffset + 2] + 2 * BoxMarginMm, boxH = steps[o + BoxOffset + 3] + 2 * BoxMarginMm;
if (x < boxX || x > boxX + boxW || y < boxY || y > boxY + boxH) { low = high = 0f; return false; }
```

**[read]** The CUDA side is the same expression, operation for operation, with `const` instead of `float` and
`return false` without writing the outputs — which is the convention already there, because `dexel_apply_column`
skips on `!hit` (`zmap.cu:1108`).

---

## 2. Why it is sound

**[read]** `ConvexProfile.Pack` (`ConvexProfile.cs:118-145`) takes `tool.CornersMm`, applies
`step.Orientation.Apply` in double, and evaluates both `t = 0` and `t = 1`, adding `ax, ay` and `f * w`. The stored
box is therefore the AABB of `{R·c + T_A} ∪ {R·c + T_B}` over the corner set.

**[derived]** That is the *exact* AABB of the swept body, not merely a bound. A sweep of a convex body along a segment
is the convex hull of the body's two endpoint positions, and a linear functional is affine in `t`, so it attains its
extreme over `[0,1]` at an endpoint. Both ends of the box are therefore attained at real points of the sweep.

**[derived]** `convex_span`'s body is `T_A + t·w + R·polytope` — the linear image of the same polytope under the same
`R`, since the normals are rotated as `m = R·n`. That identity holds for any matrix, orthonormal or not, so the
argument survives a caller-supplied `Orientation3` that is not a rotation.

**[derived]** Hence: a vertical line whose x and y are both outside the box cannot meet the body, in any orientation.
The test is one-sided by construction — inside the box proves nothing and does not claim to — so it can only ever
reject, never alter an interval.

**[read]** The comparison is strict (`<`, `>`), so a centre exactly on the boundary is accepted. That is the
conservative direction and matches the model's closed rule, where a centre sitting on the tool's edge counts as
inside (`ConvexDexelTests.cs:138-143`).

---

## 3. The margin, and what it is for

**[read]** `1e-6 mm`, now one constant: `ConvexProfile.BoxMarginMm`, mirrored by `StepBins.MarginMm` (now a reference
to it, `StepBins.cs:26-28`) and by `kConvexBoxMarginMm` on the native side.

**[derived]** `2 * margin` is exact in binary — multiplying by two only changes the exponent — so nvcc's
`--fmad=true` contracting `w + 2m` cannot diverge from a JIT that does not contract.

**The margin's purpose is to keep the two filters from parting company**, not to absorb the float chain. That is why
it has to be the same number on both sides: `StepBins` decides which tiles a step goes to, and `Span` now decides
which columns of those tiles survive. Different numbers would mean a column the binning bothered to route that the
solver never sees.

---

## 4. What the draft's comments got wrong

The draft said *"the sweep lies inside it"* and *"may only reject what the host's binning already refused to route to
this column"*. Both are wrong as absolutes, and both comments are corrected in the landed code.

**[derived]** The box is the exact AABB in **double** (§2). It is stored as four floats rounded *independently*:
`(float)(minX - origin.X)`, `(float)(maxX - minX)`, and the consumer adds them back. That is three roundings on the
upper edge. One ulp of a float at 8 mm is `2^(3-23) = 9.5e-7 mm`, so the reconstructed edge can sit roughly one and a
half ulp — of the order of `1e-6` mm — from the true edge, which is the same order as the margin. Past roughly 16 mm
it is several times the margin.

**[derived]** No fixed margin absorbs this, because the error scales with the coordinate, not with the margin. The
right response is the one taken: say what the constant does and does not cover. `CudaLongProgramsFindings.md` §9.4
already measured the same effect in the sphere chain and reached the same conclusion.

**[derived]** The second claim was also too strong for a simpler reason. `StepBins` computes `x1 = x0 + w + 2*margin`
associating left to right (`StepBins.cs:107`); the kernel computes `boxX + (w + 2*margin)`, associating the other way.
Different order, up to an ulp apart. Then the binning turns the edge into a column range via
`floor(x1/cellX - 0.5f)` (`StepBins.cs:127`) while the kernel compares against `(i + 0.5f) * cellX`
(`zmap.cu:1097`) — two entirely different float chains that agree to within that same nanometre-scale noise.

**[read]** This is inherited from the binning, which reads the same four floats and reaches the same pairs. The
early-out does not add a dependency on the box being generous; it makes an existing one per-column instead of
per-tile.

---

## 5. What this would have cost — the finding that mattered

A verifier pass was run over the draft before it was implemented. Its blocking finding was not in the change.

**[read]** `StepBinsTests.EveryConvexStepThatReachesAColumnIsInThatColumnsTile` decides "does this step reach this
column?" by calling `ConvexProfile.Span` (`StepBinsTests.cs:158`), and `StepBins.Build` assigns the step from the
same four floats the early-out reads (`StepBins.cs:105-108`).

**[derived]** After the change, that test compares the box predicate against the box predicate. It keeps passing —
it would have kept passing if the box were **too small**, which is the single defect it exists to catch, and its own
comment ("the same evaluation the kernel applies") would have stopped being true. A green suite would have said
nothing.

**[read]** Fixed by moving the oracle onto a copy of the packed steps with every box widened past the map, so the
answer comes from the half-spaces. The general lesson, and it is not specific to this change: **a test that uses the
function under test as its own oracle stops testing it the moment that function grows a shortcut.** Any future
shortcut in `Span` — a silhouette pre-test, a tighter margin, a per-step bound — has to be checked against this.

**[derived]** The same shape exists at `ConvexDexelTests.cs:494`, but its subject is the subtraction order rather than
the box, so the degradation matters less there. It was left alone rather than changed on speculation.

---

## 6. The test that was missing, and its numbers

`ConvexDexelTests.TheSweptBoxTurnsAwayNothingTheLinearProgramWouldAccept` runs one program twice — once as packed,
once against the blunted copy — and requires both to agree on every column of every step, hits bit-for-bit.

**[derived]** Because both arms come out of the *same* `Pack` output, this is stronger than a plain differential: if
the box is too small in the source data, the unblunted arm rejects a column the blunted arm accepts, and the test
fails. It catches a real defect rather than merely a change of plumbing.

**[measured]** 18 steps, **165 888 (step, column) pairs, 5 031 reaching, 160 857 turned away** by the box alone.
Both counts are asserted non-trivially so the test cannot pass quietly on a geometry where nothing reaches, or where
everything does.

**[derived]** That 97 % is **not** the 43 % of `ConvexKernelFindings.md` §4, and must not be quoted as if it were.
The denominators differ: this test's pairs are every column of a 10 mm map under a 2.4 mm tool, where almost
everything is far away by construction. The 43 % is over *assigned tiles* only, which is the number that describes
the GPU's actual work. Two numbers, two questions.

**[measured]** The test failed on its first run — not because the box was wrong, but because the counter was written
to count the failure case (box rejects, half-spaces accept) instead of the useful one. The zero it reported *was* the
result being looked for. Worth remembering as a caution: a guard can be wired to the wrong side and still look like
it is working.

---

## 7. What was measured, and what was not

**[measured]** On this machine, all of it:

| Check | Result |
| --- | --- |
| `dotnet build -c Release` | 0 warnings, 0 errors |
| `Stykker.NanoCut.Tests` | 252 of 252 |
| `Stykker.NanoCut.OracleTests` | 4 of 4 |
| `nvcc` 13.4 → `zmap.cu` | compiles, 378 KiB, `sm_120` + `compute_75` PTX |
| CUDA vs CPU, convex | 58 081 columns × 4 tool configurations, **0** differing in interval count, overflow counts equal |

**[derived]** That last row is stronger than passing. `CudaAgreesWithTheCpuReference` tolerates a hundredth of a
cell and up to `columns / 10 000` differing counts, so one wrongly rejected rim column would have been inside the
budget. The measured figure is zero, not inside the budget.

**[measured]** Getting that row to mean anything took a step worth recording: the CUDA cases early-return when
`CudaBackend.IsAvailable` is false, and they **count as passed while taking about a millisecond**. `build.ps1` puts
`nanocut_gpu.dll` only into `src/Stykker.NanoCut.Gpu/bin/Release/net10.0/`; it has to be copied by hand into
`tests/Stykker.NanoCut.Tests/bin/Release/net10.0/` before a `--no-build` run loads it. A green suite that never
touched the GPU is indistinguishable from a real one unless the `not run:` line is looked for.

**Not verified:**

- **[unmeasured]** **The gain.** No profiler has run. The 43 % remains a prediction from tile geometry, and the
  `columnsSeen` / `columnsInBox` counters that would make it a fact do not exist. This is the one number the change
  exists for, and it is the one number nobody has.
- **[unmeasured]** **The margin's adequacy.** The differential proves the early-out changes no verdict *at this
  margin*. It says nothing about whether `1e-6` suffices at production coordinate magnitudes (§4).
- **[unmeasured]** **`ConvexTool.Corners` completeness at the level that matters here.** A triple is skipped when
  `|det| < MinDet = 1e-9` (`ConvexTool.cs:47`, `:284`) or rejected when the solution sits more than
  `SlackMm = 1e-7` outside another plane (`ConvexTool.cs:44`, `:254`). **[flagged]** Neither is caught by the
  `carries[]` guard, and a missed near-degenerate vertex would shrink the box. This was raised by the verifier pass
  and not reproduced here. Note it is not new — such a tool already loses whole tiles at binning time.
- **[read]** **No CUDA-side test can fail in CI.** `Stykker.NanoCut.Gpu.Native` is not in the solution and CI has no
  nvcc, so a syntax error in `zmap.cu` is caught by nothing but a local build. That is why §7's nvcc row is a local
  fact, not a project guarantee.
- **[unmeasured]** **Degenerate boxes.** A tool collapsed toward a plane gives a box a couple of nanometres wide on
  one axis. Still correct, but untested, and it is where a future "optimisation" of this comparison would break
  first.

---

## 8. Next steps, in the order they pay

1. **Measure it.** Two counters in `convex_span` — `columnsSeen`, `columnsInBox` — turn the 43 % from a prediction
   into a fact. Cheap, independent of whatever optimisation comes next, and it is the only way to know whether the
   other levers are worth taking at all.
2. **Close the margin question.** Shrink the box by 0, one ulp of the coordinate, `1e-7`, `1e-6` and require the
   verdicts to stay equal. That *measures* the margin instead of assuming it.
3. **An independent check on the box.** Recompute the swept AABB in the test from `tool.CornersMm` and the step in
   double, and assert the stored floats contain it. It is the only test that would catch a `Corners` that loses a
   vertex, and nothing in the tree does that today.
4. **Decide about the binning itself.** At 43 % of the *assigned-tile* pairs, the waste is a property of the 16×16
   tile, not of the tool (§4 of `ConvexKernelFindings.md`). The early-out collects it; it does not remove it.
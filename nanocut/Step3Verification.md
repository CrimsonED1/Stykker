# Step 3 verification: the CUDA half and the new tests

Read-only pass over **`da26cfc`** ("Long programs step 3: a convex tool on a pose sequence in the dexel kernel"),
branch `long-programs-step-1`.

**Branch state at the time of writing:** `git log --oneline -3 long-programs-step-1` → `da26cfc` (HEAD),
`63adb49`, `69a1675`. `git log --oneline -3 origin/long-programs-step-1` → `63adb49`. `git status --porcelain`
is empty. **The tip had not moved; everything below is against `da26cfc`.**

Nothing was built, run, installed or written outside this file. Every kernel line number below is from
`git show da26cfc:src/Stykker.NanoCut.Gpu.Native/zmap.cu`; every C# and doc line number is from the working tree,
which equals the commit.

---

## Findings, worst first

### F1 — CRITICAL: a convex program followed by a ball program on the same `DexelMap` wipes the map

The dexel object decides sphere-vs-polytope by **the planes pointer being null**, not by the plane count — and the
planes pointer is never cleared.

- `zmap.cu:1106-1107` — the dispatch is `planes == nullptr ? swept_span(...) : convex_span(...)`. The doc comment
  above it, `zmap.cu:1078-1079`, says the opposite: *"`<planes>` The tool's half-spaces, or null for the sphere;
  `<planeCount>` tells the two apart"*. The code reads the pointer, not the count.
- `zmap.cu:1259` — `reserve_planes` begins `if (planeCount <= 0) return cudaSuccess;`. For a ball program
  (`nc_dexel_apply_steps`, `zmap.cu:1377`, passes `planes = nullptr, planeCount = 0`) it returns immediately and
  leaves `d->planes` and `d->planesCapacity` exactly as the last convex call left them.
- `zmap.cu:1341-1342` (unbinned) and `:1346-1348` (binned) launch with `d->planes` and `planeCount` — the stale
  pointer, with a count of zero.
- `CudaBackend.cs:22` and `:386` — the device dexel is a `ConditionalWeakTable<DexelMap, DeviceDexel>`, one per map
  for the map's whole lifetime. So the state is not per call.

What the kernel then does, with `planeCount == 0`:

1. `convex_span` (`zmap.cu:988`) loops over zero planes, so `nLo = nHi = 0` and `tLo = 0`, `tHi = 1` (`:1002`).
2. `convex_where(gLo, 0, gHi, 0, …)` (`:1029`) runs no pair loop and returns `tLo <= tHi`, i.e. **true**.
3. `convex_extremum(g, 0, …)` hits `if (count == 0) return low ? -INFINITY : INFINITY;` (`:951`), so
   `low = -INFINITY`, `high = +INFINITY`.
4. `return high > low;` (`:1032`) → **`+INF > -INF` → true**. Every column in the map is reported as hit.
5. `dexel_subtract(local, n, k, -INF, +INF)` (`:1109`): the guard `if (!(hi > lo)) return false;` (`:1038`) passes;
   per interval `hi <= a` and `lo >= b` are both false, so `left = -INF > a` and `right = +INF < b` are both
   **false** (`:1045-1058`) — the interval is dropped, not cut. `n` falls to 0, and the `n > 0` condition on the
   step loop (`:1101`) ends the column after its first step.
6. `Overflows` is **not** incremented — the capacity guard at `:1052` is never reached. No error, no warning.

Net effect: `map.Counts` all zero, `map.RemovedVolumeMm3 == BoxVolumeMm3` (everything removed), overflows 0,
return code 0. Before that it also reads `p[4] … p[19]` out of a `ToolProfile` step, which is only
`StepFloats = 12` floats (`ToolProfile.cs:16`) — 8 floats into the next step, and 20 past the last step of the batch.

Reachable from the public API with no special setup: `DexelMap.ApplyConvexSteps` then `DexelMap.ApplySteps` on the
same map with the CUDA backend. Nothing in `DexelMap`, `CudaBackend` or the native layer records which tool shape the
device object was last used with.

**The one mixed-order test covers only the safe direction.** `ConvexDexelTests.ASphereThenAConvexToolOnTheSameDevice`
(`ConvexDexelTests.cs:632-651`) runs sphere → convex. A grep for `ApplyConvexSteps` over `tests/` returns 18 hits,
all in `ConvexDexelTests.cs`, and no test anywhere runs convex → sphere. Its closing assertion,
`Assert.True(map.Counts.Length > 0)` (`:650`), is trivially true for a 129 × 129 map and pins nothing; the useful
comparisons above it (`:648-649`) only cover the sphere-first order.

Severity: high. Silent, data-destroying, and it is the direction a real program goes (rough with a polyhedral tool,
finish with a ball). This is the one thing I would fix before step 4.

*Inferred, needs a run:* I did not execute this. The trace above is from reading; the arithmetic in steps 1-6 is
plain IEEE comparison. A three-line test (`ApplyConvexSteps` then `ApplySteps`, assert the counts are not all zero)
would confirm it in seconds.

Possible fixes, in order of preference: null `d->planes` (and reset `planesCapacity`) in `dexel_apply_common` when
`planeCount <= 0`; or dispatch on `planeCount == 0` rather than on the pointer and fix the comment at `:1078-1079`;
or add `int layout` to the `Dexel` and refuse a layout switch.

### F2 — HIGH: `docs/long-programs.md:83-84` is still wrong, and step 3 widened its blast radius

> `docs/long-programs.md:83-84` — "Removal is a union, so the order of steps inside a tile does not matter for the
> result (only the overflow counting can differ)."

The capacity guard it contradicts is `zmap.cu:1052-1056`:

```c
if (left && right && n + 1 > capacity)
{
    overflow = true;   // no room for a split: keep the part below the cut, lose the roof above it
    right = false;
}
```

**This is now wrong for convex programs too, not just ball ones.** The convex path reaches it through the same code:
`dexel_apply_column` (`:1109`) calls `dexel_subtract(local, n, k, lo, hi)` with `k = d->k` regardless of which
`Span` produced `lo`/`hi` (`:1106-1107`). A convex step removes a larger interval per call, so the guard is reached
sooner, not later.

The file contradicts itself nine lines later:

> `docs/long-programs.md:92` — "Both go through the same `dexel_apply_column`, so a column is subtracted in the same
> order whichever launch runs it."

Step 2's follow-up recorded the false line at `:83-84` and the contradicting one at `:92`. **Both line numbers are
unchanged in `da26cfc`.** The claim is false: order-independence holds only for columns that never reach capacity,
and then it is the *intervals* that move, not only the count — `right = false` deletes the roof.

### F3 — MEDIUM: `SamplingBound` is not a bound for a multi-step program, and the rotating row's number is ~35 % short

`ConvexDexelTests.SamplingBound` (`ConvexDexelTests.cs:344-379`) takes

```csharp
width  = max over steps of packed[s*StepFloats + 2]
height = max over steps of packed[s*StepFloats + 3]
```

i.e. the **largest per-step swept box**, then uses it as the bounding box of the whole previewed body. Its remark
(`:334-339`) says "P is bounded by the perimeter of the swept body's bounding box, which the packing already carries".
`ConvexProfile.Pack` (`ConvexProfile.cs:122-146`) carries **that step's** box, not the union's.

For a one-step program the two coincide and the bound is conservative. For `ARotatingToolAgreesWithTheExactCut`
(`:406-441`, 11 steps) they do not. Working the same arithmetic the function does, from the test's own geometry
(tooth 1.2 × 0.3 × 0.3, 1.2 rad turn about z, 3 mm of travel, 500 cells, cell 0.04 mm):

| quantity | per-step max (what the test computes) | union over all steps |
| --- | ---: | ---: |
| swept width (mm) | 2.7456 | 5.4757 |
| swept depth (mm) | 2.4337 | 2.4757 |
| bound = `(2(w+h)·cell/2 + cell²)·(highZ − lowZ)` mm³ | **0.1253** | **0.1918** |

The left column reproduces the documented 0.1253 to four digits, which is what makes me confident about the right one.
So the number in `docs/long-programs.md:161` and `docs/performance.md:260` is the *smaller* of the two, by a factor of
about 1.5.

This does not make the test vacuous — it makes it **stricter** than advertised (`|preview − exact| ≤ bound` at
`:317-320`). It fails green here because the actual difference is 0.0147 mm³, far inside either figure. The defect is
in the stated derivation: "the volume a column model can be off by at that grid" is not what `SamplingBound` returns
for a fanned program, and step 4 is being handed this as the model to lean on. The single-line fix is a union over
all steps and corners instead of a max over steps.

The other two rows check out exactly, by hand from the source geometry:

- **Box row.** Faces at 4.13 / 13.13 and 9.07 / 11.07 mm on a 0.02 mm centre grid give `i ∈ [206, 656]` (451 columns)
  and `j ∈ [453, 553]` (101); `451·101·0.02²·2 = 36.4408 mm³`, the documented preview at 1000 cells, against
  `36.0000` exact → +1.224 %. At 500 cells, `225 × 50` columns give exactly 36.0000. The documented bounds
  1.7728 / 0.8832 / 0.4408 reproduce exactly.
- **Octahedron row.** Per-step box 12 (x, 7 mm travel + 5 mm across) × 8 (y, 3 mm travel + 5 mm across), swept z
  extent 8 mm: `2·(12+8)·0.08/2 + 0.08² = 1.7664`, `×8 = 12.8512`; then 6.4128 and 3.2032. Exact to the digit.

### F4 — MEDIUM: `docs/performance.md:324` contradicts the table twelve lines above it

> `docs/performance.md:324` — "the box case is 0.44 % at 1000 cells, 0.00 % at 500, 0.44 % at 250."

The table at `docs/performance.md:258` says the 1000-cell difference is **+1.224 %**. 0.44 % is the 250-cell figure.
The same file is right everywhere else (`docs/long-programs.md:156-163`, the commit message, `docs/todo.md`), all of
which state the 1000-cell error as 0.4408 **mm³** (which is 1.224 % of 36 mm³). Looks like mm³ and % crossed over
while the sentence was shortened.

### F5 — MEDIUM: `docs/long-programs.md:75` says 32 half-spaces; the kernel accepts 16

> `docs/long-programs.md:75` — "A tool is a convex polytope given by its half-spaces (n·p ≤ d, **at most 32 in the
> kernel**)."

The kernel accepts 16: `kConvexPlanes = 16` (`zmap.cu:825`), enforced at `zmap.cu:1419` and `:1437`
(`planeCount > kConvexPlanes` → return 1). Managed side: `ConvexProfile.MaxPlanes = 16` (`ConvexProfile.cs:88`),
re-exported as `ConvexTool.MaxPlanes` (`ConvexTool.cs:41`), enforced at `ConvexProfile.cs:113` and
`ConvexTool.cs:99` / `:168`. The same file's step-3 section says 16, and `docs/performance.md:250` and
`docs/handoff.md:106` say 16. Only the plan's "Model" paragraph says 32, left over from the pre-step-3 plan.

Related and still open: `ConvexTool.Ball` defaults to `planeCount = 32` (`ConvexTool.cs:164`) and throws two lines
later at `:168` (`ThrowIfGreaterThan(planeCount, MaxPlanes)` = 16). `ConvexTool.Ball(2.0)` therefore throws on its
own default argument. All four call sites in the repo pass `planeCount` explicitly — `ConvexTool.Ball(1.2, 12)` at
`StepBinsTests.cs:143`, and `Ball(3, 12)`, `Ball(2.0, 12)`, `Ball(1.6, 12)` at `ConvexDexelTests.cs:64, 530, 611` —
so nothing trips today, but the first caller who omits it gets an exception instead of a tool. `ConvexTool.Box`
(6 planes, `ConvexTool.cs:120-128`) and `ConvexTool.Octahedron` (8, `:139-149`) are unaffected.

### F6 — MEDIUM: the binned/unbinned bit-identity test never reaches the guard it appears to test

`ConvexDexelTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` (`:602-629`) asserts
`Assert.Equal(b.Overflows, a.Overflows)` at `:626` with **no precondition that either side is non-zero**. If no column
reaches capacity at any of 16 / 32 / 64 / 241 cells, the assertion is `0 == 0` and the capacity guard — the thing
that makes order matter — is never entered. The commit message's "the binned launch is bit-identical to the unbinned
one" and `docs/long-programs.md:172-173` therefore rest on a comparison that cannot see the one behaviour that could
differ.

This is the exact failure mode `Step2FollowUps.md` item 2 found in `DexelMapTests`, repeated in the new file.
`ConvexDexelTests` does get it right once: `WithOneIntervalPerColumnEveryColumnIsCutDownToTheSweepsBottom` has
`Assert.True(dexel.Overflows > 0)` at `:478` before anything else. The other two overflow comparisons
(`:464` and `:596`) have no such precondition; `:596` is the CUDA-vs-CPU check, where a zero on both sides would make
the overflow half of the comparison hollow (the interval and volume assertions on the same map are still substantive).

### F7 — LOW: `zmap.cu:941-943` overclaims

> "Mirrors `ConvexProfile.Extremum`, **operation for operation, so both backends land on the same float**."

The commit message for the same commit says the opposite, and gives the reason: *"interval ends 4.3e-05 to 1.3e-04 mm
against a cell-relative threshold of cell / 100 = 8.3e-04 mm, which is what the kernel's fma contraction buys over
C#"*. nvcc contracts `a*b + c` into an FMA by default; C#'s `MathF` does not, so
`r00*nx + r01*ny + r02*nz` (`:1008-1010`, mirrored at `ConvexProfile.cs:199-201`) associates differently on the two
sides. "Operation for operation" is true of the source text and false of the machine code. Either soften the comment
or build with `-fmad=false` on this path.

### F8 — LOW: `fminf`/`fmaxf` and `MathF.Min`/`MathF.Max` disagree on NaN

Kernel `zmap.cu:918-919, 936, 955, 967` vs CPU `ConvexProfile.cs:208-209, 251-252, 272, 285, 302`. `fminf`/`fmaxf`
return the non-NaN operand; `MathF.Min`/`Max` propagate NaN. A NaN can reach `ExtremumAt` when `t` is infinite and a
line's slope is exactly zero: `-c/dot` overflows to ±∞ in `convex_where` when `s` is subnormal-tiny, and then
`g[2k] + g[2k+1]·t` computes `0 · ∞ = NaN` (`:935`). C# would then produce a NaN envelope and return false from
`high > low`; the kernel would drop the NaN and keep going. Narrow, but it is a real semantic divergence between two
implementations the commit says land on the same float.

### F9 — LOW: the 512-byte stack claim omits one of the four arrays

`ConvexProfile.cs:83-86` justifies `MaxPlanes = 16` with "the lower and the upper bound each keep 2 · m floats on top
of the column's 2 · 16 intervals, which stays under the 512 bytes a thread may use". That is `32 + 32 + 32 = 96`
floats = 384 B, and it omits `dexel_subtract`'s `float out[2 * kMaxDexelIntervals + 2]` (`zmap.cu:1039`, 136 B).
384 + 136 = 520 B, over the limit — unless nvcc reuses the convex buffers for `out`, which a liveness-based allocator
should do since `convex_span` has returned by then. See §f for the full picture; this needs a measurement, not a
reading. The sentence also lives in a C# file, where the CUDA per-thread local limit does not apply at all.

### F10 — LOW: `docs/long-programs.md:173` — "301 cells" is the ball test's number

The convex bit-identity test runs at `{ 16, 32, 64, 241 }` (`ConvexDexelTests.cs:613`). 301 is
`DexelMapTests.cs:214`, the *ball* test. The error originates in `CudaLongProgramsFindings.md:657` (§10.5), which
wrote "16, 32, 64 and 301 cells" for `ConvexDexelTests` and was copied into the log.

### F11 — LOW: `docs/performance.md:275-276` chains two numbers that do not chain

> "the baseline's covered wheel is 3793 passes over 1920 grains, 13 841 hulls, so a preview program is of the order
> of 10⁴ steps"

3793 × 1920 is 7.3 × 10⁶ grain-poses. The 10⁴ figure is supported only by the 13 841 hulls (one preview step per hull),
not by the passes × grains it is presented next to. The conclusion (the binning is a fraction of a millisecond) is
probably right and the sentence says plainly that step 4 should measure it rather than trust the arithmetic — but the
arithmetic as written does not get there from the numbers beside it.

### F12 — LOW: two constants with no link, both fail loudly, neither fails at compile time

`ConvexProfile.MaxPlanes = 16` (`ConvexProfile.cs:88`) vs `kConvexPlanes = 16` (`zmap.cu:825`), and
`StepBins.Tile = 16` (`StepBins.cs:23`) vs `kDexelTile = 16` (`zmap.cu:822`). Neither pair can drift silently into a
buffer overrun — `zmap.cu:1419`/`:1437` reject an oversized tool before any launch, and `dexel_apply_common`'s
`tileCount != tilesX * tilesY` check (`:1301-1305`) rejects a tile-size mismatch with a specific message. Both drift
outcomes are runtime errors with a generic "invalid argument" or a tile-count message, not compile-time. Acceptable,
worth a comment that says so. The guard message at `ConvexProfile.cs:115` — "the kernel keeps two lines per half-space
per thread and reads `MaxPlanes`" — is inaccurate in one word: the kernel does not *read* `MaxPlanes`; it has its own
constant that is meant to equal it.

### F13 — informational: `StepBins.Tile` / `kDexelTile` and the CSR are genuinely shared

All four entry points go through one `dexel_apply_common` (`zmap.cu:1292-1367`) and one `dexel_apply_column`
(`:1083`), and both launches are the same two kernels with a different `stepIndex` argument. Nothing about the
convex path forks the tile computation. The tile counts agree: `StepBins.cs:59-60` and `zmap.cu:1299-1300` compute
`tilesX`/`tilesY` the same way from the same `Tile = 16`.

---

## Answers a–i

### a) Does `convex_span` implement the same t-narrowing as `ConvexProfile.Span`?

**Yes, statement for statement.** I compared them line by line. The algorithm, the operand order, the association
order and the control flow match:

| step | CPU | kernel |
| --- | --- | --- |
| read T_A, w, R | `ConvexProfile.cs:181-185` | `zmap.cu:991-995` |
| buffers, `nLo`/`nHi`, `tLo=0`, `tHi=1` | `:190-193` | `:1000-1002` |
| `m = R·n`, `dot = m·w`, `c = d + m·T_A − m_x·x − m_y·y` | `:199-203` | `:1008-1012` |
| `mz == 0` → bound t alone, `c<0` with `dot==0` → miss | `:205-212` | `:1014-1021` |
| `inv = 1/mz`, split by sign into `below`/`above` | `:213-217` | `:1022-1026` |
| `tLo > tHi \|\| !Where(...)` → miss | `:220` | `:1029` |
| `low = Extremum(below,…,low:true)`, `high = Extremum(above,…,low:false)` | `:222-223` | `:1030-1031` |
| `!(high > low)` → miss | `:224` | `:1032` |

`Where` vs `convex_where` (`:242-258` vs `:908-924`) is character-for-character the same, including
`else if (k > 0f) return false;` for the parallel-and-above case and the `bi == bj` skip in `Extremum`
(`:264-289` vs `:948-971`). The `low = high = 0f` assignments at `ConvexProfile.cs:210, 220, 224` have no kernel
counterpart; that is harmless because the only caller checks the bool first
(`zmap.cu:1105-1108` — `float lo, hi; … if (!hit) continue;`), but it does mean `convex_span` leaves its `low`/`high`
**uninitialised** on every failure path where the CPU reference pins them to 0.

Divergences found, all of them real but none algorithmic: **F7** (fma contraction, which the commit message
acknowledges and the kernel comment denies) and **F8** (NaN handling in `fminf`/`fmaxf`). The `mx`, `my`, `mz`, `dot`
and `c` expressions are textually identical, so the *source* is a faithful mirror; the *machine code* is not, and the
comment at `zmap.cu:941-943` claims it is.

I found no place where the two narrow `t` differently, drop a candidate differently, or order the bounds differently.

### b) Is `MaxPlanes = 16` mirrored in the kernel?

Yes, as a **separate constant**, not a mirror. `kConvexPlanes = 16` at `zmap.cu:825`, with the comment "Mirrors
`ConvexProfile.MaxPlanes`". It is enforced in both convex entry points (`zmap.cu:1419`, `:1437`), and it sizes the
per-thread buffers `float gLo[2 * kConvexPlanes], gHi[2 * kConvexPlanes]` (`zmap.cu:1000`), which are the same size
as the managed `stackalloc float[2 * MaxPlanes]` at `ConvexProfile.cs:190-191`.

So the two numbers are equal today and **cannot drift apart into a memory-safety problem**: the native guard runs
before any launch, so a tool over 16 planes gets `nc_dexel_apply_convex_steps: invalid argument` (return 1), which
`CudaBackend.cs:333` turns into a `GpuNativeException`. The drift is loud, not silent — but it is loud *at run time*,
with a message that names neither `MaxPlanes` nor the tool, and the guard message at `ConvexProfile.cs:115` misstates
the mechanism (the kernel does not read `MaxPlanes`; see **F12**). A static assert or a generated header would close it
properly; nothing enforces it today.

### c) What does the kernel do for `m_z` near zero?

**Bare division, exact-zero test only, no epsilon and no early out.** `zmap.cu:1014` tests `mz == 0.f` exactly; every
other value falls through to `const float inv = 1.f / mz;` at `:1022`. The CPU reference is identical
(`ConvexProfile.cs:205`, `:213`), so the two do not diverge here — which is the right outcome, since a divergence
would have been worse than the shared exposure.

The exposure is real and is documented, but in a remark rather than in code:
`ConvexProfile.cs:60-64` states that "the division by mz is where the conditioning goes", that it "cannot happen for
a turn about the z-axis, where m_z = n_z regardless of the angle", and that for a tilted rotation "the caller should
keep the tool's normals away from the horizontal". `ConvexTool.Bounded()` (`ConvexTool.cs:210-237`) refuses an
unbounded plane set and `Corners()` (`:243-278`) refuses redundant planes, but neither of those constrains `|m_z|`
after a rotation — `Bounded()` reads `p[q].Nz`, not the rotated normal. So a tilted rotation over a tool with a
near-horizontal face gives an unbounded, not a small, error, and nothing in the code or in `ConvexTool` bounds it.

The only test that exercises this is `CudaAgreesWithTheCpuReference` (`:568-599`), and it exercises it as a
*tolerance*: `worst < cell / 100` (`:592`), with the 1/mz conditioning argued in a comment (`:589-591`) and in
`CudaLongProgramsFindings.md` §10.6, not asserted. That is the right call and I would not change it — but it means a
future change to the division order moves silently inside the bound.

### d) Does the convex path go through `dexel_subtract` and inherit the capacity guard?

**Yes, unconditionally.** `zmap.cu:1106-1109`:

```c
const bool hit = planes == nullptr ? swept_span(x, y, p, lo, hi)
                                   : convex_span(x, y, p, planes, planeCount, lo, hi);
if (!hit) continue;
if (dexel_subtract(local, n, k, lo, hi)) over++;
```

One `dexel_subtract` (`:1036-1063`), one `capacity = k`, one guard (`:1052-1056`), for both tool shapes and both
launches. `DexelMap.Subtract` (`DexelMap.cs:216-241`) is the same algorithm on the host, guard included at `:232-237`.
The order-dependence therefore applies to convex programs exactly as it does to ball ones — see **F2**.

The mitigating observation, which is not in the docs: a convex step removes a larger interval per call than a ball
step does, so the guard is reached sooner, and the split pattern over a rotating pose sequence is not the same as
over a straight one. I have not measured how often a convex program at `k = 4` (the `DexelMap` default,
`DexelMap.cs:25`) reaches the guard; `ConvexDexelTests` uses `k = 6` or `8` almost everywhere, which is itself a hint
that the author knew the default does not hold up.

### e) Does the convex path report overflows, and is there a non-zero precondition?

**Overflows are reported, through exactly the same path.** `dexel_apply_column` returns `over` (`:1100, 1109, 1114`)
and both kernels `atomicAdd` it (`zmap.cu:1127`, `:1145`) to the same `d->overflows` counter the ball path uses, which
`nc_dexel_read` copies out (`:1461-1468`). There is no convex-specific overflow channel and none is needed.

**Preconditions: one yes, two no.**

| assertion | line | non-zero precondition? |
| --- | --- | --- |
| `Assert.True(dexel.Overflows > 0)` | `ConvexDexelTests.cs:478` | **yes** — before anything else in that test |
| `Assert.Equal(one.Overflows, many.Overflows)` | `:464` | no, but the same test also compares all 62 500 counts, so it is not hollow |
| `Assert.Equal(cpu.Overflows, gpu.Overflows)` | `:596` | **no** |
| `Assert.Equal(b.Overflows, a.Overflows)` | `:626` | **no** — and this is the one the commit message leans on (F6) |

So the exact failure mode `Step2FollowUps.md` item 2 found in `DexelMapTests` is **still open** there and has been
**copied into the new file** at `:626`, in the one test whose advertised purpose is to rule out exactly the
behaviour that only shows up when a column fills.

### f) Per-thread register/stack cost of the two-lines-per-half-space buffers

Arithmetic, from the source at `kConvexPlanes = kMaxDexelIntervals = 16`:

| array | line | floats | bytes |
| --- | --- | ---: | ---: |
| `float local[2 * kMaxDexelIntervals]` (`dexel_apply_column`) | `zmap.cu:1096` | 32 | 128 |
| `float gLo[2 * kConvexPlanes]`, `gHi[...]` (`convex_span`) | `zmap.cu:1000` | 64 | 256 |
| `float out[2 * kMaxDexelIntervals + 2]` (`dexel_subtract`) | `zmap.cu:1039` | 34 | 136 |
| **total if all four are live at once** | | **130** | **520** |

The ball path today peaks at `local` + `out` = 66 floats = 264 B (its `swept_span` has no arrays). The convex path
peaks at **384 B** if nvcc reuses `gLo`/`gHi`'s frame for `out` — which it should, since `convex_span` has returned
by then and only `lo`/`hi` survive — and at **520 B** if it does not. 520 B is over the 512 B per-thread local limit.

**This needs a measurement and I did not run one.** The right measurement is `nvcc -Xptxas -v` on `zmap.cu`, reading
`lmem` and `reg` for `dexel_apply_kernel` and `dexel_apply_binned_kernel`, or
`cudaFuncGetAttributes().localSizeBytes` at runtime. Until that is read, `ConvexProfile.cs:83-86`'s "stays under the
512 bytes a thread may use" is an arithmetic claim that omits one of the four arrays (**F9**). A `static_assert` on the
sum would make the question impossible to get wrong.

**Does the binned twin share them?** Yes, literally — the same function. `dexel_apply_binned_kernel` (`:1134-1146`)
calls the same `dexel_apply_column` (`:1083-1115`) that `dexel_apply_kernel` (`:1118-1128`) calls; the only
difference is `stepIndex = tileSteps` and the `first`/`last` range. There is one copy of the buffers in the binary,
and it is used by all four entry points.

### g) CSR sharing and the convex completeness test

**Shared, completely.** `dexel_apply_common` (`zmap.cu:1292-1367`) is the single implementation for all four entry
points; `planes`, `planeCount` and `stride` are its only per-layout parameters, and the tile-count validation, the
`cudaMalloc`s, the uploads, the launch geometry and the timing are shared code. The CSR is uploaded by the same
three `cudaMemcpy`s (`:1328-1332`) and consumed by the same `dexel_apply_binned_kernel`.

**The convex branch of `StepBins.Tiles` has its own independent completeness test.** `StepBinsTests
.EveryConvexStepThatReachesAColumnIsInThatColumnsTile` (`StepBinsTests.cs:117-146`) builds the CSR with
`convex: true`, then checks every (step, column) pair that **`ConvexProfile.Span` accepts** (`:135`) against the CSR
(`:138`), i.e. it calls the interval routine itself rather than reusing `StepBins.Tiles` — the same independence the
ball version has. It has a non-zero precondition too (`reached > 5_000`, `:145`), which the ball version's does not
lack either (`reached > 10_000`, `:62`). The walk it uses (`WalkConvex`, `StepBinsTests.cs:149-160`) turns the tool
as it goes (`Orientation3.AboutZ(0.3 * Math.Sin(t))`), so the swept boxes are not all axis-aligned copies — the case
that would break a naive reading of the first four fields.

**So the ball one is not the only coverage.** The residual gap is the opposite direction: nothing tests that a convex
step is *not* over-binned, so the `2 * MarginMm` growth in the convex branch (`StepBins.cs:105-108`, two margins
where the ball branch adds one) is untested in the tight direction. `extra` is counted and printed and never
asserted, in both files — carried over from step 2.

### h) The four step-2 follow-ups, re-checked against `da26cfc`

| # | Item | Verdict |
| --- | --- | --- |
| 1 | the false order claim | **still open**, `docs/long-programs.md:83-84`, same lines, and step 3 extends it to convex programs (F2) |
| 2 | overflows not pinned | **still open** in `DexelMapTests` (untouched by this commit) and **copied** into `ConvexDexelTests.cs:626` (F6) |
| 3 | `PackMs` missing | **still open, worse** — six untimed pack sites: `ToolProfile.Pack` at `CpuBackend.cs:40`, `:184` and `CudaBackend.cs:76`, `:304`; `ConvexProfile.Pack` at `CpuBackend.cs:221` and `CudaBackend.cs:336`. `PackMs` appears nowhere in `src/` or `docs/`. The pack is still inside the CUDA `WallMs` (`CudaBackend.cs:302-303` starts the clock, `:304` packs) and outside the CPU one (`CpuBackend.cs:184` packs, `:192` starts the stopwatch) |
| 4 | CI does not compile `zmap.cu` | **still open** — `Stykker.NanoCut.Gpu.Native` still not in `Stykker.NanoCut.slnx`, CI still does not call `build.ps1`. The commit added 387 changed lines to a file that nothing but a local nvcc run compiles |

Also re-checked, both **still open**:

- `ConvexTool.Ball` still throws on its own defaults: `planeCount = 32` (`ConvexTool.cs:164`) against
  `ThrowIfGreaterThan(planeCount, MaxPlanes)` (`:168`) with `MaxPlanes = 16` (`ConvexProfile.cs:88`). F5.
- `DexelMap.ApplyConvexSteps` still defaults to `ZMapReadBack.Always` (`DexelMap.cs:146`), repeating the `ApplySteps`
  default at `:131`. And the underlying reason has not changed either: `nc_dexel_read` (`zmap.cu:1459`) copies
  `count * d->k * 2 * sizeof(float)` — the whole slot, not the live prefix. So there are now two entry points that
  pay it on every batch.

Line numbers from `Step2FollowUps.md` that have moved: **none.** `docs/long-programs.md:83-84` and `:92` are where the
note says; `StepBins.cs:27` (`MarginMm = 1e-6f`, still 1 nm) still holds; and `StepBinsTests.cs:51, 65, 68`
(`extra` counted and printed, never asserted) still do too — the new convex test was appended after them, so the ball
test's line numbers did not move. The new convex completeness test reproduces the same shape at
`StepBinsTests.cs:149, 163, 166`.

### i) Every claim and number in the docs, against the source

**`docs/performance.md` (+60).** Checked:

- `:250` "Up to 16 half-spaces per tool (`MaxPlanes`)" ✓ (`ConvexProfile.cs:88`, `zmap.cu:825`).
- `:252-254` "What is measured here is correctness, not time" ✓ — consistent with `bench/README.md`'s added paragraph
  and with the commit message's "No time measurement of the convex path".
- `:256-260` the table. All nine preview/exact/difference figures are the ones in `docs/long-programs.md:155-158`
  and in the commit message. The four sampling bounds reproduce exactly from the test's own geometry (F3); the two
  single-step rows are also confirmed against a hand count of the sampled columns (F3).
- `:262-264` "the volume a column model can be off by at that grid — the rim it samples, P·h/2 of area over the
  silhouette's perimeter times the swept height" — **not supported** for a multi-step program (F3).
- `:269-272` O(m³) and the envelope-is-O(m) remark ✓ matches `ConvexProfile.cs:66-77` and `zmap.cu:945-947`.
- `:273-277` "18.9 ms at 793 600 steps, about 24 ns per step" ✓ (18.9 ms / 793 600 = 23.8 ns) — the 18.9 ms is the
  step-2 bench figure already in the repo, not a new measurement, and the sentence says so. The 10⁴-step step is the
  weak link (F11).
- `:278-283` `Process3.Sample`'s linear-in-angle guard, ~50 000 poses, 65 536 with the binary subdivision, 11 preview
  steps, 4 µm, 512 exact intervals, 72 s. The 11 preview steps I can check and they are right:
  `ConvexStep.StepsForRotation(1.2, sqrt(1.2²+0.3²) = 1.2379, 0.002) = ceil(1.2/sqrt(8·0.002/1.2379)) = ceil(10.56) =
  11` (`ConvexStep.cs:103-112`, `ConvexDexelTests.cs:409`). The rest are recorded measurements I did not re-derive.
- `:324` **contradicts `:258`** (F4).
- `:347-352` the new lesson on the feasibility test — ✓ matches the log and the source (`ConvexProfile.cs:229-241`,
  `zmap.cu:901-907`).
- `:281-285` the O(m³) walk and "the interval search is O(m³) in the half-spaces and the envelope would make it
  O(m)" ✓.

**`docs/long-programs.md` (+98).** Checked:

- `:75` "at most 32 in the kernel" — **wrong, should be 16** (F5). Contradicts `:120` of the same file.
- `:83-84` the order claim — **wrong**, contradicted by `:92` (F2).
- `:89` "grown by the radius and a margin of 1 nm" — true of the ball branch (`StepBins.cs:110-117`); the convex
  branch has no radius and grows both ends by `2 * MarginMm` (`:105-108`). Step 2's section predates step 3, so this
  is not a regression, but the step-3 section does not correct it either.
- `:140-152` the step-3 build description, file by file ✓ — every file and symbol named exists at the cited shape.
- `:154-158` the table ✓ (F3).
- `:160-167` "What the table checks" ✓ — the 451 × 101 count, the 450 × 100 it displaces, and the 0.4408 mm³ against
  0.4408 mm³ all check out by hand, as does "the same closed rule is why the 500-cell row lands on the exact number
  to the digit" (225 × 50 × 0.04² × 2 = 36.0000 exactly).
- `:169-173` the CUDA paragraph. "four tools (6, 8, 12 and 6 planes), 220 steps, 241 × 241 columns, binned launch"
  ✓ (`ConvexDexelTests.cs:529-530` for the tools, `:538` for the 220, `:579` for 241 × 241; `BinSteps` defaults to
  true at `CudaBackend.cs:51`).
  "0.083 mm grid" ✓ (20/241). "8.3e-04" ✓ (0.083/100). "**301 cells**" ✗ — the test uses 241 (F10).
  "no column differs in its interval count anywhere" — the assertion is `differing <= columns/10_000`
  (`:593`), i.e. up to 5 of 58 081, **not** zero. The docs say "no column differs"; the test allows five. Small, but
  the doc is stricter than the test it cites.
- `:190-196` the step-3 log entry ✓, except the "the box inside its sampling bound at every grid" phrasing, which is
  true but sits next to a case that sits exactly *on* its bound.
- `:197-203` the "first version was wrong" log entry ✓, consistent with the source comments at
  `ConvexProfile.cs:236-241` and `zmap.cu:901-907`.
- `:210-218` the `Process3` rotation cost ✓ (recorded, not re-derived).
- `:220-224` the tolerance entry ✓, and it is the reason F7 matters: the log correctly attributes the gap to fma
  contraction, while the kernel comment at `zmap.cu:943` claims the two land on the same float.

**`docs/todo.md` (+45), `README.md` (+6), `bench/README.md` (+5), `docs/handoff.md` (+39).** All checked, all
consistent with the source. `handoff.md:20` "250 + 4 … (24 of the 250 are new)" ✓ — `ConvexDexelTests` has exactly 24
`[Fact]` methods and `StepBinsTests` 4, so 28, matching the recorded run. `bench/README.md` says plainly that no bench
mode exists for step 3 ✓. `docs/todo.md` states the 0.4408 mm³ at 1000 cells correctly, in mm³, where
`docs/performance.md:324` does not.

---

## Verified by reading

Every claim above with a `file:line` was read out of `da26cfc` (kernel via `git show`, the rest from the working tree
which `git status` shows is identical). Specifically:

- F1's whole chain: `zmap.cu:1002, 1029, 951, 1032, 1038, 1045-1058, 1052-1056, 1060-1061, 1101, 1105-1109, 1127,
  1145, 1259, 1308, 1341-1348, 1377, 1419, 1437, 1459`; `CudaBackend.cs:22, 298-326, 330-360, 333, 386`;
  `DexelMap.cs:130-157`; `ToolProfile.cs:16`. Reached from the public API because `DexelMap` keeps no tool-shape state
  and the device dexel is cached per map for the map's lifetime.
- F3's arithmetic: `ConvexDexelTests.cs:317-320, 334-339, 344-379, 406-441`; `ConvexProfile.cs:120-146`;
  `ConvexStep.cs:103-112`. The 451 × 101 / 225 × 50 column counts, the 12 × 8 octahedron box and the 0.1253 /
  12.8512 / 1.7728 bounds are my arithmetic from the test's own constants, not a run.
- a)–g): the two `Span` implementations compared statement by statement; `ConvexProfile.cs:83-88, 99, 113-115, 168,
  176-226, 242-258, 264-289, 296-305`; `zmap.cu:824-837, 863-896, 908-924, 930-939, 948-971, 988-1033, 1036-1063,
  1083-1115, 1118-1146, 1233-1267, 1292-1367`; `ConvexTool.cs:15, 38-41, 95-101, 164-168, 210-278`;
  `StepBins.cs:14-27, 44-93, 97-127`; `StepBinsTests.cs:41-71, 117-160`; `ConvexDexelTests.cs` in full;
  `CpuBackend.cs:182-245`; `CudaNative.cs:118-131`; `IDexelBackend.cs:28-34`.
- h): `docs/long-programs.md:83-84, 92, 173`; `DexelMap.cs:146`; `DexelMapTests.cs:86, 214`;
  `ConvexDexelTests.cs:478, 596, 613, 626`; `CpuBackend.cs:40, 184, 221`; `CudaBackend.cs:76, 302-304, 336`;
  `Stykker.NanoCut.slnx` (no Native project).
- i): every number in `docs/performance.md:244-283, 324`, `docs/long-programs.md:75, 83-84, 89, 140-173`,
  `docs/todo.md`, `README.md`, `bench/README.md`, `docs/handoff.md`, cross-checked against the source above.

## Inferred — needs a measurement I did not run

- **F1 is a trace, not a run.** Every step of it is a direct read of the code and IEEE comparison, but nobody has
  watched a map come back empty. It should be confirmed before it is fixed and again after.
- **F3's ~0.1918 mm³** for the rotating case is my hand arithmetic using the same formula the test uses; it has not
  been produced by running `SamplingBound` with a union instead of a max. The 0.1253 it is compared against *did*
  reproduce to four digits, which is the reason I trust the method.
- **f)** whether the convex launch spills 384 B or 520 B of local memory, and what it does to occupancy, is a
  codegen question. `nvcc -Xptxas -v` on `zmap.cu` settles it; I did not run it and am not quoting a register count.
- **F6** says the bit-identity test cannot *see* the order-dependence. I did not check whether these particular
  scenes overflow; I only checked that nothing asserts it either way.
- **F8** requires `t` to reach ±∞ from a subnormal `s`, and I did not construct a tool and pose that does it.
- `Process3`'s 512 exact intervals / 72 s, the octahedron preview figures and the 1.3e-04 mm worst interval end are
  recorded measurements, reproduced here only where they could be checked by hand from the geometry (the bounds and
  the column counts, which matched to the last digit). I have not re-run anything.

## What I could not determine

- Whether `dexel_apply_column`'s unused `low`/`high` on the failure paths ever trips a compiler diagnostic in an
  nvcc build — the kernel is not compiled in CI (**item 4**) or by anything I was permitted to run.
- Whether the `fminf`/`fmaxf` NaN difference (F8) has ever been hit in practice; it needs a degenerate tool.
- How often a realistic convex program at the default `k = 4` reaches the capacity guard, which is what would tell
  you whether F2 matters in step 4 or only in principle.

## Not done

No build, no test, no nvcc, no benchmark, no app. No commit, no push, no branch moved, no worktree created, no patch
applied, no file written outside this one. `Step2FollowUps.md`'s prepared `step2-followups.patch` was not applied and
was not tested against `da26cfc`.

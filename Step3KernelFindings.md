# Step 3, CUDA half: findings

Read-only review of the kernel, the new tests and the new docs. Written 2026-10-03 in `worktree-keen-leaf-f9cab1`
against the main checkout at `C:\_AI\StykkerNanoCut\StykkerNanoCutRepo`. Nothing was built, run, or edited; every
line reference below was read from the source in that checkout.

## State of the tree — the handoff is stale on this point

`Step3KernelHandoff.md` says step 3 is "finished, documented and still uncommitted, in the working tree of the main
checkout". It is not any more. As of now:

```
da26cfc (HEAD -> long-programs-step-1) Long programs step 3: a convex tool on a pose sequence in the dexel kernel
63adb49 (origin/long-programs-step-1) Long programs step 2: ...
```

`git status --porcelain` is empty. Everything below is therefore read from commit `da26cfc`, and
`git diff 63adb49 da26cfc` is the step-3 delta (18 files, +2125/−121). Nothing in this review depends on the
"uncommitted" framing.

---

## a) `convex_span` vs `ConvexProfile.Span` — no logic divergence found

**Verified by reading.** I walked the two implementations side by side. They are the same algorithm, in the same
order, with the same sign conventions and the same comparison directions.

| Step | CPU (`ConvexProfile.cs`) | Kernel (`zmap.cu`) | Same? |
| --- | --- | --- | --- |
| entry | `Span` :176 | `convex_span` :988 | — |
| lower/upper line buffers | `stackalloc float[2 * MaxPlanes]` ×2 :186-187 | `float gLo[2 * kConvexPlanes], gHi[...]` :1000 | yes |
| `m = R·n` | :200-202 | :1010-1012 | yes, same order, same pairing |
| `g = m·w` | :203 | :1013 | yes |
| `c = d + m·T_A − m_x·x − m_y·y` | :204 | :1014 | yes — **and the `+ m·T_A` sign is right**, which the kernel's own remark (`:983-985`) calls out as the trap |
| horizontal plane | `if (mz == 0f)` :205 | `if (mz == 0.f)` :1014 | yes, exact-zero test, not an epsilon |
| `dot > 0` → raise `tLo` | :208 `MathF.Max(tLo, -c/dot)` | :1017 `fmaxf(tLo, -c/dot)` | yes |
| `dot < 0` → lower `tHi` | :209 | :1018 | yes |
| `dot == 0 && c < 0` → miss | :210 | :1019 | yes |
| slope line | `float inv = 1f / mz;` :213 | `const float inv = 1.f / mz;` :1022 | yes — bare division, see (c) |
| which list | `mz < 0f ? below : above` :214-215 | :1024-1025 | yes |
| t-range guard | `if (tLo > tHi \|\| !Where(...))` :220 | :1029 | yes (the `tLo <= tHi` inside `Where` makes the first half redundant in both) |
| t narrowing | `Where` :242 | `convex_where` :908 | yes — `s > 0 → tHi`, `s < 0 → tLo`, else `k > 0 → false` |
| the two ends | `Extremum` :264 | `convex_extremum` :948 | yes |
| envelope at t | `ExtremumAt` :296 | `convex_envelope` :930 | yes, `low ? max : min` |
| empty list | `±INFINITY` :266 | `±INFINITY` :951 | yes |
| candidates | both ends of t, then every pair :270-271, :277-286 | :953-971 | yes |
| parallel pair skip | `if (bi == bj) continue;` :280 | :969 | yes |
| crossing test | `if (t < tLo \|\| t > tHi) continue;` :282 | :971 | yes |
| final test | `if (!(high > low))` :224 | `return high > low;` :1032 | yes, same NaN behaviour |

So the specific fear — one of the two hand-written copies quietly differing — does not materialise. Two differences
remain, and both are float-semantics rather than logic.

**a1. `fmaxf`/`fminf` and `MathF.Max`/`MathF.Min` disagree on NaN, and the two implementations use one each.**
CUDA's `fmaxf`/`fminf` follow IEEE 754-2008 `maximumNumber`: if one operand is NaN the other is returned. C#'s
`MathF.Max`/`MathF.Min` return NaN if either operand is. `ConvexProfile.cs:299` (`v = low ? MathF.Max(v, q) : MathF.Min(v, q)`)
against `zmap.cu:937` (`v = low ? fmaxf(v, q) : fminf(v, q)`), and the same substitution at `:252`/`:914`, `:1017`/`:208`.
Where a NaN can appear at all is under (c). *Inferred:* for a tool that passed `ConvexTool.Bounded()` the NaN path is
not reachable in practice — `Bounded()` guarantees the normals positively span, so a valid rotation gives at least
one `mz > 0` and one `mz < 0`, so neither list is ever empty and neither `±INFINITY` end is ever returned. The
divergence is latent, not live.

**a2. The kernel's "operation for operation, so both backends land on the same float" comment is false as built.**
`zmap.cu:946-947` says that about `convex_extremum`. `build.ps1:78` compiles with

```
$nvccArgs = "$optimise -shared -std=c++17 -cudart static $gencode -Xcompiler `"/EHsc`" -o ..."
```

with no `--fmad=false`, so nvcc's default `--fmad=true` applies and every `a*b + c` in `convex_span` is contracted
into an FMA. RyuJIT does not contract float multiply-add. The two backends therefore differ by roughly an ulp per
operation, propagated through the O(m³) envelope walk. That is exactly what `ConvexDexelTests.cs:563-566` says, and
it is what the tolerance at `:593` is sized for. **The comment at `zmap.cu:946-947` is the stale one** — it asserts a
bit-parity that neither the build nor the test claims. Worth deleting or re-wording, because it is the sentence a
future reader will trust when deciding whether the two copies may drift.

**a3.** CPU writes `low = high = 0f` on every failure path (`:210`, `:220`, `:224`); the kernel leaves them
unwritten (`:1019`, `:1029`). Harmless — the only caller (`zmap.cu:1108-1109`) reads them only when `hit` is true.

---

## b) `MaxPlanes = 16` *is* mirrored — the handoff's premise was wrong, but the drift risk is real

There is no `kMaxPlanes`, but there **is** `kConvexPlanes`:

```cpp
// zmap.cu:824-825
/// <summary>Half-spaces the convex tool sweep accepts. Mirrors ConvexProfile.MaxPlanes.</summary>
constexpr int kConvexPlanes = 16;
```

It is used at `zmap.cu:1000` (the buffer size), `:1419` and `:1437` (both native entry points reject
`planeCount > kConvexPlanes`). The C# side caps at the same value in four places: `ConvexProfile.cs:88` (`MaxPlanes`),
`ConvexProfile.cs:113-115` (`Pack` throws), `ConvexTool.cs:99-101` (`FromPlanes` rejects `0 or > MaxPlanes`),
`ConvexTool.cs:164,168` (`Ball`). **Both ends are 16 and both are enforced. There is no silent overflow today.**

What is worth saying:

- They are two hand-written literals with no shared source of truth, and **no test pins them together**.
- The failure mode if they drift is not a buffer overrun but a wrong exception: raise `ConvexProfile.MaxPlanes` and
  `ConvexTool` accepts a 24-plane tool, `Pack` accepts it, the host uploads it, and the native guard at `:1419` throws
  a generic `"invalid argument"` surfaced as `GpuNativeException` — not the `ArgumentOutOfRangeException` the
  message at `ConvexProfile.cs:115` promises, and not before the O(steps × corners) pack has been paid.
- **The native guard is unreachable, so it is untested.** Nothing can construct a `ConvexTool` with more than 16
  planes, so `planeCount > kConvexPlanes` can never be true from the managed side. There is no test for it and no
  way to write one without a native test harness. Given the handoff's standing finding that CI never compiles
  `zmap.cu` at all, a future raise of `MaxPlanes` would be discovered by a runtime throw on a GPU machine, not by a
  build or a test.
- Minor: the guard message at `ConvexProfile.cs:115` interpolates the **C#** constant, not the kernel's, so if the
  two ever disagreed the message would name a number the kernel does not use.

---

## c) `m_z` near zero: bare division, no epsilon, no early-out — and no test ever gets near it

**Verified by reading.** Both implementations do the same thing, which is to do nothing about it:

```csharp
// ConvexProfile.cs:205-213
if (mz == 0f) { ...horizontal case, bounds t alone... continue; }
float inv = 1f / mz;
```
```cpp
// zmap.cu:1014-1022
if (mz == 0.f) { ... }
const float inv = 1.f / mz;
```

An exact-zero test, then an unguarded reciprocal. There is no `|mz| > eps` early-out, no `isinf` check, no clamp,
no `low = -inf` sentinel other than the empty-list case. The remarks name this honestly
(`ConvexProfile.cs:40-44`): *"The division by mz is where the conditioning goes … the caller should keep the tool's
normals away from the horizontal."* So it is a documented, deliberate contract — not an oversight — but it is a
contract with no enforcement and no test.

**The consequential finding is that the contract is never exercised.** Every step-3 test rotates about z only:

- `ConvexDexelTests.cs:220` (`Turn`), `:429` (`ARotatingToolAgreesWithTheExactCut`), `:542` (`MixedSteps`)
- `StepBinsTests.cs:181` (`WalkConvex`)
- `Orientation3.AboutZ` is a rotation about z, so `r20 = r21 = 0, r22 = 1` (`ConvexStep.cs:26-30`) and therefore
  **`mz = n_z` exactly**, regardless of the angle.
- `Orientation3.AboutAxis` (`ConvexStep.cs:34`) exists and is **used nowhere in the repository** — grep over all
  `*.cs` returns only its own definition.

Consequences for the tools actually tested: `BoxTool` has `nz = ±1`, the octahedron `nz = ±0.577`, and a
12-plane Fibonacci ball has smallest `|nz| = 1/12`, so `1/mz ≤ 12`. The conditioning is at worst 12× everywhere in
the suite. **The CUDA-vs-CPU tolerance in `CudaAgreesWithTheCpuReference` (`ConvexDexelTests.cs:593`, `worst <
cell/100`) is justified in the docs by the `1/mz` conditioning, but the test cannot demonstrate that
justification** — a single tilted step would be the first thing to, and there is none.

What a genuinely ill-conditioned plane does, for the record: `inv` becomes large, `c * inv` / `dot * inv` overflow
to ±inf, then in `convex_where` a pair computes `k = ai - above[2*j]` and `s = bi - above[2*j+1]` that can be
`inf - inf = NaN`, and all three comparisons are then false, so **the pair is silently skipped** (`zmap.cu:913-916`,
`ConvexProfile.cs:251-255`). A NaN that reached `convex_envelope` would also behave differently on the two backends
(a1). All of that is *inferred* — it needs an input with a near-horizontal normal, which nothing constructs today.

There is one unvalidated public route to the degenerate case. `Orientation3` is a `public readonly record struct`
with an unvalidated primary constructor (`ConvexStep.cs:18-21`), so a caller can pass a matrix that is not a rotation.
If every `mz` comes out zero, the plane loop contributes no lines at all, `nLo = nHi = 0`, `convex_where` returns
`tLo <= tHi` = true, and `convex_extremum` returns `-inf` / `+inf` (`zmap.cu:951`). `high > low` is then true and
`dexel_subtract` is handed `[-inf, +inf]`, which turns every interval into `[a, -inf]` — an inverted column and a
`+inf` volume. `ConvexTool`'s own doc (`ConvexTool.cs:26-30`) refuses unbounded tools precisely to avoid "a map that
removes the whole stock"; a malformed orientation walks straight past that guard. Low severity — it needs a
nonsensical caller — but it is a public API with no check and no test.

---

## d) The convex path **does** inherit the capacity guard, so the docs are now wrong about convex programs too

**Verified by reading.** There is one subtract, shared by both tools:

```cpp
// zmap.cu:1106-1109
const bool hit = planes == nullptr ? swept_span(x, y, p, lo, hi)
                                   : convex_span(x, y, p, planes, planeCount, lo, hi);
if (!hit) continue;
if (dexel_subtract(local, n, k, lo, hi)) over++;
```

`dexel_subtract` (`:1036`) contains the guard at **`:1052`**:

```cpp
if (left && right && n + 1 > capacity)
{
    overflow = true;   // no room for a split: keep the part below the cut, lose the roof above it
    right = false;
}
```

and its managed twin `DexelMap.Subtract` (`DexelMap.cs:230-234`) is character-for-character the same. So a convex
program that fills a column gets the same order-dependent loss of the roof as a ball program. **Nothing in the step-3
text mentions this**, and `docs/todo.md:7-14` carries the claim forward into its Done entry.

The contradiction, with both references:

| Claim | Where | Truth |
| --- | --- | --- |
| "Removal is a union, so the order of steps inside a tile does not matter for the result (only the overflow counting can differ)." | `docs/long-programs.md:83-84` | **Wrong**, for the ball path already and now for the convex path too. When the guard fires, which side of the cut survives is decided by the order the steps arrive in. |
| "the count-then-place pass writes every tile's step indices in ascending order. … Both go through the same `dexel_apply_column`, so a column is subtracted in the same order whichever launch runs it." | `docs/long-programs.md:88-91` | This is the part that is right — binned and unbinned agree because the *order* matches. It does not rescue `:83-84`. |
| "**The result does not move.** Both launches report the *same* removed volume to the last digit at every size (0 overflows in every case, since a finishing pass never fills a column)" | `docs/long-programs.md:107-108` | True, and it says why in its own parenthetical: **0 overflows**. The moment a column overflows, `:83-84` stops holding for the binned-vs-unbinned pair too. |
| "A tool is a convex polytope given by its half-spaces (n·p ≤ d, **at most 32 in the kernel**)" | `docs/long-programs.md:75` | **Wrong.** `ConvexProfile.cs:88` = 16 and `zmap.cu:825` = 16. The same file says 16 at `:135` and `:200`, `docs/performance.md:250` says 16, `docs/todo.md:75` says 16. `:75` is the Model section and is simply stale. |

I checked whether step 3 accidentally contradicts the docs anywhere *new*, and the only two hits are `:75` (the 32)
and the un-corrected `:83-84`. The step-3 sections themselves (`long-programs.md:118-175`, `performance.md:244-283`)
do not restate the order-independence claim, so they do not add a new error — they inherit one.

---

## e) Overflows are reported end to end, and one convex test *does* have a non-zero precondition — the other three do not

**Verified by reading.** The convex path reports overflows at every layer, exactly like the ball path:

- `dexel_apply_column` returns `over` (`zmap.cu:1109`), both `__global__`s `atomicAdd` it
  (`dexel_apply_kernel` :1127, `dexel_apply_binned_kernel` :1145), `nc_dexel_read` copies it.
- `CpuBackend.ApplyConvexDexels` accumulates into `map.Overflows`; `CudaBackend.ApplyConvexDexels` goes through
  `ReadDexelDevice`. Nothing drops it.

The test side is mixed, and the pattern is instructive:

| Test | Assertion | Non-zero precondition? |
| --- | --- | --- |
| `WithOneIntervalPerColumnEveryColumnIsCutDownToTheSweepsBottom` :478 | `Assert.True(dexel.Overflows > 0)` | **Yes** — this is the gap found in `DexelMapTests`, correctly closed here |
| `ManySmallStepsCutTheSameAsTheOneBigStep` :464 | `Assert.Equal(one.Overflows, many.Overflows)` | No |
| `CudaAgreesWithTheCpuReference` :596 | `Assert.Equal(cpu.Overflows, gpu.Overflows)` | No |
| `BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` :626 | `Assert.Equal(b.Overflows, a.Overflows)` | No |

Two things follow, and the second is the one I would not have predicted:

1. **`ConvexDexelTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` (`:600-630`) reproduces the exact defect the
   earlier review found in its ball twin.** It asserts overflow equality on a scenario that is very unlikely to
   overflow (k = 6, a 1.6 mm 12-plane ball over a 220-step walk on a 20 mm block), so the assertion proves nothing
   about the counter. The fix is the two-line shape already used at `:478`: assert the scenario overflows, then
   compare.
2. **The one convex test that *does* assert a non-zero overflow count runs on the CPU, not on CUDA.** `:478` is
   inside a test built by `Stock(97, k: 1)` with no backend, and `DexelMap.cs:60` defaults to
   `CpuBackend.Instance`. So `DexelMap.Subtract`'s guard is exercised; `dexel_subtract`'s guard at `zmap.cu:1052`
   is **not exercised by any test with a non-zero precondition**, on either tool. The CPU and CUDA guards are
   identical source, which is why this is tolerable — but it is untested, and the whole point of the kernel's
   "Mirrors `DexelMap.Subtract`" comment is that they stay identical.

---

## f) Per-thread local memory roughly doubles, and the **ball** path pays for it

**Verified by reading — the byte counts are arithmetic on the array declarations; the occupancy effect is inferred.**

Buffers live in `dexel_apply_column` (`zmap.cu:1082`), which both `__global__`s call, so the binned and unbinned
twin share everything below.

| Array | Site | Bytes/thread |
| --- | --- | --- |
| `float local[2 * kMaxDexelIntervals]` | `:1096` | 32 floats = **128** |
| `float out[2 * kMaxDexelIntervals + 2]` (inlined `dexel_subtract`) | `:1039` | 34 floats = **136** |
| `float gLo[2 * kConvexPlanes], gHi[2 * kConvexPlanes]` | `:1000` | 64 floats = **256** |

Before step 3: 264 B. After: **520 B**. It is close to double. (If nvcc overlaps `out` with the `gLo`/`gHi` live
ranges the peak is 512 B instead; either way it is ~2× the previous figure.)

Two things follow, and the first is the one that matters:

- **The ball path pays it too.** `convex_span` is `__forceinline__` (`:988`) into a `?:` branch that the ball
  launch never takes, but a local-memory frame is sized statically per kernel — the compiler cannot shrink it on a
  branch it cannot prove dead. So `nc_dexel_apply_steps` and `nc_dexel_apply_steps_binned`, which never execute a
  single convex instruction, now carry 256 bytes of convex scratch per thread. Every measured ball number from
  step 2 (`docs/long-programs.md:97-101`, 2.7 ms at 793 600 steps) was taken on a binary whose per-thread frame was
  half this. **Inferred; needs `cuobjdump -res-usage` or a rebuild with `-Xptxas -v` on `nanocut_gpu.dll` to confirm
  the frame, and a re-run of `bench LongPrograms dexel` to confirm the cost is small.** My expectation is that it is
  small — local memory does not consume the register file or shared memory, so it does not move the occupancy
  calculator's bound, and the accesses are lane-interleaved so they coalesce — but it is bandwidth and L1 pressure,
  and it is not zero.
- **The dynamic indexing keeps the buffers out of registers.** `float* g = mz < 0.f ? gLo : gHi;` (`:1024`) selects
  the array at runtime and `at` (`:1025`) indexes it at runtime, so no amount of `-O3` will promote them to
  registers. That costs a handful of registers for the pointer and the address arithmetic, on the same path that
  already has `local[]`. Register count is the part that would actually move occupancy. *Not measured.*

The C# side of this same claim is undercounted. `ConvexProfile.cs:84-87` says the buffers *"stay under the 512
bytes a thread may use"*, counting 2·m floats ×2 plus the column's 2·16 — 384 B — and omitting `dexel_subtract`'s
`out[34]`. Two problems: the arithmetic misses 136 bytes, and 512 B is the pre-Volta local-memory limit; `build.ps1`
targets `sm_120` with `compute_75` PTX (`build.ps1:36`), and both are ≥ 7.0, where the limit is 1024 B. So 520 B is
fine — but the sentence is wrong twice over and would mislead the next person who comes near the ceiling.

---

## g) The binning and the CSR are fully shared, and the convex branch has its own completeness test

**Verified by reading.** Yes to both halves.

- **Shared.** `CudaBackend.ApplyConvexDexels` calls the same `StepBins.Build` (`CudaBackend.cs:343`) with
  `convex: true`, against the ball call at `:310` with `convex: false`. The only difference inside `StepBins` is
  `StepBins.Tiles` (`StepBins.cs:112-121`) reading the packed box at offsets 0-3 instead of the sphere's
  segment-plus-radius. Both launches then go through the same `dexel_apply_common` (`zmap.cu:1292-1371`), which is
  where the CSR upload, the tile-count validation (`:1301-1305`) and the launch selection live. `StepBins.Tile` is 16
  and `kDexelTile` is 16 (`zmap.cu:821`) — a literal pair, same drift caveat as (b), but `dexel_apply_common`
  rejects a wrong tile count at runtime so a drift is loud, not silent.
- **Covered.** `StepBinsTests.EveryConvexStepThatReachesAColumnIsInThatColumnsTile` (added in this commit,
  `StepBinsTests.cs:131-169`) is a real completeness test with the same shape as the ball one: it evaluates
  `ConvexProfile.Span` on every (step, column) pair and asserts `missed == 0` (`:167`), with a non-zero precondition
  (`reached > 5_000`, `:168`). That is the check the handoff asked for and it is there.
- **But it covers the easy half of the convex branch.** Like every other step-3 test, `WalkConvex` uses
  `Orientation3.AboutZ` (`StepBinsTests.cs:181`), so the box the binning reads is always an AABB of a z-rotated
  prism. The hard case for `Tiles` is a tilt, where the swept box's z-extent and the travel are not separable and a
  too-tight box would silently lose a step. Untested — same blind spot as (c), and the two would be closed by the
  same test.

One latent native-API gap, pre-existing from step 2 and not convex-specific: neither
`nc_dexel_apply_steps_binned` nor `nc_dexel_apply_convex_steps_binned` validates
`tileStart[tileCount] == tileStepCount`, so a caller passing a short `tileSteps` gets an out-of-bounds read in
`dexel_apply_binned_kernel`. The managed side is safe by construction (`StepBins.cs:78`), so this is only reachable
from a future non-managed caller.

---

## Doc contradictions, collected

| # | Claim | Reference | Reality |
| --- | --- | --- | --- |
| 1 | "Removal is a union, so the order of steps inside a tile does not matter for the result (only the overflow counting can differ)." | `docs/long-programs.md:83-84` | Wrong on both tools. `zmap.cu:1052` / `DexelMap.cs:230-234` discard the roof when there is no room, which makes the surviving geometry order-dependent. `long-programs.md:107-108` only holds because that program had 0 overflows. |
| 2 | "at most 32 in the kernel" | `docs/long-programs.md:75` | 16, on both sides: `ConvexProfile.cs:88`, `zmap.cu:825`. Contradicts `long-programs.md:135`, `:200`, `performance.md:250`, `todo.md:75`. |
| 3 | "no column differs in its interval count anywhere" | `docs/long-programs.md:170` | The test asserts `differing <= columns / 10_000` (`ConvexDexelTests.cs:594`), which at 241² = 58 081 columns permits 5. The doc states a strict zero. (The reported run may well have been zero; the *claim* is stronger than the *assertion*.) |
| 4 | "bit-identical at 16, 32, 64 and 301 cells" | `docs/long-programs.md:172` | The convex test iterates `{ 16, 32, 64, 241 }` (`ConvexDexelTests.cs:613`). 301 is the **ball** test's grid from the fixed step-2 bug (`todo.md`), carried over. |
| 5 | "Mirrors `ConvexProfile.Extremum`, operation for operation, so both backends land on the same float." | `zmap.cu:946-947` | Built with `--fmad=true` (`build.ps1:78`), so the backends differ by ulps. `ConvexDexelTests.cs:563-566` says the opposite and is right. |
| 6 | "the local arrays … stay under the 512 bytes a thread may use" | `ConvexProfile.cs:86-87` | Omits `dexel_subtract`'s `out[34]` (real peak 520 B, not 384 B), and the limit is 1024 B on every arch this builds for (`sm_120`, `compute_75`). |
| 7 | "the preview's own 2 µm chord" | `ConvexDexelTests.cs:437` | It is a **sagitta**: `StepsForRotation` (`ConvexStep.cs:95`) uses `d ≤ √(8·tol/r)` (`ConvexStep.cs:88`), and at θ-step 0.1091 rad the chord is 143 µm while the radial deviation is 2 µm. The number is right, the word is not. |

---

## Numbers I checked rather than repeated

Per the instruction not to pass a measured number along without a source:

- **Sampling-bound column of both tables — verified by arithmetic, all three rows.** `SamplingBound`
  (`ConvexDexelTests.cs:344-360`) computes `(2·(w+h)·cell/2 + cell²)·(highZ − lowZ)` from the packed box.
  Box 2×2×2 swept 7 mm: w = 9, h = 2, Δz = 2 → `(11·cell + cell²)·2` = 1.7728 / 0.8832 / 0.4408 at 250/500/1000
  cells — matches `long-programs.md:145` and `performance.md:257` exactly. Octahedron r = 2.5 swept (7,3,3): w = 12,
  h = 8, Δz = 8 → `(20·cell + cell²)·8` = 12.8512 / 6.4128 / 3.2032 — matches. Rotating box, 11 steps: per-step
  width maxes at 2·1.23643 + 3/11 = 2.7456, height at 2·1.21653 = 2.4331, Δz = 0.6 → 0.12525 ≈ 0.1253 — matches.
- **Box row, "451 × 101 instead of 450 × 100", verified by hand.** Centres `(i+0.5)·0.02` inside x ∈ [4.13, 13.13]
  give i ∈ [206, 656] = 451 columns; inside y ∈ [9.07, 11.07] give j ∈ [453, 553] = 101 rows. 451·101·2·0.0004 =
  **36.4408 mm³**, which is the documented preview value to the digit, and 36.4408 − 36.0 = **0.4408**, which is the
  documented bound to the digit. The "it sits on the bound to the last digit" remark is earned.
- **Octahedron "Exact = 108.3333", verified against the closed form.** `Vol(K ⊕ [0,w]) = (4/3)r³ + |w|·A_proj`, with
  `A_proj = ½ · A_face · Σ_faces |n·ŵ|`, `A_face = (√3/2)r²`. For r = 2.5, w = (7,3,3): 20.83333 + 8.18535 · 10.68982
  = **108.3321 mm³**, i.e. 0.001 % from the documented 108.3333 and well inside the preview's own −0.001 %. The
  documented exact value is a real `Process3` number, not a rounded closed form that happens to match.
- **"11 preview steps", verified.** `StepsForRotation(1.2, sqrt(1.2²+0.3²+0.3²) = 1.27279, 0.002)`
  = `ceil(1.2 / sqrt(8·0.002/1.27279))` = `ceil(10.70)` = **11**. Consistent with `performance.md:281`,
  `long-programs.md:207` and `todo.md:82`.
- **"512 exact intervals", verified as the lower bound the angular guard forces.** `Process3.Sample`
  (`Process3.cs:185-186`) accepts when `diameter·angle/2 ≤ maxDeviationNm`. With sweepNm = 4000 and
  diameter = 2.5456 mm, angle ≤ 3.143e-3 rad, so 1.2 rad needs ≥ 382 intervals; binary subdivision lands on **512**
  (256 < 382 ≤ 512). The same arithmetic gives the documented "~50 000 poses … 65 536 with the binary subdivision" at
  the default 30 nm. Whether `MaxDeviation` (`Process3.cs:202-218`) forces *deeper* subdivision than the angular
  bound is not something I can check without running it.
- **"18.9 ms at 793 600 steps, about 24 ns per step"** (`performance.md:274`) — 18.9 ms / 793 600 = **23.8 ns**,
  and 18.9 ms is the step-2 table's own bin figure at that size (`long-programs.md:100`). Consistent.
- **"3793 passes over 1920 grains, 13 841 hulls"** (`performance.md:275`) — both appear in the step-1 table at
  `long-programs.md:18` and `:50`. Consistent.

**Not verifiable by reading, flagged rather than repeated as fact:** the wall-clock "72 s" for
`ARotatingToolAgreesWithTheExactCut` (a code comment at `ConvexDexelTests.cs:438`, repeated in `todo.md:82` and
`performance.md:281`) is machine-dependent and I did not run it; the "4.3e-05 … 1.3e-04 mm" worst-end figures at
`long-programs.md:170-171` are test output I did not reproduce. Both are consistent with the tolerances and the
arithmetic above, and the 8.3e-04 mm hundredth-of-a-cell tolerance is correct for a 20 mm / 241-cell grid.

---

## One finding that is not on the list: the sampling bound under-counts for any turning program

`SamplingBound` is what `PreviewAgainstProcess3` asserts against (`ConvexDexelTests.cs:322`, three grids), so it is
load-bearing for every claim in both tables. It takes

```csharp
// ConvexDexelTests.cs:349-352
for (int s = 0; s < steps.Length; s++)
{
    width  = Math.Max(width,  packed[s * ConvexProfile.StepFloats + 2]);
    height = Math.Max(height, packed[s * ConvexProfile.StepFloats + 3]);
}
```

— the **maximum over steps** of each per-step swept box, which is what its own remark describes as "P … bounded by
the perimeter of the swept body's bounding box, which the packing already carries" (`:338`). It is not the
bounding box of the sweep; it is the widest *single step's* box. For a turning tool the union is much wider than any
one step: in the rotating-box case, 5.473 mm of union x-extent against a per-step maximum of 2.746 mm, so the bound
comes out **0.1253 instead of 0.1907 — 34 % low**. The test passes anyway (the observed difference is 0.0147 mm³)
because the box is swept 3 mm while it turns 1.2 rad, and it passes by luck of that ratio, not by construction.

For step 4 this matters concretely: a grinding preview is 60-1920 grains each turning along a trochoid, which is the
shape where the union is farthest from the per-step box, and it is the case the bound is supposed to police. The fix
is to take the union of the per-step boxes (min of the x0 fields, max of x0+width, same in y) rather than the max of
the widths — a four-line change to a helper, and it only ever makes the bound larger, so it cannot break a test that
currently passes.

---

## Summary of what I would change, in order of value

1. Fix `SamplingBound` (`ConvexDexelTests.cs:349-352`) to union the per-step boxes. It is the weakest link in every
   correctness claim step 3 makes.
2. Fix `docs/long-programs.md:83-84` and `:75`. Both are one-line edits and both are now wrong about the convex path
   too, not only the ball.
3. Add the `Assert.True(...Overflows > 0)` precondition to
   `ConvexDexelTests.BinnedLaunchGivesTheSameBitsAsTheUnbinnedOne` (`:626`), and drive `WithOneIntervalPerColumn…`
   (`:478`) through a `CudaBackend` as well so `dexel_subtract`'s guard at `zmap.cu:1052` is exercised, not just
   `DexelMap.Subtract`'s.
4. Add one tilted-rotation case (`Orientation3.AboutAxis` exists and is unused) to `MixedSteps` /
   `WalkConvex`, which would turn the documented `1/m_z` conditioning from an argument into a measurement and close
   the only genuinely untested hazard in `convex_span`.
5. Reword `zmap.cu:946-947` and `ConvexProfile.cs:86-87`, both of which assert something about the build that the
   build does not do.
6. Measure the per-thread frame (`cuobjdump -res-usage` on `nanocut_gpu.dll`) and re-run
   `bench LongPrograms dexel`, because the ball kernel's step-2 numbers were taken on a binary with half the local
   frame it has now.

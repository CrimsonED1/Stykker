# Step 4: long-lived device state — a sweep for the bug class behind `909d9f2`

Read-only audit written 2026-10-03 against the main checkout `C:\_AI\StykkerNanoCut\StykkerNanoCutRepo`, branch
`long-programs-step-1`, `HEAD = f5af7c2`, working tree clean. That commit contains the fix (`909d9f2`). Nothing was
built, run, installed or edited; every line reference below was read from the source in that checkout.

**The class, as I read it out of the code:** state that lives on a device object as long as the managed object does,
is written by one public entry point, is read by a *different* public entry point, and is not invalidated when the
entry point that last wrote it decides it has nothing to say — because the reserve/upload path took an early return.
The fixed instance is `Dexel::planes` + `Dexel::planesCapacity`.

**Headline result:** after `909d9f2` there is **no live instance of the class left on the device side**. The two other
long-lived device objects (`ZMap`'s `steps`/`queryIn`/`queryOut`, `PointSet`) do not have the shape at all. What the
sweep *did* find is one **live instance of the same shape on the managed side** — `DexelMap.Overflows` — plus three
latent instances that are unreachable from the four `nc_dexel_apply*` entry points but are one edit away from being
reachable, and a list of order-dependent pairs that no test exercises.

---

## 0. Correction to a standing finding

`Step3KernelHandoff.md` and `Step3KernelFindings.md` both record that `ConvexTool.Ball(r)` **throws on its own default
argument** (`planeCount = 32` against `MaxPlanes = 16`). That is no longer true in `f5af7c2`:

```csharp
// ConvexTool.cs:164
public static ConvexTool Ball(double radiusMm, int planeCount = MaxPlanes, double cx = 0, double cy = 0, double cz = 0)
```

The default is now `MaxPlanes` itself, so `ConvexTool.Ball(2.0)` returns a 16-plane tool and the
`ThrowIfGreaterThan(planeCount, MaxPlanes)` on the next line cannot fire. **Verified by reading.** No action; it is
recorded here so the next reader does not re-investigate a hazard that is closed.

---

## 1. The device structs — inventory

### `ZMap` — `zmap.cu:49-66`

| field | written by | read by | note |
| --- | --- | --- | --- |
| `nx`, `ny`, `cellX`, `cellY`, `bottom`, `top` | `nc_zmap_create` (`:332-337`) only | every kernel and every query | **immutable after create** |
| `heights` | `fill_kernel` (create), `zmap_apply_kernel` (`:400`) | sample / sample_set / probe / read / volume | always fully written before read |
| `steps`, `stepsCapacity` | `nc_zmap_apply_steps` only (`:375-387`) | `zmap_apply_kernel` | **one layout only** — no stride field exists |
| `queryIn`, `queryCapacity` | `upload_bytes` (`:459-472`), from `nc_zmap_sample` and `nc_zmap_probe` | `sample_d_kernel`, `probe_kernel` | grow-only reserve, **always memcpy'd after** |
| `queryOut`, `queryOutCapacity` | `reserve_query_out` (`:481-491`) | written by each kernel, read by `copy_back` | grow-only reserve, output-only |
| `volumePartials`, `volumeOut` | `nc_zmap_create` | `nc_zmap_volume` | fixed size `kVolumeBlocks` |

### `PointSet` — `zmap.cu:74-78`

`xy`, `count`. Written once by `nc_pointset_create` (`:645-681`), freed by `nc_pointset_destroy`. There is no setter and
no mutator, and the origin is passed **per call** by `nc_zmap_sample_set` (`:729`), not stored. One point set can
therefore be asked about any map at any origin — which is exactly what the struct comment claims. **Safe by
construction.**

### `Dexel` — `zmap.cu:839-861`

| field | written by | read by | risk |
| --- | --- | --- | --- |
| `nx`, `ny`, `k`, `cellX`, `cellY`, `top` | `nc_dexel_create` (`:1202-1207`) only | `dexel_apply_column`, `dexel_volume_partial_kernel`, `nc_dexel_read`, `nc_dexel_volume` | **immutable after create** |
| `intervals` (nx·ny·k·2 floats), `counts` | `dexel_init_kernel` (create), both apply kernels | both apply kernels, `dexel_volume_partial_kernel`, `nc_dexel_read` | — |
| `overflows` | `cudaMemset` at create (`:1213`), `atomicAdd` in both apply kernels | `nc_dexel_read`, `nc_dexel_volume` | **cumulative, never reset** — see §4.3 |
| `steps`, `stepsCapacity`, `stepsStride` | `reserve_steps` (`:1235-1254`) + the memcpy at `:1324` | the launches get `stride` as an **argument** (`:1345`, `:1350-1352`); `d->stepsStride` is read *only* inside `reserve_steps` | **safe** — see §2.1 |
| `planes`, `planesCapacity` | `reserve_planes` (`:1257-1267`) + the memcpy at `:1325-1327` | `devicePlanes` at `:1339`, gated on `planeCount > 0` | the fixed bug; field now **dead** — see §2.2 |
| `tileStart`, `tileCapacity` | `reserve_ints` (`:1309`) + memcpy at `:1329` | `dexel_apply_binned_kernel` (`:1143-1144`) | always `tileCount+1` ints uploaded when binned |
| `tileSteps`, `tileStepsCapacity` | `reserve_ints` (`:1310`) + memcpy at `:1331-1332` **only when `tileStepCount > 0`** | `dexel_apply_binned_kernel` (`:1143-1144`) | see §3.4 and §5.2 |
| `volumePartials`, `volumeOut` | `nc_dexel_create` | `nc_dexel_volume` | fixed size |

**No managed cache mirrors any of these.** `DexelMap.MaxIntervals`, `CellsX/Y`, `CellSizeXMm/Ym` and `TopRelative`
are all get-only after the constructor (`DexelMap.cs:41-70`), and `DeviceDexel.Create` builds the device object from
exactly those (`CudaBackend.cs:402-404`). So `d->k` can never disagree with the length of `map.Intervals`, which is what
`nc_dexel_read` writes into (`:1463`). **Verified by reading.**

---

## 2. Every `reserve_*` / `init_*` / `*_release`

There are exactly five. Three on the dexel side, two on the Z-map side, plus one inline reserve.

### 2.1 `reserve_steps` — `zmap.cu:1235-1254` — **correct, and the model the others should have followed**

```c
if (d->steps != nullptr && d->stepsStride != stride) { free; steps = nullptr; stepsCapacity = 0; }   // :1237-1242
if (d->stepsCapacity >= count) return cudaSuccess;                                                   // :1243
```

The stride-mismatch branch zeroes `stepsCapacity`, so the capacity early return at `:1243` is **only reachable when
the stride already matches**. That is the whole difference between this function and `reserve_planes`: it invalidates
the cache *before* deciding not to reuse it. A second structural difference: `stepsStride` is not read by any launch —
`dexel_apply_column` receives `stride` as a kernel argument (`:1105`, `p = steps + index * stride` at `:1104`) — so a
stale `stepsStride` could not produce a wrong read even if the bookkeeping were wrong. `reserve_steps` also handles the
shrink case: capacity never shrinks, but the memcpy at `:1324` always copies exactly `stepCount * stride` floats and
the launch is bounded by `stepCount`, so a smaller batch on a bigger buffer is fine.

**Safe. Do not touch.**

### 2.2 `reserve_planes` — `zmap.cu:1257-1267` — the bug site, now inert but not cleaned up

```c
if (planeCount <= 0) return cudaSuccess;          // :1259  — the line that caused the bug
if (d->planesCapacity >= planeCount) return cudaSuccess;   // :1260
```

Both early returns leave `d->planes` and `d->planesCapacity` pointing at the last convex program's half-spaces. After
`909d9f2` nothing reads them when `planeCount == 0` (`:1339`), so the stale value cannot reach the kernel. I grepped
the whole file for `d->planes`: the only occurrences are `:1261`, `:1263-1264` (inside `reserve_planes` itself),
`:1326` (the memcpy destination) and `:1339` (the launch, now gated). **Verified.**

Two residuals, both low:

- **R1 — memory retention (verified).** After a convex program, `d->planes` stays allocated for the life of the
  `DexelMap` even if every later program is a ball program. Bounded by `MaxPlanes · 4 · 4 = 256` bytes. Not worth a
  fix; worth knowing.
- **R2 — a twin guard on the other side of the same expression (verified).** The *upload* is still guarded on the host
  pointer and the *launch* on the count:

  ```c
  if (e == cudaSuccess && planes != nullptr)                       // :1325  upload
      e = cudaMemcpy(d->planes, planes, planeCount * kPlaneFloats * sizeof(float), ...);
  ...
  const float* devicePlanes = planeCount > 0 ? d->planes : nullptr;   // :1339  launch
  ```

  Today the two are equivalent, because all four entry points enforce `planes != nullptr ⟺ planeCount > 0`
  (`:1376-1379`, `:1399-1402`, `:1422-1425`, `:1440-1445`). But the fix established the invariant in one place and
  left the other copy of it expressed differently. **A fifth entry point, or an internal caller of
  `dexel_apply_common`, that passes `planes = nullptr, planeCount > 0` would allocate the buffer, skip the upload, and
  run the convex kernel over uninitialised device memory.** Marked *inferred* because nothing reaches it today. The
  cheap closure is one word: make `:1325` read `planeCount > 0` too, so the two guards are the same expression.

### 2.3 `reserve_ints` — `zmap.cu:1270-1281` — safe for `tileStart`, latent for `tileSteps`

```c
if (count <= 0) return cudaSuccess;         // :1271
if (*capacity >= count) return cudaSuccess; // :1272
```

`tileStart` is called with `tileCount + 1 ≥ 2`, and `tileCount` is validated against `tilesX * tilesY` before the
reserve runs (`:1301-1304`), so that buffer is always exactly the right size and always fully overwritten
(`:1329`). **`tileSteps` is called with `tileStepCount`, which can be 0** — see §3.4 and §5.2.

### 2.4 `upload_bytes` / `upload_query` / `reserve_query_out` — `zmap.cu:459-491` — safe

`upload_bytes` reserves in units of *floats derived from bytes* (`(bytes + sizeof(float) - 1) / sizeof(float)`,
`:461`) and then **always** `cudaMemcpy`s (`:471`), so the buffer holds the current caller's data even when the reserve
early-returns. The two users happen to need the same 16 bytes per point (`nc_zmap_sample`: 2 doubles; `nc_zmap_probe`:
4 floats), but even if they did not, the byte-derived capacity is per-call, so a growing user still reallocates.
`reserve_query_out` only sizes an output buffer that its own kernel fills every time. **`z->queryIn` and `z->queryOut`
are not instances of the class.**

### 2.5 The inline reserve in `nc_zmap_apply_steps` — `zmap.cu:375-387` — safe

`if (z->stepsCapacity < stepCount) { free; realloc; }` then `cudaMemcpy(z->steps, steps, bytes, ...)` at `:393`. Grow
only, always refreshed, and **there is only one step layout on the Z-map side** — `kStepFloats = 12`, one constant,
no `stepsStride` field, no second pack. The whole reason `reserve_steps` needs the stride dance is that the dexel has
two layouts; the Z-map does not. **Safe, and it will stay safe as long as a second Z-map layout is not added without
adding the stride field with it.**

### 2.6 `free_dexel` / `nc_dexel_destroy` / `nc_zmap_destroy` — `zmap.cu:1172-1185`, `:1519-1522`, `:798-809`

Every pointer is null-checked, every capacity field is irrelevant after `delete d`. **Safe.** Note the lifecycle: the
managed side never calls destroy. `DeviceDexel` and `DeviceMap` are finalizable (`CudaBackend.cs:410-414`, `:284-288`)
and held in a `ConditionalWeakTable` (`:21-22`), so device memory is released only when the GC collects the
`DexelMap`. In `bench/Stykker.NanoCut.LongPrograms/Program.cs` the repeat loop allocates a fresh `DexelMap` per run
(`:580`, `:589`, `:599`, each inside a `Measure*` helper), so a long bench run accumulates device dexels until the
collector runs. Not a correctness issue, and not the class.

---

## 3. Public entry-point pairs that can legally run on one object in sequence

### 3.1 ball → ball, convex → convex — **safe**

Same stride, so `reserve_steps` returns early on the second call and the memcpy at `:1324` refreshes the buffer;
`planeCount` may shrink between convex calls, `reserve_planes` returns early at `:1260`, the memcpy at `:1325-1328`
copies exactly `planeCount · 4` floats, and the launch passes `planeCount`. The kernel reads `planes[0 .. 4·planeCount)`.
`reserve_planes` never shrinks, but the count is what bounds the read, so a stale tail is never touched.

*Untested*: no test changes the tool on a map mid-program (see §6.5).

### 3.2 ball ↔ convex in either order — **fixed, and the stride half was already right**

`stride` is a launch argument, not a device field (`:1345-1346`, `:1350-1352`); `reserve_steps` reallocates on the
change (`:1237-1242`); `devicePlanes` now follows `planeCount` (`:1339`). The one-line fix closed the pointer; the stride
had never been at risk.

### 3.3 binned ↔ unbinned in any combination — **safe**

`binned` is decided on the **host** pointer `tileStart` (`:1298`), not on device state. The unbinned launch passes
`stepIndex = nullptr` into `dexel_apply_column` (`:1124-1125`), so `dexel_apply_column` uses `s` directly
(`:1103`) and never dereferences `d->tileSteps`. A stale or null `d->tileSteps` is unreachable from the unbinned
launch. **Verified.**

### 3.4 read / volume in any order — **safe, with one semantic note**

`nc_dexel_read` (`:1453-1475`) and `nc_dexel_volume` (`:1477-1517`) read only immutable geometry plus `intervals`,
`counts` and `overflows`, all of which the launches write. `overflows` is **cumulative over the life of the map** and
there is no reset entry point anywhere — `nc_dexel_create` is the only place it is zeroed (`:1213`). That matches the
managed contract (both backends accumulate; see §4.3), so it is a design choice rather than a defect. Worth knowing
because "reset the map and run a second program" is not expressible: **a `DexelMap` has no clear/reset, on either
side**, which is why "reuse across programs" necessarily means "continue the same cut".

### 3.5 The specific case the brief asked about — **no cached stride anywhere**

- Device: `stride` is a parameter of `dexel_apply_common` (`:1292`), a kernel argument (`:1345-1346`, `:1350-1352`) and a
  parameter of `dexel_apply_column` (`:1105`). `d->stepsStride` is **never** read outside `reserve_steps` — I grepped
  for it; the only two hits are `:1237` and `:1251`.
- Managed: `StepBins.Build` takes `stride` and `convex` as **arguments** on every call
  (`StepBins.cs:42`), and `CudaBackend` passes `ToolProfile.StepFloats` + `convex: false`
  (`CudaBackend.cs:307`) or `ConvexProfile.StepFloats` + `convex: true` (`:343`) at the call site. There is no field,
  no static and no cached `Bins`. `StepBins.Bins` is a `readonly record struct` of freshly allocated arrays
  (`StepBins.cs:29-31`, `Build` at `:38-81`).
- The two layouts really do differ (`ToolProfile.StepFloats = 12`, `ConvexProfile.StepFloats = 32`; `Tiles` reads
  offsets 0-3 for the convex box and 3/4/5 for the sphere's radius and move, `StepBins.cs:112-124`), and nothing
  carries the first program's choice into the second.

**Clean. No managed or device cache of the stride survives a change of tool kind.**

### 3.6 The Z-map side — **does not have the shape**

One step layout, so no stride field. Every long-lived buffer is either written by exactly one entry point
(`steps`) or is refreshed by `cudaMemcpy` after a reserve that is allowed to early-return (`queryIn`). `PointSet` is
immutable and keeps the origin out. **No instance of the class.** See §5.3 for the one asymmetry worth naming.

---

## 4. The managed side

### 4.1 Managed caches — **none survive a change of tool kind** *(verified)*

| buffer | where | verdict |
| --- | --- | --- |
| `ToolProfile.Pack` result | `ToolProfile.cs:24` — `new float[steps.Length * StepFloats]` per call | fresh |
| `ConvexProfile.Pack` result | `ConvexProfile.cs:118` — fresh per call | fresh |
| `ConvexProfile.PackPlanes` result | `ConvexProfile.cs:90` — fresh per call | fresh |
| `StepBins.Build` result | `StepBins.cs:57-79` — three fresh arrays per call | fresh |
| `StepBins` stride / `convex` | passed per call (`StepBins.cs:42`) | not cached |
| `DeviceMap.PackedPoses` | `CudaBackend.cs:269`, reused at `:187-188` | **safe** — `PackPoses` (`:212-219`) writes indices `0 .. 4·len-1` unconditionally and the native side copies `4·count` floats into a freshly-reserved `queryIn`. A longer pose list only ever reuses a longer buffer for a shorter write. |

**There is no managed cache of `k`, of a plane buffer, of a `StepFloats`, or of a packed step array.** Everything is
per-call. This is the answer to §4 of the brief and it is a clean one.

### 4.2 `Dispose` / reset, and whether a `DexelMap` is reused across programs *(verified)*

**No `Dispose`, no `Clear`, no reset** on `DexelMap` or `ZMap` — I grepped the whole `Stykker.NanoCut.Gpu` project.
Lifetime is entirely `ConditionalWeakTable` (`CudaBackend.cs:21-22`, lookups at `:228` and `:386`): **one `DeviceDexel`
per `DexelMap`, for the life of that map.** So the device state genuinely is long-lived and genuinely is reused across
calls, which is exactly the precondition for the bug class. The fix's premise holds.

**Where it is actually reachable today — and this bounds the severity honestly:**

- `bench/Stykker.NanoCut.LongPrograms/Program.cs:483-484, 511-512, 580-581, 589-590, 599-605, 632-633` — every call
  site constructs a **fresh** `DexelMap` inside its `Measure*` helper, so no bench case ever puts two tools on one
  device dexel. `ConvexCases.Run` even interleaves `MeasureConvex` and `MeasureBall` on the **same** `CudaBackend`
  (`:643-650`) — but on different maps, so the weak table hands out different device dexels.
- `tests/` — see §6.
- **`samples/` — nothing.** `Stykker.NanoCut.Server`, `Stykker.NanoCut.Demo` and `Stykker.NanoCut.Snapshot` do not
  reference `Stykker.NanoCut.Gpu` at all: a grep for `NanoCut\.Gpu|CudaBackend|ZMap|Dexel` over
  `samples/**/*.cs` returns **no matches**. Verified.
- The library itself is public API, and `docs/long-programs.md` / `docs/handoff.md` hand `DexelMap.ApplyConvexSteps`
  to step 4 as the grinding-grain path. So the bug was a live public-API defect with no in-repo trigger, not a
  production incident.

### 4.3 **`DexelMap.Overflows` — a live instance of the same class, on the managed side**

*(verified by reading; the consequence is arithmetic)*

`Overflows` is a plain `{ get; internal set; }` property (`DexelMap.cs:108`) with **no `IsCurrent` guard** — unlike
`Intervals`, `Counts`, `RemovedVolumeMm3`, `TopHeights` and `ToMesh`, which all go through `RequireCurrent()`
(`:215-221`, `:176`, `:204`, `:213`).

It is written from exactly three places:

| writer | when |
| --- | --- |
| `CpuBackend.ApplyDexels` / `ApplyConvexDexels` | **always**, `map.Overflows += overflows` (`CpuBackend.cs:196`, `:243`) — `readBack` is ignored, because the CPU map *is* the host array |
| `CudaBackend.ReadDexelDevice` | only when `readBack == Always` (`CudaBackend.cs:324`, `:357` → `:395-396`) |
| `CudaBackend.RemovedVolumeMm3` | only when the caller asks (`CudaBackend.cs:378`) |

and it is **not written at all** when a CUDA `Apply*` call takes `readBack: Never` (`DexelMap.cs:139`, `:158` set
`IsCurrent` but nothing refreshes `Overflows`).

**Symptom.** On the CUDA backend, `map.Overflows` silently reports the value from the *last read-back* while the device
counter has moved on. Because `ApplyConvexSteps` now defaults to `Never` (`DexelMap.cs:148`) and `ApplySteps` still
defaults to `Always` (`DexelMap.cs:130`), **the same pair of programs reports a different `Overflows` depending on the
order they are applied in**:

```csharp
map.ApplyConvexSteps(tool, convex);   // Never  -> Overflows untouched, still 0 on a fresh map
map.ApplySteps(ball);                 // Always -> Overflows = cumulative device counter  (correct)
var a = map.Overflows;

map.ApplySteps(ball);                 // Always -> Overflows = cumulative after the ball
map.ApplyConvexSteps(tool, convex);   // Never  -> Overflows untouched  (the convex part is invisible)
var b = map.Overflows;                // a != b whenever the convex program overflows
```

And the CPU backend reports the same total for both orderings, because it accumulates unconditionally. **So the two
backends disagree on `Overflows` for the same program sequence on the same map.** This is the exact failure signature of
the bug that was just fixed — *"overflow counter still zero"* — reproduced on the managed side, one layer up.

- **Existing coverage:** none. `AConvexToolThenASphereOnTheSameDevice` (`ConvexDexelTests.cs:670-702`) never reads
  `Overflows`; `ASphereThenAConvexToolOnTheSameDevice` (`:639-658`) never reads it either. Both pass `Always`
  explicitly, so the default-`Never` path is untested in both directions. `DexelMapTests.CudaChunksGiveTheSameBitsAsOneBatch`
  (`:239-267`) *does* assert `one.Overflows == chunks.Overflows`, but both arms end on a read-back
  (`ApplySteps` default `Always`, and an explicit `chunks.ReadBack()`), so it compares two reads of the same cumulative
  counter.
- **Inferred (needs a run):** that the two orderings actually differ requires a convex program that overflows at the
  map's `k`. The arithmetic above does not.
- **Related, low, and a design smell rather than a defect:** `CudaBackend.RemovedVolumeMm3` (`:373-380`) assigns
  `map.Overflows` as a **side effect of a query**. So `map.Overflows` changes when you ask the map how much material
  is gone, and does not change when you cut more. Both backends' values are cumulative, so the two are not in
  conflict — but the mutation-on-read is surprising and is what makes the staleness in the paragraph above invisible.

### 4.4 The `readBack` default asymmetry, and what `Never` leaves behind *(verified)*

`readBack` **never crosses the native boundary**. `CudaNative.cs` has no read-back parameter on any apply entry point
(`:106-135`), and `dexel_apply_common` has none either — `CudaBackend.cs:324` and `:357` show it is purely a managed
decision to call `ReadDexelDevice` afterwards. **So the device side is identical under `Always` and `Never`, and leaves
nothing behind either way.** The fix's premise that a read-back mode could leave device state behind does not hold, and
that is worth recording as a *negative* result.

What `Never` leaves behind is entirely managed: `Intervals`, `Counts` and `Overflows` keep their previous values, and
`IsCurrent` goes false (`:138`, `:159`). Three of the four are guarded; `Overflows` is not (§4.3). Separately: the two
methods' defaults differ, so a caller who calls both in one routine gets a read-back on one and not the other without
having said so.

---

## 5. Latent instances — reachable only by a caller that does not exist yet

*Marked inferred: I found no path from the managed API, the four `nc_dexel_apply*` entry points, or any in-repo
consumer to any of these. They are one edit away, which is why they belong in the report.*

### 5.1 `planes == nullptr` with `planeCount > 0` → the convex kernel over uninitialised device memory

**Sequence:** anything that reaches `dexel_apply_common` with a null host `planes` and a positive `planeCount`.
`reserve_planes` (`:1308`) allocates, the upload at `:1325-1326` is skipped because it tests the *pointer*, and the
launch at `:1339` passes `d->planes` because it tests the *count*.

**Symptom:** `convex_span` reads `planeCount` half-spaces of whatever was in freshly-`cudaMalloc`'ed memory. Silent
wrong geometry, or a whole map removed, depending on the bits.

**Unreachable today:** all four entry points reject it (`:1376-1379`, `:1399-1402`, `:1422-1425`, `:1440-1445`).

**Covered by a test:** no. There is no native test harness and no way to write one from C#, which is the same gap
`Step3KernelFindings.md` §b already recorded for the `kConvexPlanes` guard.

### 5.2 An inconsistent CSR → out-of-bounds read of `d->tileSteps`

**Sequence:** `tileStepCount == 0` (or short) with `tileStart[tileCount] > tileStepCount`.
`reserve_ints(&d->tileSteps, …, 0)` returns at `:1271`, so `d->tileSteps` keeps whatever it had — `nullptr` if this is
the first binned call, or the *previous program's step indices* if there was one — and the memcpy at `:1331-1332` is
skipped because it also tests `tileStepCount > 0`. The kernel then reads `tileSteps[s]` for `s ∈ [tileStart[t], tileStart[t+1])`
(`:1143-1144`). `dexel_apply_common` validates only `tileCount == tilesX * tilesY` (`:1301-1304`); **it never checks
`tileStart[tileCount] == tileStepCount`.**

**Symptom:** an out-of-bounds device read, producing a garbage step index (and therefore a garbage cut) or a crash,
depending on what is in memory. No error, no exception, `overflows` unchanged.

**Unreachable today:** `StepBins.Build` ends with `tileStart[tileCount] == tileSteps.Length`
(`StepBins.cs:71-79`), so the managed side is consistent by construction.

**Covered by a test:** no. The safe case (`tileStepCount == 0`, all-`tileStart`-zero, previous program had references)
would produce the *same* volume as a fresh map and pass silently, which is why the absence of a test is not itself
evidence of safety — only the reading is.

*This one is pre-existing from step 2, not convex-specific; `Step3KernelFindings.md` §g flagged it and it is still
open. The convex path made it worse in reach, because `d->tileSteps` can now hold a buffer written under a **different
stride's** step array.*

### 5.3 The Z-map's `queryIn` is shared between two callers with different element types *(safe, but the same shape)*

`nc_zmap_sample` stages 2 doubles per point and `nc_zmap_probe` 4 floats per point into the *same* `z->queryIn`
(`:609`, `:766`). This is the closest the Z-map comes to the bug shape — one buffer, two entry points, two meanings.
It is **safe** because `upload_bytes` always `cudaMemcpy`s after the reserve, each entry point launches its own kernel
immediately, and `CudaBackend` serialises both under `device.Gate` (`CudaBackend.cs:124`, `:183`). *Verified.* Worth
naming only so a future reader knows the safety comes from "always refreshed", not from "different buffers".

### 5.4 No gate on the dexel path *(inferred, low)*

The Z-map query path locks `device.Gate` (`CudaBackend.cs:124`, `:183`, `:196`). The dexel path
(`ApplyDexels`, `ApplyConvexDexels`, `ReadDexels`, `RemovedVolumeMm3`) takes **no lock**, and they all write or read
`d->steps`, `d->planes`, `d->tileStart`, `d->tileSteps`. All CUDA work is on the null stream with an explicit
`cudaDeviceSynchronize`, so the *device* ordering is fine; the *host* interleaving of two `cudaMemcpy`s into
`d->steps` from two threads is not. Neither `DexelMap` nor `ZMap` documents a thread-safety contract, so this is not a
defect — it is the reason the Z-map side has a gate and the dexel side does not, which is unexplained.

---

## 6. Order-dependent pairs that no test covers

The fixed bug was invisible because the existing test ran the safe order (sphere → convex) and never the dangerous one.
`ConvexDexelTests.AConvexToolThenASphereOnTheSameDevice` (`:670-702`, added by `909d9f2`) now covers the binned
convex → ball direction. Here is what is still uncovered. *(Named only; I did not write these tests.)*

| # | sequence | why it matters | covered? |
| --- | --- | --- | --- |
| 6.1 | **`new CudaBackend { BinSteps = false }`**, convex then ball, one map | The fix's regression test uses the default `BinSteps = true` for both calls, so it exercises `devicePlanes` on the **binned** launch (`zmap.cu:1350-1352`). The **unbinned** launch at `:1345-1346` carries the identical `devicePlanes` argument and has no cross-tool test at all. | no |
| 6.2 | convex then ball, last call `Never` | The order that also exposes §4.3: `Overflows` would read 0 while the map is intact | no |
| 6.3 | ball then convex, last call **by default** (`Never`) | `ASphereThenAConvexToolOnTheSameDevice` (`:639-658`) passes `Always` explicitly, so the new default is untested in both directions | no |
| 6.4 | binned ball then binned convex, and the reverse, one map | The CSR buffers `d->tileStart` / `d->tileSteps` are shared across tool kinds exactly as `d->planes` is. I read them as safe (§3.1, §3.3); nothing asserts it. | no |
| 6.5 | **convex with tool A (16 planes), then convex with tool B (4 planes)**, one map | The nearest remaining analogue of the fixed bug: `reserve_planes` early-returns at `:1260` and the upload copies only `planeCount · 4` floats, so the kernel must be reading tool B's planes, not A's tail. I believe it is correct; nothing pins it. | no |
| 6.6 | chunked convex program on one device dexel, vs one batch | `DexelMapTests.CudaChunksGiveTheSameBitsAsOneBatch` (`:239-267`) does this for the ball. There is no convex twin, so `reserve_steps`' shrink-with-same-stride path (`:1243` early return + always-memcpy) is untested for the 32-float layout. | no |
| 6.7 | a binned program where **no step reaches any tile** (`bins.References == 0`), on a device dexel that has already run a binned program | The only way to reach §5.2. Safe as far as I can tell, and it would pass silently either way — which is exactly why it needs a positive assertion, not just an absence of failures. | no |
| 6.8 | **two different backends' `Overflows` for the same two-program sequence** | §4.3. `ConvexDexelTests.ThreadCountDoesNotMatter` compares `Overflows` but only across two CPU backends. | no |

The cheapest high-value additions, in order: **6.1** (a two-line variant of the test that was just added, flipping one
initialiser), **6.5** (a tool swap on one map, compared against a fresh map), and **6.3** (drop the explicit `Always`
from the existing test so the shipped default is what runs).

---

## 7. Summary — what is safe, and what is not

**Safe, and worth stating so the risk is bounded:**

- `reserve_steps` / `stepsStride` (`:1235-1254`). Correct by construction: it invalidates before deciding not to
  reuse, and the launch takes the stride as an argument anyway.
- Every immutable `Dexel` and `ZMap` field (`nx`, `ny`, `k`, `cellX`, `cellY`, `top`, `bottom`), because the managed
  counterparts are get-only.
- `d->planes` / `d->planesCapacity` after `909d9f2` — dead, unreachable from the launch.
- `d->overflows` — cumulative by design on both backends, and `nc_dexel_create` is the only reset.
- The whole Z-map side: one layout, no stride field, every long-lived buffer refreshed on every call.
- `PointSet`: immutable, and the origin is per-call rather than stored.
- binned ↔ unbinned in any combination: the unbinned launch passes `stepIndex = nullptr` and never reaches
  `d->tileSteps`.
- Managed caches: **none** survive a change of tool kind. Every pack, every plane array, every `Bins` is allocated per
  call; the one reused buffer (`DeviceMap.PackedPoses`) is unconditionally rewritten.
- `readBack` leaves nothing behind on the device — the mode never crosses the native boundary.

**Not safe:**

- **`DexelMap.Overflows` (`DexelMap.cs:106`)** — the one live instance of the bug class that survives. No `IsCurrent`
  guard, order-dependent under the two different `readBack` defaults, and it disagrees with the CPU backend. §4.3.
- **The twin guard at `zmap.cu:1325`** — the upload tests the host pointer while the launch tests the count. Latent
  (§5.1); one word closes it.

**Latent, unreachable today:** the CSR consistency gap (§5.2, pre-existing, still open) and the missing dexel gate
(§5.4).

**Documentation note:** `docs/long-programs.md:268-273` describes the fix accurately and every line reference in it
(`zmap.cu:1106`, `:1259`, `:951`, `CudaBackend.cs:386`) is still correct at `f5af7c2`. The `ConvexTool.Ball` default
that two earlier documents flag as a throw is no longer one (§0). Nothing in `docs/` needed correcting for this sweep.
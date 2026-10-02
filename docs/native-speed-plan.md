# Plan: would Rust or C make NanoCut faster?

Status: plan only. Nothing here is decided. Every step ends with a measurement, and the decision gate in step 5 decides
whether native code is worth the extra build and packaging complexity.

## Where the time goes today (known)

- **Algorithms dominate.** The largest speed-ups so far came from the algorithms, not from code generation:
  - floating-point filters with exact fallback: 62 s → 11 s;
  - the split tree that restores faces: 14 645 → 1 109 faces;
  - coplanar face merging.

  Remaining known cost: fragments at T-junctions that cannot be merged, and many swept hulls when a tool rotates.
- **Browser:** the .NET interpreter is about 5× slower than the AOT build. The AOT build still uses the .NET runtime
  (GC, checked arithmetic, bounds checks), and the download includes the runtime.
- **Exact arithmetic:** `Int384` (checked, struct based) is only used when a filter is uncertain. How much time it
  takes after the filters is not measured yet.

## Where Rust or C could help

| | Native (.NET JIT) | Browser (.NET AOT wasm) | Browser (.NET interpreter) |
| --- | --- | --- | --- |
| Expected gain, tight arithmetic | 1.0–1.5× (RyuJIT is close to C for scalar int code) | 1.5–3× | 10× and more |
| Expected gain, whole Boolean (allocations, lists, BVH) | 1.2–2× | 2–4× | 10× and more |

These are estimates; they are what the steps below measure.

### Ways to integrate

1. **C through Emscripten, linked into the Blazor runtime** (`<NativeFileReference>`, called with P/Invoke).
   - The Emscripten toolchain already comes with the `wasm-tools` workload, so no extra toolchain.
   - No JavaScript between .NET and the native code, so calls are cheap.
   - Natively it is the same code as a shared library, called with P/Invoke.
2. **Rust as a static library** (`wasm32-unknown-emscripten`), linked the same way.
   - Safer code than C, but the Rust target has to match the exact Emscripten version .NET uses, and that pairing is
     fragile.
3. **Rust as a separate wasm module**, called through JavaScript interop.
   - Each call costs a copy of the data in and out, so it only works with a coarse API: whole solids in, whole solids out.
   - Strategic upside: the kernel would also work from plain JavaScript (three.js / Babylon.js users), without the .NET
     runtime download.

**Costs of native code**
- One native build per platform (win/linux/mac × x64/arm64, plus wasm).
- The "no runtime dependencies" rule then only holds per platform.
- Two languages in the exact kernel, so bit-identical results must be checked continuously.

## Steps

### 0. Benchmark suite (baseline)

Add `bench/Stykker.NanoCut.Bench` (BenchmarkDotNet) and a browser timing page (the self-test page gets a "Bench" tab).
The fixed workloads:

| # | Workload | Why |
| --- | --- | --- |
| B1 | 10⁷ `orient3d` / plane-side tests on random and near-degenerate input | pure arithmetic, filter hit rate |
| B2 | `Int384` multiply/compare chain | exact fallback cost |
| B3 | Face split of 10⁵ faces | allocation-heavy kernel step |
| B4 | Box − sphere (50 nm chord), sphere ∪ sphere | full Boolean |
| B5 | Example 1 3D (ball through block) | reference case |
| B6 | Saw blade on the 3D route (6 mm, 100 µm) | today's slowest case (11 s) |
| B7 | Grinding default (60 grains) | many small Booleans |
| B8 | Axial envelope of the shaft, 36 angles | sections |

Measure each workload in three runtimes: native JIT, browser AOT and browser interpreter. Record time, allocations and
filter hit rate in `docs/benchmarks.md`.

### 1. Profile

- **Native:** `dotnet-trace` / `dotnet-counters`.
- **Browser AOT:** the Chrome performance profiler with wasm symbols (`WasmNativeDebugSymbols`).

Result: which share goes to arithmetic, allocation/GC, BVH, classification and hull. If arithmetic and allocation
together are below about 40 %, native code cannot pay off much, and the work continues with the algorithms.

### 2. Cheap .NET improvements (the reference a native port must beat)

- `Int384`: unchecked limb arithmetic in the hot paths, and an `Int128` fast path when operands are small.
- Fewer allocations: pooled lists, `stackalloc`, `struct` points, no LINQ in kernels.
- SIMD (`Vector256`) for the double filters, and parallel loops natively.
- Run B1–B8 again. This result is the bar the native prototype has to clear.

### 3. Native prototype of the hottest leaf

Port the same piece in both C and Rust: the exact predicates (orient3d, plane side in 384-bit), plus the face split.

- **C:** Emscripten object file linked with `NativeFileReference`, P/Invoke in the browser; a shared library natively.
- **Rust:** `wasm32-unknown-emscripten` static library on the same path (variant 2), plus a standalone wasm module
  (variant 3) for comparison.
- **Measurements:** B1–B3 including call overhead, and run time per call against batched calls (arrays of faces).

### 4. Correctness

- Switch the backend (`NANOCUT_NATIVE=1`) and run the full test suite and the oracle tests against it.
- Results must be bit-identical: same grid points, same planes, same face count.

### 5. Decision gate

| Result (browser AOT, B4–B7, end to end) | Decision |
| --- | --- |
| < 1.5× | Stop. Stay pure C#; keep the improvements from step 2. |
| 1.5–3× | Optional native backend for the browser only (C through Emscripten). The C# kernel stays the reference. |
| > 3× and arithmetic/allocation dominate | Estimate porting the whole 3D Boolean to Rust (variant 3, coarse API, also for JavaScript users). Plan it as its own project with the C# kernel as the test oracle. |

## Effort (rough)

| Step | Effort |
| --- | --- |
| 0–1 | 1–2 days |
| 2 | 2–4 days |
| 3–4 | 3–5 days |
| Porting the whole kernel | several weeks |

## First measurements (CLI race, `bench/`)

Same input on the nm grid for all engines. Medium scene: 96 hull subtractions.

| Engine | Language | Arithmetic | Time |
| --- | --- | --- | ---: |
| NanoCut | C# | exact | 2.39 s |
| CGAL | C++ | exact | 3.29 s |
| ManifoldSharp | C# | double | 1.03 s |
| Manifold | C++ | double | 0.53 s |

The same algorithm runs about 2× faster in C++. An exact C++ kernel (CGAL) is not faster than NanoCut. See
`bench/README.md`.

**Long task** (pocket-large, 876 steps, sized so that the fastest engine needs about 10 s):

| Engine | Language | Arithmetic | Time |
| --- | --- | --- | ---: |
| NanoCut | C# | exact | 30.9 s |
| ManifoldSharp | C# | double | 31.1 s |
| CGAL | C++ | exact | 347 s |
| Manifold | C++ | double | 9.8 s |

- NanoCut is as fast as the same-language double-precision engine, and 11× faster than the exact C++ kernel.
- The remaining 3.2× to C++ Manifold equals the C#/C++ factor of the identical algorithm. That is the most a native port
  could gain on long tasks.

**After the first C# optimisation round:** NanoCut needs 18.4 s on the long task, down from 30.9 s. It is now faster than
the C# port of Manifold (30.8 s) and 2.0× behind C++ Manifold (9.4 s). The gap a native port could close has shrunk from
3.2× to about 2×.

**After the second round:** NanoCut needs 10.4 s, C++ Manifold 10.1 s (2.9 cores). The engines are on par in wall time,
and NanoCut uses fewer CPU-seconds per run. A native port is not needed for speed on this workload.


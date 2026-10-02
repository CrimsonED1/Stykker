# Stykker-NanoCut

C# library for material removal and penetration in 2D and 3D with an accuracy below 0.1 µm.
Pure managed C# on .NET 10, also runs on `browser-wasm`, no runtime dependencies.
Background, research and phase plan: [docs/plan.md](docs/plan.md) (German).

## Status

| Phase | Scope | Status |
| --- | --- | --- |
| 0 – Repo & CI | Project structure, GitHub Actions for `net10.0` and `browser-wasm`, MIT | ✅ |
| 1 – Core | 1 nm grid, `Int128` predicates, `Int384`, planes and homogeneous intersection points, tolerance model | ✅ predicates match `BigInteger` on 10⁶ cases, bit budget verified ([docs/bit-budget.md](docs/bit-budget.md)) |
| 2 – 2D kernel | Booleans (4 fill rules), arcs with chord error, offset, Minkowski sweep, area, depth | ✅ example 1 (2D) within budget, oracle vs. Clipper2 on 10,000 random polygons |
| 3 – 3D kernel | Plane-based B-rep, exact Booleans, BVH, primitives, STL/OBJ | open |
| 4 – 3D sweep & analysis | Linear/arc sweeps, volume, depth | open (2D sweep done) |
| 5 – Web | Buffer interop, web worker, npm adapters for three.js/Babylon.js, Blazor demo | open |
| 6 – Hardening & release | Fuzzing, benchmarks, packages | open |

## Quick start (2D)

```csharp
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Cutting;

var tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50);

var rect = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
var circ = Region2.Circle(Vec2.Mm(10, 12), radiusMm: 3, tol);
double cutArea = (rect & circ).AreaMm2;     // 3.09732 (exact 3.097482, deviation 1.7e-4 ≤ 5.0e-4)
Region2 rest2D = rect - circ;

// Material removal along a path
var result = Cutter2.Cut(rect, Tool2.Circle(3), ToolPath2.Linear(Vec2.Mm(-5, 12), Vec2.Mm(25, 12)), tol);
Console.WriteLine(result.RemovedAreaMm2);   // 20.000000
Console.WriteLine(result.MaxDepthMm);       // 1.000000

float[][] lines = rest2D.ToPolylines();     // for three.js LineLoop / Babylon.js CreateLines
```

## Layout

```
src/Stykker.NanoCut.Core/         Vec2/Vec3 (1 nm), Int384, predicates, Plane3/HomogeneousPoint3, tolerance
src/Stykker.NanoCut.Geometry2D/   Region2, exact Boolean kernel, arcs, offset, Minkowski, penetration
src/Stykker.NanoCut.Cutting/      Tool2, ToolPath2, Cutter2 (2D removal along a path)
tests/Stykker.NanoCut.Tests/          analytic reference cases, predicates vs. BigInteger
tests/Stykker.NanoCut.OracleTests/    comparison with Clipper2 (test-only dependency)
tests/Stykker.NanoCut.WasmSmoke/      reference checks in the real browser-wasm runtime (node)
```

## Build and test

```bash
dotnet test                                          # net10.0: reference and oracle tests
dotnet build tests/Stykker.NanoCut.WasmSmoke -c Release
node tests/Stykker.NanoCut.WasmSmoke/bin/Release/net10.0/wwwroot/main.mjs   # browser-wasm
```

## 2D kernel

The 2D kernel is an exact arrangement method, not a Vatti scanbeam:

1. All edges of both operands are collected with their winding multiplicity.
2. A sweep over x finds crossings, T-junctions and collinear overlaps; edges are split there.
   Crossing points are rounded to the grid once and never moved again.
3. A half-edge structure with exact angular order yields the faces. Winding numbers are propagated face to face,
   with exactly one exact ray test per connected component.
4. Edges between inside and outside (per fill rule and operation) are linked into result contours:
   outer contours counter-clockwise, holes clockwise.

Results and fill rules (EvenOdd, NonZero, Positive, Negative) are the same as Clipper2's, but every decision is made
by an exact integer predicate and the topology follows directly from the winding numbers, which makes the kernel
easier to verify against the error budget.

## License

MIT

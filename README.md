# Stykker-NanoCut

C#-Bibliothek für Abtrag und Durchdringung in 2D und 3D mit einer Genauigkeit unter 0,1 µm.
Läuft als reines managed C# auf .NET 10, auch unter `browser-wasm`, und hat keine Laufzeit-Abhängigkeiten.
Hintergrund, Recherche und Phasenplan stehen in [docs/plan.md](docs/plan.md).

## Stand

| Phase | Inhalt | Status |
| --- | --- | --- |
| 0 – Repo & CI | Projektstruktur, GitHub Actions für `net10.0` und `browser-wasm`, MIT | ✅ |
| 1 – Core | 1-nm-Gitter, `Int128`-Prädikate, `Int384`, Ebenen und homogene Schnittpunkte, Toleranzmodell | ✅ Prädikate 10⁶ Fälle = `BigInteger`, Bitbudget nachgerechnet ([docs/bit-budget.md](docs/bit-budget.md)) |
| 2 – 2D-Kern | Booleans (4 Füllregeln), Bögen mit Sehnenfehler, Offset, Minkowski-Sweep, Fläche, Tiefe | ✅ Beispiel 1 (2D) im Budget, Oracle gegen Clipper2 bei 10.000 Zufallspolygonen |
| 3 – 3D-Kern | Ebenen-B-Rep, exakte Booleans, BVH, Primitive, STL/OBJ | offen |
| 4 – Sweep & Analyse 3D | lineare/bogenförmige Sweeps, Volumen, Tiefe | offen (2D-Sweep fertig) |
| 5 – Web | Puffer-Interop, Web Worker, npm-Adapter three.js/Babylon.js, Blazor-Demo | offen |
| 6 – Härtung & Release | Fuzzing, Benchmarks, Pakete | offen |

## Schnellstart (2D)

```csharp
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Cutting;

var tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50);

var rect = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
var circ = Region2.Circle(Vec2.Mm(10, 12), radiusMm: 3, tol);
double cutArea = (rect & circ).AreaMm2;     // 3.09732 (exakt 3.097482, Abweichung 1.7e-4 ≤ 5.0e-4)
Region2 rest2D = rect - circ;

// Abtrag entlang einer Bahn
var result = Cutter2.Cut(rect, Tool2.Circle(3), ToolPath2.Linear(Vec2.Mm(-5, 12), Vec2.Mm(25, 12)), tol);
Console.WriteLine(result.RemovedAreaMm2);   // 20.000000
Console.WriteLine(result.MaxDepthMm);       // 1.000000

float[][] lines = rest2D.ToPolylines();     // für three.js LineLoop / Babylon.js CreateLines
```

## Aufbau

```
src/Stykker.NanoCut.Core/         Vec2/Vec3 (1 nm), Int384, Prädikate, Plane3/HomogeneousPoint3, Toleranz
src/Stykker.NanoCut.Geometry2D/   Region2, exakter Boolean-Kern, Bögen, Offset, Minkowski, Penetration
src/Stykker.NanoCut.Cutting/      Tool2, ToolPath2, Cutter2 (2D-Abtrag entlang Bahn)
tests/Stykker.NanoCut.Tests/          analytische Referenzfälle, Prädikate gegen BigInteger
tests/Stykker.NanoCut.OracleTests/    Vergleich mit Clipper2 (nur Test-Abhängigkeit)
tests/Stykker.NanoCut.WasmSmoke/      Referenzprüfungen in der echten browser-wasm-Laufzeit (node)
```

## Bauen und testen

```bash
dotnet test                                          # net10.0: Referenz- und Oracle-Tests
dotnet build tests/Stykker.NanoCut.WasmSmoke -c Release
node tests/Stykker.NanoCut.WasmSmoke/bin/Release/net10.0/wwwroot/main.mjs   # browser-wasm
```

## 2D-Kern

Der 2D-Kern ist ein Arrangement-Verfahren mit exakter Arithmetik, kein Vatti-Scanbeam:

1. Alle Kanten beider Operanden werden mit ihrer Windungs-Multiplizität gesammelt.
2. Ein Sweep über x findet Kreuzungen, T-Stöße und kollineare Überlappungen; die Kanten werden dort geteilt.
   Kreuzungspunkte werden einmal auf das Gitter gerundet und danach nicht mehr bewegt.
3. Eine Halbkanten-Struktur mit exakter Winkelordnung liefert die Flächen. Die Windungszahlen werden von Fläche zu Fläche
   weitergegeben, mit genau einem exakten Strahltest pro Zusammenhangskomponente.
4. Kanten zwischen Innen und Außen (je nach Füllregel und Operation) werden zu Ergebnis-Konturen verkettet: außen gegen
   den Uhrzeigersinn, Löcher im Uhrzeigersinn.

Ergebnis und Füllregeln (EvenOdd, NonZero, Positive, Negative) sind dieselben wie bei Clipper2. Jede Entscheidung
fällt aber über ein exaktes Ganzzahl-Prädikat, und die Topologie wird direkt aus den Windungszahlen abgeleitet.
Damit lässt sich der Kern leichter gegen das Fehlerbudget prüfen.

## Lizenz

MIT

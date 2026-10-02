# Stykker-NanoCut – Recherche & Plan

Oct 2, 2026 · @Karsten

## Ziel & Anforderungen

Wir bauen **Stykker-NanoCut**, eine eigene C#-Bibliothek für Abtrag und Durchdringung in 2D und 3D. Keine gefundene Bibliothek erfüllt die drei Kernpunkte zugleich: Genauigkeit unter 0,1 µm, Abtrag entlang einer Bahn und Lauffähigkeit in Blazor WebAssembly mit Ausgabe an Babylon.js bzw. three.js. Alle Kerne werden selbst gebaut; die Bibliothek hat keine externen Laufzeit-Abhängigkeiten.

| Anforderung | Zielwert |
| --- | --- |
| Genauigkeit | < 0,0001 mm (0,1 µm) Abweichung zur exakten Geometrie, inkl. Diskretisierung |
| 2D | Kontur-Booleans, Abtrag entlang Bahn, Durchdringungsfläche und -tiefe |
| 3D | Körper-Booleans, Abtrag entlang Bahn (Sweep), Durchdringungsvolumen und -tiefe |
| Laufzeit | reines managed C#, .NET 10, auch `browser-wasm` ohne native und ohne externe Laufzeit-Abhängigkeiten |
| Ausgabe | Dreieckspuffer (Positionen + Indizes) für Babylon.js/three.js, 2D-Konturen als Polylinien |
| Lizenz | permissiv (MIT/Boost/Apache), kein GPL |

Namen: Repo `Stykker-NanoCut`, NuGet `Stykker.NanoCut.*`, npm `@stykker/nanocut-three` und `@stykker/nanocut-babylon`. Auf GitHub gibt es ein Python-Projekt [aradi/nanocut](https://github.com/aradi/nanocut) (Kristallstrukturen, anderes Feld); durch das Präfix Stykker gibt es keine Verwechslung.

## Was gesucht wurde

Gesucht wurde auf GitHub, NuGet und in der Fachliteratur, in neun Richtungen:

- GitHub-Topic `material-removal` und CNC-Abtragssimulation in C#
- C#-Mesh-Booleans/CSG mit exakter Arithmetik
- Dexel-/Voxel-Schnittsimulation
- Swept-Volume-Bibliotheken (Körper entlang Bahn)
- OpenCascade-Anbindung für C#/.NET
- 2D-Polygon-Clipping in C# (Clipper2 und Ports)
- geometry3Sharp / geometry4Sharp
- ManifoldSharp und Lauffähigkeit in Blazor WASM
- Exakte Mesh-Booleans (EMBER, Cork, Carve, MeshLib)

## Was es gibt

Keine Bibliothek wird ausgeliefert. Clipper2 und ManifoldSharp dienen nur als Vergleich in der Test-Suite; ember-csg und die Shewchuk-Prädikate liefern Algorithmus-Vorlagen.

| Bibliothek | Bereich | Genauigkeit / Robustheit | Blazor WASM | Rolle in NanoCut |
| --- | --- | --- | --- | --- |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) (C#) | 2D-Booleans, Offset, Minkowski | intern Int64, frei skalierbar → exakt auf Gitter | ja (managed) | **Testreferenz 2D**; Vatti-Kern wird selbst gebaut |
| [ManifoldSharp](https://github.com/larsbrubaker/manifold-sharp) | 3D-Mesh-Booleans | Robust-Engine mit exakter rationaler Arithmetik | ja, `browser-wasm` | **Testreferenz 3D** (Volumenvergleich), nicht ausgeliefert |
| [ember-csg](https://github.com/zalo/ember-csg) | 3D, exakte Integer-Booleans | bitgenau, Zwischenwerte bis 256 Bit bei 26-Bit-Positionen | nein (C++) | **Algorithmus-Vorlage** für den 3D-Kern |
| [RobustGeometry.NET](https://github.com/govert/RobustGeometry.NET), [robust-predicates (C#)](https://github.com/modios/robust-predicates) | Shewchuk-Prädikate orient2d/3d | adaptiv exakt für double | ja (managed) | Vorlage für Import-Prädikate auf double |
| [geometry4Sharp](https://github.com/NewWheelTech/geometry4Sharp) / geometry3Sharp | Mesh-Werkzeuge | MeshBoolean laut README nicht robust | ja | nur als Ideengeber (AABB, Mesh-IO) |
| [OpenCascade](https://github.com/Open-Cascade-SAS/OCCT-samples-csharp) / [OCCT3D-Wrapper](https://occt3d.com/components/csharp-wrapper/) | 3D B-Rep | exakte Flächen, Booleans teils instabil | nein (nativ) | optional später: STEP-Import serverseitig |
| [MeshLib](https://github.com/ehtick/MeshLib) | 3D-Mesh-Booleans | exakte Booleans | nein (nativ) | nicht genutzt |
| [MachineSimulation.NET](https://github.com/FishGPT/3D-MachineSimulation.NET) | 3D-Abtragssimulation | voxelorientiert | teilweise | nicht genutzt, zu ungenau |
| [cutsim](https://github.com/aewallin/cutsim) | 3D-Abtrag, Octree | gitterbasiert | nein | GPL-3.0, ausgeschlossen |
| [Swept-Volume-CSG](https://github.com/jjxia81/Swept-Volume-CSG), [swept-volumes](https://github.com/sgsellan/swept-volumes) | 3D-Sweep | Gitter-Näherung | nein | nicht genutzt; Verfahren pending US-Patent (Adobe) |
| [pb\_CSG](https://github.com/karl-/pb_CSG), [CarveSharp](https://github.com/Maghoumi/CarveSharp) | 3D-CSG | BSP mit Float bzw. nativ | nein / nativ | nicht genutzt |

## Was es nicht gibt

Es gibt keine C#-Bibliothek, die Abtrag entlang einer Bahn mit nachweisbarer Genauigkeit unter 0,1 µm berechnet und im Browser läuft. Die Lücke hat vier Teile:

- **Sweep/Abtrag:** Exakte Booleans existieren, aber kein managed Abtrag eines Werkzeugs entlang einer Bahn.
- **Fehlerschranke:** Exakte Arithmetik entfernt nur Rundungsfehler. Der Sehnenfehler beim Triangulieren von Bögen und Freiformflächen wird nirgends gegen eine Vorgabe gesteuert.
- **2D und 3D in einer API:** Clipper2 und ManifoldSharp haben getrennte Datenmodelle ohne gemeinsames Koordinaten- und Toleranzkonzept.
- **Web-Ausgabe:** Keine der Bibliotheken liefert fertige Puffer bzw. Adapter für Babylon.js oder three.js.

Diese vier Punkte und beide Geometriekerne baut Stykker-NanoCut selbst. Damit liegt jede Fehlerquelle im eigenen Code und lässt sich gegen das Budget prüfen.

## Architektur der eigenen Lib

&#91;embedded content: Architektur · 4 Schichten, 2 eigene Kerne, Referenzen nur im Test\]

Beide Kerne sind Eigenbau hinter einer gemeinsamen API und gibt nach oben nur Puffer heraus; die Adapter machen daraus Geometrie in three.js oder Babylon.js.

## Kern-Algorithmen

Alle Rechnungen laufen auf ganzzahligen Koordinaten; Gleitkomma kommt nur beim Import und bei der Anzeige vor.

**Koordinaten:** 1 Einheit = 1 nm, Wertebereich ±2³¹ nm = ±2,147 m. Das hält die Zwischenwerte der 3D-Prädikate in einer festen Bitbreite.

**2D-Kern (Vatti-Sweepline):**

- Kanten werden nach y sortiert, eine Sweepline läuft über alle Ereignisse (Endpunkte, Schnittpunkte).
- Füllregeln NonZero und EvenOdd; Operationen Union, Intersection, Difference, Xor.
- Schnittpunkte werden auf das 1-nm-Gitter gerundet (≤ 0,71 nm Abweichung). Bereits gerasterte Punkte werden nie wieder bewegt, deshalb summiert sich der Fehler über viele Schritte nicht.
- Offset und Minkowski-Summe für Werkzeugkontur entlang Bahn.
- Prädikat orient2d exakt mit `Int128` (Differenzen ≤ 2³², Produkte ≤ 2⁶⁴).

**3D-Kern (Ebenen-basierte exakte Booleans nach EMBER-Prinzip):**

- Jede Fläche wird über ihre Trägerebene aus drei Gitterpunkten beschrieben, nicht über gerundete Schnittpunkte.
- Neue Eckpunkte entstehen als Schnitt dreier Ebenen und werden homogen (X, Y, Z, W) als Ganzzahlen gespeichert. Es wird nie gerundet, also gibt es keine Drift.
- Klassifikation innen/außen über Windungszahlen, lokale Arrangements in einem BVH-Raster.
- Bitbudget überschlägig: Normalen ≈ 2⁶⁵, Ebenenabstand ≈ 2⁹⁸, Schnittpunkt-Zähler ≈ 2²³¹, Punkt-gegen-Ebene-Test ≈ 2³⁰⁰. Dafür ein eigener Festbreiten-Typ `Int384` (6 × 64 Bit). Die genaue Grenze wird in Phase 1 nachgerechnet und per Test abgesichert.

**Sweep (Werkzeug entlang Bahn):**

- Lineare Bewegung eines konvexen Werkzeugs: das überstrichene Volumen ist exakt die konvexe Hülle aus Start- und Endlage (Minkowski-Summe mit Strecke).
- Kreisbögen und Drehungen: Zerlegung in lineare Teilstücke, Schrittweite aus dem zulässigen Hüllfehler.
- Restkörper = Rohteil minus Vereinigung aller Sweep-Stücke; Vereinigung zuerst lokal, dann ein Boolean gegen das Rohteil.
- Kugel- und Zylinderwerkzeuge werden als Polyeder mit gesteuertem Sehnenfehler erzeugt.

**Analyse:** Abtragsvolumen und Fläche exakt aus den Ganzzahl-Koordinaten (Divergenzsatz), Durchdringungstiefe als größter Abstand der Abtragsfläche zur Ausgangsfläche.

## Genauigkeitskonzept

Die 0,1 µm werden als Fehlerbudget auf vier Quellen aufgeteilt; jede Quelle bekommt eine eigene, prüfbare Grenze.

| Fehlerquelle | Maßnahme | Budget |
| --- | --- | --- |
| Zahlendarstellung | Rechenkoordinaten als Int64 auf 1-nm-Gitter, exakte Prädikate | ±0,5 nm |
| Diskretisierung (Bögen, Flächen) | Segmentzahl aus vorgegebenem Sehnenfehler berechnen | ≤ 50 nm |
| Bahn-/Sweep-Schrittweite | adaptive Schritte, bis Hüllfehler unter Grenze | ≤ 30 nm |
| Reserve | Import-Rundung, Sonderfälle | ≤ 20 nm |

Die Segmentzahl ergibt sich aus Radius r und zulässigem Sehnenfehler s:

```latex
\theta = 2 \arccos\left(1 - \frac{s}{r}\right), \qquad n = \left\lceil \frac{2\pi}{\theta} \right\rceil
```

Bei s = 50 nm ergibt das 2.222 Segmente pro Vollkreis bei r = 50 mm und 545 Segmente bei r = 3 mm. Polygone werden einbeschrieben erzeugt, die Abweichung liegt also immer auf einer Seite (Material bleibt eher stehen). Der Wertebereich ±2³¹ nm deckt Bauteile bis gut 4 m Kantenlänge ab.

Float32 wird nur für die Anzeige im Browser genutzt (bei 100 mm etwa 0,01 µm Auflösung), nie für die Rechnung.

**Nachweis:** Referenztests gegen analytische Lösungen, z. B. Zylinder-Zylinder-Durchdringung (Steinmetz-Körper, V = 16r³/3), Kugel-Kugel-Linse, Kreis-Kreis-Schnittfläche. Jeder Test prüft die Abweichung gegen das Budget.

## Erstes Beispiel: Kugelkopf durch Quader

Ein Kugelwerkzeug fährt geradlinig 1 mm tief über einen Quader. Das Ergebnis ist analytisch bekannt und prüft 2D und 3D mit demselben Fall.

| Größe | Wert |
| --- | --- |
| Rohteil | Quader 20 × 20 × 10 mm, Ecke bei (0, 0, 0) |
| Werkzeug | Kugel, r = 3 mm |
| Bahn | linear von (−5, 10, 12) nach (25, 10, 12) mm |
| Eintauchtiefe | h = 1 mm unter der Oberseite (z = 10) |
| Toleranz | Gesamtbudget 0,1 µm, Sehnenfehler 50 nm |

Die Kugel startet und endet ganz außerhalb des Quaders. Deshalb ist die Nut über die volle Länge L = 20 mm gleich, und der Querschnitt ist ein Kreisabschnitt:

```latex
A = r^2 \arccos\left(\frac{r-h}{r}\right) - (r-h)\sqrt{2rh-h^2}, \qquad V = A \cdot L
```

| Prüfgröße | Sollwert | zulässige Abweichung |
| --- | --- | --- |
| 2D: Schnittfläche Kreis ∩ Rechteck (Schnitt bei x = 10) | 3,097482080 mm² | ≤ Bogenlänge × 0,1 µm = 0,000505 mm² |
| 3D: Abtragsvolumen | 61,949641602 mm³ | ≤ benetzte Fläche × 0,1 µm = 0,0101 mm³ |
| Nutbreite an der Oberseite | 4,472135955 mm | ± 0,0002 mm |
| Maximale Tiefe | 1,000000 mm | ± 0,0001 mm |
| Abstand jedes Nutpunkts zur Achse y = 10, z = 12 | 3,000000 mm | ± 0,0001 mm |

Der letzte Test ist der eigentliche Genauigkeitsnachweis: Er prüft jeden Eckpunkt der erzeugten Nutfläche, nicht nur eine Summe.

**Ablauf des Beispiels:**

1. 2D: Rechteck 20 × 10 mm und Kreis um (10, 12) mit r = 3 mm, Schnittfläche und Differenz berechnen.
2. 3D: Quader minus Sweep der Kugel, Volumen, Tiefe, Breite und Abstände prüfen.
3. Restkörper als Puffer ausgeben und in der Blazor-Demo in three.js und Babylon.js anzeigen.
4. Gegenprobe in der Test-Suite: gleicher Fall mit Clipper2 (2D) und ManifoldSharp (3D).

Danach folgen Beispiel 2 (schräge Bahn, Eintritt und Austritt im Material mit Kugelkappen) und Beispiel 3 (Kreisbogenbahn).

## API-Entwurf

Die API arbeitet in Millimetern nach außen und in Nanometern nach innen; die Toleranz wird bei jeder Operation mitgegeben.

```csharp
using Stykker.NanoCut;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Cutting;

var tol = Tolerance.Budget(totalUm: 0.1, chordNm: 50);

// 2D
var rect = Region2.Rectangle(Vec2.Mm(0, 0), Vec2.Mm(20, 10), tol);
var circ = Region2.Circle(Vec2.Mm(10, 12), radiusMm: 3, tol);
double cutArea = (rect & circ).AreaMm2;     // 3.0974…
Region2 rest2D = rect - circ;

// 3D
var stock = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(20, 20, 10), tol);
var tool  = Tool.Ball(radiusMm: 3);
var path  = ToolPath.Linear(Vec3.Mm(-5, 10, 12), Vec3.Mm(25, 10, 12));

CutResult r = Cutter.Cut(stock, tool, path, tol);
Console.WriteLine(r.RemovedVolumeMm3);      // 61.9496…
Console.WriteLine(r.MaxDepthMm);            // 1.0000

// Ausgabe für den Browser
MeshBuffers buf = r.Remaining.ToMeshBuffers(OriginMode.Centroid);
```

```js
import { toThreeGeometry } from '@stykker/nanocut-three';
import { toBabylonMesh } from '@stykker/nanocut-babylon';

const geo  = toThreeGeometry(buffers);          // THREE.BufferGeometry
const mesh = toBabylonMesh(buffers, 'rest', scene); // BABYLON.Mesh
```

## Repo-Struktur

Ein Repo, mehrere kleine Pakete, damit 2D ohne 3D nutzbar ist.

```
Stykker-NanoCut/
  src/Stykker.NanoCut.Core/         Ganzzahltypen (Int128, Int384), Prädikate, Toleranz
  src/Stykker.NanoCut.Geometry2D/   Konturen, Bögen, Vatti-Booleans, Offset, Minkowski
  src/Stykker.NanoCut.Geometry3D/   Ebenen-B-Rep, exakte Booleans, Primitive, STL/OBJ
  src/Stykker.NanoCut.Cutting/      Werkzeuge, Bahnen, Sweep, Analyse
  src/Stykker.NanoCut.Interop/      Puffer, [JSExport], Web-Worker-Einstieg
  js/nanocut-three/                 npm-Adapter three.js
  js/nanocut-babylon/               npm-Adapter Babylon.js
  samples/Stykker.NanoCut.Demo/     Blazor-Demo, 2D- und 3D-Ansicht
  tests/Stykker.NanoCut.Tests/          analytische Referenzfälle
  tests/Stykker.NanoCut.OracleTests/    Vergleich mit Clipper2 / ManifoldSharp
  bench/Stykker.NanoCut.Bench/      BenchmarkDotNet, WASM-Messungen
```

Lizenzvorschlag: MIT. CI über GitHub Actions baut `net10.0` und `browser-wasm` und führt beide Test-Suiten aus.

## Integration Blazor / Babylon.js / three.js

Die Bibliothek rechnet in C# und gibt nur rohe Puffer heraus; ein dünner JS-Adapter pro Engine baut daraus die Geometrie.

- **Betriebsarten:** dieselbe Bibliothek in Blazor WASM (Rechnung im Browser) oder hinter einer ASP.NET-API (große Jobs auf dem Server).
- **Rechenleistung im Browser:** Ohne AOT läuft .NET in WASM über einen IL-Interpreter; laut [Microsoft-Doku](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-build-tools-and-aot?view=aspnetcore-10.0) bringt AOT bei rechenintensiven Aufgaben deutliche Gewinne. NanoCut-Demo wird deshalb mit `RunAOTCompilation=true` veröffentlicht.
- **Hintergrund-Rechnung:** Lange Jobs laufen in einem [.NET-Web-Worker](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-with-dotnet-on-web-workers?view=aspnetcore-10.0), damit die Oberfläche flüssig bleibt. Echtes Multithreading (`WasmEnableThreads`) ist ab .NET 10 möglich, braucht aber COOP/COEP-Header und hat noch [offene Fehler](https://github.com/dotnet/runtime/issues/129900); es bleibt optional.
- **Datenübergabe:** `Float32Array` für Positionen und Normalen, `Uint32Array` für Indizes, per `[JSImport]`/`[JSExport]` als Byte-Block statt JSON.
- **three.js-Adapter:** `BufferGeometry` mit `position`-, `normal`- und Index-Attribut; 2D-Konturen als `LineLoop`.
- **Babylon.js-Adapter:** `VertexData` (positions, indices, normals) auf ein `Mesh` anwenden; 2D-Konturen als `CreateLines`.
- **Lokaler Ursprung:** Puffer relativ zum Bauteilmittelpunkt, damit Float32 in der Anzeige genau bleibt.
- **Export:** STL und glTF für externe Viewer, SVG und DXF für 2D.

## Umsetzungsplan in Phasen

Sieben Phasen, jede endet mit einer messbaren Abnahme. 2D kommt vor 3D, und der eigene 3D-Kern ist eine eigene Phase vor dem Sweep, weil er der schwerste Teil ist.

1. **Phase 0 – Repo & CI:** Repo `Stykker-NanoCut` anlegen, Projektstruktur, GitHub Actions für `net10.0` und `browser-wasm`, MIT-Lizenz.
   - Abnahme: leerer Testlauf grün in beiden Zielen.
2. **Phase 1 – Core:** Koordinatentyp (1 nm), `Int128`-Prädikate für 2D, `Int384` für 3D, Toleranzmodell, Referenztest-Framework.
   - Bitbudget der 3D-Prädikate nachrechnen und mit Grenzfall-Tests absichern.
   - WASM-Benchmark: Interpreter gegen AOT, `Int384`-Multiplikation pro Sekunde.
   - Abnahme: Prädikate liefern bei 10⁶ Zufallsfällen dasselbe Vorzeichen wie `BigInteger`.
3. **Phase 2 – 2D-Kern:** Vatti-Sweepline, Füllregeln, Bögen mit gesteuertem Sehnenfehler, Offset und Minkowski, Fläche und Tiefe.
   - Abnahme: Beispiel 1 in 2D im Budget; Oracle-Test gegen Clipper2 bei 10.000 Zufallspolygonen flächengleich.
4. **Phase 3 – 3D-Kern:** Ebenen-B-Rep, exakte Booleans, Windungszahlen, BVH, Primitive (Quader, Zylinder, Kugel, Kegel, Rotationskörper), STL/OBJ-Import.
   - Abnahme: Steinmetz-Körper (16r³/3) und Kugel-Linse im Budget; Volumenvergleich gegen ManifoldSharp.
5. **Phase 4 – Sweep & Analyse:** lineare Sweeps konvexer Werkzeuge, dann Kreisbögen, dann Werkzeugdrehung; Abtragsvolumen, Tiefe, Kontaktfläche.
   - Abnahme: Beispiel 1 in 3D, alle fünf Prüfgrößen im Budget; danach Beispiele 2 und 3.
6. **Phase 5 – Web:** Puffer-Interop, Web-Worker-Einstieg, npm-Adapter für three.js und Babylon.js, Blazor-Demo mit 2D- und 3D-Ansicht.
   - Abnahme: Beispiel 1 läuft im Browser in beiden Engines, Rechenzeit gemessen.
7. **Phase 6 – Härtung & Release:** Fuzzing mit Zufallskörpern, Benchmarks, Doku, NuGet- und npm-Pakete v0.1.
   - Abnahme: 24 h Fuzzing ohne Fehler, Pakete veröffentlicht.

## Risiken & offene Fragen

Die größten Risiken sind der eigene 3D-Kern und die Rechenzeit des 3D-Sweeps im Browser.

| Risiko | Gegenmaßnahme |
| --- | --- |
| Eigener 3D-Kern: Sonderfälle (koplanare Flächen, Berührungen, Null-Volumen) | exakte Prädikate statt Toleranzen; Fuzzing; Oracle-Vergleich gegen ManifoldSharp |
| `Int384`-Arithmetik langsam, besonders im WASM-Interpreter | Filter: erst schneller double-Test mit Fehlerschranke, exakt nur im Grenzfall; AOT |
| 3D-Sweep: Dreieckszahl und Laufzeit | Verfeinerung nur im Kontaktbereich; Vereinigung lokal; große Jobs auf dem Server |
| WASM-Multithreading noch nicht stabil | Web Worker ohne Threads als Standard; Threads optional |
| Name auf NuGet/npm vergeben | vor Phase 0 prüfen und reservieren |

Offene Fragen:

- [ ] Bauteilgröße und Wertebereich (größte Abmessung in mm)?
- [ ] Eingabeformate: reicht STL/OBJ, oder wird STEP gebraucht (dann OpenCascade serverseitig)?
- [ ] Werkzeugformen: nur rotationssymmetrisch oder beliebig?
- [ ] Ergebnisse: Restgeometrie, Abtragsvolumen, Durchdringungstiefe – welche zuerst?
- [ ] Rechnung im Browser oder auf dem Server als Standard?

## Quellen

- [Clipper2](https://github.com/AngusJohnson/Clipper2) · [Clipper2 Doku](https://angusj.com/clipper2/Docs/Overview.htm)
- [ManifoldSharp](https://github.com/larsbrubaker/manifold-sharp) · [NuGet](https://www.nuget.org/packages/ManifoldSharp/)
- [ember-csg](https://github.com/zalo/ember-csg)
- [RobustGeometry.NET](https://github.com/govert/RobustGeometry.NET) · [robust-predicates (C#)](https://github.com/modios/robust-predicates)
- [geometry4Sharp](https://github.com/NewWheelTech/geometry4Sharp)
- [OCCT-samples-csharp](https://github.com/Open-Cascade-SAS/OCCT-samples-csharp) · [OCCT3D C# Wrapper](https://occt3d.com/components/csharp-wrapper/)
- [MeshLib](https://github.com/ehtick/MeshLib)
- [3D-MachineSimulation.NET](https://github.com/FishGPT/3D-MachineSimulation.NET) · [cutsim](https://github.com/aewallin/cutsim)
- [Swept-Volume-CSG](https://github.com/jjxia81/Swept-Volume-CSG) · [swept-volumes](https://github.com/sgsellan/swept-volumes)
- [pb\_CSG](https://github.com/karl-/pb_CSG) · [CarveSharp](https://github.com/Maghoumi/CarveSharp)
- [aradi/nanocut](https://github.com/aradi/nanocut) (Namensprüfung)
- [Blazor AOT](https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-build-tools-and-aot?view=aspnetcore-10.0) · [Blazor Web Workers](https://learn.microsoft.com/en-us/aspnet/core/blazor/blazor-with-dotnet-on-web-workers?view=aspnetcore-10.0) · [WASM-Threads Issue](https://github.com/dotnet/runtime/issues/129900)

# Performance-Findings — Stykker-NanoCut

**Datum:** 2026-10-02
**Branch/Commit der Analyse:** `worktree-eager-ray-993e76` @ `7b83ecf`
**Art der Arbeit:** reine Lese-Analyse + externe Recherche. Es wurde **nichts gebaut, nichts getestet, nichts committet** — in einer anderen Session laufen Builds/Tests.

Alle Vorschläge sind Kandidaten mit geschätztem Effekt und Risiko, keine umgesetzten Änderungen. „Bitgleich" heißt: Ergebnis darf sich zwischen altem und neuem Code nicht unterscheiden (die vorhandene Test-/Oracle-Suite entscheidet).

---

## 1. Ausgangslage (aus `bench/README.md`)

pocket-large (876 Schritte), gleiche Maschine, 4 Kerne:

| Engine | Sprache | Exakt | Zeit | CPU (ganzer Prozess) |
| --- | --- | --- | ---: | ---: |
| nanocut | C# | ja | 10,4 s | 35,9 s |
| manifoldsharp | C# | nein | 29,7 s | 78,8 s |
| manifold | C++ | nein | 10,1 s (≈2,9 Kerne) | 29,3 s |

NanoCut ist in Wall-Time gleichauf mit C++ Manifold und braucht **weniger CPU-Sekunden**. Die Sprache ist damit kein dominanter Faktor mehr. Das Projekt nennt als Restkosten selbst:

- Fragmente an T-Junctions, die nicht gemerged werden können,
- viele geswepte Hulls bei Rotation,
- exakte Arithmetik (`Int384`) nur als Fallback — Trefferquote der Filter ist **nicht gemessen**.

`docs/native-speed-plan.md` Schritt 2 nennt bereits richtige Punkte (Int128-Fastpath in `Int384`, weniger Allokationen, SIMD für die Double-Filter), die im Code noch nicht vorhanden sind.

---

## 2. Übersicht (Priorität)

| # | Fund | Ort | Effekt (Schätzung) | Risiko |
| --- | --- | --- | --- | --- |
| A1 | `FaceMerge.Join`: Konvexität O(V·E) → O(V) | `FaceMerge.cs:124` | mittel–hoch | niedrig |
| A2 | `FaceMerge.Join`: Kollinear-Entfernung O(V²) → O(V) | `FaceMerge.cs:113-121` | klein–mittel | niedrig |
| B1 | `Point3.SideOf` ohne Float-Filter für Grid-Punkte | `Point3.cs:72` | mittel–hoch | niedrig |
| B2 | `ConvexHull3.Above`: Fallback als `Orient3D` statt `Plane3.FromPoints` + `Side` | `ConvexHull3.cs:197` | klein–mittel | niedrig |
| B3 | `ConvexHull3.Height`: Conflict-List skalar → SIMD (`Vector256`) | `ConvexHull3.cs:182` | mittel (kugelig) | niedrig |
| C1 | `Point3.Exact` als Klasse → Vertex-Pool (int-Index) | `Point3.cs` | mittel | mittel |
| C2 | `SolidBoolean.Locate`: `new Probe` pro Versuch → Thread-Scratch | `SolidBoolean.cs:252` | klein–mittel | niedrig |
| C3 | `SolidBoolean.ProcessFace`: Listen pro Splitting-Ebene → Scratch/Arrays | `SolidBoolean.cs:164,168` | klein–mittel | niedrig |
| C4 | `ConvexHull3.Compute`: Union-Find-Arrays/Dicts pro Aufruf → `Scratch` | `ConvexHull3.cs:23` | mittel | niedrig |
| C5 | 2D `BooleanKernel.BuildGraph`: Delegate-Allokation je Vertex | `BooleanKernel.cs:357` | klein–mittel (2D) | niedrig |
| C6 | 2D `RemoveCollinear`: O(n²) mit Neustart | `BooleanKernel.cs` | klein (2D) | niedrig |
| D1 | BVH wird pro Boolean neu gebaut → Lazy-Cache im `Solid` | `SolidBoolean.cs:47-48` | mittel–hoch (Langlauf) | mittel |
| D2 | `Int384`-Operatoren: stackalloc/Store-Kopien, kein Int128-Fastpath | `Int384.cs:169,214` | mittel (nur bei Fallback) | mittel |
| — | Kein `[MethodImpl(AggressiveInlining)]`, kein `SkipLocalsInit`, kein SIMD, kein `ServerGC` | repo-weit / `Directory.Build.props` | klein | niedrig |

Empfohlene Reihenfolge: **erst messen** (Abschnitt 5), dann B1 + A1 + B2 (klein, bitgleich prüfbar), dann C-Reihe, zuletzt D1/D2.

---

## 3. Details

### A1 — `FaceMerge.Join`: Konvexität in O(V) statt O(V·E)

`src/Stykker.NanoCut.Geometry3D/FaceMerge.cs:124`

```csharp
// Konvex iff every vertex is on the inner side of every edge plane.
for (int e = 0; e < edges.Count; e++)
    foreach (var v in verts)
        if (v.SideOf(edges[e]) > 0) return null;
```

Der Aufruf ist O(V·E) exakter Side-Tests. Für einen **einfachen** Polygonzug gilt: konvex ⟺ alle Innenwinkel ≤ π ⟺ alle Abbiegungen in dieselbe Richtung. Da die Kanten bereits die innere Seite negativ kodieren (siehe `Face3`-Kommentar „interior on the negative side"), genügt **eine** Prüfung je Kante:

```csharp
for (int i = 0; i < verts.Count; i++)
    if (verts[(i + 1) % verts.Count].SideOf(edges[i]) > 0) return null;
```

Das prüft die lokale Ecke bei `verts[i+1]`. Alle anderen Constraints folgen für ein einfaches, konvex-geordnetes Loop.

**Externe Bestätigung:** Das Mergen koplanarer konvexer Polygone läuft über die Boundary und eine **Reflex-Ecken-Prüfung**; die Konvexitätserkennung ist optimal O(n) (siehe Quellen). Genau das macht der lokale Test.

**Effekt:** FaceMerge läuft auf jedem Boolean-Ergebnis. Bei vielen koplanaren Fragmenten (lange Strips aus aufeinanderfolgenden Hulls) ist V·E deutlich größer als V.
**Verifikation:** Vorhandene FaceMerge-/Boolean-Tests; zusätzlich Property-Test „gemergtes Face ist konvex" gegen die alte O(V·E)-Prüfung auf Zufallsdaten.

### A2 — `FaceMerge.Join`: Kollinear-Entfernung ohne Neustart

`FaceMerge.cs:113-121`

```csharp
for (int i = 0; i < verts.Count && verts.Count > 3; i++)
{
    int prev = (i - 1 + verts.Count) % verts.Count, next = (i + 1) % verts.Count;
    if (verts[next].SideOf(edges[prev]) == 0)
    {
        verts.RemoveAt(i);
        i = -1;              // Neustart → O(V²) mit List-Shifting
    }
}
```

Ein einzelner Durchgang mit Stack/Deque (wie bei A1) macht daraus O(V).

### B1 — `Point3.SideOf` ohne Float-Filter für Grid-Punkte

`src/Stykker.NanoCut.Geometry3D/Point3.cs:72`

```csharp
public int SideOf(in Plane3 plane)
{
    if (_exact is null) return Predicates.Side(plane, _grid);   // immer volles Int128
    int f = Filter.Sign(plane, _exact.X, _exact.Y, _exact.Z);   // exakter Zweig hat schon einen Filter
    return f != Filter.Uncertain ? f : Predicates.Side(plane, _exact.H);
}
```

Der exakte Zweig filtert bereits, der Grid-Zweig nie. `SideOf` sitzt in den heißen Kandidatenschleifen von `SolidBoolean`:

- `SideSummary(q.Support, p.Vertices)` — einmal pro Kandidatenpaar,
- `Separated(q, p.Vertices)` — O(|q.Edges| · |p.Vertices|),
- `TouchesOrCrosses(p.Support, q.Vertices)` — O(|q.Vertices|),
- `Face3.Split` — O(V) pro Split.

Vorschlag:

```csharp
if (_exact is null)
{
    int f = Filter.Sign(plane, _grid.X, _grid.Y, _grid.Z);
    return f != Filter.Uncertain ? f : Predicates.Side(plane, _grid);
}
```

`Filter.Sign(in Plane3, x, y, z)` existiert bereits (`Filter.cs`), Bound = `1e-11 × Σ|Terme|` — sehr konservativ; Grid-Koordinaten sind exakt in `double`, die `Int128`-Koeffizienten haben rel. Fehler ~1e-16. Damit bleibt der Fallback exakt und das Ergebnis bitgleich.

**Noch besser (optional):** `Face3` cached `PlanesD` (double) bereits. Ein Overload `SideOf(double[] planesD, int offset, …)` würde die 4 `Int128`→`double`-Konvertierungen pro Aufruf sparen. Größerer Refactor, später.

**Effekt:** mittel–hoch in 3D-Boolean, weil `Separated` quadratisch in den Vertex-Zahlen ist.

### B2 — `ConvexHull3.Above`: Fallback ist ein `orient3d`

`src/Stykker.NanoCut.Geometry3D/ConvexHull3.cs:197`

```csharp
var (a, b, c) = faces[f];
return Predicates.Side(Plane3.FromPoints(p[a], p[b], p[c]), p[q]) > 0;
```

`Side(plane, q) = sign(n·(q−a))` für eine Ebene durch a,b,c — identisch zu

```csharp
return Predicates.Orient3D(p[a], p[b], p[c], p[q]) > 0;
```

Der `orient3d`-Aufruf spart `Plane3.FromPoints` (Normalenprodukte **plus** `d = −n·a`) und einen Multiply-Add. `Above` ist der Kern der Conflict-List-Verwaltung und damit der heißeste Test des Hulls.

**Externe Bestätigung:** Für 3D-Hulls ist genau dieser Plane-Distance-Test der dominierende Kostenblock (qhull `qh_distplane`). Kleine Einsparung pro Aufruf × sehr viele Aufrufe.

### B3 — `ConvexHull3.Height`: Conflict-List vektorisieren

`ConvexHull3.cs:182`

```csharp
double Height(int f, int q)
{
    var pl = planesD[f];
    return pl.X * p[q].X + pl.Y * p[q].Y + pl.Z * p[q].Z + pl.D;
}
```

Die Farthest-Point-Schleife ruft das pro Punkt der Conflict-List auf; das ist ein reiner Skalar-Dot-Product-Loop. Mit `Vector256<double>` (4 Punkte/Vektor) oder `TensorPrimitives` batchbar. **Ohne NuGet** mit Intrinsics, damit die Regel „no runtime dependencies" hält (`Directory.Build.props`: `IsAotCompatible`, `IsTrimmable`). Derzeit gibt es im ganzen `src/` **keinen** SIMD-Code (`grep` nach `Vector256`/`TensorPrimitives` = 0 Treffer).

**Effekt:** mittel bei kugeligen/fein tessellierten Punktmengen — und genau so ein Ball-Hull ist das Bench-Szenario. **Externe Bestätigung:** VQhull (2025) beschleunigt Quickhull genau über Vektorisierung + Multithreading.

### C1 — `Point3.Exact` als Klassenobjekt → Vertex-Pool

`Point3.cs`: Der exakte Zweig hält

```csharp
private sealed class Exact(HomogeneousPoint3 h)   // 192 B Int384-Quadrupel + 3 doubles + Header
```

Damit kostet **jeder erzeugte exakte Vertex** eine Heap-Allokation von ~224 B. Bei jedem Boolean entstehen viele neuer Eckpunkte (Schnittpunkte dreier Ebenen in `Face3.Split` / `Section3.Segment`). Die Identität wird bewusst geteilt (`Identity`-Property) — das bleibt erhalten, wenn man stattdessen einen Index teilt.

Vorschlag: die `HomogeneousPoint3`-Quadrupel in einem wachsenden `Int384[]`-Pool ablegen; `Point3` hält nur `Vec3` (24 B) + `int index` + ein `IsGrid`-Flag. `SideOf`/`SameAs`/`Homogeneous`/`Big` indexieren den Pool. Nutzen: keine Einzelallokationen, deutlich bessere Cache-Lokalität (Arrays von kompakten 32-B-Structs statt Pointer-Chasing).

**Risiko:** mittel (berührt viele Stellen), aber lokal begrenzt und über die Oracle-Tests (bitgleiche Volumes/Face-Zahlen) absicherbar.

### C2 — `SolidBoolean.Locate`: `Probe` allokiert pro Versuch

`SolidBoolean.cs:252`

```csharp
for (int attempt = 0; attempt < Weights.Length; attempt++)
{
    var c = new Probe(f, attempt);   // Heap-Objekt je Fragment (i. d. R. 1×)
    ...
}
```

`Probe` cacht `_big` (lazy), ist also ein Kandidat für ein **Thread-lokales Scratch-Objekt** mit `Reset`. `Buffers` ist bereits thread-lokal vorhanden; `Probe` dort mitführen.

### C3 — `SolidBoolean.ProcessFace`: Listen pro Ebene

`SolidBoolean.cs:164,168`:

```csharp
var fragments = new List<Node> { root };
for (int pi = 0; pi < b.Planes.Count; pi++)
{
    var next = new List<Node>(fragments.Count + 4);
    ...
}
```

Pro Face und Splitting-Ebene eine neue Liste; dazu `var nodes = new Node[faces.Count]` (`:110`) und `return [.. nodes]` (Array→List-Kopie). Zwei Scratch-Listen (Ping-Pong) im `Buffers`-Objekt plus Rückgabe der Liste ohne Kopie würden den Druck senken.

### C4 — `ConvexHull3.Compute`: Union-Find-Strukturen pro Aufruf

`ConvexHull3.cs:23`

```csharp
var parent = new int[tris.Count];
var size = new int[tris.Count];
var groups = new Dictionary<int, List<(int, int, int)>>();
var next = new Dictionary<int, int>();
var inner = new HashSet<long>();
var loop = new List<Vec3>();
```

Runde 6/10 hat die Hull-Puffer in `Scratch` gepoolt, aber diese Strukturen werden weiterhin **pro Aufruf neu** angelegt. Im Bench läuft `ConvexHull3.Compute` 876× → viel kurzlebiger Müll. Ins vorhandene `Scratch` verschieben und `Clear()`-en.

Ebenfalls in der Hull: `pts = points.Distinct().ToArray()` (`:131`, LINQ/HashSet) und `faces.Where((_, i) => alive[i]).ToList()` — LINQ im Kernel, im Plan explizit als „no LINQ" markiert.

### C5 — 2D `BooleanKernel.BuildGraph`: Delegate je Vertex

`src/Stykker.NanoCut.Geometry2D/BooleanKernel.cs:357`

```csharp
Array.Sort(list, (h1, h2) => CompareAngle(vertices[origin[h1 ^ 1]] - pv, vertices[origin[h2 ^ 1]] - pv));
```

Der Lambda captured `pv` (ändert sich je Vertex) → **Closure + Delegate-Allokation pro Vertex-Knoten**. Das ist dasselbe Problem, das `Bvh3.Build` bereits mit einem Key-Array gelöst hat („Sort with a key array"): Winkel-Keys vorab in ein `double[]`/`long[]` schreiben und `Array.Sort(keys, list)` nutzen. Ebenso die Sort-Lambdas in `Merge` und `AddPolyline`.

### C6 — 2D `RemoveCollinear`: O(n²)

`BooleanKernel.RemoveCollinear`: `List.RemoveAt(i); i--; changed = true;` in einer `while(changed)`-Schleife — gleiches Muster wie A2.

### D1 — BVH-Neubau pro Boolean

`SolidBoolean.cs:47-48`

```csharp
var bvhA = new Bvh3(a);
var bvhB = new Bvh3(b);
```

Jeder Aufruf baut beide BVHs neu (Median-Split + `Array.Sort`). Bei der inkrementellen Schnitt-Simulation (876 Schritte, wachsendes Werkstück) wird der große Workpiece-BVH **jeden Schritt** neu gebaut. `Solid` ist unveränderlich → ein **lazy gecachter `Bvh3` im `Solid`** ist sicher und hilft überall dort, wo ein Operand mehrfach benutzt wird:

- `UnionTree` (paarweise Vereinigungen),
- `Process3.Flush`: `result[w] − swept` über mehrere Werkstücke (derselbe `swept`),
- Demo-Schleifen, die dasselbe Tool mehrfach schneiden.

Darüber hinaus (höheres Risiko): **BVH-Refit** statt Neubau, da sich das Ergebnis nur lokal ändert.

### D2 — `Int384`-Operatoren

`Int384.cs:169` (`+`) und `:214` (`*`): je Operation `stackalloc ulong[6]`/`[12]`, `Store`-Kopien und Limb-Schleifen. Das ist Plan-Schritt 2 („unchecked limb arithmetic + Int128-Fastpath, wenn Operanden klein"). Relevanz hängt davon ab, wie oft der exakte Fallback greift — zuerst Trefferquote messen (Abschnitt 5). `Predicates.Side(Plane3, HomogeneousPoint3)` fährt pro Aufruf 4 `Int384`-Multiplies + 3 Adds.

---

## 4. Externe Recherche (Ergebnis)

- **Koplanare konvexe Polygone mergen:** Boundary-Pfad laufen lassen und **Reflex-Ecken** zählen; Konvexitätstest optimal O(n). Bestätigt A1. (Quellen unten.)
- **Quickhull 3D:** dominanter Kostenblock ist der Plane-Distance-Test über der Conflict-List (qhull `qh_distplane`, meist aus `qh_findbestnew`). Praktische Beschleuniger: Vektorisierung + Multithreading (VQhull 2025, AVX-512/AVX2, bandbreitenbewusst). Bestätigt B3. Randnotiz aus den Quellen: inkrementelles `addpoint` ist oft langsamer als ein Rebuild — spricht dafür, beim „Hull pro Schritt neu"-Ansatz zu bleiben und **innen** zu vektorisieren statt die Hull-STruktur umzubauen.
- **Parallel Randomized Incremental Construction** (Blelloch, Gu, Shun, Sun) ist hier nicht direkt anwendbar: die Schritte der Schnitt-Simulation hängen sequenziell voneinander ab.
- **Robustheit bleibt der übliche Stolperstein** inkrementeller 3D-Hulls (Koplanarität/Degeneriertheit) — deshalb A1/B2 immer über die vorhandene exakte Suite absichern.

---

## 5. Mess-/Vorgehensplan

Ohne Attribution optimiert man leicht das Falsche; die Runde-2-Notiz („EventPipe-Sampler zeigt GC-Polls/Copy-Loops") zeigt, dass das schon passiert ist.

1. **Micro-Benchmarks** aus `docs/native-speed-plan.md` Schritt 0 anlegen (`bench/Stykker.NanoCut.Bench` hat aktuell nur den Szenen-Runner, kein BenchmarkDotNet):
   - B1: 10⁷ `orient3d`/Plane-Side-Tests (zufällig + fast-degeneriert) → **Filter-Trefferquote**,
   - B2: `Int384`-Multiply/Compare-Kette → **Anteil des exakten Fallbacks**,
   - B3: Face-Split 10⁵ Faces,
   - B4/B5: Box − Kugel, Beispiel 1; B6: Sägeblatt; B7: Schleifen; B8: Axial-Envelope.
2. Zähler einbauen: Anzahl `Filter.Uncertain`, Anzahl exakter `Int384`-Auswertungen, `GC.GetTotalAllocatedBytes`, Face-/Fragment-Zahlen pro Schritt.
3. Dann in dieser Reihenfolge umsetzen und jeweils verifizieren:
   - **B1 + A1 + B2** (klein, bitgleich),
   - **C1–C6** (Allokationen),
   - **B3/D1/D2** (SIMD, BVH-Cache, Int384-Fastpath).
4. Jeden Schritt gegen `dotnet test` (Referenz + Oracle) und, wo möglich, gegen die STL-Volumes des Bench (ΔV = 0) prüfen.

---

## 6. Nicht durchgeführt

- Kein `dotnet build`, kein `dotnet test`, kein Benchmark-Lauf, kein App-Start (Tests laufen in einer anderen Session).
- Keine Datei im Repo verändert außer dieser Notiz; kein Commit/Push.
# Performance-Audit

Stand: 2026-10-02, Branch `perf-round-3`. Ausgangspunkt war Commit `f2229f2` (nach der zweiten Optimierungsrunde).

Dieses Dokument ist das Ergebnis einer Analyse-**und** Umsetzungsrunde. Jede Zahl ist auf dieser Maschine gemessen
(AMD Ryzen 7 5800X3D, Windows 11, .NET 10.0.401, Release, `TieredPGO`, Workstation-GC), nicht aus der Doku
übernommen. Wo eine Behauptung nicht standhielt, steht das hier, mit dem Messweg.

**Vier der zunächst angenommenen Hebel waren falsch oder weit kleiner als angenommen.** Sie sind unten mit dem
Grund aufgeführt, weil eine Optimierungsrunde, die sie wiederholt, Zeit verschwendet.

## 1. Ausgangsbaseline und Ergebnis

Szenen aus `bench/scenes/`, Median über 3 Zeiten pro Prozess × 3 Prozesse:

| Szene | vorher | jetzt | Faktor | Allokation vorher → jetzt |
| --- | ---: | ---: | ---: | ---: |
| `ball-small` (16 Schritte) | 189 ms | 157 ms | 1,20× | 19 → 17 MB |
| `ball-medium` (96) | 632 ms | 578 ms | 1,09× | 485 → 424 MB |
| `pocket-profile` (372) | 2 344 ms | 2 145 ms | 1,09× | 2 151 → 1 949 MB |
| `pocket-large` (876) | 5 426 ms | 5 018 ms | 1,08× | 5 081 → 4 599 MB |

Die Volumina sind in allen Szenarien bit-identisch (`90947.264700`, `84860.612637`, `3603.390950`, `10188.091001`).
Bei `ball-small` und `ball-medium` ist ein Teil des Gewinns Harness-Korrektur (Abschnitt 3), nicht Kernel-Arbeit.

Der 2D-Pfad ist davon nicht betroffen: Das Gear-Szenario der Demo liegt unverändert bei ~87 s
(`Process2.Cut` 34,8 s, `Solid.Extrude` 52,7 s).

## 2. Was am meisten zählt: der Bench misst nicht den Produktionspfad

`bench/` misst pro Schritt `ConvexHull3.Compute` der beiden Schrittpositionen und zieht die ab. Das ist eine
synthetische Worst-Case-Workload. Der Produktionspfad macht etwas anderes:

| | Zeit | Hulls | Cuts | Volumen |
| --- | ---: | ---: | ---: | ---: |
| Produktion `Process3.Cut` | **357 ms** | 7 | 1 | 84860,614 mm³ |
| Bench-Harness | 5 018 ms | 846 | ~110 | 84860,613 mm³ |

15,7× Unterschied für dasselbe Ergebnis. Zwei Gründe:

1. **`Process3` sampelt Translation nicht.** `Process3.Sample` (`Process3.cs:198`) unterteilt nur, wenn
   `Angle(p0, p1) != 0`, also bei Rotation. Für eine Translationsbahn macht es **einen** exakten
   Minkowski-Sweep pro Segment. Der Bench erzwingt dagegen 846 künstliche 0,75-mm-Schritte.
2. **`Process3` cacht den Hull pro Orientation** (`Process3.cs:53-58`). Reine Translation hat genau eine
   Orientation, also wird der Hull einmal gebaut und danach nur verschoben.

Folge: Der Hull-Anteil, den der Harness misst, ist in der Produktion nahezu null. Wer den Harness optimiert,
optimiert die halbe Laufzeit und bekommt wenig davon in der Anwendung zurück.

**Konsequenz für weitere Arbeit:** Der Boolean-Anteil ist das, was beide Pfade teilen — dort sind die Änderungen
dieser Runde gelandet. Neue Bench-Szenen für `Process3`/`Process2` fehlen noch (Abschnitt 7).

## 3. Der Mess-Harness war defekt

Vier getrennte Fehler, jeder für sich klein, zusammen so wirksam, dass die alten Zahlen nicht vergleichbar sind:

| Fehler | Wirkung |
| --- | --- |
| `HullMs`/`BooleanMs` waren `static`, der Warm-up addierte mit hinein | Die Phasenaufteilung las 12 936 ms gegen einen Lauf von 5 689 ms. Sie war um Faktor 2 falsch. |
| `Solid.Box` wurde zwischen `StartNew()` und `Restart()` erzeugt | Die Initialisierung war in `totalMs` enthalten, obwohl nur der Schnitt versprochen ist. |
| Das Harness fütterte `ConvexHull3` mit einem `Select`-Enumerable | Jeder Lauf maß seine eigene Allokation mit. |
| Der Warm-up fuhr die Szene einmal | Kurze Szenen wurden teilweise auf Tier 0 gemessen. Jetzt mindestens fünf Durchläufe, dann wiederholen bis drei aufeinanderfolgende innerhalb 10 % liegen. |

`bench/run.py` machte `import resource` und **lief unter Windows gar nicht**. Das ist der Grund, warum
`bench/results-2026-10-02.html:160` „CPU-Zeit wurde unter Windows nicht erfasst" vermerkt.

Berichtet werden jetzt Median, Minimum, Maximum und Streuung statt `min()` über drei Läufe, und `results.json`
liegt neben `results.md`, damit ein Bericht aus Daten erzeugt und nicht abgetippt werden kann.

**`ball-small` fiel dadurch von 186 ms auf ~157 ms Median — das ist Harness-Overhead, keine Kernel-Arbeit.**
Alle Zahlen vor diesem Commit sind mit allen nach diesem Commit nicht vergleichbar.

## 4. Messwerte, die die Prioritäten ändern

`KernelStats` zählt pro Thread hinter einem `Counting`-Flag. Auf `pocket-profile` (372 Schritte):

| Kennzahl | Wert | Bedeutung |
| --- | --- | --- |
| `Filter`-Aufrufe | 1 337 119, davon **10,26 % unsicher** | `docs/processes.md:186` behauptet „< 1 % exakter Pfad". Das ist um eine Größenordnung falsch. |
| Seiten-Tests | 1 262 952 Gitter / 286 827 exakt (**18,5 % exakt**) | Der Filter arbeitet beim homogenen Zweig gut; die 38 % aus einer früheren Messung waren ein Zählfehler. |
| `hull-Above` | 22 483 138 Aufrufe, **0,65 % unsicher** | Der Hull-Filter ist gesund. Der Fehler liegt nicht dort. |
| Hull | 857 088 Dreiecke → 839 238 Faces | Bei einer Kugel bleiben 2 256 von 2 304 Dreiecken einzeln: Quadrate und Meridiane einer Kugel sind nicht koplanar. |
| Kantenebenen | 2 535 576 | Pro Hull 6 813 Stück. |
| Merging | 19 739 Pässe über 630 250 Keys | ~53 Pässe pro Boolean. Bestätigt den quadratischen `FaceMerge`-Punkt (Abschnitt 6, B3). |
| Kandidaten | 152 923 geholt, 83 379 getestet (**54,5 %**) | Der Rest wird von `Separated` verworfen. |

Der Zähler kostet 9 ms auf einem 2,3-s-Lauf (0,4 %). Er darf aber **nicht** in `Point3.SideOf` stehen: dort hat
allein ein `if` 9 % des ganzen Bench gekostet (2 134 → 2 326 ms), weil es das heißeste Blatt des Kernels ist.
Die Aufteilung wird deshalb einmal pro Aufrufstelle gezählt, nicht pro Vertex.

## 5. Was umgesetzt wurde

### 5.1 `Plane3.EdgePlane` — 2,30×, dann mit der Seitensuche 1,57×

`Face3.FromGrid` baute jede Kantenebene als `Plane3.FromPoints(a, b, a + e_k).Canonical()`: drei 128-Bit-GCDs
und ein allgemeines Kreuzprodukt. Die Normale einer Randebene ist aber `(b - a) × e_k`, hat also **genau eine
Nullkomponente**, die Differenzen passen in 64 Bit, und der GCD der beiden übrigen Komponenten teilt `d = -n·a`
bereits. Drei 128-Bit-GCDs werden einer 64-Bit.

Interleaved A/B auf echter Hull-Geometrie (69 Mio Aufrufe je Variante, abwechselnd im selben Prozess):

| | je Kante |
| --- | ---: |
| nur Ebene (`FromPoints` + `Canonical`) | 97,9 ns |
| nur Ebene (`EdgePlane`) | 42,6 ns |
| Ebene **und** Seitensuche, vorher | 123,8 ns |
| Ebene **und** Seitensuche, jetzt | 79,0 ns |

Die Seitensuche testet nicht mehr die beiden Vertices, die konstruktionsbedingt auf der Kantenebene liegen. Bei
einem Dreieck bleibt damit **ein** Int128-Test statt drei. Kollineare Vertices sind weiterhin erlaubt: ein Vertex auf
der Ebene entscheidet die Orientierung nicht, die Suche läuft weiter wie vorher.

`Face3.FromTriangle` ersetzt `FromGrid([a, b, c])` — ein Dreieck braucht kein `Vec3[]` und keinen
interface-dispatchten Zugriff. Und `ConvexHull3.Compute` baut keine Gruppenliste mehr für Dreiecke, die allein in
ihrer koplanaren Gruppe sind: Bei einer 48-Segment-Kugel wären das 2 256 weggeworfene Listen.

Abgesichert durch `EdgePlaneEqualsGeneralCrossProductPlusCanonical` (300 000 Konfigurationen, bit-identisch gegen
`FromPoints(...).Canonical()`, inklusive der Fälle mit Nullkomponente und `a == b`).

### 5.2 `Face3` Box-Pad — etwa 6 %

`Face3` blähte jede Box um 1 nm auf, mit dem Kommentar, die Näherung exakter Vertices sei weit genauer. Sie ist es:
Ein homogener Vertex ist nur über das Double `X/W` bekannt, rund vier ulp relativ, bei `|c| ≤ 2³¹` also höchstens
1,9·10⁻⁶ nm. Das Pad ist jetzt 1·10⁻⁵ nm — Faktor fünf Reserve, fünf Dekaden weniger.

Das Pad ist_lastend: es entscheidet die BVH-Abfrage, die Außen-Entscheidung in `ProcessFace` und die
Split-Entscheidung in `Face3.Split`. Was es kostete, stand in den neuen Zählern: Nur 54,5 % der geholten
Kandidaten überstanden bis zu einem echten Test.

**Die Wirkung ist kleiner als angenommen** (Median 2 328 → 2 186 ms, Kandidaten 163 607 → 152 923, also −6 %).
Der Rest der verworfenen Kandidaten ist echte Geometrie, nicht Padding. Der Audit-Punkt war als „hoch"
eingestuft — das war zu optimistisch.

Der BVH-Test, der das abgesichert hat, prüfte selbst das Padding statt der BVH-Invariante: Er schoss einen Strahl
auf y = z = 1, der die Box nur erreichte, weil das Pad deren y-Ausdehnung auf 11 dehnte. Der Test leitet den
Strahl jetzt aus der Box ab und prüft zusätzlich, dass ein Strahl knapp neben und einer hinter der Box nichts findet.

## 6. Widerlegte und neu bewertete Punkte

| Punkt | Behauptung | Ergebnis |
| --- | --- | --- |
| **A1** | Grid-Punkte sollten im `SideOf` auch filtern; die `Int128→double`-Konvertierung sei exakt | **Falsch.** Interleaved A/B, 32,8 Mio Tests auf 1-nm-Gitterpunkten eines 80 × 60 × 20-mm-Blocks: direkt exakt 178 ms, erst filtern 472 ms — **62 % langsamer**. `Int128→double` ist keine Einzelinstrinstruktion, sondern Limb-Extraktion plus Skalierung. Der Gitter-Zweig bleibt exakt; der Kommentar im Code sagt, warum. |
| **Rotations-Sampling** | `diameter × Δθ / 2` sei quadratisch statt linear, also ~25× zu konservativ | **Falsch.** Gemessen durch Halbieren von `SweepNm` (= Δθ halbieren) am Szenario aus `ProcessTests.cs:137`. Differenzen der Stufen: 0,000012 → 0,000012 → 0,000006 → 0,000003 → 0,000001, also **linear** in Δθ. Gegenprobe am Würfel aus `docs/processes.md:173`: bei 400 µm Sweep entspricht der Overcut exakt dem erlaubten Budget. Weniger Intervalle wären reiner Genauigkeitsverlust. |
| **A2** | `pad = 1.0` nm erzeuge systematisch überflüssige Kandidaten | **Richtig, aber klein.** −6 % statt der erwarteten Größenordnung. |
| **Hull-Anteil** | Der Hull sei der Hauptkostenblock | **Im Harness ja (56 %), in der Produktion nein** (Abschnitt 2). |
| **Native Port** | C# → C++ würde den Faktor bringen | Weiterhin nein: nach zwei Runden Parität mit C++ Manifold (`native-speed-plan.md:145`). |
| **GC-Tuning** | Server-GC / gen0-Budget | `bench/README.md:112` vermerkt erfolglos, ohne dokumentierten A/B-Lauf. Unverändert offen. |

## 8. `Solid.Extrude`: der Hebel ist real, aber nicht durch die naheliegende Änderung zu erreichen

Das ist der teuerste gemessene Posten (`gear-m2-z20`: **101 555 ms**, 428 800 Faces, 39,5 GB Allokation), und die
Ursache ist eindeutig: `Solid.cs:192` ruft `FromTriangleList`, und das macht **kein Merging** — jedes Dreieck wird
allein zu einem Face3:

```csharp
foreach (var t in tris)
    if (!Plane3.FromPoints(t[0], t[1], t[2]).IsDegenerate) faces.Add(Face3.FromGrid(t));
```

Eine triangulierte Deckfläche bleibt also ein Face pro Dreieck. `ConvexHull3.Compute` hat die passende Logik bereits
(Union-Find über koplanare Nachbarn, Orient3D, Boundary-Walk) und wurde als `FacesFromTriangles` extrahiert.

**An einfachen Formen funktioniert das und halbiert die Face-Anzahl bei exaktem Volumen:**

| Form | Faces vorher | Faces nachher | ΔV |
| --- | ---: | ---: | ---: |
| Rechteck | 12 | **6** | 0,000000 |
| L-Form | 14 | **8** | 0,000000 |
| Kreis | ~1 300 | **634** | 0,000000 |
| Rechteck mit Loch | ~2 700 | **1 356** | 0,000000 |
| Kreis mit Quadrat-Loch | ~3 800 | **1 908** | 0,000000 |
| Zwei getrennte Rechtecke | 24 | **12** | 0,000000 |

**An rotierten Prismen funktioniert es nicht.** `Process3.CutPlanar` erzeugt die Prisma über
`Solid.Extrude(region, z0, z1, placement)`, also mit gerundeten, rotierten Eckpunkten. Dort liefert der Planar-Spinning-Pfad
ein falsches Ergebnis: im Testfall `ToothedDiscApproachesPlainDiscAtSlowFeed` entfernt das Sägeblatt **negative** Volumen
(−7,76 mm³, also Material hinzugefügt) statt 2,33 mm³, und der Restkörper hat 22 Faces nach 3 840 Cuts statt
akkurat zu arbeiten.

Drei Befunde daraus, alle offen:

1. **`Join` braucht eine Randkante.** Eine konvexe Hülle ist geschlossen, die Zwillernkante existiert also immer.
   Eine Dreieckssuppe aus einem Extrusat kann eine Randkante ohne Nachbar haben; `edgeTri[Key(u,v,n)]` wirft dann
   `KeyNotFoundException` (beobachtet bei `CutSpinning`, Zahnrad-Auflösung 27 003 Punkte).
2. **Zwei Dreiecke können mehr als eine Kante teilen.** Der bestehende `far`-Ausdruck nimmt dann den falschen Vertex;
   ein `far == u || far == v`-Test muss das abfangen.
3. **Der eigentliche Grund ist nicht gefunden.** Der `far`-Test und der `TryGetValue`-Test beheben die Exception,
   aber nicht das falsche Volumen. Die naheliegendste Vermutung ist, dass das Runden bei der Rotation zusätzliche
   Koplanarität erzeugt: Deckflächen- und Seitenwand-Dreiecke eines Prismas können in dieselbe Ebene fallen, und die
   Boundary-Walk liefert dann ein Polygon, für das `Face3.FromGrid` (das Konvexität voraussetzt und stillschweigend den
   Rest verwirft) kein gültiges Face liefert.

**Empfehlung:** Die Änderung ist der richtige Hebel und für achsenparallele Extrusionen nachweislich korrekt, sie ist
aber ohne Ursachenklärung nicht einspielbar. Vor dem nächsten Versuch: `FromGrid` muss die Konvexität der gelaufenen
Boundary *prüfen* statt sie vorauszusetzen — dann wird ein ungültiges Polygon zu einem Fehler statt zu stillem Unsinn.
Das ist unabhängig vom Performance-Thema die richtige Härtung.

Die Extrusion ist auch nur eine Seite: `Triangulator2.ConvexParts` ist O(n²) im Ear-Clipping über 107 k Punkte, und die
428 800 Faces sind nur die Hälfte des Problems — die andere Hälfte ist die Zeit in der Zerlegung selbst.

## 9. Was jetzt ansteht, nach Messung geordnet

| # | Punkt | Erwartung | Warum jetzt |
| --- | --- | --- | --- |
| 1 | **`Face3.FromGrid` muss Konvexität prüfen.** Es nimmt sie an und verwirft den Rest der Schleife stillschweigend, wenn ein Randdreieck auf die Kante fällt. Genau das macht die coplanare Zusammenlegung (Abschnitt 8) unbrauchbar. | Härtung | Kleine, lokale Änderung, macht den Extrude-Hebel überhaupt erst spielbar. |
| 2 | **`Triangulator2.ConvexParts`** — Ear-Clipping O(n²) über 107 k Punkte. | hoch | Die andere Hälfte der Extrusions-Zeit, unabhängig vom Merging. |
| 3 | **B1: Bandindex für `FindSplitPoints`** (`BooleanKernel.cs:227-239`). Die Doppelschleife filtert nur über x und y, also Faktor ~√n; ein 1-D-Bandindex über `y0` bringt Faktor 10–500. | sehr hoch im 2D-Kern | Treibt die 39 GB Allokation im Gear-Szenario. Größter verbleibender Einzelposten. |
| 4 | **B3: `FaceMerge` inkrementell** (`FaceMerge.cs:29-79`). `touched[i]` erzwingt *k*−1 Pässe für einen Streifen aus *k* Stücken, jeder Pass baut das 48-Byte-Key-Dict neu. Vertex-IDs statt Geometrie-Keys machen den Schlüssel 8 Byte. | hoch | 53 Pässe pro Boolean gemessen. |
| 5 | **C1: exakte Ganzzahl-Translation.** `Process3.cs:135` → `Solid.Transform` rechnet pro Dreieck `Plane3.FromPoints` + binären GCD; `v' = v + t`, `d' = d − n·t` wäre exakt und O(Vertices). | mittel-hoch | Betrifft den Translations-Fastpath, also den G-Code-Pfad der Demo. |
| 6 | **D1: exakter Face-Sweep für Rotation.** Zwei-Posen-Hull übercutet linear in Δθ; ein Face-Sweep analog zum 2D-Kanten-Sweep würde die Abtastdichte stark senken. | hoch, aber groß | `docs/processes.md:199` benennt es selbst. Eigene Aufgabe. |
| 7 | **B2: `Int384`** ohne `stackalloc`/`Span`, mit `Int128`-Fast-Path. `Int256` zeigt im selben Repo das richtige Muster. | offen | Nur sinnvoll, wenn der exakte Pfad teuer bleibt. Die Zähler sagen: 18,5 % der Seiten-Tests sind exakt, der Filter fällt 10 % der Fälle durch. |
| 8 | **B7: `[MethodImpl(AggressiveInlining)]`** auf den heißen Blättern — im ganzen `src/` gibt es kein einziges. | 5–15 % auf prädikatlastigen Schleifen | Mechanisch, kein Architekturentscheid. |
| 9 | **C2/C3/C4:** `Overlaps(Solid, Solid)` ist O(F) statt O(1) (`bounds[w]` existiert bereits); `BoundsNm` baut ein `Vec3[]` pro Vertex; feste `Batch = 8` ist semantisch frei, weil `(A\B)\C = A\(B∪C)`. | mittel | |
| 10 | **A3: `Bvh3.cs:72` `stackalloc int[256]`** = 1 024 Byte Null-Memset pro Query bei einer tatsächlichen Tiefe von ~11. `[SkipLocalsInit]`. | niedrig | |
| 11 | **A4: BVH liefert `List<Face3>`** statt `int[]`-Indizes; der Konsument dereferenziert jedes Face erneut, obwohl `_boxes` flach vorliegen. | mittel | |

## 8. Reproduktion

```bash
# Alle Szenen, mit CPU-Zeit auf Windows:
python bench/run.py bench/scenes/pocket-profile.json --engines nanocut,manifoldsharp --repeat 5

# Gegen zwei Stände messen (min statt Median, 5 Zeiten × 6 Prozesse):
python <ab-runner>.py bench/scenes/pocket-profile.json f2229f2 f673c60 5 6

# Phasenaufteilung und Kernel-Zähler:
dotnet bench/Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll \
       <expanded.json> <out> nanocut --warm --repeat 3 --stats

# Hull in Quickhull- und Face-Build-Phase zerlegen:
dotnet <dasselbe> <expanded.json> <out> nanocut --hull-breakdown
```

Ausschlaggebend für diese Runde war ein interleaved A/B-Skript, das beide Stände im selben Prozess abwechselnd
misst: die Streuung zwischen Prozessen liegt bei 10–13 %, was normale Vorher/Nachher-Vergleiche unter dieser
Schwelle wertlos machte.
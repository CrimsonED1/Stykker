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
| **`gear-m2-z20`** (Demo-Gear) | **101 555 ms** | **~41 000 ms** | **2,48×** | 39,5 → 39,5 GB |

Die Volumina sind in allen Szenarien bit-identisch (`90947.264700`, `84860.612637`, `3603.390950`, `10188.091001`,
`12312.529413`). Bei `ball-small` und `ball-medium` ist ein Teil des Gewinns Harness-Korrektur (Abschnitt 3), nicht
Kernel-Arbeit. Das Gear-Szenario hat über die langen Läufe selbst ~7 % Streuung (40,8 / 43,8 / 40,8 s); die
2,48× sind deutlich ausserhalb davon.

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

## 8. `Solid.Extrude`: umgesetzt, 1,18× auf dem teuersten Posten

Das ist der teuerste gemessene Posten (`gear-m2-z20`: **101 555 ms**, 428 800 Faces, 39,5 GB Allokation), und die
Ursache war eindeutig: `Solid.cs:192` rief `FromTriangleList`, und das machte **kein Merging** — jedes Dreieck wurde
allein zu einem Face3, also blieb eine triangulierte Deckfläche ein Face pro Dreieck.

**Der erste Versuch schlug fehl und war lehrreich.** Drei Dinge passierten in falscher Reihenfolge:

1. Der erste Versuch baute die Gruppierung ein, und der Saw-Blade-Test entfernte **negatives Volumen** (−7,76 mm³
   statt +2,33) mit 22 Faces nach 3 840 Cuts. Zurückgenommen.
2. Die eigentliche Ursache war woanders: `Face3.FromGrid` **setzt Konvexität voraus**, statt sie zu prüfen. Die
   Seitensuche stoppte beim ersten Vertex neben der Ebene, also bekam ein danach konkaves Polygon ein Face, dessen Ebene
   nur die Ecken davor enthielt — stillschweigend. Das ist jetzt behoben (`Face3Tests` sichert es ab), kostet messbar
   nichts (2257 → 2263 ms) und ist auf 12 Vertices pro Kante begrenzt, weil vollständig O(n²) wäre.
3. Mit der Härtung zuerst wurde die Gruppierung sicher: eine Gruppe, die zu einem nicht-konvexen Loop schließt, wirft
   jetzt einen Fehler statt ein verstümmeltes Face zu erzeugen.

**Der zweite Teil der Ursache: die Boundary-Walk konnte nur einen Loop.** Eine triangulierte Deckfläche **mit Löchern**
ist koplanar, ihre Dreiecke gruppieren also zu *einer* Gruppe — deren Boundary aber aus der Außenkontur **plus einer
Kontur pro Loch** besteht. Deshalb blieb das Zahnrad bei exakt 428 800 Faces: sein 20-Zahn-Profil hat 68 Löcher. Jetzt
läuft die Boundary als mehrere Loops, jeder wird ein eigenes Face (`Face3` kann keine Löcher, mehrere koplanare Faces
aber sehr wohl).

Ergebnis (alle Volumina bit-identisch, ΔV = 0,000000):

| Form | vorher | jetzt |
| --- | ---: | ---: |
| Rechteck | 12 | **6** |
| L-Form | 20 | **14** |
| Kreis | 2 524 | **634** |
| Rechteck mit Loch | 1 808 | **456** |
| Kreis mit Quadrat-Loch | 2 544 | **640** |
| Zwei getrennte Rechtecke | 24 | **12** |

`gear-m2-z20`: **105 325 → 89 443 ms, Faktor 1,18.** Die verbleibenden 214 656 Faces sind die Seitenwände, die der
Involutenflanke folgen und also tatsächlich nicht koplanar sind; nur die 214 144 Deckflächen-Dreiecke können kollabieren.

`pocket-profile` unverändert (2282 → 2275 ms, Boolean 975 → 973 ms).

**Nachtrag aus dem Review (2026-10-03).** Zwei Aussagen dieses Abschnitts hielten nicht, beide sind behoben:

- *Löcher.* Eine Deckfläche mit Loch wurde zu einer Außenfläche, die **über** dem Loch liegt, plus einer umgekehrt
  orientierten Lochfläche darunter. Das Volumen stimmt (die beiden Schichten heben sich auf), deshalb sah ΔV = 0 nichts;
  aber das Netz eines extrudierten 10 × 10-mm-Quadrats mit 2 × 2-mm-Loch hatte oben 100 mm² nach oben und 4 mm² nach
  unten statt 96 mm², das Loch war im Viewer und im STL zu, und ein Quader, der nur im Loch steckt, verdoppelte beim
  Abziehen die Faces (12 → 24). Jetzt behält eine Gruppe mit einer entgegengesetzt orientierten Schleife ihre Dreiecke
  (dieses Beispiel: 24 Faces, `main` vorher 32). Die Zeilen „Rechteck mit Loch“ und „Kreis mit Quadrat-Loch“ der
  Tabelle oben gelten damit nicht mehr; Formen ohne Loch verschmelzen wie dort angegeben.
- *Konvexität.* Die Prüfung in `FromGrid` vergleicht jede Kante nur mit den ersten 12 Ecken; ein konkaves Polygon,
  dessen erste Ecken im Kern liegen, kommt durch (gebaut und belegt: 20 Ecken, Kerbe hinten). Die Gruppierung prüft
  deshalb jetzt jede Schleife selbst vollständig und exakt in O(n) (`ConvexHull3.IsConvexLoop`).
- *Woher der Gewinn beim Zahnrad kommt.* Die 69 Konturen des erzeugten Profils sind Inseln, keine Löcher (alle gegen den
  Uhrzeigersinn), und die Deckfläche ist konkav, bleibt also trianguliert. Verschmolzen werden die Seitenwände: je zwei
  Dreiecke zu einem Viereck (428 800 → 321 382 Faces).

Gemessen nach dem Merge mit `main` (Ryzen 7 5800X3D, 16 logische Kerne, gleiches Messprogramm gegen beide Stände,
Median aus 3 bzw. 30 Läufen):

| Arbeit | `main` | dieser Branch | |
| --- | ---: | ---: | ---: |
| Extrusion des erzeugten Zahnrads (107 k Profilpunkte) | 47 791 ms | 803 ms | 60× |
| Zahnrad erzeugen (Zahnstange, `Process2.Cut`) | 36 648 ms | 37 231 ms | gleich |
| `pocket-large`, 876 Schritte | 3 852 ms | 3 774 ms | 1,02× |
| Extrusion Evolventenrad (Profilseite der Demo) | 17 ms | 15 ms | gleich |
| Zahnrad − Bohrung − Scheibe | 64–69 ms | 60–65 ms | gleich |

Alle Volumina gleich (die Extrusion bis auf die letzte Stelle der double-Summe, weil die Faces anders aufgeteilt sind).
Die 60× kommen fast ganz aus dem Rasterindex im Ohrentest (Abschnitt 2D-Kern), nicht aus dem Verschmelzen.

**Was davon bleibt:** Die Extrusion ist nur die eine Hälfte. `Triangulator2.ConvexParts` ist O(n²) im Ear-Clipping über
107 k Punkte, und die Seitenwände bleiben bei ~215 k Faces. Beides ist der nächste Schritt, nicht dieser.

## 9. B1 (Bandindex für `FindSplitPoints`): widerlegt, nicht umgesetzt

`BooleanKernel.cs:227-239` sah nach dem klassischen quadratischen Paar-Scan aus. Ein Bandindex über `y0` mit
`G = clamp(⌈√n⌉, 8, 512)` Bändern wurde implementiert (Counting-Sort über die Bänder, Deduplizierung über Stempel,
beide billigen Filter zusätzlich). **Das Ergebnis ist schlechter, nicht besser:**

| Variante | `gear-m2-z20` |
| --- | ---: |
| Original (x-Scan mit `break`) | ~41 000 ms |
| Bandindex, immer | 144 273 ms |
| Bandindex nur ab 512 Kanten | 146 553 ms |

Der Grund ist die Zeile, die ich beim Lesen als Filter übersehen hatte:

```csharp
if (q.A.X > p.B.X) break;
```

Die Kanten sind nach `A` sortiert, und `A.X` ist das Minimum der Kante. Sobald `q.A.X` das Ende von `p` passiert,
**bricht die innere Schleife ab** — sie läuft nicht weiter bis `n`. Der Scan ist dadurch weit unter O(n²), und der
Bandindex kann diesen Abbruch nicht nachbilden: seine Kandidaten sind nicht x-sortiert, also bleibt nur `continue`,
und er besucht mehr Paare als der Scan, für den er gebaut wurde.

Zwei Fehler in meiner Implementierung kamen obendrauf, beide von den Tests gefangen:

* Der Counting-Sort war falsch. `binStart[lo+1]++; binStart[hi+1]++` mit anschliessender Präfix-Summe lässt die Bänder
  **zwischen** `lo` und `hi` leer, eine breite Kante ist also nur in ihrem ersten und ihrem letzten Band auffindbar.
  Korrekt ist `binStart[b+1] = binStart[b] + (Präfix von diff über 0..b)`.
* `inBin` war auf `n` dimensioniert, obwohl eine bandübergreifende Kante mehrfach belegt.

Die Lehre ist dieselbe wie bei A1 und beim Rotations-Sampling: „quadratische Doppelschleife" heisst nicht
„langsam". Bevor eine asymptotische Verbesserung eingebaut wird, muss der bestehende Abbruch gelesen werden.

**Konsequenz:** Punkt B1 ist gestrichen. Der verbleibende 2D-Kern-Posten ist nicht die Paarsuche, sondern die
Allokation: `Graph.Outgoing` allokiert ein `int[]` pro Vertex und `faceEdges` eine `List<int>` pro Face (Punkt B4 im
Abschnitt 10), und `RemoveCollinear` kopiert die Punktliste pro Sweep-Stück (Punkt B6).

## 10. Was jetzt ansteht, nach Messung geordnet

| # | Punkt | Erwartung | Warum jetzt |
| --- | --- | --- | --- |
| 1 | **B4: Allokation im 2D-Kern.** `Graph.Outgoing` allokiert ein `int[]` pro Vertex, `faceEdges` eine `List<int>` pro Face (`BooleanKernel.cs:348`, `:384`). `counts`/`fill` als CSR-Prepass sind bereits da. | hoch | Nach dem gescheiterten B1 ist die Allokation der verbleibende 2D-Posten: 39,5 GB im Gear-Szenario. |
| 2 | **`MergeConvex`** (`Triangulator2.cs:235-265`): eine `List<Vec2>` pro Dreieck, `owner.Keys.Where(...)` mit LINQ, `(Vec2, Vec2)`-Tuple-Keys. 363 ms nach dem Ear-Clipping-Fix, war der zweitgrößte Posten in `ConvexParts`. | mittel-hoch | Direkt nach dem Ohr-Test, gleiche Datei, mechanisch. |
| 3 | **`Bridge`** (`Triangulator2.cs:113-139`): sortiert pro Loch das **gesamte** Brückenpolygon per `OrderBy` mit Lambda, und `otherHoles.Any(h => Blocked(v, mp, h.Points.ToArray()))` kopiert pro Kandidat jedes verbleibende Loch-Array. Bei 68 Löchern und 107 k Punkten ist das der nächste Kandidat. | mittel | Jetzt sichtbar, weil `EarClip` von 55 s auf 0,18 s gefallen ist. |
| 4 | **`Triangulator2.ConvexParts`** selbst ist jetzt 544 ms; davon 363 s `MergeConvex`. Weitere Zersetzung des Ear-Clippings bringt nichts mehr. | — | Abgeschlossen, siehe Abschnitt 8. |
| 5 | **B3: `FaceMerge` inkrementell** (`FaceMerge.cs:29-79`). `touched[i]` erzwingt *k*−1 Pässe für einen Streifen aus *k* Stücken, jeder Pass baut das 48-Byte-Key-Dict neu. Vertex-IDs statt Geometrie-Keys machen den Schlüssel 8 Byte. | hoch | 53 Pässe pro Boolean gemessen. |
| 6 | **C1: exakte Ganzzahl-Translation.** `Process3.cs:135` → `Solid.Transform` rechnet pro Dreieck `Plane3.FromPoints` + binären GCD; `v' = v + t`, `d' = d − n·t` wäre exakt und O(Vertices). | mittel-hoch | Betrifft den Translations-Fastpath, also den G-Code-Pfad der Demo. |
| 7 | **D1: exakter Face-Sweep für Rotation.** Zwei-Posen-Hull übercutet linear in Δθ; ein Face-Sweep analog zum 2D-Kanten-Sweep würde die Abtastdichte stark senken. | hoch, aber groß | `docs/processes.md:199` benennt es selbst. Eigene Aufgabe. |
| 8 | **B2: `Int384`** ohne `stackalloc`/`Span`, mit `Int128`-Fast-Path. `Int256` zeigt im selben Repo das richtige Muster. | offen | Nur sinnvoll, wenn der exakte Pfad teuer bleibt. Die Zähler sagen: 18,5 % der Seiten-Tests sind exakt, der Filter fällt 10 % der Fälle durch. |
| 9 | **B7: `[MethodImpl(AggressiveInlining)]`** auf den heißen Blättern — im ganzen `src/` gibt es kein einziges. | 5–15 % auf prädikatlastigen Schleifen | Mechanisch, kein Architekturentscheid. |
| 10 | **C2/C3/C4:** `Overlaps(Solid, Solid)` ist O(F) statt O(1) (`bounds[w]` existiert bereits); `BoundsNm` baut ein `Vec3[]` pro Vertex; feste `Batch = 8` ist semantisch frei, weil `(A\B)\C = A\(B∪C)`. | mittel | |
| 11 | **A3: `Bvh3.cs:72` `stackalloc int[256]`** = 1 024 Byte Null-Memset pro Query bei einer tatsächlichen Tiefe von ~11. `[SkipLocalsInit]`. | niedrig | |
| 12 | **A4: BVH liefert `List<Face3>`** statt `int[]`-Indizes; der Konsument dereferenziert jedes Face erneut, obwohl `_boxes` flach vorliegen. | mittel | |
| 13 | **B6: `RemoveCollinear`** kopiert die Punktliste und benutzt `RemoveAt` (O(n)-Memmove); `Process2.AddCcw` ruft es pro Sweep-Stück. | niedrig | |

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
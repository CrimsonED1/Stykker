# Performance-Audit

Stand: 2026-10-02, nach der zweiten Optimierungsrunde (`bench/results-2026-10-02.html`, Commit `7b83ecf`).
Gegenstand ist eine reine Analyse: **es wurde kein Produktionscode geändert.** Alle Zahlen unten sind auf dieser
Maschine gemessen, nicht aus der Doku abgeleitet. Punkte, die nur aus dem Code gelesen und nicht gemessen sind,
sind als *Hypothese* markiert und mit dem jeweiligen Messweg versehen.

Messumgebung: AMD Ryzen 7 5800X3D, Windows 11, .NET 10.0.401, Release, `TieredPGO=true`, Workstation-GC.

## 1. Baseline

| Szenario | Zeit | Allokation | GCs | Faces am Ende |
| --- | ---: | ---: | ---: | ---: |
| `bench/scenes/ball-small.json` (16 Schritte) | 186 ms | 19 MB | 0 | – |
| `bench/scenes/pocket-large.json` (876 Schritte) | 5 689 ms | 5 082 MB | 150 gen0, 53 gen1 | – |
| Gear-Szenario (Demo „Gear generation", m = 2, z = 20) | **76 531 ms** | **39 423 MB** | 1 593 / 1 526 / 1 433 | **428 800** |

`ball-small` und `pocket-large` decken sich mit `bench/results-2026-10-02.html` (186 ms bzw. 5 816 ms). Die
beiden `best of 3` stimmen also überein, die Reproduzierbarkeit ist gegeben.

### 1.1 Der teuerste Posten ist nicht im Kernel

Das Gear-Szenario ist der mit Abstand teuerste Ablauf der Anwendung, wird aber von **keinem** Benchmark erfasst
(`bench/scenes/` enthält vier reine Ball-Zickzack-Szenen). Aufteilung der 76,5 s:

| Phase | Zeit | Allokation | Ergebnis |
| --- | ---: | ---: | ---: |
| `Process2.Cut` | 37 384 ms | 39 049 MB | 107 328 Profil-Vektoren, 69 Konturen |
| `Solid.Extrude` | 52 664 ms | 374 MB | **428 800 Faces** |

Zwei Befunde:

- **`Solid.Extrude` erzeugt 428 800 Faces für ein 20-Zahn-Rad** — vier Faces pro Profil-Vektor. Ursache ist
  `Solid.cs:184` → `Triangulator2.ConvexParts`: das Ear-Clipping ist O(n²) über 107 k Punkte, danach läuft
  `MergeConvex` mit eigenem Bucketing. Das ist der größte Einzelposten der Anwendung.
- **`Process2.Cut` allokiert 39 GB für 2 560 Intervalle**, also rund 15 MB pro Intervall. Ursache ist der
  quadratische Kreuzungsloop im 2D-Kern (Punkt B1).

Für den Vergleich: `pocket-large` liegt bei 5,8 MB Allokation pro Schritt, `Process2` bei 15 MB pro Intervall.

## 2. Widerlegte Hypothese: das Rotations-Sampling ist nicht zu fein

`Process3.cs:199` prüft

```csharp
diameter * angle / 2 <= maxDeviationNm && MaxDeviation(...) <= maxDeviationNm
```

`diameter` (`Process3.cs:172`) ist die AABB-Diagonale des Werkzeugs, und die Schranke wächst **linear** in
Δθ. Für eine Rotation um eine feste Achse ist die Abweichung zwischen dem Zwei-Posen-Hull und dem wahren Sweep
die Kreisbogensagitta `r·(1 − cos(Δ/2)) ≈ r·Δ²/8`, also **quadratisch**. Daraus folgt die Erwartung, dass das Gate
um etwa Δ/8 zu konservativ ist und ~25× weniger Intervalle reichen müssten.

Gemessen wurde das durch Halbieren von `SweepNm` (das halbiert Δθ) am Szenario aus
`tests/…/ProcessTests.cs:137` (Balken 4 × 1 × 6 mm, 3° um z, Block 5 × 6 × 1 mm):

| `SweepNm` | Intervalle | Volumen mm³ | ΔV zur feinsten Stufe |
| ---: | ---: | ---: | ---: |
| 4 000 | 64 | 26,289042958 | – |
| 2 000 | 128 | 26,289066895 | 0,000024 |
| 1 000 | 256 | 26,289078885 | 0,000036 |
| 500 | 512 | 26,289084886 | 0,000042 |
| 250 | 1 024 | 26,289087822 | 0,000045 |
| 125 | 2 048 | 26,289089344 | 0,000046 |

Differenz der Stufen: 0,000012 → 0,000012 → 0,000006 → 0,000003 → 0,000001. Das ist **linear** in Δθ, nicht
quadratisch. Die Prognose ist falsch, das Gate ist korrekt dimensioniert.

Gegenprobe an der 8-mm-Würfel-Rotation aus `docs/processes.md:173`: Bei 400 µm Sweep beträgt der Overcut
0,993 mm³; das entspricht einer mittleren Überfräsungstiefe von 0,4 mm, also exakt das erlaubte Budget.

**Folgerung:** Eine Reduktion der Abtastdichte bei Rotation wäre Genauigkeitsverlust ohne Zeitgewinn. Der
richtige Hebel ist der in `docs/processes.md:199` bereits benannte: ein exakter Face-Sweep analog zum
2D-Kanten-Sweep, statt des Zwei-Posen-Hulls. Das ist ein Algorithmus-Thema, kein Stellschrauben-Thema.

## 3. Hebel Klasse A — billig, hoher Effekt, niedriges Risiko

| # | Stelle | Befund | erwarteter Effekt | Risiko |
| --- | --- | --- | --- | --- |
| A1 | `Point3.cs:72` | Grid-Punkte nehmen **keinen** Float-Filter und gehen immer über `Predicates.Side(plane, Vec3)`, also drei 128-Bit-Multiplikationen. Der exakte Zweig `:75` filtert sehr wohl. Für Grid-Koordinaten ist die `double`-Konvertierung exakt (|c| ≤ 2³¹ < 2⁵³); die Fehlerquelle ist nur die Rundung der Ebene, die `Filter.Sign` bereits abdeckt. | hoch (30–40 % des Kandidatenloops `SolidBoolean.cs:217-255`) | niedrig |
| A2 | `Face3.cs:37` | `const double pad = 1.0` bläht jede Face-Box um 1 nm auf. Exakte Vertices haben ~1e-16 relativen Fehler, bei \|c\| ≈ 10⁸ nm also ~1e-8 nm absolut. Das Padding ist rund acht Dekaden zu groß, und weil benachbarte Boxen sich dadurch um 2 nm überlappen, erzeugt es systematisch überflüssige Kandidatengetests. | hoch, aber unquantifiziert | mittel — `pad` ist an drei Stellen korrektheitskritisch (BVH-Query, `SolidBoolean.cs:139`, `:181`) |
| A3 | `Bvh3.cs:72` | `stackalloc int[256]` = 1 024 Byte, die der JIT bei jedem Aufruf nullinitialisiert. Die tatsächliche Tiefe ist ⌈log₂(n/8)⌉+1 ≈ 11. Rund 2 200 Queries pro Boolean-Operation. | mittel (reines Memset) | keiner |
| A4 | `Bvh3.cs:67`, `:82` | `Query` liefert `List<Face3>` (8-Byte-Referenzen). Der Konsument dereferenziert jedes Face erneut, obwohl `_boxes` und `_sorted` bereits flach vorliegen. Auf `int[]`-Indizes umstellen, damit der Kandidatenloop `SolidBoolean.cs:149-166` flach liest. | mittel-hoch (Lokalität) | niedrig |

**Reihenfolge:** A3 sofort (einzeilig), dann A1 (zwei Zeilen), dann A2 **mit Messung** (Zähler auf die Zahl der
Kandidatenpaare in `SolidBoolean.cs:149`), dann A4. A2 darf nur mit `OptimizationVerification2Tests` als Gate
und gemessener Kandidatenzahl geändert werden.

## 4. Hebel Klasse B — strukturell

| # | Stelle | Befund | erwarteter Effekt |
| --- | --- | --- | --- |
| B1 | `BooleanKernel.cs:227-239` | `FindSplitPoints` ist eine Doppelschleife über alle Kantenpaare. Die Filter (x-`break` bei `:234`, y-Überlappung bei `:236`) senken die Paarzahl nur um etwa √n. Ein 1-D-Bandindex über `y0` (Counting-Sort in `G` Bändern, `G = clamp(⌈√n⌉, 8, 512)`) bringt Faktor 10–500. | sehr hoch im 2D-Kern, treibt die 39 GB von P2 |
| B2 | `Int384.cs:169-189`, `:214-260` | `operator +` und `operator *` nutzen `stackalloc`-Spans: pro Addition 2 × `stackalloc` (96 Byte genullt, davon 96 toter Overhead), 12 Bounds-Checks im Konstruktor (`Int384.cs:25`), dazu je zwei `Magnitude`- und zwei `UsedLimbs`-Schleifen. Das repo-eigene `Int256.cs:9-14` zeigt das richtige Muster: benannte Felder, vollständig entrollt, keine Bounds-Checks. Zusätzlich fehlt der im Plan (`native-speed-plan.md:78`) vorgesehene `Int128`-Fast-Path für kleine Operanden. | hoch auf dem exakten Fallback, sonst gering |
| B3 | `FaceMerge.cs:29-79` | Quadratisch: `touched[i] = true` (`:67`) verhindert, dass ein Stück in einem Pass zweimal merged wird, ein Streifen aus *k* Stücken braucht also *k*−1 Pässe, und jeder Pass baut das 48-Byte-Key-Dict über **alle** Kanten neu auf. Dazu 6 `Math.Round` pro Key (`:86`) und `Point3.SameAs` (`:80-86`), das im Exact-Fall 12 `BigInteger`-Multiplikationen macht. Vertex-IDs statt Geometrie-Keys machen den Schlüssel 8 Byte und umgehen `SameAs` komplett. | hoch, besonders bei langen T-Junction-Streifen |
| B4 | `BooleanKernel.cs:348`, `:384-389` | `Graph.Outgoing` allokiert ein `int[]` **pro Vertex**, `faceEdges` eine `List<int>` **pro Face**. Bei 40 k Kanten sind das ~40 k Arrays bzw. 2,5 MB Müll pro Boolean-Operation, und `Process2` ruft den Kernel ~80× auf. `counts`/`fill` sind der CSR-Prepass bereits vorhanden. | mittel-hoch (GC-Druck) |
| B5 | `BooleanKernel.cs:312`, `:500` | `%` mit variabler Länge in `Next` und `NextKept`, also `idiv` pro Half-Edge bzw. pro Ergebnis-Half-Edge. Durch Wraparound-Bedingung ersetzbar. | mittel, sehr billig |
| B6 | `BooleanKernel.cs:535-555` | `RemoveCollinear` kopiert die Punktliste (`new List<Vec2>(pts)`), wiederholt die Schleife im `while (changed)` und benutzt `RemoveAt` (O(n)-Memmove). Wird von `Process2.AddCcw` pro Sweep-Stück aufgerufen. | mittel |
| B7 | `src/**` | **Kein einziges** `[MethodImpl(AggressiveInlining)]` im ganzen `src`-Baum, obwohl die heißen Blätter (`Predicates.Orient2D` `Predicates.cs:22`, `Vec2.Cross` `:31`, `Int384.Sign` `Int384.cs:40`) alle ≤ 20 IL-Bytes haben. | typisch 5–15 % auf prädikatlastigen Schleifen |

## 5. Hebel Klasse C — Prozessschicht

| # | Stelle | Befund | erwarteter Effekt |
| --- | --- | --- | --- |
| C1 | `Process3.cs:135` → `Solid.cs:238-248` | Der Translations-Fastpath ruft `Solid.Transform`, das **jede** Face in einen Dreiecks-Fan zerlegt und pro Dreieck `Plane3.FromPoints` + `Canonical()` (binärer GCD) berechnet. Bei Ganzzahl-Verschiebung ist das unnötig: `v' = v + t` und `d' = d − n·t` sind exakt, O(Vertices), ohne GCD. Zusätzlich wird zweimal gearbeitet: erst `Translate(placed[k], t0)`, dann `Sweep3.Translate`. | hoch im Translations-Fastpath |
| C2 | `Process3.cs:86` → `Solid.cs:346-361` | `Overlaps(Solid, Solid)` liest `BoundsMm` beider Operanden, also je ein LINQ-Durchlauf über **alle** Vertices — pro Flush und pro Workpiece. Die gepflegten Boxen in `bounds[w]` (`:36`, `:88`) reichen für einen O(1)-Test. | hoch bei mehreren Workpieces |
| C3 | `Process3.cs:148-149` | `BoundsNm(Solid)` baut `s.Vertices.Select(...).ToArray()`, also ein `Vec3[]` **pro Vertex** — bei 35 k Faces rund 3 MB pro Aufruf. Tight loop ohne Zwischenarray, besser im `Solid` cachen. | hoch (Müll) |
| C4 | `Process3.cs:100` | Feste `Batch = 8`. Weil `(A\B)\C = A\(B∪C)` gilt, ist die Batch-Größe semantisch frei — der Kommentar `:76-77` nennt sie eine Heuristik. 876 Schritte ergeben so 110 Booleans, deren jedes O(F)-Kosten trägt (2 BVHs, Face-Liste, `MergeAll`). Adaptiv nach Operanden- vs. Werkzeugvolumen flushen. | hoch bei langen Bahnen |
| C5 | `Pose2.cs:24-28` | `Pose2.Apply` ruft pro Punkt `Math.Cos`/`Math.Sin` neu. Aufrufer: `Process2.cs:70` (pro Vertex pro Pose), `:236-238` (3× pro Probe-Punkt). Bei 48 Kanten × 1 808 Steps sind das ~87 k Aufrufe mit je 2 Transzendenten. | mittel, Planar-Pfad |
| C6 | `Process3.cs:129` | `Oriented` hullt `part.Vertices` = alle Vertices mit Duplikaten (~1 400), während `partPoints[k]` (`:29`) bereits distinct ist (~344). `ConvexHull3` dedupliziert erst danach (`ConvexHull3.cs:131`). | mittel, Rotationspfad |

## 6. Hebel Klasse D — größerer Algorithmuswechsel

| # | Stelle | Befund |
| --- | --- | --- |
| D1 | `docs/processes.md:199` | Der Rotationspfad nutzt den Zwei-Posen-Hull, der linear in Δθ übercut (siehe Abschnitt 2). Ein exakter Face-Sweep analog zum 2D-Kanten-Sweep würde die Abtastdichte von 128 auf wenige Intervalle pro 45° senken und den Aufwand von O(n²) auf O(n) bringen. Das ist der im Dokument selbst benannte nächste Schritt. |
| D2 | `Process3.cs:58-67`, `GrindingSimulation.cs:262-279` | `Flush` schneidet jedes Workpiece gegen den **kompletten** Bestand; es gibt nur BBox-Reject, keine räumliche Zerlegung. `GrindingSimulation.Split` zeigt das Zellmuster bereits (x/y-Grid, Zellgröße 2 × größtes Merkmal, max. 4 096 Zellen). Bei 35 k Faces und 110 Booleans ist der quadratische Anteil dort der eigentliche Kostenblock — nicht bei kleinen Werkstücken. |
| D3 | `SolidBoolean.cs:70` → `FaceMerge.cs:11-27` | `MergeAll` baut ein `Dictionary<Plane3, List<Face3>>` über **alle** Faces beider Operanden. Da `Emit` (`SolidBoolean.cs:80`) ungesplittete Knoten unverändert durchreicht, genügt es, die Support-Ebenen der Splits zu sammeln und nur diese Gruppen zu mergen. |
| D4 | `docs/processes.md:200` | Fragmente, die sich an T-Junctions treffen, lassen sich noch nicht zurückführen. Das ist die dokumentierte Ursache für das Flächenwachstum bei langen Rotationen (35 000 Faces nach der zweiten 45°-Rotation) und damit der Grund für D1. |

## 7. Lücken in der Mess-Infrastruktur

Diese Punkte verhindern derzeit, eine Optimierungsrunde datenbasiert zu entscheiden.

| # | Stelle | Befund |
| --- | --- | --- |
| E1 | `bench/run.py:14` | `import resource` ist Unix-only. **Das Skript läuft unter Windows überhaupt nicht.** Damit ist das in `bench/results-2026-10-02.html:160` vermerkte „CPU-Zeit wurde unter Windows nicht erfasst" kein Randfall, sondern der Normalzustand unter Windows. |
| E2 | `bench/run.py:110` | Aggregation ist `min(totalMs)` über Wiederholungen, ohne Mittelwert, Streuung oder Fehlerbalken. „Bester von 3" ist systematisch optimistisch. |
| E3 | `Program.cs:67` | `HullMs`/`BooleanMs` sind `public static double` (`:105`) und akkumulieren über den Warm-up hinweg (`:39-42` benutzt dieselben Felder). Da `--warm` der Default ist, sind **beide Zahlen rund 2× zu hoch**. Gemessen: 7 762 ms Hull + 5 174 ms Boolean bei 5 689 ms Gesamtlauf. |
| E4 | `Program.cs:45-48` | `Solid.Box` wird zwischen `StartNew()` und `Restart()` angelegt, ist also im `totalMs` enthalten, obwohl `bench/README.md:11` „only the cutting" verspricht. |
| E5 | `Program.cs:110`, `ConvexHull3.cs:131` | Das Harness erzeugt pro Schritt ein `Select`-Enumerable, ein `HashSet` und ein Array **im Messbereich**. Der Bench misst seine eigenen Allokationen mit. |
| E6 | `Program.cs:79` | `stepMs` wird nach `stats.json` geschrieben, aber von `run.py` **nie** gelesen. Damit fehlen p50/p95/max und die Zuordnung „Hull wird teurer" vs. „Boolean wird teurer". |
| E7 | `docs/native-speed-plan.md:56-66` | Die Workloads B1–B8 sind **keiner** implementiert, `docs/benchmarks.md` existiert nicht, BenchmarkDotNet wurde nie eingeführt (nur eine `.gitignore`-Zeile und eine Erwähnung in `docs/plan.md:214`). |
| E8 | `Filter.cs:11` | Kein Zähler für `Filter.Uncertain`. `docs/processes.md:186` behauptet „the exact path now runs for < 1 % of the tests" — unbelegt. `docs/native-speed-plan.md:16-17` sagt ausdrücklich, der `Int384`-Anteil sei ungemessen; das gilt unverändert. Ohne diese Zahl sind B2, A1 und die Frage nach der Schranke in `Filter.cs:12` (`Rel = 1e-11`, laut Kommentar vier Dekaden über dem Worst Case) Spekulation. |
| E9 | `bench/scenes/*.json` | Alle vier Szenen sind Ball-Zickzack-Bahnen, also reine Translations-Fastpaths. Der Rotationspfad, der Planar-Pfad (`Process2`), das Schleifen und der Gear-Pfad werden nicht gemessen. Abschnitt 1.1 zeigt, dass dort die größten Kosten liegen. |
| E10 | `SolidBoolean.cs:89` | `MaxParallelism` ist öffentlich, wird aber von `Program.cs` und `run.py` nie gesetzt oder durchgereicht. Es gibt keinen Lauf mit 1/2/4/8/16 Threads, also lässt sich der Anteil der Parallelisierung an der Runde-2-Verbesserung (30,9 → 10,4 s) nicht von den algorithmischen Punkten trennen. |
| E11 | `Stykker.NanoCut.slnx` | Das Bench-Projekt ist nicht in der Solution. `dotnet build` in der CI kompiliert es nie, ein Build-Bruch fällt nicht auf. |
| E12 | `Directory.Build.props` | Enthält nur `Deterministic`, `InvariantGlobalization` und `IsTrimmable`/`IsAotCompatible`. Kein `AllowUnsafeBlocks`, kein `SkipLocalsInit`, kein `PublishReadyToRun`. In allen vier `src`-csproj steht kein einziges performance-relevantes Property. |
| E13 | `Program.cs` (Szenen) | Kurze Szenen messen teilweise Tier-0: der Warm-up fährt die Szene einmal, bei 16 Schritten erreicht kein Aufruf die Tier-1-Aufrufzählungsschwelle. Szenen sind untereinander nicht vergleichbar. |

## 8. Was ausdrücklich **nicht** das Problem ist

- **Kein nativer Port.** Nach zwei Runden liegt NanoCut auf `pocket-large` bei 5,7 s gegen 7,6 s für C++
  Manifold (`bench/results-2026-10-02.html:104-110`). `docs/native-speed-plan.md:145` schließt den Plan mit
  „A native port is not needed for speed on this workload". Die verbleibende Differenz zur C++-Version ist
  Sprachfaktor, nicht Algorithmus.
- **Kein GC-Tuning.** `bench/README.md:112` vermerkt, dass gen0-Budget und Server-GC nicht geholfen haben —
  allerdings ohne dokumentierten A/B-Lauf (siehe E-Leiste, Zeile C3 in Abschnitt 7).
- **Keine reduzierte Abtastung bei Rotation**, siehe Abschnitt 2.
- **`Int384` als Hauptkostenpunkt.** Der Kernel ist gefiltert (`Filter.cs`, `Point3.cs:75-76`), und die
  Belege nennen den Filter, nicht `Int384`. Wie groß der verbleibende Anteil ist, ist lediglich ungemessen (E8).

## 9. Empfohlene Reihenfolge

| Schritt | Inhalt | Begründung |
| --- | --- | --- |
| 1 | E1, E3, E4, E5: Mess-Harness instand setzen | Ohne belastbare Zahlen ist jede spätere Änderung eine Vermutung. E1 blockiert auf Windows alles. |
| 2 | E8: Filter-Trefferquote und `Int384`-Zeit zählen | Entscheidet B2, die Schranke in `Filter.cs:12` und A1. |
| 3 | E9: `gear` und ein Rotationsszenario als Bench-Szenen aufnehmen | Der mit Abstand größte Kostenblock (76,5 s) ist derzeit völlig unbeobachtet. |
| 4 | A3, A1 | Ein- bzw. zwei Zeilen, niedriges Risiko, hohe Wirkung auf den 3D-Kern. |
| 5 | A2 | Braucht den Kandidatenzähler aus Schritt 2, sonst Raten. |
| 6 | C1, C3, C2 | Reiner Translationspfad, exakte Arithmetik, kein Genauigkeitsrisiko. |
| 7 | B1 | Der größte Hebel im 2D-Kern und Ursache der 39 GB im Gear-Pfad. |
| 8 | B7, B5, B4 | Mechanisch, keine Architekturentscheidung. |
| 9 | B2, B3 | Eigentliche exakte Arithmetik bzw. Merge-Komplexität; braucht Schritt 2 als Entscheidungsgrundlage. |
| 10 | A4, C4 | Umbaupunkte mit mittlerem Risiko. |
| 11 | D1, D2, D3 | Eigene Projekte, klar getrennt vom Rest. D1 ist die Antwort auf Abschnitt 2. |

Schritte 1 bis 3 kosten überschaubar und sind die Voraussetzung dafür, dass Schritte 4 bis 11 nicht auf Vermutungen
aufbauen. Schritt 4 ist der erste Punkt, der ohne weitere Vorarbeit umgesetzt werden kann.

## 10. Reproduktion der Messungen

`bench/run.py` läuft unter Windows nicht (E1). Für die Werte in Abschnitt 1 wurde dieselbe Expansion wie in
`run.py:37-60` verwendet und die Bench-DLL direkt aufgerufen:

```bash
dotnet build bench/Stykker.NanoCut.Bench -c Release
dotnet bench/Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll \
       <expanded.json> <outdir> nanocut --warm
```

Die Szenen und der Gear-Aufbau (`samples/Stykker.NanoCut.Snapshot/Program.cs:39-73`) sind unverändert
übernommen. Für die Rotations- und Genauigkeitsmessung in Abschnitt 2 wurde das Szenario aus
`tests/Stykker.NanoCut.Tests/ProcessTests.cs:137` nachgebaut und nur `Tolerance.SweepNm` variiert.
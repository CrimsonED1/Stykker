# Convex kernel: where the cost actually is, and four ways out

**Datum:** 2026-10-04
**Branch/Commit der Analyse:** `worktree-keen-elm-b95ffa` @ `861c6eb` (zweite Fassung, siehe §0.1)
**Art der Arbeit:** reine Lese-Analyse. Nichts gebaut, nichts getestet, nichts committet, keine Datei außer dieser
Notiz geändert. In einer anderen Session laufen Builds und Tests.

Jede Aussage trägt eine Marke: **[geprüft]** = am Quelltext gelesen, mit Datei:Zeile belegt · **[rechnet]** = reines
Rechnen aus bereits gemessenen oder bereits belegten Größen · **[abgeleitet]** = gefolgert, nicht gemessen ·
**[zu messen]** = braucht eine Messung, bevor es gläubt wird. Die Abschnitte §7 stammen aus zwei Rechercheagenten
und tragen zusätzlich deren eigene Marken; sie sind **nicht** von mir am Quelltext geprüft, wo sie das behaupten.

---

## 0. Kurzfassung

**Erstens das Regiment, in dem die Arbeit zählt.** Der Bench-Lauf mit 793 600 Schritten ist **40× länger als das, was
Schritt 4 tatsächlich braucht.** `Step4FeasibilityFindings.md:267,412` rechnet die Schleif-Vorschau mit **~19 000
Schritten** durch, und `:283` mit **0,45 ms** Host-Binning. Die 372,6 ms Wand aus `docs/performance.md:275` sind ein
Artefakt des Bench-Laufs, nicht des Produkts. Wer die Kernel-Kosten an der 19 000-Schritt-Vorschau misst, misst die
richtige Größe.

**Dann die vier Hebel**, alle am Quelltext geprüft, in der Reihenfolge, in der ich sie angehen würde:

| # | Hebel | Fundstelle | Größenordnung | Risiko |
| --- | --- | --- | --- | --- |
| **A** | `m(m+1)/2` IEEE-Divisionen pro (Spalte, Schritt) — vorzeichenbasierte Tests | `ConvexProfile.cs:213,251,281` / `zmap.cu:1022,918,964` | ~ein Drittel der Befehle im heißesten Loop | niedrig |
| **B** | Der Kreuzungspunkt `t_ij` ist **affin in (x, y)** — `Where` ganz divisionsfrei | `ConvexProfile.cs:242-260` | ~4,5× auf dem Teilmengenlauf, der **immer** läuft | niedrig–mittel |
| **C** | Early-Out gegen die Box, die schon im Schritt-Payload steht | `zmap.cu:1107` | **~43 % der Paare** im Mittel (§10.3) | sehr niedrig |
| **D** | Pro-Schritt-Vorberechnung (Rotation, Divisionen, Steigung, Aufteilung) nach `__shared__` | `zmap.cu:1000-1030` | groß, entkoppelt m von der Spalte | mittel |
| ~~C2~~ | ~~Silhouetten-Vorprüfung~~ — **gestrichen, siehe §10.3**: für diese Geometrien nahezu wertlos | — | — | — |

**Die Reihenfolge:** C ist der billigste und der größte Hebel — vier Vergleiche gegen Floats, die der Thread ohnehin
liest, **unabhängig von der Envelope**, ohne Eingriff in die Mathematik. A und B ebenfalls. **D** ist der große
strukturelle Umbau. Wer in einem Schritt den größten Hebel auf das kleinste Risiko will, macht **C zuerst** und **D
zuletzt**.

**Was der Plan nicht weiß:** die geplante O(m)-Envelope steht in `docs/todo.md` und `docs/long-programs.md`. Sie ist
richtig, aber sie ist der **viertgrößte** von vier Hebeln — und der größte (§C) steht in keinem Dokument.

### 0.1 Korrekturen gegenüber der ersten Fassung

Diese Notiz hatte eine erste Fassung. Zwei ihrer Aussagen waren falsch; beide fielen erst beim Nachtragen der
Recherche auf. Sie bleiben hier stehen, weil das Projekt Fehler so dokumentiert.

1. **„`MaxPlanes = 16` ist durch den Lokalspeicher begrenzt, nicht durch den Algorithmus."** Falsch, und der Punkt
   ist **längst erledigt**: `Step3KernelFindings.md:275-278` und `:321` sagen bereits, dass der Satz in
   `ConvexProfile.cs:84-87` **zweimal** falsch ist — die Arithmetik lässt `dexel_subtract`'s `out[34]` weg (520 B
   statt 384 B), **und** 512 B ist die Grenze vor Volta, während `build.ps1` `sm_120` und `compute_75` fährt, beide
   ≥ 7.0, wo sie 1024 B ist. Ich habe einen geschlossenen Punkt neu entdeckt. Siehe §5.
2. **Die 289 ms Host-Anteil.** Ich hatte die Spalten der Tabelle in `docs/long-programs.md:161-165` verwechselt, weil
   der Kopf in meinem Ausschnitt fehlte. Richtig ist: **Convex wall 372,6 ms** gegen **Convex kernel 65,8 ms** und
   **Bin (host) 17,7 ms** (`docs/performance.md:275`) — es sind also **289 ms Host-Arbeit**, nicht Kernel. Für den
   793 600-Schritt-Bench gilt das. Für Schritt 4 gilt es **nicht**, siehe oben.

---

## 1. In welchem Regiment die Kernel-Arbeit zählt

**[geprüft]** `docs/performance.md:271-275`, Spaltenkopf:

| Schritte | Convex kernel | Bin (host) | Convex wall | Unbinned kernel | Ball kernel | Ball wall |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 24 800 | 3,0 ms | 1,1 ms | 16,4 ms | 480,5 ms | 0,2 ms | 1,9 ms |
| 99 200 | 7,3 ms | 1,9 ms | 47,1 ms | 1935,4 ms | 0,5 ms | 5,7 ms |
| 793 600 | 65,8 ms | 17,7 ms | **372,6 ms** | 15 546,9 ms | 3,4 ms | 38,2 ms |

**[rechnet]** Bei 793 600 Schritten: 372,6 − 65,8 − 17,7 = **289,1 ms Host-Anteil**, also **77,6 %** der Wand. Der
Rest ist das Packen in das 32-Float-Layout plus die Orchestrierung in `ApplyConvexDexels`. Ein unendlich schneller
Kernel ließe in diesem Lauf noch 355 ms stehen.

**[geprüft]** Für Schritt 4 gilt das Gegenteil. `Step4FeasibilityFindings.md:267` — die Vorschau braucht
**~19 000 Schritte** bei 1920 Körnern, 40× weniger als der längste bereits gemessene Bench-Lauf. `:283` — Host-Binning
**0,45 ms** bei 23,8 ns/Schritt. `:287` — dasselbe 17,7 ms Host ist auf dem konvexen Pfad nur 4,7 % einer 372,6-ms-Wand,
„the host does not become the bottleneck again".

**[rechnet]** Aus `Step4FeasibilityFindings.md:290-296`: bei 99 200 Schritten und 12 Ebenen kostete der gebinnte
konvexe Kernel 7,3 ms für 8,03·10⁶ (Schritt, Spalte)-Paare — **0,91 ns pro Paar**; auf 8 Ebenen umgerechnet
(1055,6 / 1935,4 = 0,545) **≈ 0,50 ns pro Paar**. Für ein Oktaeder-Korn bei 5 Schritten pro Pass, 19 000 Schritte:

| Zellgröße `h` | Spalten in der Box | Paare | **Kernel** | exakt | Verhältnis |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0,010 mm | 48 × 13 = 624 | 1,18·10⁷ | **5,9 ms** | 5,82 s | ~980× |
| 0,005 mm | 96 × 26 = 2 496 | 4,73·10⁷ | **24 ms** | 5,82 s | ~245× |
| 0,002 mm | 240 × 65 = 15 600 | 2,96·10⁸ | **148 ms** | 5,82 s | ~39× |
| 0,001 mm | 480 × 130 = 62 400 | 1,18·10⁹ | **592 ms** | 5,82 s | ~10× |

**[abgeleitet]** Die Form dieser Tabelle ist der Punkt, und `Step4FeasibilityFindings.md:310` sagt es selbst: **die
Kosten hängen am Raster, nicht am Rad.** Die Paarzahl wächst mit der Spaltenzahl in der Box, das heißt mit `1/h²`.
Was `m` steuert, ist der **Preis pro Paar** — ein Multiplikator auf einer Basis, die das Raster setzt.

**[abgeleitet]** Daraus folgt die Antwort auf die Frage, die niemand gestellt hat: **die Kernel-Arbeit lohnt sich in
der Schritt-4-Geometrie sehr wohl**, aber nicht, weil der konvexe Kernel „19× der Ball-Kernel" ist, sondern weil
0,001 mm Zellgröße ohne Hebel 592 ms kostet. §A–§D sind Multiplikatoren auf diese 592 ms. Ein Faktor 5 gesamt bringt
592 ms auf ~120 ms. Das ist der Hebel, nicht die Division für sich genommen.

---

## 2. Hebel A — die Divisionen, eine geschlossene Formel

**[geprüft]** `build.ps1:81` baut mit `-O3 -shared -std=c++17 -cudart static` plus zwei `-gencode`. **Kein
`-use_fast_math`, kein `--prec-div=false`.** Es gelten die nvcc-Defaults: **`-prec-div=true`**, jedes `/` wird zu
einer IEEE-korrekten `div.rn.f32` / `rcp.rn.f32`. (`-fmad=true` ist ebenfalls Default — das ist die „fma contraction",
von der `docs/long-programs.md:145` berichtet.)

**[geprüft]** Die drei Divisionsstellen, C# und CUDA operation-for-operation identisch:

| Stelle | C# | CUDA | Anzahl |
| --- | --- | --- | --- |
| `inv = 1 / mz` | `ConvexProfile.cs:213` | `zmap.cu:1022` | `m` |
| `tHi`/`tLo` = `-k / s` | `ConvexProfile.cs:251-252` | `zmap.cu:918-919` | `nLo · nHi` |
| `t = (aj - ai) / (bi - bj)` | `ConvexProfile.cs:281` | `zmap.cu:964` | `C(nLo,2) + C(nHi,2)` |

**[geprüft]** Die Summe ist geschlossen und **unabhängig von der Aufteilung**, weil `C(a,2) + C(b,2) + a·b = C(a+b,2)`:

```
m + nLo·nHi + C(nLo,2) + C(nHi,2)  =  m + C(m,2)  =  m(m+1)/2
```

**[rechnet]** m = 6 → **21**, m = 8 → **36**, m = 12 → **78**, m = 16 → **136** Divisionen pro Spalte und Schritt.
Zwei unabhängige Herleitungen kamen auf dieselbe Zahl: meine über die Identität oben, die des Rechercheagenten über
`~78` bei m = 12 (`§7.2`). **[abgeleitet]** Eine `div.rn.f32` kostet auf aktueller NVIDIA-Hardware rund 8–12 Befehle;
136 davon sind also **1100–1600 Befehle**, gegen etwa 3000 für die gesamte Envelope-Arithmetik im selben Aufruf. Das
ist **ein Drittel bis mehr** der Befehle im heißesten Loop.

### 2.1 Weg damit, ohne den Algorithmus zu ändern

**[abgeleitet]** Beide Stellen lassen sich durch vorzeichenbasierte Tests ersetzen, ohne die Kandidatenmenge zu
ändern:

- **`Where`** (`ConvexProfile.cs:251`): statt `tHi = min(tHi, -k/s)` genügt die Vorzeichenfrage. Für `s > 0` ist
  `−k/s ≤ tHi` äquivalent zu `k + s·tHi ≥ 0`; für `s < 0` entsprechend `k + s·tLo ≤ 0`. **`0` Divisionen.**
- **`Extremum`** (`ConvexProfile.cs:279-282`): die Bereichsprüfung `t < tLo || t > tHi` ist äquivalent zu den beiden
  Vorzeichen von `(aj − ai) − tLo·(bi − bj)` und `(aj − ai) − tHi·(bi − bj)`, gesteuert vom Vorzeichen von `bi − bj`.
  Die Division fällt weg, **außer** der Kandidat liegt wirklich im Bereich — und genau dann wird sie für die
  Envelope-Auswertung ohnehin gebraucht.

**[abgeleitet]** Das ist der billigste mögliche Diff mit dem größten Hebel: keine neue Theorie, und das dokumentierte
Ulop-Verhalten bleibt („a ulop moves an end of the range by an ulop", `ConvexProfile.cs:236-239`), nur eben ohne
Division.

**[zu messen]** Der Ertrag ist eine Hypothese. Die billigste Bestätigung kostet keinen Produktionscode:
`cuobjdump -sass src/Stykker.NanoCut.Gpu.Native/bin/nanocut_gpu.dll` und die `MUFU`-Sequenzen in
`dexel_apply_binned_kernel` zählen. Erst bauen, dann messen.

**[zu messen]** Die-global Alternative `--prec-div=false` ist **ein Flag ohne Quelltextänderung** und trifft allerdings
**auch** `1/mz` — und damit direkt die `lo`/`hi`-Grenzen. `docs/long-programs.md:159` misst die heutige Übereinstimmung
CPU/CUDA mit 4,3·10⁻⁵ … 1,3·10⁻⁴ mm gegen eine Toleranz von 8,3·10⁻⁴ mm: **Faktor 6–19 Reserve, nicht
unbegrenzt.** Die gezielte Form `__fdividef` **nur** auf `-k/s` in `Where` ist das risikoärmere und deckt den
größten der drei Posten.

---

## 3. Hebel B — der Kreuzungspunkt ist affin in (x, y)

**[geprüft]** In `ConvexProfile.Span` (`ConvexProfile.cs:200-219`) gilt, mit `inv = 1/mz`:

```
g[at]     = c · inv        ← Achsenabschnitt a_i
g[at + 1] = dot · inv      ← Steigung b_i
```

**[rechnet]** Der Achsenabschnitt lässt sich umschreiben zu

```
a_i = (d_i + m_i·T_A)/m_zi + a_z − (m_xi/m_zi)·x − (m_yi/m_zi)·y  =  R_i + a_z − P_i·x − Q_i·y
b_i = dot_i / m_zi                                          ← hängt an **keinem** x, y
```

**[rechnet]** Und jetzt die entscheidende Beobachtung. `Where` (`ConvexProfile.cs:249-253`) rechnet je Paar:

```
k = a_i − a_j   = (R_i − R_j) − (P_i − P_j)·x − (Q_i − Q_j)·y    ← spaltenabhängig
s = b_i − b_j   = dot_i/m_zi − dot_j/m_zj                        ← **spaltenunabhängig**
```

**`s` hängt nicht von x und y ab** — und der Zweig, der es konsumiert (`if (s > 0) … else if (s < 0) …`), auch nicht.
Und der Kreuzungspunkt selbst:

```
t_ij = −k / s  =  A_ij·x + B_ij·y + C_ij      mit A, B, C fix je (Schritt, Ebenenpaar)
```

**[abgeleitet]** **Jeder Kandidat `t` in `Where` und in `Extremum` ist eine affine Funktion von (x, y) mit
pro-Schritt-Koeffienten.** Pro Halbraum braucht man dafür vier Floats (`P_i`, `Q_i`, `R_i`, `b_i`), plus
`a_z`. Damit lässt sich `Where` als Kette von `fmaxf`/`fminf` auf affinen Ausdrücken schreiben — **ohne eine einzige
Division und ohne die Kopfschleife** über die Halbräume.

**[rechnet]** Auf die innerste Schleife bezogen, nach den Angaben in §7.2 für 36 Paare:

| | heute | danach |
| --- | ---: | ---: |
| je Paar | 2 sub, 2 cmp, **1 div (≈ 10 Befehle)**, 1 min/max, 2 LDS ≈ **18** | 1 mul, 2 FFMA, 1 max, 1 LDS ≈ **4** |
| `Where` gesamt | **≈ 648** | **≈ 144** |
| Divisionen | 36 | **0** |

**[geprüft]** Wichtig für die Priorität: `Where` läuft **unkonditioniert** — es hat genau einen frühen Ausstieg
(`s == 0 && k > 0`, `ConvexProfile.cs:253`) und verengt `tLo`/`tHi` sonst bis zum Ende. Nur `Extremum`'s O(m³) ist
über `tLo ≤ tHi` gefiltert (§7.2). **Was immer läuft, ist `Where`, und `Where` ist genau die Schleife, die
`docs/long-programs.md:239-241` den „queued O(m) envelope" zurechnet — aber mit dem O(m²)-Anteil, der nicht
hervorgehoben ist.**

---

## 4. Hebel C — der Early-Out, und warum er größer ist als ich zuerst dachte

**[geprüft]** `ConvexProfile.Pack` (`ConvexProfile.cs:126-136`) legt die **AABB des Sweeps** in die ersten vier Floats
jedes Schritts. `StepBins.Tiles` (`StepBins.cs:100-124`) weist den Schritt genau den Kacheln zu, die diese Box
berührt — „deliberately generous: a step lands in more tiles than it strictly needs, never in fewer".

**[geprüft]** Danach ruft **jede** Spalte der Kachel `convex_span` auf, auch wenn ihr Mittelpunkt außerhalb der Box
liegt. Es gibt keinen Vorab-Test. Die Kachel ist `kDexelTile = 16` × 16 = 256 Spalten (`zmap.cu:822`), und die
Zuweisung ist **kachel-, nicht spaltengenau**.

**[rechnet]** Deshalb ist die Verworfenquote in der Schritt-4-Geometrie erheblich. Bei `h = 0,010 mm` ist die Box des
Korns nach `Step4FeasibilityFindings.md:298` **48 × 13 = 624 Spalten**. Die Kachel ist 16 × 16. Ein Lauf von `L`
Zellen berührt im Mittel `1 + (L−1)/16` Kacheln, also

```
x: 1 + 47/16 = 3,94 Kacheln        y: 1 + 12/16 = 1,75 Kacheln
zusammen 6,89 Kacheln = 1103 Spalten, von denen 624 in der Box liegen
```

```
1 − 624 / 1103  ≈  43 %   (Mittelwert; je nach Ausrichtung 19 % … 70 %)
```

**[abgeleitet]** Der Verwurf kommt also **nicht** daher, dass das Werkzeug seine Box schlecht füllt — es füllt sie
gut (siehe §10.3). Er kommt daher, dass die Kachel 16 Zeilen hoch ist und die Box nur 13. Das ist der eigentliche
Grund, und er ist nicht schöner, sondern wichtiger: **er ist eine Eigenschaft des Binnings, nicht des Werkzeugs**, und
er trifft jede Geometrie mit einem Werkzeug, das kleiner ist als eine Kachel.

**[rechnet]** Die Wirkung auf die Schritt-4-Vorschau: die 5,9 ms der Tabelle in §1 zählen **nur** die Spalten **in**
der Box. Mit dem Early-Out wären es faktisch ~57 % davon, also **~3,4 ms statt 5,9 ms** bei `h = 0,01 mm` und
~340 ms statt 592 ms bei `h = 0,001 mm`.

**[abgeleitet]** **Der Test kostet vier Vergleiche gegen vier Floats, die der Thread ohnehin liest** (`p[0]` bis
`p[3]`, dieselbe Cache-Zeile wie der Rest des Schritt-Payloads). Er ist **korrekt**, weil der Sweep in seiner eigenen
Box liegt: eine senkrechte Gerade außerhalb der Box kann den Körper nicht schneiden. Er ist einschließend, also
konservativ in derselben Richtung wie die geschlossene Regel, die das Modell sonst benutzt.

**[rechnet]** Die Wirkung auf die Schritt-4-Vorschau: die 5,9 ms der Tabelle in §1 zählen **nur** die Spalten **in**
der Box. Mit dem Early-Out wären es faktisch ~40 % davon, also **~2,4 ms statt 5,9 ms** bei `h = 0,01 mm` und
~240 ms statt 592 ms bei `h = 0,001 mm`. **Das ist derselbe Faktor, den §A und §B zusammen erreichen sollen — für
vier Vergleiche und ohne Eingriff in die Mathematik.**

**[geprüft]** Der *exakte* Test ist die Silhouette des Sweeps und ein anderer: die Projektion eines konvexen Polytops
auf xy ist ein konvexes Polygon mit höchstens `m + 2` Ecken, also ein O(m)-Punkt-im-Polygon-Test. Er fängt auch die
Spalten **innerhalb** der Box, die **außerhalb** des Körpers liegen — beim Korn `ConvexTool.Octahedron(r)` sind das
viele, denn ein regelmäßiges Oktaeder füllt von seiner AABB nur `4r³/3 / 8r³ = 1/6 ≈ 17 %`. **[abgeleitet]** Nach der
Umstellung aus §A–§B ist der Silhouetten-Test aber derselbe Aufwands-Bereich wie das LP selbst und lohnt sich erst
danach. **Als vierte Stufe, nicht als erste.**

---

## 5. Hebel D — Pro-Schritt-Vorbereitung, und wohin sie gehört

**[geprüft]** Die Schleifenstruktur (`zmap.cu:1085-1113`): **ein Thread pro Spalte**, die Schritt-Schleife innen.
Innerhalb eines Threads gibt es also keine Wiederverwendung über Spalten hinweg.

**[geprüft]** Aber: **alle 256 Threads eines Blocks sehen denselben Schritt gleichzeitig**, weil ein Block genau eine
Kachel ist. Die Pro-Schritt-Daten sind über den Block hinweg gemeinsam.

**[abgeleitet]** §3 gezeigt, dass pro (Schritt, Halbraum) nur `P_i`, `Q_i`, `R_i`, `b_i`, `a_z` nötig sind — **4 Floats
je Halbraum**, bei m = 16 also 64 Floats = **256 Byte `__shared__`**, plus die Sortierung nach Steigung für die
Envelope. Kein Payload-Wachstum.

**[rechnet]** Die Alternative auf dem Host ist teurer, als sie aussieht. Das Payload ist heute 32 Floats (128 Byte)
pro Schritt; bei 793 600 Schritten sind das ~102 MB Upload, bei der gemessenen Rate von 8,7 GB/s
(`docs/gpu-findings.md`) also ~11,7 ms. `+4m` Floats wären 96 Floats = 384 Byte, also ~305 MB und **+23 ms** Upload.
§7.2 kommt unabhängig auf dieselbe Richtung (~+19 ms Upload gegen −12,5 ms Kernel, also netto ein Verlust).

**[zu messen]** Der Preis ist ein `__syncthreads()` pro Schritt, und die Schleife läuft pro Kachel über alle ihr
zugewiesenen Schritte. §7.2 rechnet ~377 Barrieren pro Block bei ~3400 uniformen Warp-Befehlen pro Barriere und
kommt auf unter 1 % Overhead. **Nachmessen**, weil eine Zahl im Kopf besser ist als eine Vermutung.

**[abgeleitet]** **`__constant__` ist hier falsch** — 64 KB pro SM insgesamt, ein streamendes Payload über 800 000
Schritte passt da nicht hinein.

---

## 6. Korrigiert: `MaxPlanes = 16` ist nicht durch den Lokalspeicher begrenzt

**[geprüft]** `ConvexProfile.cs:112-121` und `zmap.cu:825` begründen 16 mit dem O(m³)-Walk **und** mit „the local arrays
fit either way: … which stays under the 512 bytes a thread may use". **Beide Gründe sind bereits zerlegt**, in
`Step3KernelFindings.md:275-278` und in der Befundtabelle `:321`:

- die Arithmetik zählt `dexel_subtract`'s `out[34]` nicht mit — real sind es **520 Byte** pro Thread, nicht 384;
- **512 Byte ist die Grenze vor Volta.** `build.ps1:38-39` fährt `-gencode arch=compute_120,code=sm_120` und
  `-gencode arch=compute_75,code=compute_75`, und beide sind ≥ 7.0, wo die Grenze **1024 Byte** ist.

**[rechnet]** Meine erste Fassung dieser Notiz schloss daraus, bei m = 32 wären es 640 Byte und die Grenze sprenge.
**Das ist falsch** — 640 B liegt unter 1024 B. Die 16 sind also **nicht** durch den Lokalspeicher begrenzt.

**[abgeleitet]** Was die 16 wirklich begrenzt, ist die **Befehlszahl**, denn der Walk wächst mit `m` und der Walk
läuft pro (Spalte, Schritt). Ob die 16 nach §A–§D steigen können, ist damit **offen** und eine Frage für eine
Messung, keine für eine Rechnung.

**[geprüft]** Der zweite Teil von `Step3KernelFindings.md §f` bleibt gültig und ist hier nur wiederholt, weil er leicht
übersehen wird: der **Ball-Pfad zahlt denselben Rahmen mit**, weil `convex_span` `__forceinline__` in einen `?:`
eingezogen ist, den der Ball-Launch nie nimmt — ein Lokalspeicherrahmen wird aber statisch pro Kernel bemessen. **Jede
gemessene Ball-Zahl aus Schritt 2 stammt aus einer Binärdatei mit halb so großem Rahmen.** Das ist in §7.2
unabhängig bestätigt.

---

## 7. Ergebnisse der beiden Rechercheagenten

> **Zur Einordnung:** die Rechercheagenten hatten **keinen Netzzugang** — `web_fetch` und ausgehendes HTTP wurden
> verweigert. Es gibt **keine abgerufenen URLs und keine wörtlichen Zitate**; sie haben nichts erfunden, sondern die
> CUDA-13.4-Header auf der Platte und das Repo gelesen. Alle Hardwarezahlen sind Folgerungen, keine Messungen.

### 7.1 Algorithmus (externe Literatur)

**[Agent, „bestätigt"/„Folgerung"]** Inhaltlich richtig und in der Substanz bestätigt; **die Literatur selbst ist
unbelegt** — der Agent hatte keinen Netzzugang, und die beiden von ihm genannten URLs (cp-algorithms
`geometry/convex-hull-trick.html`, `atcoder.github.io/slope-trick/slope_trick.html`) liefern inzwischen **404**.
Wer die Quellen lesen will, muss sie selbst suchen. Der Inhalt:

**Der Hüllenalgorithmus.** Die obere Hülle von n Geraden ist eine Davenport-Schinzel-Folge der Ordnung 2, hat also
**höchstens 2n − 1 Stücke** und ist bei vorsortierten Steigungen **Θ(n)**. Der Stack hält Tripel `(a, b, start)`,
`start` = linkester Punkt, ab dem diese Gerade die_maximierende_ ist, und `start` ist streng steigend. Der Pop-Test:

```
p = S[-1];  b >= p.b
if b == p.b:                      # parallel
    if a <= p.a: i verwerfen; break     # i dominiert überall
    S.pop(); continue                   # p dominiert überall
s = (p.a - a) / (b - p.b)
if s <= p.start: S.pop(); continue      # <<< DIE POP-BEDINGUNG
```

**`<=` ist wesentlich.** `<` liefert Stücke der Länge null, und ein Stück der Länge null schiebt einen redundanten
Kandidaten `t` ein, dessen U/V-Werte bis auf ein Ulop gleich sind — genau die Falle, gegen die der Kommentar in
`ConvexProfile.cs` argumentiert. Links auf `tLo` geclippt, rechts **nicht** — rechts zu clippen nachdem man in
derselben Iteration schon gepoppt hat, ist im Float unsicher.

**Minimum der oberen Hülle über `[tLo, tHi]`.** U ist konvex PWL, also liegt das Minimum am Rand oder an einem
Hüllenknick:

```
Kandidaten = {tLo, tHi} ∪ {S[k].start : S[k].start < tHi},  Minimum darüber
```

**Und hier ist der eigentliche Gewinn, der größer ist als die O(m³) → O(m)-Reduktion:** an einem Knick
`s = S[k].start` gilt `U(s) = S[k].a + S[k].b·s` — **eine** Gerade, nicht das Maximum über m. Die innere Schleife
`convex_envelope` / `ExtremumAt`, die das O(m³) erzeugt, **verschwindet**. Ein Durchlauf über ≤ n Stack-Einträge.

**Die Falle, die man übersieht.** Das Minimum **während** des Aufbaus zu akkumulieren ist **falsch**: wird eine
Gerade später gepoppt, ist der akkumulierte Wert der Hüllenwert der bis dahin gesehenen Geraden, also ≤ dem der
endgültigen Hülle — ein **zu kleines** `low`, also mehr abgetragen als das Werkzeug je tat. Genau das verbietet der
Kommentar an `ConvexAt`. Also: zwei Durchläufe, Aufbau und Akkumulation. Bei ≤ 16 Einträgen sind das ~16 triviale
Iterationen.

**F in O(m), und das ist Vorarbeit, nicht Theorie.** `D(t) = V(t) − U(t)` ist konkav PWL, `F = {t : D ≥ 0}` ein
Intervall. Man finde das **Maximum** von `D` über die zusammengeführten Knickpunkte (zwei monotone Zeiger, O(1)
amortisiert je Punkt), laufe dann nach links und rechts bis zum ersten Vorzeichenwechsel und interpoliere **auf dem
einen Stück, das die Wurzel nachweislich enthält**. Der Nenner der Interpolation ist `D(τ_p) + |D(τ_{p+1})|` —
**keine Subtraktion ähnlicher Größen**, also besser konditioniert als der heutige Paar-Durchlauf. Und die Fehlerwirkung
ist beschränkt: eine falsch gelesene Vorzeichenlage verschiebt eine Wurzel um **eine Stückbreite**, heute um einen
beliebigen Betrag. Der Agent verweist auf `SLOPE_TRICK::find_roots` in der AtCoder Library — **unbelegt**, der Link
ist 404.

**[Agent, „Folgerung"] — asymptotisch besser, aber nicht jetzt.** Weil `s_ij(x, y)` affin in (x, y) ist, ist die
**kombinatorische Struktur der Hülle** über jeder Zelle einer Planaranordnung aus ≤ C(m,2) Geraden konstant. Pro
Schritt auf dem Host die Anordnung bauen, pro Spalte nur noch **eine Punktlokation und zwei FMA**. Das ist O(1) pro
Spalte statt O(m). Der Agent rät **ausdrücklich davon ab**, und ich auch: die Punktlokation ist der Aufwand, nicht
die Auswertung, sie kollidiert mit dem bestehenden Binning und die O(m)-Fassung holt den größten Teil des Gewinns
bei einem Bruchteil des Risikos.

**[Agent — die Silhouetten-Vorprüfung ist besser als mein Box-Test in §4]** und ich habe sie nachgeprüft: der
Support-Funktionssatz gibt den Innen-Test **ohne Polygon und ohne Punkt-im-Polygon**:

```
für jeden Halbraum i:   n_i·(p − T_A) − d_i ≤ max(0, n_i·w)
```

16 Punktprodukte, 16 `fmaxf`, eine Reduktion — **keine Zweige, keine Sortierung, kein Stack, 4-fach vektorisierbar**,
und die Schwelle `max(0, n_i·w)` ist eine **Pro-Schritt-Konstante**. Zwei Einschränkungen, beide ehrlich: der Test
ist **korrekt, aber nicht vollständig** — die Facettennormalen von `P ⊕ [0,w]` sind die m Facettennormalen **plus**
die ⊥ w zu den Kanten von P (die Kontur). Er kann also eine Spalte durchlassen, die draußen liegt; **das ist die
sichere Richtung**, ein falsches Ablehnen ist unmöglich. Und der Ertrag hängt von der Werkzeugform ab, denn der
Binning liefert bereits eine AABB-Vorprüfung. **[abgeleitet]** Die richtige Schichtung ist also: **AABB-Test (§4, vier
Vergleiche) → Silhouetten-Test (~50 Befehle) → LP.** Beide sind unabhängig von der Envelope und können sofort.

**[Agent — der wichtigste Einzelfund, unabhängig von der Envelope]** `-fmad=false`:

> nvcc defaultet auf `-fmad=true` und **kontrahiert** `p*q − r*s` zu `fmaf(p, q, −(r*s))` — ein anderes Ergebnis als
> erst beide Produkte zu runden und dann zu subtrahieren. **RyuJIT kontrahiert nicht.**

`x*y − z*w` ist genau die Form der Pop-Bedingung. Das wäre also der Ort, an dem CPU und CUDA **stillschweigend**
auseinanderlaufen — deterministisch auf beiden Seiten, aber verschieden. **[abgeleitet]** Nur als „Agent, Folgerung"
markiert, weil das nvcc-Verhalten in der Doku nicht wörtlich steht (siehe §7.3 — dort steht es für `--fmad`
immerhin als Default). **Und wichtig für die Reihenfolge:** `--fmad=false` ist ein **globales** Flag und trifft auch
`swept_span`, den Ball-Pfad, auf dem die Referenzzahlen aus Schritt 2 stehen. Es ist **kein kostenloser Gewinn,
sondern eine Entscheidung mit Neubaseline.**

### 7.2 CUDA-Leistungsverhalten

Die Punkte, die für die Reihenfolge zählen:

**Lokalspeicher ist eine bescheidene Steuer, nicht die Geschichte.**
**[Agent, „known"/„infer"]** Lokalspeicher ist threadprivat, in L1 zwischengespeichert und **interleaved**: Element `k`
von Thread *t* liegt bei `k·(warpSize·elemSize) + t·elemSize`. Eine warpweite `local[k]` mit **einheitlichem** `k`
liest deshalb **32 aufeinanderfolgende 4-Byte-Elemente = eine zusammenhängende 128-Byte-Zeile** — ein perfekt
koaleszierender Zugriff, **kein** 32-Sektor-Gather. Und genau das ist hier der Fall: in `convex_where`,
`convex_extremum` und `convex_envelope` ist der Index `k` über den Warp **einheitlich**.

**Das schreibt die Schreib-Schleife um.** **[geprüft von mir]** `zmap.cu:1024-1025`: `g[at]` mit
`at = 2 * (mz < 0.f ? nLo++ : nHi++)` — **das ist der einzige divergente Zugriff**, und er liegt in der Aufbau-
schleife, nicht im heißen Lesepfad.

**[abgeleitet von mir]** Folgerung: die Lokalspeicherarrays kosten **Issue-Slots**, nicht Latenz — der innere Lesepfad
ist ein koaleszierter L1-Zugriff pro Warp-Befehl. **[Agent]** Größenordnung 10–20 % des Kernels, **nicht** 19×.

**Belegung wird von Registern gesetzt, nicht vom Lokalspeicher.**
**[Agent, „known"]** Per-Thread-Lokalspeicher geht **nicht** in die Belegungsrechnung ein — `cudaOccupancyMaxActiveBlocksPerMultiprocessor`
nimmt nur Blockgröße, dynamischen Shared Memory und Registerzahl. Für sm_120: 1536 Threads/SM, 65 536 32-Bit-Register/SM,
255 Register/Thread.

**[Agent, „infer"]** Und hier ist die **Warnung**, die in den Bericht gehört: **vollständige Register-Promotion der
`gLo`/`gHi` wäre ein Rückschritt.** 130 Floats = 130 Register je Thread, bei 256 Threads/Block 1,88 Blöcke/SM =
**16,7 % Belegung**. Also: die Arrays *nicht* in Register zwingen, sondern nach `__shared__` legen — was genau §5
vorschlägt.

**`runtime planeCount` ist die Wurzel von allem.**
**[geprüft von mir]** `planeCount` ist ein Laufzeit-`int` (`zmap.cu:990`). Das verhindert das Unrollen, die
Register-Promotion und `#pragma unroll` auf drei separaten Schleifen. **[Agent]** Ein Template auf `planeCount` mit
Runtime-Fallback ist die eine Änderung, die §7.2-Punkt 1–3 erst möglich macht.

**Ball und konvex in getrennte `__global__`s.**
**[geprüft von mir]** `convex_span` ist `__forceinline__` in einem `?:` (`zmap.cu:1107-1108`), beide `__global__`s
rufen dasselbe `dexel_apply_column`. **[Agent]** Der **Register**-Anteil davon ist bisher nirgends notiert —
`Step3KernelFindings.md §f` hat nur den **Rahmen** erkannt. **[abgeleitet]** Registers sind das, was die Belegung
bewegt. Das würde die Ball-Zahlen aus Schritt 2 wieder vertrauenswürdig machen.

**Nicht zu tun**, alle drei mit Begründung des Agenten: Register-Promotion (s.o.), Host-Payload verbreitern (§5),
Intervall-Array coalescen. **[geprüft von mir]** Letzteres stimmt: `intervals + column * k * 2` (`zmap.cu:1094`)
hat bei k = 16 **128 Byte** Abstand zwischen benachbarten Lanes — ein Warp überstreicht 4 KB. Aber es sind nur zwei
Ladungen pro Thread am Einstieg und `2·n` am Ausstieg, amortisiert über ~377 Schritte: **~2 %**. Nicht anfassen.

**Die Messung, die alles entscheidet.**
**[Agent]** `ncu --kernel-name regex:dexel_apply_binned_kernel` mit
`sm__throughput.avg.pct_of_peak_sustained_elapsed`, `smsp__inst_executed_pipe_alu.sum`,
`smsp__inst_executed_op_local_ld.sum`, `launch__registers_per_thread`, `launch__shared_mem_per_block`, dazu
`cuobjdump -res-usage`. **Vier Zahlen entscheiden:** (1) Ist der Kernel issue-bound (> 50 %)? Dann zahlen §A–§D wie
geschätzt. (2) Wie groß ist der Anteil der Lokalspeicher-Befehle? (3) Wie viele Register? (4) Wie viele Register hat
der **Ball**-Kernel gegen den konvexen?

**[geprüft, Nachtrag 2026-10-04]** Die sechs Namen oben sind an der Quelle geprüft, gegen
`ncu --query-metrics --query-metrics-mode all` aus Nsight Compute 2026.3.0 auf einer GeForce RTX 5070 Ti
(GB203), ergänzt um `--query-metrics-collection launch` für die beiden `launch__`-Namen. Gültig sind
`smsp__inst_executed_pipe_alu.sum` und `smsp__inst_executed_op_local_ld.sum` (je 44 Suffixe, `.sum` ist
enthalten) sowie `launch__registers_per_thread` und `launch__shared_mem_per_block` (nur in der
Launch-Sammlung, ohne Suffix). **Falsch war nur `smsp__throughput.avg.pct_of_peak_sustained_elapsed`**:
eine Familie `smsp__throughput` gibt es nicht, `sm__throughput` hat dagegen acht Suffixe, weshalb das Präfix
oben korrigiert wurde. Für Frage (1) ist zusätzlich
`sm__instruction_throughput.avg.pct_of_peak_sustained_elapsed` vorhanden und die treffendere Größe, weil
„issue-bound" genau das beschreibt.

**[zu messen, Nachtrag]** Die Messung selbst ist **nicht ausgeführt**, sie ist aber jetzt ausführbar: der
Zugriff auf die GPU-Performance-Counter ist seit dem 2026-10-04 freigegeben (NVIDIA Control Panel,
*Performance Counter Permissions*), vorher brach `ncu` mit `ERR_NVGPUCTRPERM` ab. Die Tabellen für
Compute Capability 12.x bleiben leer, also wäre für Frage (3) und (4) `cuobjdump -res-usage` oder `ncu`
der einzige Weg — beides steht noch aus.

**[Agent — widerlegt, siehe §8]** Dessen Erklärung für `m^1.57` war, `nLo + nHi ≪ m`, weil viele Halbräume `|mz| ≈ 0`
hätten und den `mz == 0`-Zweig nähmen.

### 7.3 Primärquellen — was ich selbst nachgeladen und wörtlich belegt habe

**[geprüft]** Die Doku liegt ab jetzt lokal unter
`.qwen/refs/cuda-programming-guide/` im Worktree keen-elm-b95ffa (11 Seiten, ~2,1 MB, **nicht** committet), weil sie
sich sonst bei jedem Nachschlag neu durch ein Modell schicken ließe. Geladen: Programming Model, Writing CUDA
Kernels, Writing Tile Kernels, Understanding Memory, nvcc, Advanced Kernel Programming, Compute Capabilities,
C/C++ Language Extensions, Floating-Point Computation, CUDA C++ Execution Model, plus die nvcc-Compiler-Referenz.

**§2 steht damit auf Primärquelle.** Aus `05-appendices/nvcc.html`, wörtlich:

> `--prec-div=true` enables the IEEE round-to-nearest mode and `--prec-div=false` enables the fast approximation mode.

> This option is set to `true` and `nvcc` enables the contraction of floating-point multiplies and
> adds/subtracts into floating-point multiply-add operations (FMAD, FFMA, or DFMA).

> `--use_fast_math` implies `--ftz=true --prec-div=false --prec-sqrt=false --fmad=true`.

**[geprüft]** `build.ps1:81` setzt keines dieser Flags, also gelten beide Defaults. Meine Überschrift aus §2 und der
`--fmad`-Punkt aus §7.1 sind damit belegt statt vermutet.

**Die Größenordnung beim Local Memory wird durch die Doku entschieden — und der Algorithmus-Agent lag falsch.**
Aus `02-basics/writing-cuda-kernels.html:780`, wörtlich:

> Because the local memory space resides in device memory, local memory accesses have **the same latency and
> bandwidth as global memory accesses** and are subject to the same requirements for memory coalescing... However,
> local memory is organized such that consecutive 32-bit words are accessed by consecutive thread IDs. Accesses are
> therefore **fully coalesced as long as all threads in a warp access the same relative address, such as the same
> index in an array variable**.

Das ist genau die Analyse des CUDA-Agenten und **widerspricht** der Schätzung des Algorithmus-Agenten von einem
„silent 10–100× penalty". **[abgeleitet]** Die drei Leser in `convex_where` / `convex_extremum` /
`convex_envelope` haben über den Warp **denselben** Index — also sind sie nach der Doku **vollständig
koalesziert**, und kosten einen globalen Speicherzugriff, keinen Registeroperanden. Der einzige divergente Zugriff
bleibt `g[at]` in der Aufbauschleife (`zmap.cu:1025`). **Die Wirkung ist eine Größenordnung, nicht zwei, und sie ist
kein Grund, die Arrays in Register zu zwingen — §7.2 zeigt, dass das ein Rückschritt wäre.**

**[geprüft]** Und die Grenze dieser Doku: Für **compute capability 12.x sind die Technical-Specifications-Tabellen
leer**. `05-appendices/compute-capabilities.html` führt 12.x als Spalte, aber ohne Werte. Belegt sind dort nur
7.5 bis 11.0 sowie 12.x ohne Eintrag; die höchsten belegten Zahlen (1536 Threads/SM, 255 Register je Thread,
512 KB Lokalspeicher je Thread) stehen für **compute capability 9.0**. **Die Kennzahlen für die Karte, auf der
dieser Kernel läuft, sind nicht dokumentiert** — sie müssen `cuobjdump -res-usage` oder `ncu` liefern.

**Unbelegt bleibt:** die Literatur in §7.1 (beide vom Agenten genannten URLs 404), und alle Hardwarezahlen in §7.2
sind Folgerungen eines Agenten ohne Profiler.

---

## 8. Offen: die 33 % beim Drehen, und warum beide Erklärungen noch nicht tragen

**[geprüft]** `docs/long-programs.md:230-232` schreibt die Mehrkosten eines drehenden Werkzeugs so:

> „because the kernel turns the half-spaces per column rather than per step."

**[abgeleitet]** Diese Begründung trägt nicht:

1. **Die Drehung wird auch ohne Drehung bezahlt.** Bei reiner Verschiebung ist `R = I` — dieselben 9 Mul + 6 Add je
   Halbraum, nur mit einer Einheitsmatrix alsoperand. Es gibt keinen arithmetic Grund, warum `R ≠ I` mehr kostet.
2. **Für eine Drehung um die z-Achse ist die Aufteilung identisch.** `ConvexProfile.cs:39-45` sagt es selbst: „It
   cannot happen for a turn about the z-axis, where `m_z = n_z` regardless of the angle." Damit sind `nLo`, `nHi` und
   alle Schleifenlängen unverändert.

**[geprüft]** Und die Erklärung des Rechercheagenten ist ebenfalls widerlegt. Er vermutete, `nLo + nHi ≪ m`, weil
viele Halbräume `|mz| ≈ 0` hätten. Aber `ConvexTool.Ball` (`ConvexTool.cs:163-171`) verteilt die Normalen über eine
**Fibonacci-Spirale**: bei 12 Ebenen sind die z-Werte **±0,9167 / ±0,5833 / ±0,25** — **keine** Ebene trifft den
`mz == 0`-Zweig, auch bei `R = I` nicht. Es gilt also `nLo + nHi = m` **exakt**.

**[abgeleitet]** Damit steht sogar die **gemessene Exponentzahl** unter Druck. `Where` läuft unkonditioniert und ist
Θ(`nLo·nHi`) = Θ(m²/4). Ein m²-Wachstum müsste man also **sehen**. Gemessen ist m^1.57, bei den Werten
704,1 / 1055,6 / 1935,4 / 3278,2 ms für 6 / 8 / 12 / 16 Halbräume (`docs/performance.md:290`). **[zu messen]**
**Das ist eine offene Widersprüchlichkeit in den vorliegenden Daten, keine Erklärung.** Zwei Zähler entscheiden sie:
`nLo`/`nHi`-Histogramm, und ein Zähler für die (Schritt, Spalte)-Paare.

**[zu messen]** Deren Zweck: sie dreht die Reihenfolge. Wenn es an den Paaren liegt (§C Early-Out), ist die Envelope
die falsche Arbeit. Wenn es an den Kreuzungen im Bereich liegt, ist sie die richtige — und dann ist §3 die
Durchschlagsmitte.

---

## 9. Reihenfolge, wie ich sie vorschlagen würde

**[abgeleitet]** jede Stufe einzeln messbar, jede unabhängig von der nächsten:

1. **Zähler und Bestätigung** (§2, §8). Kein Produktionscode. `cuobjdump -sass` für die Divisionslast, `ncu` für die
   vier entscheidenden Zahlen, Zähler für Paare und Kreuzungen. **Ergebnis: die richtige Reihenfolge ist bekannt,
   bevor irgendetwas gebaut wird.**
2. **Early-Out gegen die Box** (§4). Ein Vergleich pro Achse in `convex_span`, keine Payload-Änderung, keine
   Algorithmus-Änderung. **Kleinster Diff im ganzen Vorschlag, größter Faktor in der Schritt-4-Geometrie.**
3. **Divisionen raus** (§2.1, §3). Vorzeichenbasierte Tests in `Where` und im Bereichstest von `Extremum`, und `Where`
   als affine Kette. C# und CUDA symmetrisch — die Invariante, die `docs/handoff.md` zu Recht verteidigt.
4. **Pro-Schritt-Vorberechnung nach `__shared__`** (§5). Größter Diff, größter struktureller Gewinn.
5. **Envelope** (§7.1, sobald die Recherche da ist). Danach mit der offenen Frage aus §6, ob die 16 steigen können.
6. **Ball und konvex trennen** (§7.2) — nicht für den konvexen Kernel, sondern damit die Ball-Zahlen aus Schritt 2
   wieder vertrauenswürdig sind.

**[abgeleitet]** Punkt 3 und 4 fallen auch dann durch, wenn 5 sich als weniger erfolgreich erweist als erwartet.
Deshalb die Reihenfolge.

### Was jede Stufe an Resultaten kostet

**[geprüft]** Die dokumentierte Invariante: alle Referenzwerte in `docs/handoff.md` („must not change unless the kernel
or the preview model changes") und der Binned-gegen-Unbinned-Vergleich **bit für bit**
(`docs/long-programs.md:159-160`).

**[abgeleitet]** Stufe 2 verschiebt Spalten zwischen „getroffen" und „nicht getroffen" nur, wenn ein Mittelpunkt exakt
auf der Boxkante liegt (`StepBins.MarginMm` ist 10⁻⁶ mm). Stufe 3 ändert, welche Kreuzungen im Bereich liegen, um
Ulop. Stufe 4 ändert die Rechnung des Achsenabschnitts von `c·inv` auf `α + βx + γy`, ebenfalls Ulop. **Keine Stufe
ändert die Mathematik** — alle lösen dieselbe Intervallbedingung — aber alle verschieben die letzten Stellen der
Volumina. Die `cell/100`-Toleranzen sollten das schlucken; die Bench-Referenzzahlen werden sich in den letzten Stellen
bewegen und **neu gemessen** werden müssen.

**[abgeleitet]** Der Binned-gegen-Unbinned-Vergleich ist **nicht** betroffen: beide Launches gehen durch dasselbe
`dexel_apply_column` und dieselbe `convex_span`. Das ist die eine Aussage, die durch alle Stufen exakt erhalten bleibt.

---

## 10. Die drei offenen Fragen, beantwortet

### 10.1 Der Exponent m^1.57 löst sich auf, ohne dass etwas gebaut wird — er ist 2

**[rechnet]** §8 hat den Widerspruch offengelassen: `nLo + nHi = m` **exakt** (die Fibonacci-Normalen treffen den
`mz == 0`-Zweig nie), also ist `Where` Θ(m²/4) und läuft unkonditioniert — **ein m²-Wachstum müsste man sehen.**
Gemessen sind 704,1 / 1055,6 / 1935,4 / 3278,2 ms bei 6 / 8 / 12 / 16 Halbräumen (`docs/performance.md:290`). Der
Widerspruch löst sich, wenn man die **richtige Modellform** benutzt. Statt eines Potenzgesetzes:

| m | gemessen | `289,4 + 11,63·m²` | Abweichung |
| ---: | ---: | ---: | ---: |
| 6 | 704,1 | 708,1 | +0,6 % |
| 8 | 1055,6 | 1033,8 | −2,1 % |
| 12 | 1935,4 | 1964,3 | +1,5 % |
| 16 | 3278,2 | 3266,9 | −0,3 % |

**[rechnet]** Zwei freie Parameter treffen alle vier Punkte innerhalb ±2,1 %. Und die dokumentierte Zahl 1,57 ist
genau das, was ein Potenzgesetz durch dieselben Daten liefert — ich habe die Kleinste-Quadrate-Fit in log-log
nachgerechnet und komme auf **1,558**. **[abgeleitet]** **Die Daten sind nicht `Potenz`, sondern `konstant +
quadratisch`.** Der Exponent 1,57 ist ein Artefakt des falschen Modells: eine Potenzkurve durch Daten mit
konstantem Anteil berichtet den Exponenten zu klein.

**[rechnet]** Und der konstante Anteil ist nicht klein:

| m | Anteil der Konstante an der gemessenen Zeit |
| ---: | ---: |
| 6 | **41 %** |
| 8 | 27 % |
| 12 | 15 % |
| 16 | 9 % |

**[abgeleitet]** **Das ist der eigentliche Fund dieser Sektion.** Es gibt eine große, m-unabhängige Arbeit pro
(Spalte, Schritt) — bei m = 6 sind es 41 % der Laufzeit. Sie ist genau das, was **§5 (Pro-Schritt-Vorberechnung)**
angreift: die Rotation, das `m·w`- und `m·T_A`-Dotprodukt und die Division `1/mz` pro Halbraum, die alle nicht von m
abhängen, sondern nur vom Schritt und der Spalte. **Die Exponent-Frage hat diese Arbeit die ganze Zeit verdeckt.**

**[abgeleitet]** Und die 33 % beim Drehen (§8) fügen sich in dasselbe Bild: Drehen fügt **keine** m-abhängige Arbeit
hinzu. Bleibt nur, dass die Paarzahl steigt — die gedrehte Keule hat eine fattere Box und landet in mehr Kacheln. Das
ist die Erklärung (a) aus §8, und sie ist mit dem konstanten Anteil konsistent.

**[zu messen]** Der Fit ist eine Rechnung auf vier Punkten. Er sollte an einem fünften geprüft werden — der m-Sweep
existiert bereits im Bench (`LongPrograms convex`, `--planes`), ein weiterer Wert kostet einen Lauf. **Fällt ein
gemessener Punkt auf `289 + 11,63·m²`, ist die Sache erledigt.** Fällt er nicht, ist das Modell zu grob und die
Konstante muss aufgeteilt werden.

### 10.2 `-fmad=false` ist nicht neu — es ist F7, und was fehlt, ist die Kontrolle

**[geprüft]** Ich habe das letzte Mal als „der wichtigste Einzelfund" dargestellt. Das war falsch: **der Punkt ist im
Projekt bereits viermal dokumentiert.**

- `Step3Verification.md:190-194` — **F7**, wörtlich: „nvcc contracts `a*b + c` into an FMA by default; C#'s `MathF`
  does not". Und `:275-276`: „Divergences found, all of them real but none algorithmic: **F7** (fma contraction,
  which the commit message acknowledges and the kernel comment denies)".
- `Step3KernelFindings.md:70-71` — dieselbe Aussage mit dem RyuJIT-Gegenstück, und `:320` stuft den Kommentar
  `zmap.cu:946-947` als **falsch** ein: „so the backends differ by ulps. `ConvexDexelTests.cs:563-566` says the
  opposite and is right."
- `CudaLongProgramsFindings.md:756-757` — dasselbe, plus die zweite Abweichung; `:783` — der Kommentar sei
  „**still false, still unreworded**".

**[geprüft] Die eigentliche Lücke steht wörtlich in `CudaLongProgramsFindings.md:327-329`:**

> Release builds with `-O3` only, no `-fmad=false` variant (`build.ps1:70-72`), while the CPU/CUDA divergence is
> attributed to fma contraction. Step 3's test has more multiply-adds than the ball's, so **the one control that
> would separate a real bug from contraction does not exist.**

und in der Tabelle `:369` als **„7.6 no `-fmad=false` variant | absent"**.

**[abgeleitet]** Das ist die Antwort auf die Frage, und sie ist eine andere als die gestellte. **Nicht** „das Flag
setzen", sondern **die Kontrolle bauen**: eine zweite Build-Konfiguration mit `-fmad=false`, ein Bench-Lauf und ein
Testlauf, dann ist F7 eine Messung statt einer Vermutung — und man weiß, wie viel der beobachteten Divergenz
(4,3·10⁻⁵ … 1,3·10⁻⁴ mm gegen 8,3·10⁻⁴ Toleranz) wirklich von der Kontraktion stammt und wie viel von etwas
Unerwartetem. Das ist ein halber Tag Aufwand und es beantwortet gleichzeitig die Frage, ob die Toleranzen in
`ConvexDexelTests.cs:593` und `DexelMapTests.cs:191` die **feste** Grenze `1e-4` brauchen.

**[geprüft] Zwei Dinge, die das Flag _nicht_ repariert:**

1. **F8** — `fmaxf`/`fminf` folgen IEEE-754-2008 `maximumNumber` und **schlucken NaN**, `MathF.Max`/`MathF.Min`
   **propagieren** es (`zmap.cu:937` gegen `ConvexProfile.cs:302`;
   `Step3KernelFindings.md:54-57`, `Step3Verification.md:196`). `-fmad=false` ändert daran nichts. Ob F8 je
   aufgetreten ist, steht in `Step3Verification.md:531` weiterhin offen und braucht ein entartetes Werkzeug.
2. **Die Ball-Referenzzahlen aus Schritt 2.** Das Flag ist global und trifft auch `swept_span`. Die Baseline müsste
   neu gemessen werden — und die ist nach `Step3KernelFindings.md §f` ohnehin schon verdächtig, weil sie aus einer
   Binärdatei mit doppeltem Local-Memory-Rahmen stammt.

**[abgeleitet] Empfehlung: die Kontrolle bauen, das Flag noch nicht setzen.** Und unabhängig davon sollte jemand den
Kommentar an `zmap.cu:946-947` korrigieren — er behauptet „both backends land on the same float" und das ist
falsch. Das ist eine Zeile und der billigste Widerspruch im ganzen Repository.

### 10.3 Korrektur: der Box-Test schlägt den Silhouetten-Test deutlich, und meine 58 % waren zu hoch

**[rechnet]** Beides in einer Rechnung, weil die Geometrie des Sweeps sie entscheidet.

**Der Box-Test** steht in §4: **43 % im Mittel**, 19–70 % je nach Ausrichtung (nicht 58 % — das war der Mittelwert
über eine zu grob geschätzte Kachelzahl). Die Korrektur ändert den Hebel, nicht seine Richtung.

**Der Silhouetten-Test** ist der Support-Funktionssatz aus §7.1. Sein Ertrag hängt davon ab, wie gut der Sweep seine
eigene Box füllt — und das habe ich in §7.1 nicht gerechnet, sondern behauptet. Für die Schritt-4-Geometrie rechnet es
sich so (`Step4FeasibilityFindings.md:290-298`: der Schritt bewegt das Korn 0,349 mm in x, die Oktaederbreite ist
0,075–0,13 mm, genommen 0,13, die Sweep-Box ist 0,48 × 0,13 mm):

- Die Projektion des geschlobenen Oktaeders ist die Raute der Breite `w` **⊕** das Segment der Länge `L`. Nach der
  Minkowski-Formel für ein Segment: `Fläche = w²/2 + L·w`.
- `0,13²/2 + 0,349 · 0,13 = 0,00845 + 0,04537 = 0,0538 mm²` gegen eine Box von `0,48 · 0,13 = 0,0624 mm²`.

```
Füllgrad der Sweep-Box  ≈  86 %     →  der Silhouetten-Test verwirft nur 14 %
```

**[abgeleitet]** Und das ist keine Eigenschaft des Korns, sondern **eine Eigenschaft des Regimes**: der Füllgrad
`w²/2 + L·w` über `w(L+w)` geht gegen 1, wenn `L ≫ w`. **Der Silhouetten-Test zahlt genau dann, wenn der Schritt kurz
ist, und am wenigsten, wenn er lang ist** — und Schritt 4 hat mit 0,349 mm gegen 0,13 mm ausgerechnet den Fall
`L ≫ w`. Für den Bench-Fall (0,2-mm-Polyeder auf einem langen Finish-Pass) gilt dasselbe.

**[abgeleitet] Also: §7.1 hat den Silhouetten-Test zu hoch bewertet, und ich habe das letzte Mal übernommen.** Für die
Geometrien dieses Projekts ist er nahezu wertlos, und ich habe ihn in §0 gestrichen. **Der Box-Test ist der Early-Out,
und der einzige, den man braucht** — vier Vergleiche, keine Theorie, und 43 % der Paare.

**[zu messen]** Die Verwerfquote des Box-Tests ist eine Rechnung auf der Kachelgeometrie, keine Messung. Ein Zähler
(`columnsSeen`, `columnsInBox`) im Kernel macht sie in einem Lauf zur Tatsache — und er ist derselbe Zähler, den §8
für die Paarzahl ohnehin braucht.

---

## 11. Nicht durchgeführt

- Kein `dotnet build`, kein `dotnet test`, kein Bench-Lauf, kein `cuobjdump`, kein `ncu`, kein `nvcc`.
- Keine Datei im Repo verändert außer dieser Notiz. Die CUDA-Doku liegt unter `.qwen/refs/` und ist nicht Teil des
  Commits.
- **Keine der Zahlen in §1, §3, §4, §10.1, §10.3 ist gemessen.** Sie folgen aus dem Quelltext, aus den bereits
  gemessenen Größen in `docs/performance.md`, `docs/long-programs.md` und `Step4FeasibilityFindings.md`. Der Ertrag
  jeder Stufe ist eine Hypothese mit einer Größenordnung, **kein Ergebnis**.
- **Der Fit in §10.1 ist eine Rechnung auf vier Punkten** und nicht durch einen fünften Messpunkt bestätigt. Das ist
  die einzige Aussage in dieser Notiz, die schon eine eigene Messung verdient, weil sie die anderen trägt.
- Die Hardwarezahlen in §7.2 sind Folgerungen des Rechercheagenten ohne Profiler. Sie sind als solche markiert und
  **nicht** von mir geprüft. Die Aussagen aus §7.3 sind von mir gegen die geladene Doku geprüft.
- Die Literatur in §7.1 ist **unbelegt**: beide vom Agenten genannten URLs liefern 404.
# Convex kernel: where the m^1.57 really comes from, and three ways out

**Datum:** 2026-10-04
**Branch/Commit der Analyse:** `worktree-keen-elm-b95ffa` @ `861c6eb`
**Art der Arbeit:** reine Lese-Analyse. Nichts gebaut, nichts getestet, nichts committet, keine Datei außer dieser
Notiz geändert. In einer anderen Session laufen Builds und Tests.

Zwei Rechercheagenten liefen parallel (Algorithmus-Literatur und CUDA-Leistungsverhalten); ihre Ergebnisse sind als
**§6.1** und **§6.2** mit Quelle markiert. Alles andere hier ist am Quelltext geprüft und mit Datei:Zeile belegt.

Jede Aussage trägt eine Marke: **[geprüft]** = am Quelltext gelesen, **[abgeleitet]** = aus dem Quelltext gefolgert,
nicht gemessen, **[zu messen]** = eine Messung wird gebraucht, bevor es gläubt wird.

---

## 0. Kurzfassung

Die geplante O(m)-Envelope ist der richtige Schritt, aber sie ist **nicht der größte Hebel**, und der Plan nennt den
zweiten und dritten nicht. Drei unabhängige Stellen, alle verifiziert:

| # | Hebel | Aufwand | Erwarteter Effekt | Risiko |
| --- | --- | --- | --- | --- |
| **A** | `m(m+1)/2` IEEE-Divisionen pro (Spalte, Schritt) durch vorzeichenbasierte Tests ersetzen | klein, kein Algorithmuswechsel | **groß**, ~ein Drittel der Befehle im heißesten Loop sind Division | niedrig |
| **B** | Pro-Schritt-Vorberechnung: Steigung und Achsenabschnitt sind **unabhängig von der Spalte** | mittel | groß, macht m fast spaltenfrei | niedrig–mittel |
| **C** | Early-Out gegen die Bounding-Box, die schon im Schritt-Payload steht | sehr klein | klein–mittel, 20–50 % der Spalten | sehr niedrig |

Der Plan in `docs/todo.md` und `docs/long-programs.md` („the queued O(m) envelope") kennt nur die Envelope. **B ist
der größere Hebel und steht in keinem Dokument.** [abgeleitet]

---

## 1. Der teuerste Posten sind nicht die Geometrie, sondern die Divisionen

**[geprüft]** `src/Stykker.NanoCut.Gpu.Native/build.ps1:81` baut mit:

```
-O3 -shared -std=c++17 -cudart static -gencode arch=compute_120,code=sm_120 -gencode arch=compute_75,code=compute_75
```

Kein `-use_fast_math`, kein `--prec-div=false`. Es gelten also die nvcc-Defaults: **`-prec-div=true`**, jedes `/` wird
zu einer IEEE-korrekten `div.rn.f32`, die der Compiler in eine Newton-Raphson-Sequenz mit Sonderfällen aufbläht.
(`-fmad=true` ist ebenfalls Default — das ist die „fma contraction", von der `docs/long-programs.md:145` berichtet.)

**[geprüft]** Die Divisionstellen, C# und CUDA operation-for-operation identisch:

| Stelle | C# | CUDA | Anzahl |
| --- | --- | --- | --- |
| `inv = 1 / mz` | `ConvexProfile.cs:213` | `zmap.cu:1022` | `m` |
| `tHi/tLo = -k / s` | `ConvexProfile.cs:251-252` | `zmap.cu:918-919` | `nLo · nHi` |
| `t = (aj - ai) / (bi - bj)` | `ConvexProfile.cs:281` | `zmap.cu:964` | `C(nLo,2) + C(nHi,2)` |

**[geprüft]** Die Summe ist eine geschlossene Form und **unabhängig von der Aufteilung** der Halbräume:

```
m + nLo·nHi + C(nLo,2) + C(nHi,2)  =  m + C(m,2)  =  m(m+1)/2
```

weil `C(a,2) + C(b,2) + a·b = C(a+b,2)`. Für m = 16 sind das **136 Divisionen**, für m = 12 genau 78, für m = 6 genau
21 — **pro Spalte und pro Schritt**, bei jeder Aufteilung in „below" und „above".

**[abgeleitet]** Zum Vergleich die Envelope-Arithmetik im selben Aufruf: `convex_envelope` wird zweimal für die
Ränder aufgerufen und einmal je Kreuzungspunkt, der in `[tLo, tHi]` fällt. Im schlechtesten Fall sind das
`2 + C(m,2)` Aufrufe über `nLo` bzw. `nHi` Linien — bei m = 16 also 122 × 8 ≈ 980 Iterationen mit je einer
Multiplikation, einer Addition und einem Vergleich. **[abgeleitet]** Eine IEEE-`div.rn.f32` kostet auf aktueller
NVIDIA-Hardware rund 10–20 Befehle; 136 davon sind also **1300–2700 Befehle**, gegenüber etwa 3000 für die gesamte
Envelope-Arithmetik. **Die Divisionen sind rund ein Drittel der Befehle im heißesten Loop des Kernels — und keines
der Projektdokumente erwähnt sie.**

### 1.1 Wie man es ohne Algorithmuswechsel wegbekommt

**[abgeleitet]** Beide Divisor-Stellen lassen sich durch vorzeichenbasierte Tests ersetzen, ohne die Kandidatenmenge
zu ändern:

- `Where` (`ConvexProfile.cs:251`): statt `tHi = min(tHi, -k/s)` genügt die Vorzeichenfrage „liegt `-k/s` unter dem
  jetzigen `tHi`?". Für `s > 0` ist das äquivalent zu `k + s·tHi ≥ 0`; für `s < 0` zu `k + s·tLo ≤ 0`. Der
  `min`/`max` bleibt eine Kette, das Verhalten bei Ulop-Ungenauigkeit bleibt das dokumentierte („an ulop moves an end
  of the range by an ulop", `ConvexProfile.cs:236-239`) — nur eben ohne Division.
- `Extremum` (`ConvexProfile.cs:279-282`): die Bereichsprüfung `t < tLo || t > tHi` ist äquivalent zu
  `(aj - ai) - tLo·(bi - bj)` und `(aj - ai) - tHi·(bi - bj)` mit dem Vorzeichen von `bi - bj`. Die Division fällt
  ganz weg, **außer** der Kandidat liegt wirklich im Bereich — und genau dann wird sie für die Envelope-Auswertung
  ohnehin gebraucht.

**[abgeleitet]** Das ist die billigste mögliche Änderung mit dem größten Hebel: kein neues Verfahren, keine neue
Theorie, und der Rest der Divisionen (die `m` aus der Ebene-Schleife plus die wenigen echten Kreuzungen) fällt auf
O(m).

**[zu messen]** Der Ertrag ist eine Hypothese. Die billigste Bestätigung kostet keinen Code: `cuobjdump -sass
src/Stykker.NanoCut.Gpu.Native/bin/nanocut_gpu.dll` und die Anzahl der `MUFU`/`FFMA`-Sequenzen in
`dexel_apply_binned_kernel` zählen, oder Nsight Compute mit der Instruction-Metrik. Erst bauen, dann messen.

**[zu messen]** Alternativ, und deutlich größer im Effekt aber global: `--prec-div=false` oder `-use_fast_math`
setzen. Das macht *jede* Division zu `rcp.approx` plus eine Korrektur und kostet Genauigkeit **im ganzen Kernel**,
nicht nur im konvexen Pfad. Bei einem Projekt, das CPU und CUDA auf `cell/100` zusammenbringt, ist das eine
Entscheidung, die eine Messung und keine Vermutung verdient — es steht hier nur als Option, nicht als Empfehlung.

---

## 2. Der Plan übersieht den größeren Hebel: die Vorbereitung ist spaltenunabhängig

**[geprüft]** In `ConvexProfile.Span` (`ConvexProfile.cs:186-224`) ist

```csharp
float dot = mx * wx + my * wy + mz * wz;     // Steigungsteil
float c   = d + (mx*ax + my*ay + mz*az) - mx*x - my*y;   // nur hier steht x, y
float inv = 1f / mz;
g[at]     = c * inv;      // Achsenabschnitt
g[at + 1] = dot * inv;    // Steigung
```

**[abgeleitet]** `b_i = dot_i / mz_i` hängt **überhaupt nicht von (x, y) ab** — nur von der Rotation `R`, der Ebene `n`
und der Bewegung `w`. Und der Achsenabschnitt lässt sich so umschreiben, dass auch er nur noch zwei FMA braucht:

```
A_i = c_i / mz_i  =  α_i + β_i·x + γ_i·y
     mit  α_i = (d_i + m_i·T_A) / mz_i
          β_i = −m_x / mz_i
          γ_i = −m_y / mz_i
```

**[geprüft]** `m = R·n` (Rotation pro Schritt), `T_A`, `w` (pro Schritt), `n`, `d` (pro Programm). **Kein x, kein
y.** Damit gilt pro (Schritt, Ebene):

- die Vorzeichenaufteilung in „below"/„above" (`mz < 0`), also auch `nLo` und `nHi`,
- die Steigung `b_i`,
- `α_i`, `β_i`, `γ_i` — eine Division und drei Multiplikationen **einmal** statt pro Spalte.

**[abgeleitet]** Übrig bleibt pro Spalte und Halbraum genau ein `A_i = α_i + β_i·x + γ_i·y`, zwei FMA. Aus
*m* Rotationen (9 Mul + 6 Add), *m* `m·w`-Dotprodukten, *m* `m·T_A`-Dotprodukten und *m* Divisionen werden **2·m
FMA**. Das ist der eigentliche strukturelle Gewinn: die Pro-Spalte-Arbeit skaliert dann fast nicht mehr mit m.

### 2.1 Wo die Vorbereitung hingehört

**[geprüft]** Die Schleifenstruktur (`zmap.cu:1085-1113`): **ein Thread pro Spalte**, die Schritt-Schleife innen.

```cuda
for (int s = first; s < last && n > 0; ++s) {
    const float* p = steps + index * stride;
    convex_span(x, y, p, planes, planeCount, lo, hi);
}
```

**[abgeleitet]** Innerhalb eines Threads gibt es also keine Wiederverwendung über Spalten hinweg. Aber: **alle 256
Threads eines Blocks sehen denselben Schritt gleichzeitig** (Block = eine Kachel von `kDexelTile = 16` × 16 = 256
Spalten, `zmap.cu:822`). Die Pro-Schritt-Daten sind also über den Block hinweg gemeinsam und gehören in
`__shared__` — **ohne** das Payload zu verbreitern.

**[zu messen]** Der Preis ist ein `__syncthreads()` pro Schritt, und die Schleife läuft pro Kachel über alle ihr
zugewiesenen Schritte (im Bench Hunderte). Ob das netto gewinnt, muss gemessen werden; die zweite Recherche in §8
behandelt die Größenordnung.

**[abgeleitet]** Die Alternative — Vorbereitung auf dem Host, einmal pro Programm — ist teurer, als sie aussieht:
das Payload ist heute 32 Floats (128 Byte) pro Schritt, bei 793 600 Schritten also rund 102 MB Upload. Die
zusätzlichen 4 Floats je Halbraum wären `4m = 64` Floats, also 80 Floats = 320 Byte je Schritt und rund 254 MB.
Bei der gemessenen Rate von 8,7 GB/s (aus `docs/gpu-findings.md`) wären das ~17 ms **zusätzlich** bei einem Kernel
von 372,6 ms. Shared Memory ist damit nicht nur sauberer, sondern auch billiger.

---

## 3. Early-Out gegen eine Box, die schon im Payload steht

**[geprüft]** `ConvexProfile.Pack` (`ConvexProfile.cs:126-136`) legt die **AABB des Sweeps** in die ersten vier Floats
jedes Schritts (`BoxOffset`), und `StepBins.Tiles` (`StepBins.cs:100-124`) weist den Schritt genau den Kacheln zu,
die diese Box berühren — „deliberately generous: a step lands in more tiles than it strictly needs, never in fewer".

**[geprüft]** Was danach passiert: **jede Spalte der Kachel ruft `convex_span` auf**, auch wenn ihr Mittelpunkt
außerhalb der Box liegt. `convex_span` rechnet dann das komplette O(m³) durch und liefert `false`
(`zmap.cu:1032`ff). Es gibt keinen Vorab-Test.

**[abgeleitet]** Der Test kostet vier Vergleiche gegen vier Floats, die der Thread ohnehin liest:

```
x0 ≤ x ≤ x1  ∧  y0 ≤ y ≤ y1
```

und ist **korrekt**, weil der Sweep in seiner eigenen Box liegt: eine senkrechte Gerade außerhalb der Box kann den
Körper nicht schneiden. Er ist einschließend, also konservativ in derselben Richtung wie die geschlossene Regel, die
das Modell sonst benutzt.

**[abgeleitet]** Der Gewinn hängt vom Füllgrad der Box ab. Für den kugelförmigen Bench-Fall (`ConvexTool.Ball`)
liegt er bei π/4 ≈ 79 % Belegung, also ~21 % verworfenen Spalten. Für das, was Schritt 4 und 5 wirklich brauchen —
ein Zahn, ein Schleifkorn, ein Rad — ist die Box deutlich schlechter gefüllt, und es sind eher 40–60 %. **[zu
messen]**, und die Antwort hängt davon ab, welches Werkzeug gefahren wird.

**[abgeleitet]** Der *exakte* Test ist die Silhouette des Sweeps: die Projektion eines konvexen Polytops auf xy ist
ein konvexes Polygon mit höchstens m+2 Ecken, also ein O(m)-Punkt-im-Polygon-Test. Der ist nach der Envelope-Umstellung
derselbe Aufwandsordnungs-Bereich wie das LP selbst und lohnt sich erst danach (oder gar nicht). **Als dritte Stufe,
nicht als erste.**

---

## 4. Was die Begründung für `MaxPlanes = 16` falsch aufstellt

**[geprüft]** `ConvexProfile.cs:112-121` und `zmap.cu:825` begründen 16 so:

> „finding the interval walks every crossing of two of the m lines and evaluates the envelope there over all m of
> them, which is O(m³) per column and step — eight times more per tool from 16 to 32. … The local arrays fit either
> way: the lower and the upper bound each keep 2 · m floats on top of the column's 2 · 16 intervals, which stays
> under the 512 bytes a thread may use."

**[geprüft]** Die zweite Begründung hält nicht: bei m = 32 wären die Zeilenlisten `2 · 32` Floats je Seite
= 256 Byte je Seite, zusammen 512 Byte, plus `2 · kMaxDexelIntervals = 2 · 16` Floats = 128 Byte für die Intervalle
— **640 Byte**, nicht mehr unter 512. Das Limit ist also die **Lokalspeichermenge**, nicht der Algorithmus.

**[abgeleitet]** Das ist die gute Nachricht: mit der Envelope-Umstellung und der Vorbereitung aus §2 entfällt der
Grund für die Kopplung. Wer die Zeilen nicht mehr im Thread puffert, sondern sie aus `planes` und der Vorbereitung neu
berechnet, hebt die 16 auf. **[geprüft]** Das wäre auch_accuracy_-relevant: `docs/long-programs.md:232-234` misst,
dass das einbeschriebene Polyeder bei 6 / 8 / 12 / 16 Halbräumen 18,227 / 17,131 / 11,511 / 8,789 % **weniger**
Material entfernt als die Kugel. **[abgeleitet]** Die Hälfte davon ist der Bruchteil, der an der Werkzeugform liegt
und nicht am Raster — mit m = 32 oder 64 wäre er kleiner, und `docs/long-programs.md:169-171` hat bereits
gemessen, dass der Aufwand mit m nur als m^1.57 wächst.

---

## 5. Eine Beobachtung, die der Übergabe widerspricht und eine Messung verdient

**[geprüft]** `docs/long-programs.md:230-232` schreibt die 33 % Mehrkosten eines drehenden Werkzeugs so:

> „because the kernel turns the half-spaces per column rather than per step."

**[abgeleitet]** Diese Begründung trägt nicht, und zwar aus zwei Gründen, die man am Code abliest:

1. **Die Drehung wird auch ohne Drehung bezahlt.** Bei reiner Verschiebung ist `R = I`, die Arbeit ist dieselbe
   9 Mul + 6 Add je Halbraum — nur mit einer Einheitsmatrix alsoperand. Es gibt keinen arithmetic Grund, warum
   `R ≠ I` mehr kosten sollte.
2. **Für eine Drehung um die z-Achse ist die Aufteilung identisch.** `ConvexProfile.cs:39-45` sagt es selbst: „It
   cannot happen for a turn about the z-axis, where `m_z = n_z` regardless of the angle." Damit sind `nLo`, `nHi`
   und die Größe aller Schleffen unverändert.

**[geprüft]** Damit bleibt als Erklärung nur eine von zwei **arbeitstheoretischen** Ursachen:

- **(a) Mehr (Spalte, Schritt)-Paare.** Eine drehende Keule hat eine fettere Bounding-Box, landet also in mehr
  Kacheln. Dann wäre §3 (Early-Out) die richtige Kur, nicht die Envelope.
- **(b) Mehr Kreuzungen im Bereich.** Die Steigungen ändern sich relativ zueinander, also landen mehr der
  `C(m,2)` Kreuzungen in `[tLo, tHi]` und lösen mehr `convex_envelope`-Aufrufe aus. Dann wäre §4/§1 die richtige
  Kur.

**[zu messen]** Die beiden Ursachen sind in einem Lauf unterscheidbar: der Zähler aus `docs/todo.md` („Anzahl
`Filter.Uncertain`, … Face-/Fragment-Zahlen pro Schritt") plus ein Zähler für die Paare (Spalte, Schritt) und einer
für die Kreuzungen im Bereich. **Diese Frage sollte vor der Envelope beantwortet werden**, weil sie die Reihenfolge
der Arbeit dreht. Es ist eine Stunde Messung und spart möglicherweise die halbe Implementierung.

**[abgeleitet]** Und noch eine kleinere Sache, die gegen eine Erklärung „mz == 0" spricht, falls sie jemand aufbringt:
`ConvexTool.Ball` (`ConvexTool.cs:163-171`) verteilt die Normalen über eine Fibonacci-Spirale, bei 12 Ebenen mit den
z-Werten ±0,9167 / ±0,5833 / ±0,25 — **keine** Ebene trifft den `mz == 0`-Zweig, auch bei `R = I` nicht. Der
`mz == 0`-Zweig erklärt diese 33 % also nicht.

---

## 6. Was die beiden Recherchen ergaben

### 6.1 Algorithmus (externe Literatur)

*(Platzhalter — wird ergänzt, sobald die Recherche zurückkommt.)*

Zu klären war: der Standardalgorithmus für die obere/untere Hülle einer Geradenfamilie, die Minimierung über ein
Intervall ohne den Paar-Durchlauf, die Division in den Bruchstellen-Vergleichen, der Umgang mit exakt parallelen
Geraden, und ob es für „vertikale Ausdehnung eines konvexen Sweeps pro Abfrage-Spalte" überhaupt etwas Besseres gibt
als das LP.

### 6.2 CUDA-Leistung (externe Recherche)

*(Platzhalter — wird ergänzt, sobald die Recherche zurückkommt.)*

Zu klären war: dynamisch indizierte Thread-Arrays im Local Memory und was das für Belegung und Durchsatz heißt, die
Kosten eines `__syncthreads()` pro Schritt, Broadcast-Lesezugriffe aus dem Global Memory als Alternative zu Shared
Memory, und ob 512 Byte pro Thread eine echte Architekturgrenze ist oder Folklore.

---

## 7. Vorschlag für die Reihenfolge, mit Risiko und Aufwand

**[abgeleitet]** in dieser Reihenfolge, jede Stufe einzeln messbar:

1. **Zähler und Bestätigung** (§5, §1). Kein Produktionscode. `cuobjdump -sass` für die Divisionslast, ein Zähler für
   Paare (Spalte, Schritt) und Kreuzungen im Bereich für die 33-%-Frage. Ergebnis: die richtige Reihenfolge ist
   bekannt, bevor irgendetwas gebaut wird.
2. **Early-Out gegen die Box** (§3). Ein Vergleich pro Achse in `convex_span`, keine Payload-Änderung, keine
   Algorithmus-Änderung. Kleinster Diff im ganzen Vorschlag.
3. **Divisionen raus** (§1.1). Vorzeichenbasierte Tests in `Where` und im Bereichstest von `Extremum`. Betrifft C#
   und CUDA symmetrisch — die beiden Dateien müssen operation-for-operation identisch bleiben, das ist die
   Invariante, die `docs/handoff.md` zu Recht verteidigt.
4. **Vorbereitung pro Schritt** (§2). Shared Memory, Block ist 256 Spalten. Größter Diff, größter struktureller
   Gewinn.
5. **Envelope** (§6.1). Erst danach, und dann mit der Begründung aus §4 ist `MaxPlanes` von der
   Lokalspeichergrenze entkoppelt.

**[abgeleitet]** Punkt 3 und 4 sind unabhängig von 5 und fallen auch dann noch durch, wenn 5 sich als weniger
erfolgreich erweist als erwartet. Deshalb die Reihenfolge.

### Was jede Stufe an Resultaten kostet

**[geprüft]** Die dokumentierte Invariante ist, dass **alle** Referenzwerte in `docs/handoff.md` („must not change
unless the kernel or the preview model changes") und der Binned-gegen-Unbinned-Vergleich **bit für bit**
(`docs/long-programs.md:159-160`) halten.

**[abgeleitet]** Jede der Stufen 2 bis 5 ändert die Rundung: Stufe 2 verschiebt Spalten zwischen „getroffen" und
„nicht getroffen" nur, wenn ein Mittelpunkt exakt auf der Boxkante liegt (Float-Rundung, ~1e-6 mm); Stufe 3 ändert,
welche Kreuzungen im Bereich liegen, um Ulop; Stufe 4 ändert die Rechnung des Achsenabschnitts von `c · inv` auf
`α + βx + γy`, ebenfalls Ulop; Stufe 5 ändert die Kandidatenmenge. Keine davon ändert die Mathematik — alle lösen
dieselbe Intervallbedingung — aber alle verschieben die letzten Stellen der Volumina. **[abgeleitet]** Die
`cell/100`-Toleranzen der Tests sollten das schlucken; die Bench-Referenzzahlen (84 846,915579 mm³ usw.) werden
sich in den letzten Stellen bewegen und **neu gemessen** werden müssen.

**[abgeleitet]** Der Binned-gegen-Unbinned-Vergleich ist **nicht** betroffen: beide Launches gehen durch denselben
`dexel_apply_column`, dieselbe `convex_span`. Das ist die eine Aussage, die durch alle fünf Stufen exakt erhalten
bleibt.

---

## 8. Nicht durchgeführt

- Kein `dotnet build`, kein `dotnet test`, kein Bench-Lauf, kein `cuobjdump`, kein `nvcc`. In einer anderen Session
  laufen Builds und Tests.
- Keine Datei im Repo verändert außer dieser Notiz.
- Keine der Zahlen in §1, §2, §3, §5 ist gemessen. Sie folgen aus dem Quelltext und den Befehlszahlen, nicht aus
  einem Lauf. Der Ertrag jeder Stufe ist eine Hypothese mit einer Größenordnung, kein Ergebnis.
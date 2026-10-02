# Genauigkeitskonzept – Umsetzung und Messwerte

Die Gesamtvorgabe von 0,1 µm ist in `Tolerance.Budget(totalUm: 0.1, chordNm: 50)` aufgeteilt:

| Fehlerquelle | Umsetzung | Budget |
| --- | --- | --- |
| Zahlendarstellung | Int64 auf 1-nm-Gitter, exakte Prädikate; Schnittpunkte einmal gerundet (≤ 0,71 nm) und danach nie wieder bewegt | 0,5 nm je Achse |
| Diskretisierung | `Discretization.SegmentCount` aus θ = 2·acos(1 − s/r); Kreise einbeschrieben, Segmentzahl auf Vielfaches von 4 aufgerundet (Achsen-Extrempunkte exakt) | 50 nm |
| Bahn-Schrittweite | Bögen der Werkzeugbahn als Sehnenzug mit Pfeilhöhe ≤ `SweepNm`; gerade Stücke exakt (konvexe Hülle Start/Ende) | 30 nm |
| Reserve | Rest | 19,5 nm |

## Beispiel 1, 2D-Teil (gemessen, auch im `browser-wasm`-Lauf)

| Prüfgröße | Ist | Soll | Abweichung | zulässig |
| --- | --- | --- | --- | --- |
| Schnittfläche Kreis ∩ Rechteck | 3,097316064 mm² | 3,097482080 mm² | 1,66·10⁻⁴ mm² | 5,05·10⁻⁴ mm² |
| Restfläche Rechteck − Kreis | 196,902683936 mm² | 196,902517920 mm² | 1,66·10⁻⁴ mm² | 5,05·10⁻⁴ mm² |
| Nutbreite an der Oberseite | 4,472014000 mm | 4,472135955 mm | 1,22·10⁻⁴ mm | 2·10⁻⁴ mm |
| Maximale Tiefe | 1,000000000 mm | 1 mm | 0 | 1·10⁻⁴ mm |
| Abstand Nutpunkte zur Achse (schlechtester) | 2,999999392 mm | 3 mm | 6,1·10⁻⁷ mm | 1·10⁻⁴ mm |
| Abtrag beim 2D-Sweep (20 mm × 1 mm) | 20,000000000 mm² | 20 mm² | 0 | 4·10⁻³ mm² |

Hinweis Nutbreite: Die einbeschriebene Kreisnäherung liegt radial ≤ 50 nm innen. Wo der Kreis die Oberseite
flach schneidet, wird daraus horizontal bis zu 50 nm / cos φ ≈ 67 nm je Seite. Das liegt innerhalb der im Plan
festgelegten ± 0,0002 mm, aber über 0,1 µm – bei engeren Vorgaben muss `chordNm` entsprechend kleiner gewählt werden.

## Definition der Tiefe

`Penetration2.DepthAlong` misst die Ausdehnung des Abtrags entlang einer Richtung (Standard −y bzw. „nach unten“):
max(p·d) − min(p·d) über alle Eckpunkte. Für eine Eintrittsfläche senkrecht zu d ist das genau der größte Abstand
der neuen Oberfläche zur Ausgangsfläche. Da eine lineare Funktion ihr Extremum auf einem Polygon in einem Eckpunkt
annimmt, ist der Wert bis auf die abschließende Division exakt.

# Bitbudget der exakten Prädikate

Alle Rechenkoordinaten liegen auf dem 1-nm-Gitter im Bereich |c| ≤ 2³¹ nm (±2,147 m).
Damit ist jede Koordinatendifferenz |Δ| ≤ 2³². Daraus folgen feste Schranken für alle Zwischenwerte.
Alle Typen rechnen *geprüft*: Ein Überlauf wirft `OverflowException`, er kann nie still ein falsches Vorzeichen liefern.

## 2D (`Int128`)

| Größe | Formel | Schranke |
| --- | --- | --- |
| orient2d | Δx·Δy − Δy·Δx | 2 · 2⁶⁴ = 2⁶⁵ |
| Schnittpunkt-Zähler | a.x·den + Δx·num | 2³¹·2⁶⁵ + 2³²·2⁶⁵ < 2⁹⁸ |
| Fläche (doppelt) | Σ xᵢ·yᵢ₊₁ − xᵢ₊₁·yᵢ | 2⁶³ je Term |

## 3D-Ebenen (`Int128` / `Int384`)

Eine Fläche wird durch ihre Trägerebene aus drei Gitterpunkten beschrieben: n = (b−a)×(c−a), d = −n·a.

| Größe | Herleitung | Schranke | Typ |
| --- | --- | --- | --- |
| orient3d | 3 · 2³² · 2⁶⁵ | < 2⁹⁹ | `Int128` |
| Normale nᵢ | 2 · 2³²·2³² | ≤ 2⁶⁵ | `Int128` |
| Ebenenabstand d | 3 · 2⁶⁵·2³¹ | < 2⁹⁸ | `Int128` |
| W = det(n₁,n₂,n₃) | 6 · (2⁶⁵)³ | < 2¹⁹⁸ | `Int384` |
| X, Y, Z (Cramer) | 6 · 2⁹⁸·(2⁶⁵)² | < 2²³¹ | `Int384` |
| Punkt gegen Ebene n·X + d·W | 3·2⁶⁵·2²³¹ + 2⁹⁸·2¹⁹⁸ | ≤ 2²⁹⁸ | `Int384` |

Die Schätzung im Plan (≈ 2⁶⁵ / 2⁹⁸ / 2²³¹ / 2³⁰⁰) ist damit bestätigt; `Int384` (max. 2³⁸³) lässt 85 Bit Reserve
für spätere Prädikate (z. B. Vergleich zweier Schnittpunkte entlang einer Kante).

## Absicherung durch Tests

- `PredicateTests`: orient2d und orient3d gegen `BigInteger` bei je 10⁶ Zufallsfällen (inkl. Bereichsgrenzen und exakt kollinearer/koplanarer Fälle).
- `PlanePredicateTests`: 10⁵ Ebenen-Tripel; Schnittpunkt bitgenau gleich `BigInteger`, liegt exakt auf allen drei Ebenen, gemessene Bitlängen ≤ den Schranken oben; Extremfall mit Ebenen durch die Ecken des vollen Koordinatenwürfels.
- `Int384Tests`: +, −, ×, Vergleich gegen `BigInteger` (2·10⁵ Fälle), Überlauf an allen Grenzen.

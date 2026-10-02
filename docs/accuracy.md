# Accuracy concept – implementation and measurements

The total budget of 0.1 µm is split in `Tolerance.Budget(totalUm: 0.1, chordNm: 50)`:

| Error source | Implementation | Budget |
| --- | --- | --- |
| Number representation | Int64 on a 1 nm grid, exact predicates; crossing points rounded once (≤ 0.71 nm) and never moved again | 0.5 nm per axis |
| Discretisation | `Discretization.SegmentCount` from θ = 2·acos(1 − s/r); circles inscribed, segment count rounded up to a multiple of 4 (axis extremes exact) | 50 nm |
| Path step | Arcs of the tool path as chords with sagitta ≤ `SweepNm`; straight moves exact (convex hull of start/end) | 30 nm |
| Reserve | remainder | 19.5 nm |

## Example 1, 2D part (measured, also in the `browser-wasm` run)

| Check | Actual | Exact | Deviation | Allowed |
| --- | --- | --- | --- | --- |
| Cut area circle ∩ rectangle | 3.097316064 mm² | 3.097482080 mm² | 1.66·10⁻⁴ mm² | 5.05·10⁻⁴ mm² |
| Remaining area rectangle − circle | 196.902683936 mm² | 196.902517920 mm² | 1.66·10⁻⁴ mm² | 5.05·10⁻⁴ mm² |
| Groove width at top face | 4.472014000 mm | 4.472135955 mm | 1.22·10⁻⁴ mm | 2·10⁻⁴ mm |
| Maximum depth | 1.000000000 mm | 1 mm | 0 | 1·10⁻⁴ mm |
| Distance of groove vertices to axis (worst) | 2.999999392 mm | 3 mm | 6.1·10⁻⁷ mm | 1·10⁻⁴ mm |
| Removal in 2D sweep (20 mm × 1 mm) | 20.000000000 mm² | 20 mm² | 0 | 4·10⁻³ mm² |

Note on groove width: the inscribed circle polygon lies up to 50 nm radially inside the circle. Where the circle meets
the top face at a shallow angle this becomes up to 50 nm / cos φ ≈ 67 nm horizontally per side. That is within the
plan's ± 0.0002 mm but above 0.1 µm; tighter requirements need a smaller `chordNm`.

## Definition of depth

`Penetration2.DepthAlong` measures the extent of the removed material along a direction (default −y, "downwards"):
max(p·d) − min(p·d) over all vertices. For an entry surface perpendicular to d this is exactly the largest distance of
the new surface to the original surface. A linear function attains its extremes on a polygon at a vertex, so the value
is exact up to the final division.

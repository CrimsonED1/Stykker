# Bit budget of the exact predicates

All computation coordinates lie on the 1 nm grid within |c| ≤ 2³¹ nm (±2.147 m), so every coordinate difference
satisfies |Δ| ≤ 2³². This gives fixed bounds for all intermediate values. All types use *checked* arithmetic:
an overflow throws `OverflowException` and can never silently produce a wrong sign.

## 2D (`Int128`)

| Quantity | Formula | Bound |
| --- | --- | --- |
| orient2d | Δx·Δy − Δy·Δx | 2 · 2⁶⁴ = 2⁶⁵ |
| Intersection numerator | a.x·den + Δx·num | 2³¹·2⁶⁵ + 2³²·2⁶⁵ < 2⁹⁸ |
| Area (doubled) | Σ xᵢ·yᵢ₊₁ − xᵢ₊₁·yᵢ | 2⁶³ per term |

## 3D planes (`Int128` / `Int384`)

A face is described by its supporting plane through three grid points: n = (b−a)×(c−a), d = −n·a.

| Quantity | Derivation | Bound | Type |
| --- | --- | --- | --- |
| orient3d | 3 · 2³² · 2⁶⁵ | < 2⁹⁹ | `Int128` |
| Normal nᵢ | 2 · 2³²·2³² | ≤ 2⁶⁵ | `Int128` |
| Plane offset d | 3 · 2⁶⁵·2³¹ | < 2⁹⁸ | `Int128` |
| W = det(n₁,n₂,n₃) | 6 · (2⁶⁵)³ | < 2¹⁹⁸ | `Int384` |
| X, Y, Z (Cramer) | 6 · 2⁹⁸·(2⁶⁵)² | < 2²³¹ | `Int384` |
| Point vs. plane n·X + d·W | 3·2⁶⁵·2²³¹ + 2⁹⁸·2¹⁹⁸ | ≤ 2²⁹⁸ | `Int384` |

This confirms the plan's estimate (≈ 2⁶⁵ / 2⁹⁸ / 2²³¹ / 2³⁰⁰). `Int384` (max. 2³⁸³) leaves 85 bits of headroom for
later predicates (e.g. ordering two intersection points along an edge).

## Verified by tests

- `PredicateTests`: orient2d and orient3d vs. `BigInteger` on 10⁶ random cases each (including range limits and exactly collinear/coplanar cases).
- `PlanePredicateTests`: 10⁵ plane triples; intersection point bit-identical to `BigInteger`, lies exactly on all three planes, measured bit lengths ≤ the bounds above; extreme case with planes through the corners of the full coordinate cube.
- `Int384Tests`: +, −, ×, comparison vs. `BigInteger` (2·10⁵ cases), overflow at all limits.

## Edge planes

Edge planes of input faces are built from the edge's two grid points and the first point shifted by one grid unit
along the axis most aligned with the face normal. Their normals are at most 2³² per component, so they are within
the bounds above. Splitting adds only planes of the other solid's faces, so every plane in the kernel is a
"grid plane" and every vertex is an intersection of three of them – the budget holds for chains of Booleans too.

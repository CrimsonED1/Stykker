# To do

Open features and ideas, newest first. Each entry says what is wanted and which existing building blocks it can use.

## Profile extraction from 3D solids

Take measured profiles off any 3D result, with the range defined by the user ("from where to where").

**Wanted**

- **Planar section:** intersect a `Solid` with a plane and return the exact cut as a `Region2` in plane coordinates
  (nm grid). It can be limited to a window in the plane, for example u ∈ [u0, u1] and v ∈ [v0, v1].
- **Rotational parts (lathe):** give the axis, an angle φ and a range z ∈ [z0, z1] (optionally r ∈ [r0, r1]), and
  get the r–z profile at φ.
  - Several angles, or the envelope over all angles (max/min radius per z), show run-out and anything that is not
    rotationally symmetric.
- **Circumferential profile:** at fixed z and r (or on a cylinder or circle), sample the surface over an angle range
  [φ0, φ1]. Use it for roundness and gear-tooth flanks.
- **Line profile along a path:** a 3D polyline or segment A → B. The output is height or normal distance to the
  surface along the path, plus Ra/Rz over the chosen range. Extends `SurfaceProfile.Line`.
- **Comparison with a nominal profile:** pass a nominal `Region2` or polyline and get the deviation per point and its
  maximum. Example: lathe result against the drawing contour, ground surface against the ideal plane.

**Outputs**

- `Region2` and polylines.
- SVG (already supported by `Region2.ToSvg`).
- CSV.
- Metrics: length, area, Ra/Rz, maximum deviation.

**Demo:** pick the section in the viewer (plane gizmo, or axis + angle + z range). The 2D profile is shown next to the
3D view, together with its metrics and the deviation from nominal.

**Building blocks**

- Exact plane tests and plane intersections (`Plane3`, homogeneous points).
- `Solid.Split` by a plane.
- The 2D kernel for the resulting contours.
- `SurfaceProfile` for height sampling and Ra/Rz.

**Accuracy target:** same as the rest of the library, below 0.1 µm. The section of a plane-based solid is exact up to
rounding the vertices to the grid.

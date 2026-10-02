"""Usage: python3 run.py <expanded.json> <out-dir>

Manifold (native C++ via the manifold3d Python package): workpiece box minus the convex hull of each step's points, in
order. Coordinates are passed in nm (doubles, exact below 2^53); only the cutting is timed. Python only makes one call
per step, so its overhead is negligible next to the Boolean.
"""
import json
import os
import struct
import sys
import time

import numpy as np
from manifold3d import Manifold


def write_stl(m, path):
    mesh = m.to_mesh()
    v = np.asarray(mesh.vert_properties)[:, :3] * 1e-6
    t = np.asarray(mesh.tri_verts)
    with open(path, "wb") as f:
        f.write(b"\0" * 80 + struct.pack("<I", len(t)))
        for tri in t:
            f.write(struct.pack("<3f", 0, 0, 0))
            for k in tri:
                f.write(struct.pack("<3f", *v[k]))
            f.write(b"\0\0")


def main():
    scene = json.load(open(sys.argv[1]))
    out = sys.argv[2]
    os.makedirs(out, exist_ok=True)
    save = set(scene["save"])
    mn, mx = scene["box"]["min"], scene["box"]["max"]
    work = Manifold.cube([mx[i] - mn[i] for i in range(3)]).translate(mn)
    if 0 in save:
        write_stl(work, f"{out}/step-0000.stl")
    steps = scene["steps"]
    step_ms = []
    for i, pts in enumerate(steps):
        t0 = time.perf_counter()
        work = work - Manifold.hull_points(np.asarray(pts, dtype=np.float64))
        work.num_tri()  # force evaluation (Manifold evaluates lazily)
        step_ms.append((time.perf_counter() - t0) * 1000)
        if (i + 1) in save or (i == len(steps) - 1 and -1 in save):
            write_stl(work, f"{out}/step-{i + 1:04d}.stl")
    stats = {"engine": "manifold", "language": "C++", "exact": False, "steps": len(steps), "totalMs": sum(step_ms),
             "stepMs": step_ms, "volumeMm3": work.volume() * 1e-18, "triangles": work.num_tri()}
    json.dump(stats, open(f"{out}/stats.json", "w"), indent=2)
    print(f"manifold: {len(steps)} steps in {sum(step_ms):.0f} ms, V = {stats['volumeMm3']:.9f} mm3, {stats['triangles']} triangles")


main()

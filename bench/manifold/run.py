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
from manifold3d import Manifold, OpType


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
    batch = int(sys.argv[sys.argv.index("--batch") + 1]) if "--batch" in sys.argv else 1
    step_ms = []
    if "--pipeline" in sys.argv:
        # The hull of the next step is computed in a second thread while the current step is subtracted.
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(1) as pool:
            hull = lambda pts: Manifold.hull_points(np.asarray(pts, dtype=np.float64))
            nxt = pool.submit(hull, steps[0])
            for i in range(len(steps)):
                t0 = time.perf_counter()
                tool = nxt.result()
                if i + 1 < len(steps):
                    nxt = pool.submit(hull, steps[i + 1])
                work = work - tool
                work.num_tri()
                step_ms.append((time.perf_counter() - t0) * 1000)
        if -1 in save or len(steps) in save:
            write_stl(work, f"{out}/step-{len(steps):04d}.stl")
        batch = len(steps) + 1  # skip the loop below
    for i in range(0, len(steps) if batch <= len(steps) else 0, batch):
        group = steps[i:i + batch]
        t0 = time.perf_counter()
        hulls = [Manifold.hull_points(np.asarray(pts, dtype=np.float64)) for pts in group]
        tool = hulls[0] if len(hulls) == 1 else Manifold.batch_boolean(hulls, OpType.Add)
        work = work - tool
        work.num_tri()  # force evaluation (Manifold evaluates lazily)
        ms = (time.perf_counter() - t0) * 1000
        step_ms.extend([ms / len(group)] * len(group))
        last = i + len(group)
        if any(i < s <= last for s in save if s > 0) or (last == len(steps) and -1 in save):
            write_stl(work, f"{out}/step-{last:04d}.stl")
    stats = {"engine": "manifold", "language": "C++", "exact": False, "steps": len(steps), "batch": batch if batch <= len(steps) else 1, "pipeline": "--pipeline" in sys.argv, "totalMs": sum(step_ms),
             "stepMs": step_ms, "volumeMm3": work.volume() * 1e-18, "triangles": work.num_tri()}
    json.dump(stats, open(f"{out}/stats.json", "w"), indent=2)
    print(f"manifold: {len(steps)} steps in {sum(step_ms):.0f} ms, V = {stats['volumeMm3']:.9f} mm3, {stats['triangles']} triangles")


main()

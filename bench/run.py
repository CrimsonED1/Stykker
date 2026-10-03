"""Runs one bench scene with several engines and compares time and result.

Usage: python3 bench/run.py bench/scenes/ball-small.json [--engines nanocut,manifoldsharp,cgal,manifold] [--repeat 3]

The scene (mm) describes a box, a ball tool, a path ("from where to where") and the number of steps per path
segment. It is expanded once into explicit points on the 1 nm grid, so every engine gets the identical input: per
step the convex hull of the ball at the step's start and end points, subtracted from the workpiece in order.
"save" lists the states written as STL (step numbers, 0 = stock, -1 = final); default is only the final state.
"""
import argparse
import json
import math
import os
import subprocess
import sys

try:
    import resource  # Unix only: the CPU-time column is unavailable on Windows
except ImportError:
    resource = None

ROOT = os.path.dirname(os.path.abspath(__file__))
WARM = False
BATCH = 1
PIPELINE = False


def nm(mm):
    return int(math.floor(mm * 1e6 + 0.5))


def cpu_children():
    """CPU seconds spent by child processes so far, or None where `resource` is missing."""
    if resource is None:
        return None
    u = resource.getrusage(resource.RUSAGE_CHILDREN)
    return u.ru_utime + u.ru_stime


def ball_points(c, r, segments):
    n = max(4, segments // 2 * 2)
    pts = [[c[0], c[1], c[2] + r], [c[0], c[1], c[2] - r]]
    for i in range(1, n // 2):
        th = math.pi * i / (n // 2)
        for j in range(n):
            ph = 2 * math.pi * j / n
            pts.append([c[0] + r * math.sin(th) * math.cos(ph), c[1] + r * math.sin(th) * math.sin(ph), c[2] + r * math.cos(th)])
    return pts


def expand(scene):
    ball = scene["tool"]["ball"]
    centers = []
    path = scene["path"]
    for a, b in zip(path, path[1:]):
        # Either a fixed number of steps per path segment, or a step length in mm (same chip size everywhere).
        if "stepMm" in scene:
            k = max(1, math.ceil(math.dist(a, b) / scene["stepMm"]))
        else:
            k = scene.get("stepsPerSegment", 1)
        for i in range(k):
            t = i / k
            centers.append([a[d] + (b[d] - a[d]) * t for d in range(3)])
    centers.append(path[-1])
    steps = []
    for p, q in zip(centers, centers[1:]):
        pts = ball_points(p, ball["radius"], ball["segments"]) + ball_points(q, ball["radius"], ball["segments"])
        steps.append([[nm(v) for v in pt] for pt in pts])
    return {
        "box": {"min": [nm(v) for v in scene["box"]["min"]], "max": [nm(v) for v in scene["box"]["max"]]},
        "steps": steps,
        "save": scene.get("save", [-1]),
    }


def command(engine, expanded, out):
    if engine == "nanocut-ref":
        # NanoCut from another checkout (e.g. an older commit) for before/after comparisons: NANOCUT_REF=<repo dir>.
        ref = os.environ["NANOCUT_REF"]
        dll = os.path.join(ref, "bench/Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll")
        return ["dotnet", dll, expanded, out, "nanocut"] + (["--warm"] if WARM else []) + ["--batch", str(BATCH)]
    if engine == "nanocut-pipeline":
        dll = os.path.join(ROOT, "Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll")
        return ["dotnet", dll, expanded, out, "nanocut", "--pipeline"] + (["--warm"] if WARM else [])
    if engine in ("nanocut", "manifoldsharp"):
        dll = os.path.join(ROOT, "Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll")
        return ["dotnet", dll, expanded, out, engine] + (["--warm"] if WARM else []) + ["--batch", str(BATCH)]
    if engine == "cgal":
        if BATCH != 1:
            raise SystemExit("cgal: --batch is not supported")
        return [os.path.join(ROOT, "cgal/build/bench_cgal"), expanded, out]
    if engine == "manifold-pipeline":
        return [sys.executable, os.path.join(ROOT, "manifold/run.py"), expanded, out, "--pipeline"]
    if engine == "manifold":
        return [sys.executable, os.path.join(ROOT, "manifold/run.py"), expanded, out, "--batch", str(BATCH)]
    raise SystemExit(f"unknown engine {engine}")


def main():
    # The table has non-ASCII characters (ΔV, mm³, ×); a Windows console defaults to cp1252 and cannot print them.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("scene")
    ap.add_argument("--engines", default="nanocut,manifoldsharp,cgal,manifold")
    ap.add_argument("--repeat", type=int, default=1)
    ap.add_argument("--timeout", type=float, default=3600, help="seconds per engine run")
    ap.add_argument("--out", default=os.path.join(ROOT, "out"))
    ap.add_argument("--cold", action="store_true", help="C#: include JIT compilation (no in-process warm-up run)")
    ap.add_argument("--batch", type=int, default=1, help="cut k consecutive steps at once (united first)")
    a = ap.parse_args()
    global WARM, BATCH
    WARM = not a.cold
    BATCH = a.batch

    scene = json.load(open(a.scene))
    base = os.path.join(a.out, scene["name"] + (f"-batch{a.batch}" if a.batch != 1 else ""))
    os.makedirs(base, exist_ok=True)
    expanded = os.path.join(base, "expanded.json")
    json.dump(expand(scene), open(expanded, "w"))

    rows = []
    for engine in a.engines.split(","):
        out = os.path.join(base, engine)
        os.makedirs(out, exist_ok=True)
        best = None
        for _ in range(a.repeat):
            before = cpu_children()
            try:
                r = subprocess.run(command(engine, expanded, out), capture_output=True, text=True,
                                   encoding="utf-8", errors="replace", timeout=a.timeout)
            except subprocess.TimeoutExpired:
                print(f"{engine}: timeout after {a.timeout:.0f} s")
                break
            if r.returncode != 0:
                print(f"{engine} failed:\n{r.stdout}{r.stderr}")
                break
            after = cpu_children()
            s = json.load(open(os.path.join(out, "stats.json"), encoding="utf-8"))
            s["engine"] = engine
            # CPU seconds of the whole process (all threads, including start-up and any warm-up run).
            s["cpuS"] = None if before is None else after - before
            if best is None or s["totalMs"] < best["totalMs"]:
                best = s
        if best:
            rows.append(best)
            print(f"  {engine:14s} {best['totalMs']:10.0f} ms")

    ref = next((r for r in rows if r["engine"] == "nanocut"), rows[0] if rows else None)
    json.dump(rows, open(os.path.join(base, "results.json"), "w"), indent=1)
    lines = [f"Scene `{scene['name']}`: {scene.get('description', '')} "
             f"{len(json.load(open(expanded))['steps'])} steps, batch {a.batch}, C# {'cold (JIT included)' if a.cold else 'warm'}.", "",
             "| Engine | Language | Exact | Time (ms) | per step (ms) | CPU (s, whole process) | Volume (mm³) | ΔV vs NanoCut (mm³) | Triangles |",
             "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for r in rows:
        dv = r["volumeMm3"] - ref["volumeMm3"]
        cpu = "n/a" if r.get("cpuS") is None else f"{r['cpuS']:.1f}"
        lines.append(f"| {r['engine']} | {r['language']} | {'yes' if r['exact'] else 'no'} | {r['totalMs']:.0f} | "
                     f"{r['totalMs'] / r['steps']:.1f} | {cpu} | {r['volumeMm3']:.9f} | {dv:+.2e} | {r['triangles']} |")
    table = "\n".join(lines)
    open(os.path.join(base, "results.md"), "w", encoding="utf-8").write(table + "\n")
    print()
    print(table)


main()

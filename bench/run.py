"""Runs one bench scene with several engines and compares time and result.

Usage: python3 bench/run.py bench/scenes/ball-small.json [--engines nanocut,manifoldsharp,cgal,manifold] [--repeat 3]

The scene (mm) describes a box, a ball tool, a path ("from where to where") and the number of steps per path
segment. It is expanded once into explicit points on the 1 nm grid, so every engine gets the identical input: per
step the convex hull of the ball at the step's start and end points, subtracted from the workpiece in order.
"save" lists the states written as STL (step numbers, 0 = stock, -1 = final); default is only the final state.

Runs on Windows and on Unix: CPU time is taken from the child process via psutil when available, otherwise it is
omitted rather than guessed. The report carries median, min, max and the spread of every single run.
"""
import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.abspath(__file__))
WARM = True
PSUTIL = None

# The report uses real minus and delta signs; a cp1252 console cannot print them.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

try:
    import psutil
    PSUTIL = psutil
except ImportError:
    pass


def nm(mm):
    return int(math.floor(mm * 1e6 + 0.5))


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


def command(engine, expanded, out, repeat, par):
    if engine in ("nanocut", "manifoldsharp"):
        dll = os.path.join(ROOT, "Stykker.NanoCut.Bench/bin/Release/net10.0/Stykker.NanoCut.Bench.dll")
        cmd = ["dotnet", dll, expanded, out, engine]
        if WARM:
            cmd.append("--warm")
        cmd += ["--repeat", str(repeat)]
        if par:
            cmd += ["--par", str(par)]
        return cmd
    if engine == "cgal":
        return [os.path.join(ROOT, "cgal/build/bench_cgal"), expanded, out]
    if engine == "manifold":
        return [sys.executable, os.path.join(ROOT, "manifold/run.py"), expanded, out]
    raise SystemExit(f"unknown engine {engine}")


def run_once(engine, expanded, out, repeat, par, timeout):
    """One process, `repeat` timed runs inside it. Returns the list of per-run wall times in ms."""
    t0 = time.perf_counter()
    r = subprocess.run(command(engine, expanded, out, repeat, par),
                       capture_output=True, text=True, timeout=timeout)
    if r.returncode != 0:
        print(f"{engine} failed:\n{r.stdout}{r.stderr}")
        return None, None
    wall = (time.perf_counter() - t0) * 1e3
    s = json.load(open(os.path.join(out, "stats.json"), encoding="utf-8"))
    return s, wall


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scene")
    ap.add_argument("--engines", default="nanocut,manifoldsharp,cgal,manifold")
    ap.add_argument("--repeat", type=int, default=1, help="timed runs per engine process")
    ap.add_argument("--outer", type=int, default=1, help="engine processes per repeat (variance between processes)")
    ap.add_argument("--par", type=int, default=0, help="SolidBoolean.MaxParallelism (0 = default)")
    ap.add_argument("--timeout", type=float, default=3600, help="seconds per engine run")
    ap.add_argument("--out", default=os.path.join(ROOT, "out"))
    ap.add_argument("--cold", action="store_true", help="C#: include JIT compilation (no in-process warm-up run)")
    a = ap.parse_args()
    global WARM
    WARM = not a.cold

    scene = json.load(open(a.scene, encoding="utf-8"))
    base = os.path.join(a.out, scene["name"])
    os.makedirs(base, exist_ok=True)
    expanded = os.path.join(base, "expanded.json")
    json.dump(expand(scene), open(expanded, "w", encoding="utf-8"))
    nsteps = len(json.load(open(expanded, encoding="utf-8"))["steps"])

    rows = []
    for engine in a.engines.split(","):
        out = os.path.join(base, engine)
        os.makedirs(out, exist_ok=True)
        times, cpu, last = [], [], None
        for _ in range(max(1, a.outer)):
            s, wall = run_once(engine, expanded, out, a.repeat, a.par, a.timeout)
            if s is None:
                break
            last = s
            times += s.get("totalMsAll", [s["totalMs"]])
            if s.get("cpuS") is not None:
                cpu.append(s["cpuS"])
        if not last:
            continue
        row = dict(last)
        row["runsMs"] = times
        row["medianMs"] = statistics.median(times)
        row["minMs"] = min(times)
        row["maxMs"] = max(times)
        row["spreadPct"] = (max(times) - min(times)) / statistics.median(times) * 100
        rows.append(row)
        print(f"  {engine:14s} median {row['medianMs']:9.0f} ms  (min {row['minMs']:.0f}, max {row['maxMs']:.0f}, "
              f"spread {row['spreadPct']:.1f}%)")

    ref = next((r for r in rows if r["engine"] == "nanocut"), rows[0] if rows else None)
    lines = [f"Scene `{scene['name']}`: {scene.get('description', '')} "
             f"{nsteps} steps, C# {'cold (JIT included)' if a.cold else 'warm'}, "
             f"{a.repeat} timed run(s) x {max(1, a.outer)} process(es).", "",
             "| Engine | Language | Exact | Median (ms) | Min | Max | Spread | per step (ms) | CPU (s) | "
             "Alloc (MB) | Volume (mm³) | ΔV vs NanoCut (mm³) | Triangles |",
             "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for r in rows:
        dv = r["volumeMm3"] - ref["volumeMm3"]
        cpus = f"{statistics.median(cpu):.1f}" if cpu else "–"
        lines.append(f"| {r['engine']} | {r['language']} | {'yes' if r['exact'] else 'no'} | "
                     f"**{r['medianMs']:.0f}** | {r['minMs']:.0f} | {r['maxMs']:.0f} | {r['spreadPct']:.1f}% | "
                     f"{r['medianMs'] / nsteps:.1f} | {cpus} | {r.get('allocatedMb', 0):.0f} | {r['volumeMm3']:.9f} | "
                     f"{dv:+.2e} | {r['triangles']} |")

    # Kernel-internal split, NanoCut only: hull construction vs. the Boolean itself.
    n = next((r for r in rows if r["engine"] == "nanocut"), None)
    if n and n.get("hullMs"):
        lines += ["", f"NanoCut kernel split: hull {n['hullMs']:.0f} ms, boolean {n['booleanMs']:.0f} ms, "
                      f"GC gen0 {n.get('gen0', 0)} / gen1 {n.get('gen1', 0)} / gen2 {n.get('gen2', 0)}, "
                      f"pause {n.get('gcPauseMs', 0):.0f} ms, slowest step {n.get('maxStepMs', 0):.0f} ms."]

    table = "\n".join(lines)
    open(os.path.join(base, "results.md"), "w", encoding="utf-8").write(table + "\n")
    # Machine-readable companion, so a report can be regenerated from data instead of typed by hand.
    with open(os.path.join(base, "results.json"), "w", encoding="utf-8") as f:
        json.dump({"scene": scene["name"], "steps": nsteps, "warm": WARM, "repeat": a.repeat,
                   "outer": max(1, a.outer), "maxParallelism": a.par or None, "rows": rows}, f, indent=2)
    print()
    print(table)


if __name__ == "__main__":
    main()

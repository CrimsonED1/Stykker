using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// Grinding with individual grains: every grain of a <see cref="GrindingWheel"/> follows its trochoid (feed ∘ spin)
/// and cuts the workpiece only during the short intervals in which it is inside the workpiece's bounding box
/// (found by sampling the grain's bounding sphere at ≤ 0.25° of spindle rotation). Each such pass is cut with
/// <see cref="Process3"/> (the grain is tiny, so the hull steps of the rotation are few), in time order over all
/// grains, so a grain only removes what earlier grains left. Per pass the removed volume, the largest depth of the
/// grain tip below the surface it meets (undeformed chip thickness) and the contact angles are recorded.
/// <para>
/// Use <see cref="AdvanceTo"/> to compute step by step (e.g. frames for playback) or <see cref="Run"/> for all.
/// </para>
/// </summary>
public sealed class GrindingSimulation
{
    /// <summary>One pass of a grain through the workpiece bounds.</summary>
    /// <param name="Grain">Grain index.</param>
    /// <param name="StartSeconds">Start of the pass.</param>
    /// <param name="EndSeconds">End of the pass.</param>
    public sealed record Pass(int Grain, double StartSeconds, double EndSeconds)
    {
        /// <summary>True once the pass has been cut.</summary>
        public bool Done { get; internal set; }

        /// <summary>Removed volume in mm³.</summary>
        public double RemovedMm3 { get; internal set; }

        /// <summary>Largest depth of the grain tip below the surface before the pass (undeformed chip thickness), mm.</summary>
        public double ChipThicknessMm { get; internal set; }

        /// <summary>Contact angle (°) where the tip enters the material, see <see cref="GrainResult.EntryAngleDeg"/>.</summary>
        public double EntryAngleDeg { get; internal set; } = double.NaN;

        /// <summary>Contact angle (°) where the tip leaves the material.</summary>
        public double ExitAngleDeg { get; internal set; } = double.NaN;
    }

    /// <summary>Accumulated result of one grain.</summary>
    public sealed class GrainResult
    {
        internal GrainResult(int index) => Index = index;

        /// <summary>Grain index.</summary>
        public int Index { get; }

        /// <summary>Passes through the workpiece bounds so far.</summary>
        public int Passes { get; internal set; }

        /// <summary>Passes that removed material.</summary>
        public int ActivePasses { get; internal set; }

        /// <summary>True if the grain removed material.</summary>
        public bool Active => ActivePasses > 0;

        /// <summary>Total removed volume in mm³.</summary>
        public double RemovedMm3 { get; internal set; }

        /// <summary>Largest undeformed chip thickness (tip depth below the surface met) in mm.</summary>
        public double MaxChipThicknessMm { get; internal set; }

        /// <summary>
        /// Smallest contact angle (°) at which the tip was in material. The angle is that of the grain's radial
        /// direction in the feed frame, measured from −z (the lowest point of the wheel) towards +x.
        /// </summary>
        public double EntryAngleDeg { get; internal set; } = double.NaN;

        /// <summary>Largest contact angle (°) at which the tip was in material.</summary>
        public double ExitAngleDeg { get; internal set; } = double.NaN;
    }

    private readonly GrindingWheel _wheel;
    private readonly Motion3 _feed;
    private readonly double[] _durations;
    private readonly double _start;
    private readonly Tolerance _tol;
    private readonly List<Pass> _passes;
    private readonly GrainResult[] _grains;
    private readonly ToolShape[] _grainTools;
    private int _next;

    /// <summary>Plans the passes of all grains; nothing is cut yet.</summary>
    /// <param name="workpiece">The workpiece (feed frame).</param>
    /// <param name="wheel">The wheel (spindle axis = tool z).</param>
    /// <param name="feed">Motion of the wheel frame.</param>
    /// <param name="segmentSeconds">Duration of each feed segment (see <see cref="SpinningTool.Durations"/>).</param>
    /// <param name="tol">Tolerance (chord, sweep); the sweep error should be small against the chip thickness.</param>
    /// <param name="startSeconds">Process time at the start (spindle phase).</param>
    public GrindingSimulation(Solid workpiece, GrindingWheel wheel, Motion3 feed, IReadOnlyList<double> segmentSeconds, Tolerance? tol, double startSeconds = 0)
    {
        if (segmentSeconds.Count != feed.Segments.Count) throw new ArgumentException("One duration per feed segment required.", nameof(segmentSeconds));
        Workpiece = workpiece;
        _wheel = wheel;
        _feed = feed;
        _durations = [.. segmentSeconds];
        _start = startSeconds;
        _tol = tol ?? Tolerance.Default;
        Seconds = startSeconds;
        EndSeconds = startSeconds + _durations.Sum();
        _grains = wheel.Grains.Select(g => new GrainResult(g.Index)).ToArray();
        _grainTools = wheel.Grains.Select(g => ToolShape.FromConvexParts(g.Shape)).ToArray();
        _passes = PlanPasses(workpiece);
        foreach (var p in _passes) _grains[p.Grain].Passes++;
    }

    /// <summary>The workpiece after all passes cut so far.</summary>
    public Solid Workpiece { get; private set; }

    /// <summary>Process time reached so far.</summary>
    public double Seconds { get; private set; }

    /// <summary>Process time at the end of the feed.</summary>
    public double EndSeconds { get; }

    /// <summary>All planned passes in time order.</summary>
    public IReadOnlyList<Pass> Passes => _passes;

    /// <summary>Per-grain results (index = grain index).</summary>
    public IReadOnlyList<GrainResult> Grains => _grains;

    /// <summary>Number of grains that removed material.</summary>
    public int ActiveGrains => _grains.Count(g => g.Active);

    /// <summary>Active grains ÷ all grains.</summary>
    public double ActiveRatio => (double)ActiveGrains / _grains.Length;

    /// <summary>Total removed volume so far in mm³.</summary>
    public double RemovedMm3 => _grains.Sum(g => g.RemovedMm3);

    /// <summary>Convex hull pieces swept by all grain passes so far.</summary>
    public int Hulls { get; private set; }

    /// <summary>Cuts all passes that start before <paramref name="seconds"/>. Returns true while passes remain.</summary>
    public bool AdvanceTo(double seconds)
    {
        while (_next < _passes.Count && _passes[_next].StartSeconds < seconds) CutPass(_passes[_next++]);
        Seconds = Math.Max(Seconds, Math.Min(seconds, EndSeconds));
        return _next < _passes.Count;
    }

    /// <summary>Cuts all remaining passes.</summary>
    public void Run() => AdvanceTo(double.PositiveInfinity);

    /// <summary>Pose of the wheel frame (feed ∘ spin) at process time <paramref name="seconds"/>.</summary>
    public Pose3 PoseAt(double seconds) => FeedAt(seconds).Compose(_wheel.Spindle.SpinPose(seconds));

    /// <summary>Pose of the feed alone (without spin) at process time <paramref name="seconds"/>.</summary>
    public Pose3 FeedAt(double seconds)
    {
        double t = seconds - _start;
        for (int i = 0; i < _durations.Length; i++)
        {
            if (t <= _durations[i] || i == _durations.Length - 1)
                return _feed.Segments[i](_durations[i] > 0 ? Math.Clamp(t / _durations[i], 0, 1) : 1);
            t -= _durations[i];
        }
        return _feed.Segments[^1](1);
    }

    private List<Pass> PlanPasses(Solid workpiece)
    {
        var passes = new List<Pass>();
        if (workpiece.BoundsMm is not { } b) return passes;
        double omega = Math.Abs(_wheel.Spindle.AngularVelocityRadPerS);
        double total = EndSeconds - _start;
        double dt = omega > 0 ? 0.25 * Math.PI / 180 / omega : Math.Max(total, 1e-9) / 64;
        int n = Math.Max(2, (int)Math.Ceiling(total / dt) + 1);
        var times = new double[n];
        var poses = new Pose3[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = _start + total * i / (n - 1);
            poses[i] = PoseAt(times[i]);
        }
        const double k = 1e6;
        foreach (var g in _wheel.Grains)
        {
            var verts = g.Shape.Vertices.Select(v => (v.X, v.Y, v.Z)).Distinct().ToArray();
            double cx = verts.Average(v => v.X), cy = verts.Average(v => v.Y), cz = verts.Average(v => v.Z);
            double rho = verts.Max(v => Math.Sqrt((v.X - cx) * (v.X - cx) + (v.Y - cy) * (v.Y - cy) + (v.Z - cz) * (v.Z - cz)));
            rho += _tol.SweepNm + 1;
            int first = -1;
            for (int i = 0; i < n; i++)
            {
                var (x, y, z) = poses[i].Apply(cx, cy, cz);
                bool inside = x >= b.MinX * k - rho && x <= b.MaxX * k + rho && y >= b.MinY * k - rho && y <= b.MaxY * k + rho
                              && z >= b.MinZ * k - rho && z <= b.MaxZ * k + rho;
                if (inside && first < 0) first = i;
                if ((!inside || i == n - 1) && first >= 0)
                {
                    int last = inside ? i : i - 1;
                    passes.Add(new Pass(g.Index, times[Math.Max(0, first - 1)], times[Math.Min(n - 1, last + 1)]));
                    first = -1;
                }
            }
        }
        passes.Sort((p, q) => p.StartSeconds.CompareTo(q.StartSeconds));
        return passes;
    }

    private void CutPass(Pass pass)
    {
        var grain = _wheel.Grains[pass.Grain];
        double t0 = pass.StartSeconds, t1 = pass.EndSeconds;
        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(_wheel.Spindle.AngleAt(t1) - _wheel.Spindle.AngleAt(t0)) / (Math.PI / 4)));
        var motion = Motion3.Sequence([.. Enumerable.Range(0, pieces).Select(j =>
            Motion3.Custom(u => PoseAt(t0 + (t1 - t0) * (j + u) / pieces)))]);
        var before = Workpiece;
        var after = Process3.Cut([before], _grainTools[pass.Grain], motion, _tol, out var stats)[0];
        Hulls += stats.Hulls;
        pass.Done = true;
        if (!ReferenceEquals(after, before))
        {
            double removed = before.VolumeMm3 - after.VolumeMm3;
            if (removed > 1e-15)
            {
                pass.RemovedMm3 = removed;
                MeasureChip(pass, grain, before);
                var r = _grains[pass.Grain];
                r.ActivePasses++;
                r.RemovedMm3 += removed;
                r.MaxChipThicknessMm = Math.Max(r.MaxChipThicknessMm, pass.ChipThicknessMm);
                if (!double.IsNaN(pass.EntryAngleDeg))
                {
                    r.EntryAngleDeg = double.IsNaN(r.EntryAngleDeg) ? pass.EntryAngleDeg : Math.Min(r.EntryAngleDeg, pass.EntryAngleDeg);
                    r.ExitAngleDeg = double.IsNaN(r.ExitAngleDeg) ? pass.ExitAngleDeg : Math.Max(r.ExitAngleDeg, pass.ExitAngleDeg);
                }
            }
            Workpiece = after;
        }
    }

    // Tip depth below the surface before the pass, sampled along the tip's path.
    private void MeasureChip(Pass pass, GrindingWheel.Grain grain, Solid before)
    {
        var surface = new SurfaceProfile(before);
        const int samples = 48;
        double lo = double.NaN, hi = double.NaN, depth = 0;
        for (int i = 0; i <= samples; i++)
        {
            double t = pass.StartSeconds + (pass.EndSeconds - pass.StartSeconds) * i / samples;
            var pose = PoseAt(t);
            var (x, y, z) = pose.Apply(grain.Tip.X, grain.Tip.Y, grain.Tip.Z);
            double top = surface.TopZ(x / Units.NmPerMm, y / Units.NmPerMm);
            double d = top - z / Units.NmPerMm;
            if (double.IsNaN(d) || d <= 0) continue;
            depth = Math.Max(depth, d);
            // Contact angle of the radial direction (centre → tip, in the feed frame) from −z towards +x.
            var (ox, _, oz) = pose.Apply(0, 0, 0);
            var (ax, ay, az) = pose.Apply(0, 0, 1);
            double rx = x - ox, ry = y - pose.TyNm, rz = z - oz;
            // Remove the axial component.
            double axx = ax - ox, axy = ay - pose.TyNm, axz = az - oz, dot = rx * axx + ry * axy + rz * axz;
            rx -= dot * axx; rz -= dot * axz;
            double angle = Math.Atan2(rx, -rz) * 180 / Math.PI;
            lo = double.IsNaN(lo) ? angle : Math.Min(lo, angle);
            hi = double.IsNaN(hi) ? angle : Math.Max(hi, angle);
        }
        pass.ChipThicknessMm = depth;
        pass.EntryAngleDeg = lo;
        pass.ExitAngleDeg = hi;
    }
}

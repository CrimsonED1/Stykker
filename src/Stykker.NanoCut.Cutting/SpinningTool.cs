using Stykker.NanoCut.Geometry2D;

namespace Stykker.NanoCut.Cutting;

/// <summary>
/// A tool that spins about its own axis (the tool's z-axis through its origin) at a constant speed while it is fed
/// along a <see cref="Motion3"/>: grinding wheels, cut-off discs, circular saw blades, disc and slitting cutters.
/// <para>
/// The tool is split into two parts with different physics:
/// </para>
/// <list type="bullet">
/// <item><see cref="Body"/>: rotationally symmetric about the spindle axis. Spinning maps such a body onto itself, so
/// the volume it sweeps does not depend on the spin at all – it is cut with the feed motion alone (exact for
/// translations and fast). The spin is never sampled for it.</item>
/// <item><see cref="Cutters"/>: everything else (teeth, inserts, any non-symmetric shape). Its motion is the feed
/// composed with the spindle rotation in time (<see cref="Motion"/>), so the material removed per tooth
/// (feed per tooth = feed speed ÷ (rpm / 60 · teeth)) follows from the geometry.</item>
/// </list>
/// <para>
/// The polyhedral body is an inscribed polygon prism with n-fold symmetry; treating it as spin-invariant changes the
/// result by at most the chord error it was built with (<see cref="Tolerance.ChordNm"/>).
/// </para>
/// </summary>
public sealed class SpinningTool
{
    private SpinningTool(ToolShape? body, ToolShape? cutters, double rpm, int teeth, (Region2 Profile, double Z0Mm, double Z1Mm)? prism = null)
    {
        if (body is null && cutters is null) throw new ArgumentException("A spinning tool needs a body or cutters.");
        if (!double.IsFinite(rpm)) throw new ArgumentOutOfRangeException(nameof(rpm));
        if (teeth < 0) throw new ArgumentOutOfRangeException(nameof(teeth));
        Body = body;
        Cutters = cutters;
        Rpm = rpm;
        Teeth = teeth;
        CutterPrism = prism;
    }

    /// <summary>
    /// If the cutters are a prism along the spindle axis (a planar profile in the tool xy-plane extruded from z0 to
    /// z1, e.g. a saw blade with straight teeth), its description; otherwise null. A feed in the plane of such a tool
    /// is cut with the planar process (see <see cref="Process3.CutSpinning(IReadOnlyList{Geometry3D.Solid}, SpinningTool, Motion3, IReadOnlyList{double}, Tolerance?, out Process3.SpinStats, double, bool)"/>).
    /// </summary>
    public (Region2 Profile, double Z0Mm, double Z1Mm)? CutterPrism { get; }

    /// <summary>The spin-invariant (rotationally symmetric about the tool z-axis) part, or null.</summary>
    public ToolShape? Body { get; }

    /// <summary>The spin-dependent part (teeth or any non-symmetric shape), or null for a plain wheel.</summary>
    public ToolShape? Cutters { get; }

    /// <summary>Spindle speed in revolutions per minute (positive: right-hand rule about +z).</summary>
    public double Rpm { get; }

    /// <summary>Number of teeth (cutting edges per revolution), 0 for a plain, rotationally symmetric tool.</summary>
    public int Teeth { get; }

    /// <summary>True if the swept volume does not depend on the spin (no <see cref="Cutters"/>).</summary>
    public bool IsSpinInvariant => Cutters is null;

    /// <summary>Angular velocity in rad/s (2π · rpm / 60).</summary>
    public double AngularVelocityRadPerS => Rpm * 2 * Math.PI / 60;

    /// <summary>Spindle angle (rad) after <paramref name="seconds"/>, starting at angle 0 at time 0.</summary>
    public double AngleAt(double seconds) => AngularVelocityRadPerS * seconds;

    /// <summary>The spindle rotation about the tool z-axis after <paramref name="seconds"/> (in the tool frame).</summary>
    public Pose3 SpinPose(double seconds) => Pose3.Rotation(AngleAt(seconds), 0, 0, 1);

    /// <summary>Feed per revolution in mm at a feed speed in mm/s.</summary>
    public double FeedPerRevolutionMm(double feedMmPerS) => Rpm == 0 ? double.PositiveInfinity : feedMmPerS / Math.Abs(Rpm / 60);

    /// <summary>Feed per tooth f_z = v_f ÷ (n · z) in mm at a feed speed in mm/s (∞ without teeth or spin).</summary>
    public double FeedPerToothMm(double feedMmPerS) => Teeth == 0 ? double.PositiveInfinity : FeedPerRevolutionMm(feedMmPerS) / Teeth;

    /// <summary>All parts (body and cutters) as one shape, e.g. for display.</summary>
    public ToolShape Shape => Body is null ? Cutters! : Cutters is null ? Body : ToolShape.FromConvexParts([.. Body.Parts, .. Cutters.Parts]);

    /// <summary>
    /// A rotationally symmetric tool (grinding wheel, cut-off disc, any <see cref="ToolShape.Revolved"/> shape) about
    /// the tool z-axis. Symmetry is the caller's responsibility; the spin is ignored when cutting because it cannot
    /// change the swept volume.
    /// </summary>
    public static SpinningTool Symmetric(ToolShape body, double rpm) => new(body, null, rpm, 0);

    /// <summary>Plain disc (grinding wheel, cut-off disc) of the given radius and thickness, centred on z = 0.</summary>
    public static SpinningTool Disc(double radiusMm, double thicknessMm, double rpm, Tolerance? tol = null) =>
        Symmetric(ToolShape.FromConvexParts(Geometry3D.Solid.Cylinder(Vec3.Mm(0, 0, -thicknessMm / 2), Vec3.Mm(0, 0, thicknessMm / 2), radiusMm, tol)), rpm);

    /// <summary>
    /// Toothed tool: a rotationally symmetric <paramref name="body"/> (may be null) plus non-symmetric
    /// <paramref name="cutters"/> that are swept with the spin.
    /// </summary>
    public static SpinningTool Toothed(ToolShape? body, ToolShape cutters, int teeth, double rpm) =>
        new(body, cutters ?? throw new ArgumentNullException(nameof(cutters)), rpm, teeth);

    /// <summary>
    /// Toothed tool whose cutters are a prism along the spindle axis: <paramref name="cutterProfile"/> (tool xy-plane,
    /// mm) extruded from <paramref name="z0Mm"/> to <paramref name="z1Mm"/>, plus an optional rotationally symmetric
    /// <paramref name="body"/>. Feeds in the tool plane then use the fast planar edge sweep.
    /// </summary>
    public static SpinningTool Prismatic(ToolShape? body, Region2 cutterProfile, double z0Mm, double z1Mm, int teeth, double rpm) =>
        new(body, ToolShape.Extruded(cutterProfile, z0Mm, z1Mm), rpm, teeth, (cutterProfile, Math.Min(z0Mm, z1Mm), Math.Max(z0Mm, z1Mm)));

    /// <summary>
    /// Any shape without rotational symmetry: every part is swept with the spin (the general, slowest case).
    /// <paramref name="teeth"/> is only used for <see cref="FeedPerToothMm"/>.
    /// </summary>
    public static SpinningTool Asymmetric(ToolShape shape, double rpm, int teeth = 1) => new(null, shape, rpm, teeth);

    /// <summary>
    /// Circular saw blade from <see cref="ToolShape.SawBlade"/>: the core disc is the spin-invariant body, the teeth
    /// are the cutters.
    /// </summary>
    public static SpinningTool SawBlade(double radiusMm, double thicknessMm, int teeth, double toothHeightMm, double rpm,
        Tolerance? tol = null, double? toothThicknessMm = null)
    {
        var core = ToolShape.SawBlade(radiusMm, thicknessMm, teeth, toothHeightMm, tol, toothThicknessMm).Parts[0];
        double kerf = toothThicknessMm ?? thicknessMm;
        return Prismatic(ToolShape.FromConvexParts(core), ToolShape.SawBladeTeeth(radiusMm, teeth, toothHeightMm, tol), -kerf / 2, kerf / 2, teeth, rpm);
    }

    /// <summary>
    /// Durations (s) of the feed segments at a constant feed speed: the length of the path of the tool origin
    /// (integrated numerically) divided by <paramref name="feedMmPerS"/>.
    /// </summary>
    public static double[] Durations(Motion3 feed, double feedMmPerS)
    {
        if (!(feedMmPerS > 0)) throw new ArgumentOutOfRangeException(nameof(feedMmPerS), "Feed speed must be positive.");
        const int n = 64;
        return feed.Segments.Select(seg =>
        {
            double len = 0;
            var p = seg(0);
            for (int i = 1; i <= n; i++)
            {
                var q = seg((double)i / n);
                len += Math.Sqrt((q.TxNm - p.TxNm) * (q.TxNm - p.TxNm) + (q.TyNm - p.TyNm) * (q.TyNm - p.TyNm) + (q.TzNm - p.TzNm) * (q.TzNm - p.TzNm));
                p = q;
            }
            return len / Units.NmPerMm / feedMmPerS;
        }).ToArray();
    }

    /// <summary>
    /// The combined motion of the spinning tool: pose(t) = feed(t) ∘ spin(time(t)), where segment i of the feed lasts
    /// <paramref name="segmentSeconds"/>[i] and the first segment starts at <paramref name="startSeconds"/>. Each feed
    /// segment is split into pieces of at most <paramref name="maxPieceRad"/> spindle rotation, so that whole
    /// revolutions can never cancel out between the sampled ends of a piece.
    /// </summary>
    public Motion3 Motion(Motion3 feed, IReadOnlyList<double> segmentSeconds, double startSeconds = 0, double maxPieceRad = Math.PI / 4)
    {
        if (segmentSeconds.Count != feed.Segments.Count) throw new ArgumentException("One duration per feed segment required.", nameof(segmentSeconds));
        if (!(maxPieceRad > 0)) throw new ArgumentOutOfRangeException(nameof(maxPieceRad));
        var pieces = new List<Motion3>();
        double t0 = startSeconds;
        for (int i = 0; i < feed.Segments.Count; i++)
        {
            var seg = feed.Segments[i];
            double start = t0, dur = segmentSeconds[i];
            if (!(dur >= 0)) throw new ArgumentOutOfRangeException(nameof(segmentSeconds), "Durations must be ≥ 0.");
            int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(AngularVelocityRadPerS) * dur / maxPieceRad - 1e-9));
            for (int j = 0; j < m; j++)
            {
                int jj = j;
                pieces.Add(Motion3.Custom(t =>
                {
                    double u = (jj + t) / m;
                    return seg(u).Compose(SpinPose(start + u * dur));
                }));
            }
            t0 += dur;
        }
        return Motion3.Sequence([.. pieces]);
    }
}

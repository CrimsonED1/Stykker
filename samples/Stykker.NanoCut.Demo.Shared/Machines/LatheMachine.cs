using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Demo.Scenes;
using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Demo.Machines;

/// <summary>2-axis lathe: X is the diameter, Z the spindle axis. The workpiece spins; the insert cuts in the r–z plane.</summary>
public sealed class LatheMachine : Machine
{
    private Region2 _profile = Region2.Empty;
    private Region2 _insert = Region2.Empty;
    private Tolerance _tol = Tolerance.Default;
    private double _x, _z, _barVolume;

    public override string Title => "Lathe (X = diameter, Z)";
    public override string[] Axes => ["X", "Z"];
    public override string AxisNote => "X is programmed as diameter. The bar spans Z 0 … 40; the insert has a nose radius.";
    public override double[] Position => [_x, _z];

    public override IReadOnlyList<Param> Settings { get; } =
    [
        new("d", "Bar diameter", 30, 6, 80, 1, "mm"),
        new("nose", "Insert nose radius", 0.4, 0.1, 2, 0.1, "mm"),
        new("chord", "Chord error (display & profile)", 2000, 50, 20000, 50, "nm"),
    ];

    public override string DefaultProgram =>
        "; roughing passes along Z, then a taper and a groove\n" +
        "G0 X34 Z42\nG0 X26\nG1 Z10\nG0 X34\nG0 Z42\nG0 X22\nG1 Z20\nG1 X26 Z14\nG0 X34\nG0 Z42\n" +
        "G0 X18\nG1 Z30\nG1 X22 Z26\nG0 X34\nG0 Z8\nG1 X16\nG1 X34\n";

    public override void Reset(IReadOnlyDictionary<string, double> s)
    {
        ClearHistory();
        _tol = Tolerance.Budget(totalUm: (s["chord"] + 51) / 1000, chordNm: s["chord"]);
        double r = s["d"] / 2, nose = s["nose"];
        _profile = Lathe.BarProfile(r, 0, 40);
        _barVolume = Math.PI * r * r * 40;
        var tip = Shapes2.CirclePoints(Vec2.Mm(nose, 0), nose * Units.NmPerMm, _tol.ChordNm);
        // 80° diamond-like insert, tip pointing to the axis (−r), reference point = innermost nose point.
        _insert = Region2.Polygon(ConvexHull.Compute(tip.Concat([Vec2.Mm(7, 2.5), Vec2.Mm(7, -2.5)])));
        _x = s["d"] + 4;
        _z = 42;
    }

    protected override object Snapshot() => (_profile, _x, _z);

    protected override void Restore(object snapshot) => (_profile, _x, _z) = ((Region2, double, double))snapshot;

    protected override double DoMove(Move move)
    {
        double tx = move.X ?? _x, tz = move.Z ?? _z;
        double before = PartVolume();
        var path = Motion2.Linear(Vec2.Mm(_x / 2, _z), Vec2.Mm(tx / 2, tz));
        _profile = Process2.Cut(_profile, _insert, path, _tol);
        (_x, _z) = (tx, tz);
        return before - PartVolume();
    }

    /// <summary>Exact volume of the revolved profile (Pappus: 2π ∬ r dA).</summary>
    private double PartVolume()
    {
        double v = 0;
        foreach (var c in _profile.Contours)
            for (int i = 0; i < c.Count; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % c.Count];
                v += (a.XMm * b.YMm - b.XMm * a.YMm) * (a.XMm + b.XMm) / 6;
            }
        return 2 * Math.PI * v;
    }

    public override SceneResult Render()
    {
        var res = new SceneResult { View = "lathe" };
        var part = Solid.Revolve(_profile, _tol);
        res.Add("Workpiece", part, "#9fb4c8");
        var insert = Solid.Extrude(_insert, -1, 1, Pose3.TranslationMm(_x / 2, 0, _z).Compose(Pose3.Rotation(Math.PI / 2, 1, 0, 0)));
        res.Add("Insert", insert, "#ff8a3d", 0.75);
        res.Metric("Removed so far", (_barVolume - PartVolume()).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " mm³");
        res.Metric("Profile vertices", _profile.Contours.Sum(c => c.Count).ToString());
        res.Metric("Moves", MoveCount.ToString());
        res.Result = part;
        return res;
    }
}

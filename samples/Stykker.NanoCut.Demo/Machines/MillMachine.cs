using Stykker.NanoCut.Cutting;
using Stykker.NanoCut.Demo.Scenes;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Demo.Machines;

/// <summary>3-axis mill: the tool tip moves in X, Y, Z over a 60 × 40 × 20 mm block.</summary>
public sealed class MillMachine : Machine
{
    private Solid _part = Solid.Empty;
    private ToolShape _tool = ToolShape.Ball(1);
    private Tolerance _tol = Tolerance.Default;
    private double _x, _y, _z, _blockVolume;

    public override string Title => "3-axis mill (X, Y, Z)";
    public override string[] Axes => ["X", "Y", "Z"];
    public override string AxisNote => "Block 0 … 60 × 0 … 40 × 0 … 20 mm. Positions refer to the tool tip.";
    public override double[] Position => [_x, _y, _z];

    public override IReadOnlyList<Param> Settings { get; } =
    [
        new("tool", "Tool", 0, Choices: ["Ball-nose end mill", "Flat end mill", "Chamfer mill 90°"]),
        new("r", "Tool radius", 4, 1, 10, 0.5, "mm"),
        new("chord", "Chord error", 3000, 500, 20000, 100, "nm"),
    ];

    public override string DefaultProgram =>
        "; pocket and slot\n" +
        "G0 X12 Y12 Z25\nG1 Z16\nG1 X48\nG1 Y20\nG1 X12\nG1 Y28\nG1 X48\nG0 Z25\n" +
        "G0 X8 Y36 Z25\nG1 Z17\nG1 X52 Y36\nG0 Z25\n";

    public override void Reset(IReadOnlyDictionary<string, double> s)
    {
        ClearHistory();
        _tol = Tolerance.Budget(totalUm: (s["chord"] + 51) / 1000, chordNm: s["chord"]);
        double r = s["r"];
        _tool = (int)s["tool"] switch
        {
            1 => ToolShape.Revolved(_tol, (r, 0), (r, 30)),
            2 => ToolShape.Revolved(_tol, (0, 0), (r, r), (r, 30)),
            _ => ToolShape.BallNoseMill(r, 30, _tol),
        };
        _part = Solid.Box(Vec3.Mm(0, 0, 0), Vec3.Mm(60, 40, 20), _tol);
        _blockVolume = _part.VolumeMm3;
        (_x, _y, _z) = (30, 20, 30);
    }

    protected override object Snapshot() => (_part, _x, _y, _z);

    protected override void Restore(object snapshot) => (_part, _x, _y, _z) = ((Solid, double, double, double))snapshot;

    protected override double DoMove(Move move)
    {
        double tx = move.X ?? _x, ty = move.Y ?? _y, tz = move.Z ?? _z;
        double before = _part.VolumeMm3;
        _part = Process3.Cut(_part, _tool, Motion3.Linear(Vec3.Mm(_x, _y, _z), Vec3.Mm(tx, ty, tz)), _tol);
        (_x, _y, _z) = (tx, ty, tz);
        return before - _part.VolumeMm3;
    }

    public override SceneResult Render()
    {
        var res = new SceneResult { View = "mill" };
        res.Add("Workpiece", _part, "#9fb4c8");
        res.Add("Tool", _tool.ToSolid().Transform(Pose3.TranslationMm(_x, _y, _z)), "#ff8a3d", 0.45);
        res.Metric("Removed so far", (_blockVolume - _part.VolumeMm3).ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " mm³");
        res.Metric("Faces", _part.FaceCount.ToString());
        res.Metric("Moves", MoveCount.ToString());
        res.Result = _part;
        return res;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using Stykker.NanoCut.Demo.Scenes;

namespace Stykker.NanoCut.Demo.Machines;

/// <summary>One programmed or jogged move.</summary>
public sealed record Move(bool Rapid, double? X, double? Y, double? Z);

/// <summary>
/// A virtual machine: axes, a workpiece, a tool. Every move cuts – the material swept by the tool is removed exactly.
/// Rapid moves (G0) that remove material are reported as collisions.
/// </summary>
public abstract class Machine
{
    private readonly Stack<object> _history = new();

    public abstract string Title { get; }
    public abstract string[] Axes { get; }
    public abstract string AxisNote { get; }
    public abstract double[] Position { get; }
    public abstract string DefaultProgram { get; }
    public abstract IReadOnlyList<Param> Settings { get; }
    public List<string> Log { get; } = [];
    public int MoveCount { get; private set; }

    public abstract void Reset(IReadOnlyDictionary<string, double> settings);

    /// <summary>Executes one move from the current position; returns the removed amount (mm³) and time.</summary>
    public (double Removed, long Ms) Execute(Move move)
    {
        _history.Push(Snapshot());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double removed = DoMove(move);
        sw.Stop();
        MoveCount++;
        string target = string.Join(" ", Axes.Select((a, i) => $"{a}{Position[i].ToString("0.###", CultureInfo.InvariantCulture)}"));
        string note = removed > 1e-9 ? $"removed {removed.ToString("0.######", CultureInfo.InvariantCulture)} mm³" : "air";
        if (move.Rapid && removed > 1e-9) note = "COLLISION (rapid into material), " + note;
        Log.Add($"{(move.Rapid ? "G0" : "G1")} {target}  · {note} · {sw.ElapsedMilliseconds} ms");
        return (removed, sw.ElapsedMilliseconds);
    }

    public bool Undo()
    {
        if (_history.Count == 0) return false;
        Restore(_history.Pop());
        Log.Add("undo");
        return true;
    }

    protected void ClearHistory()
    {
        _history.Clear();
        Log.Clear();
        MoveCount = 0;
    }

    protected abstract object Snapshot();
    protected abstract void Restore(object snapshot);
    protected abstract double DoMove(Move move);

    /// <summary>Geometry for the viewer.</summary>
    public abstract SceneResult Render();

    /// <summary>Parses a minimal G-code subset: G0/G1 with absolute X, Y, Z (mm); ';' and '( )' comments.</summary>
    public static List<Move> Parse(string program)
    {
        var moves = new List<Move>();
        bool rapid = false;
        foreach (var raw in program.Split('\n'))
        {
            var line = Regex.Replace(raw, @"\(.*?\)", "");
            int sc = line.IndexOf(';');
            if (sc >= 0) line = line[..sc];
            line = line.Trim().ToUpperInvariant();
            if (line.Length == 0) continue;
            double? x = null, y = null, z = null;
            foreach (Match m in Regex.Matches(line, @"([GXYZ])\s*(-?\d+(?:\.\d*)?|-?\.\d+)"))
            {
                double v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                switch (m.Groups[1].Value)
                {
                    case "G" when v == 0: rapid = true; break;
                    case "G" when v == 1: rapid = false; break;
                    case "X": x = v; break;
                    case "Y": y = v; break;
                    case "Z": z = v; break;
                }
            }
            if (x is not null || y is not null || z is not null) moves.Add(new Move(rapid, x, y, z));
        }
        return moves;
    }
}

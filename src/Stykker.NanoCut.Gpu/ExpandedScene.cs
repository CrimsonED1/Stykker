using System.Text.Json;

namespace Stykker.NanoCut.Gpu;

/// <summary>
/// An expanded bench scene, the <c>bench/out/&lt;scene&gt;/expanded.json</c> that <c>bench/run.py</c> writes and every
/// engine reads: the workpiece box and, per step, the points of the convex hull the exact kernel subtracts. All
/// coordinates are integers on the 1 nm grid.
/// </summary>
/// <remarks>
/// The Z-map works with capsules rather than point clouds, so the loader reconstructs the ball positions of each
/// step from the points: <c>run.py</c> builds a step as the points of the ball at the start position followed by the
/// points of the ball at the end position, and each ball starts with its two poles. The poles therefore give both the
/// centre (their midpoint) and the radius (half their distance). The loader checks this for every step and throws
/// rather than guess, so a scene expanded by a different tool shape fails loudly.
/// </remarks>
public sealed class ExpandedScene
{
    private ExpandedScene((double X, double Y, double Z) min, (double X, double Y, double Z) max,
        BallStep[] steps, double radiusMm, int pointsPerStep)
    {
        BoxMinMm = min;
        BoxMaxMm = max;
        Steps = steps;
        RadiusMm = radiusMm;
        PointsPerStep = pointsPerStep;
    }

    /// <summary>Corner of the workpiece box with the lowest coordinates, in mm.</summary>
    public (double X, double Y, double Z) BoxMinMm { get; }

    /// <summary>Corner of the workpiece box with the highest coordinates, in mm.</summary>
    public (double X, double Y, double Z) BoxMaxMm { get; }

    /// <summary>The tool steps in order, in mm.</summary>
    public IReadOnlyList<BallStep> Steps { get; }

    /// <summary>Radius of the ball tool in mm; every step of a scene uses the same tool.</summary>
    public double RadiusMm { get; }

    /// <summary>Points per step, that is twice the points of one polyhedral ball.</summary>
    public int PointsPerStep { get; }

    /// <summary>Box volume in mm³.</summary>
    public double BoxVolumeMm3 =>
        (BoxMaxMm.X - BoxMinMm.X) * (BoxMaxMm.Y - BoxMinMm.Y) * (BoxMaxMm.Z - BoxMinMm.Z);

    /// <summary>Reads an expanded scene. The file is scanned once, without building a JSON tree.</summary>
    /// <param name="path">Path of the <c>expanded.json</c>.</param>
    public static ExpandedScene Load(string path) => Parse(File.ReadAllBytes(path), path);

    private static ExpandedScene Parse(byte[] utf8, string path)
    {
        var r = new Utf8JsonReader(utf8);
        Expect(ref r, JsonTokenType.StartObject);

        long[]? min = null, max = null;
        var steps = new List<BallStep>();
        int pointsPerStep = 0;
        long radiusNm = -1;

        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject) break;
            if (r.TokenType != JsonTokenType.PropertyName) throw Bad(path, $"property name, found {r.TokenType}");
            string? name = r.GetString();

            if (name == "box")
            {
                Expect(ref r, JsonTokenType.StartObject);
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    string? key = r.GetString();
                    Expect(ref r, JsonTokenType.StartArray);
                    long[] corner = ReadTriple(ref r, path);
                    if (key == "min") min = corner;
                    else if (key == "max") max = corner;
                }
            }
            else if (name == "steps")
            {
                Expect(ref r, JsonTokenType.StartArray);
                var first = new List<long[]>();
                while (r.Read() && r.TokenType == JsonTokenType.StartArray)
                {
                    int index = steps.Count;
                    long[]? p0 = null, p1 = null, q0 = null, q1 = null;
                    int points = 0;
                    int half = pointsPerStep / 2;

                    while (r.Read())
                    {
                        if (r.TokenType == JsonTokenType.EndArray) break;
                        if (r.TokenType != JsonTokenType.StartArray) throw Bad(path, $"a point, found {r.TokenType}");
                        int at = points++;
                        if (index == 0)
                        {
                            first.Add(ReadTriple(ref r, path));
                        }
                        else if (at == 0 || at == 1 || at == half || at == half + 1)
                        {
                            var p = ReadTriple(ref r, path);
                            if (at == 0) p0 = p; else if (at == 1) p1 = p; else if (at == half) q0 = p; else q1 = p;
                        }
                        else
                        {
                            r.Skip();
                        }
                    }

                    if (index == 0)
                    {
                        pointsPerStep = first.Count;
                        if (pointsPerStep < 4 || pointsPerStep % 2 != 0)
                            throw Bad(path, $"{pointsPerStep} points in the first step, expected an even number of at least 4");
                        p0 = first[0]; p1 = first[1]; q0 = first[pointsPerStep / 2]; q1 = first[pointsPerStep / 2 + 1];
                        first.Clear();
                    }
                    else if (points != pointsPerStep)
                    {
                        throw Bad(path, $"{points} points in step {index}, expected {pointsPerStep}");
                    }

                    steps.Add(Step(p0!, p1!, q0!, q1!, ref radiusNm, index, path));
                }
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        if (min is null || max is null) throw Bad(path, "a box with min and max");
        if (steps.Count == 0) throw Bad(path, "at least one step");
        return new ExpandedScene(Mm(min), Mm(max), steps.ToArray(), radiusNm / 1e6, pointsPerStep);
    }

    private static BallStep Step(long[] p0, long[] p1, long[] q0, long[] q1, ref long radiusNm, int index, string path)
    {
        long rA = Ball(p0, p1, index, path, "start");
        long rB = Ball(q0, q1, index, path, "end");
        if (radiusNm < 0) radiusNm = rA;
        if (Math.Abs(rA - radiusNm) > 1 || Math.Abs(rB - radiusNm) > 1)
            throw Bad(path, $"step {index} has radii {rA / 1e6} mm and {rB / 1e6} mm, expected {radiusNm / 1e6} mm");
        return new BallStep(Mm(Mid(p0, p1)), Mm(Mid(q0, q1)), (rA + rB) / 2.0 / 1e6);
    }

    /// <summary>Centre and radius of one ball from its two poles; the poles differ in z only.</summary>
    private static long Ball(long[] a, long[] b, int index, string path, string which)
    {
        if (a[0] != b[0] || a[1] != b[1])
            throw Bad(path, $"the {which} poles of step {index} are not on a vertical line, so the points are not from bench/run.py");
        long d = Math.Abs(a[2] - b[2]);
        if (d % 2 != 0) throw Bad(path, $"the {which} poles of step {index} are {d} nm apart, which is not a diameter");
        return d / 2;
    }

    private static long[] Mid(long[] a, long[] b) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2];

    private static (double X, double Y, double Z) Mm(long[] nm) => (nm[0] / 1e6, nm[1] / 1e6, nm[2] / 1e6);

    private static long[] ReadTriple(ref Utf8JsonReader r, string path)
    {
        var v = new long[3];
        for (int i = 0; i < 3; i++)
        {
            if (!r.Read() || r.TokenType != JsonTokenType.Number) throw Bad(path, $"a number, found {r.TokenType}");
            v[i] = r.GetInt64();
        }
        Expect(ref r, JsonTokenType.EndArray);
        return v;
    }

    private static void Expect(ref Utf8JsonReader r, JsonTokenType type)
    {
        if (!r.Read() || r.TokenType != type) throw new FormatException($"expected {type}");
    }

    private static FormatException Bad(string path, string expected) =>
        new($"{path}: expected {expected}");
}

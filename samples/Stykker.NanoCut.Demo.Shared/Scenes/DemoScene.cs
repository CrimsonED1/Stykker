using Stykker.NanoCut.Geometry2D;
using Stykker.NanoCut.Geometry3D;
using Stykker.NanoCut.Testing;

namespace Stykker.NanoCut.Demo.Scenes;

/// <summary>An editable scene parameter (number or choice).</summary>
public sealed record Param(string Key, string Label, double Default, double Min = 0, double Max = 1e6, double Step = 0.1, string Unit = "", string[]? Choices = null);

/// <summary>An object shown in the viewer.</summary>
public sealed record ViewObject(string Name, MeshBuffers? Mesh, float[]? Lines, string Color, double Opacity = 1);

/// <summary>Everything a scene run produces.</summary>
public sealed class SceneResult
{
    public List<ViewObject> Objects { get; } = [];
    public List<(string Label, string Value)> Metrics { get; } = [];
    public List<ReferenceCheck> Checks { get; } = [];
    public Solid? Result { get; set; }
    public string View { get; set; } = "iso";

    public void Add(string name, Solid solid, string color, double opacity = 1) =>
        Objects.Add(new(name, solid.ToMeshBuffers(OriginMode.Absolute), null, color, opacity));

    /// <summary>A planar region shown as a thin plate (z from 0 to −thickness) plus its outline.</summary>
    public void Add(string name, Region2 region, string color, double opacity = 1, double thicknessMm = 0.15)
    {
        if (region.IsEmpty) return;
        Objects.Add(new(name, Solid.Extrude(region, -thicknessMm, 0).ToMeshBuffers(OriginMode.Absolute), null, color, opacity));
        var xyz = new List<float>();
        foreach (var c in region.Normalize().Contours)
            for (int i = 0; i < c.Count; i++)
            {
                Vec2 a = c[i], b = c[(i + 1) % c.Count];
                xyz.AddRange([(float)a.XMm, (float)a.YMm, 0.001f, (float)b.XMm, (float)b.YMm, 0.001f]);
            }
        Objects.Add(new(name + " outline", null, [.. xyz], "#1d2733"));
    }

    public void Metric(string label, string value) => Metrics.Add((label, value));
}

/// <summary>A demo scene: parameters in, geometry + metrics + checks out.</summary>
public abstract class DemoScene
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Category { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<Param> Params { get; }
    public abstract SceneResult Run(IReadOnlyDictionary<string, double> p);

    /// <summary>Budget for a chord error: numeric + chord + sweep + 20 nm reserve.</summary>
    protected static Tolerance Tol(double chordNm, double sweepNm = 30) =>
        Tolerance.Budget(totalUm: (0.5 + chordNm + sweepNm + 20) / 1000, chordNm: chordNm, sweepNm: sweepNm);

    protected static string Mm(double v, int digits = 6) => v.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture) + " mm";
    protected static string Mm2(double v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " mm²";
    protected static string Mm3(double v) => v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) + " mm³";
    protected static string Nm(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " nm";
}

public static class SceneCatalog
{
    public static IReadOnlyList<DemoScene> All { get; } =
    [
        new Example1Scene3D(), new Example1Scene2D(), new Booleans3DScene(), new Booleans2DScene(),
        new GearProfileScene(), new GearGenerationScene(), new TurningScene(), new MillingScene(),
    ];

    public static DemoScene? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}

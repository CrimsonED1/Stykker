using System.Runtime.InteropServices;
using Microsoft.JSInterop;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Demo.Services;

/// <summary>
/// Thin wrapper around wwwroot/js/viewer.js. Buffers are passed as raw bytes (Uint8Array), not JSON; in Blazor Server
/// they travel the same way over the circuit. Calls after the browser went away (a closed tab on the server host) are
/// dropped instead of throwing, so a page that is still finishing a step or disposing does not fault the circuit.
/// </summary>
public sealed class Viewer(IJSRuntime js) : IAsyncDisposable
{
    /// <summary>Where the shared library's static files are served from, relative to the base href.</summary>
    public const string ContentRoot = "./_content/Stykker.NanoCut.Demo.Shared/";

    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> Module() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", ContentRoot + "js/viewer.js");

    private async Task Invoke(string identifier, params object?[] args)
    {
        try { await (await Module()).InvokeVoidAsync(identifier, args); }
        catch (JSDisconnectedException) { }
    }

    public async Task Attach(string elementId, string engine) => await Invoke("attach", elementId, engine);

    public async Task SetEngine(string engine) => await Invoke("setEngine", engine);

    public async Task Clear() => await Invoke("clear");

    public async Task AddMesh(string name, MeshBuffers b, string color, double opacity) =>
        await Invoke("addMesh", name,
            MemoryMarshal.AsBytes(b.Positions.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Normals.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Indices.AsSpan()).ToArray(),
            color, opacity);

    public async Task AddLines(string name, float[] xyz, string color) =>
        await Invoke("addLines", name, MemoryMarshal.AsBytes(xyz.AsSpan()).ToArray(), color);

    /// <summary>Shows a movable tool (mesh in its own frame) at a pose [x, y, z (mm), qx, qy, qz, qw].</summary>
    public async Task SetTool(MeshBuffers b, string color, double opacity, double[] pose) =>
        await Invoke("setTool",
            MemoryMarshal.AsBytes(b.Positions.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Normals.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Indices.AsSpan()).ToArray(),
            color, opacity, pose);

    public async Task SetToolPose(double[] pose) => await Invoke("setToolPose", pose);

    /// <summary>
    /// Spins the tool mesh in the browser about <paramref name="axis"/> (tool frame) at <paramref name="radPerSecond"/>
    /// (0 stops); <paramref name="angle"/> (rad), if given, sets the current spin angle.
    /// </summary>
    public async Task SetToolSpin(double[] axis, double radPerSecond, double? angle = null) =>
        await Invoke("setToolSpin", axis, radPerSecond, angle);

    /// <summary>Replaces the extra parts of the tool (meshes in the tool frame that move and spin with it).</summary>
    public async Task SetToolParts(IEnumerable<(MeshBuffers Mesh, string Color, double Opacity)> parts) =>
        // Cast to object: an array argument would otherwise be spread into separate JS arguments.
        await Invoke("setToolParts", (object)parts.Select(p => new
        {
            positions = MemoryMarshal.AsBytes(p.Mesh.Positions.AsSpan()).ToArray(),
            normals = MemoryMarshal.AsBytes(p.Mesh.Normals.AsSpan()).ToArray(),
            indices = MemoryMarshal.AsBytes(p.Mesh.Indices.AsSpan()).ToArray(),
            color = p.Color,
            opacity = p.Opacity,
        }).ToArray());

    /// <summary>Removes the movable tool and stops its spin.</summary>
    public async Task RemoveTool() => await Invoke("removeTool");

    /// <summary>Gizmo mode "translate", "rotate" or "off"; drags end in <c>OnToolMoved</c> on the receiver.</summary>
    public async Task SetGizmo<T>(string mode, DotNetObjectReference<T>? receiver) where T : class =>
        await Invoke("setGizmo", mode, receiver);

    public async Task SetLocked(bool locked) => await Invoke("setLocked", locked);

    public async Task Fit(string view) => await Invoke("fit", view);

    /// <summary>Fits the camera to a box [minX, minY, minZ, maxX, maxY, maxZ] (mm) instead of the visible objects.</summary>
    public async Task Fit(string view, double[] box) => await Invoke("fit", view, box);

    public async Task Download(string fileName, byte[] data) => await Invoke("download", fileName, data);

    public async ValueTask DisposeAsync()
    {
        try { if (_module is not null) await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}

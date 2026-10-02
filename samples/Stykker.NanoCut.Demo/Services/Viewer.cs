using System.Runtime.InteropServices;
using Microsoft.JSInterop;
using Stykker.NanoCut.Geometry3D;

namespace Stykker.NanoCut.Demo.Services;

/// <summary>Thin wrapper around wwwroot/js/viewer.js. Buffers are passed as raw bytes (Uint8Array), not JSON.</summary>
public sealed class Viewer(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> Module() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/viewer.js");

    public async Task Attach(string elementId, string engine) => await (await Module()).InvokeVoidAsync("attach", elementId, engine);

    public async Task SetEngine(string engine) => await (await Module()).InvokeVoidAsync("setEngine", engine);

    public async Task Clear() => await (await Module()).InvokeVoidAsync("clear");

    public async Task AddMesh(string name, MeshBuffers b, string color, double opacity) =>
        await (await Module()).InvokeVoidAsync("addMesh", name,
            MemoryMarshal.AsBytes(b.Positions.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Normals.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Indices.AsSpan()).ToArray(),
            color, opacity);

    public async Task AddLines(string name, float[] xyz, string color) =>
        await (await Module()).InvokeVoidAsync("addLines", name, MemoryMarshal.AsBytes(xyz.AsSpan()).ToArray(), color);

    /// <summary>Shows a movable tool (mesh in its own frame) at a pose [x, y, z (mm), qx, qy, qz, qw].</summary>
    public async Task SetTool(MeshBuffers b, string color, double opacity, double[] pose) =>
        await (await Module()).InvokeVoidAsync("setTool",
            MemoryMarshal.AsBytes(b.Positions.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Normals.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(b.Indices.AsSpan()).ToArray(),
            color, opacity, pose);

    public async Task SetToolPose(double[] pose) => await (await Module()).InvokeVoidAsync("setToolPose", pose);

    /// <summary>
    /// Spins the tool mesh in the browser about <paramref name="axis"/> (tool frame) at <paramref name="radPerSecond"/>
    /// (0 stops); <paramref name="angle"/> (rad), if given, sets the current spin angle.
    /// </summary>
    public async Task SetToolSpin(double[] axis, double radPerSecond, double? angle = null) =>
        await (await Module()).InvokeVoidAsync("setToolSpin", axis, radPerSecond, angle);

    /// <summary>Removes the movable tool and stops its spin.</summary>
    public async Task RemoveTool() => await (await Module()).InvokeVoidAsync("removeTool");

    /// <summary>Gizmo mode "translate", "rotate" or "off"; drags end in <c>OnToolMoved</c> on the receiver.</summary>
    public async Task SetGizmo<T>(string mode, DotNetObjectReference<T>? receiver) where T : class =>
        await (await Module()).InvokeVoidAsync("setGizmo", mode, receiver);

    public async Task SetLocked(bool locked) => await (await Module()).InvokeVoidAsync("setLocked", locked);

    public async Task Fit(string view) => await (await Module()).InvokeVoidAsync("fit", view);

    /// <summary>Fits the camera to a box [minX, minY, minZ, maxX, maxY, maxZ] (mm) instead of the visible objects.</summary>
    public async Task Fit(string view, double[] box) => await (await Module()).InvokeVoidAsync("fit", view, box);

    public async Task Download(string fileName, byte[] data) => await (await Module()).InvokeVoidAsync("download", fileName, data);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}

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

    public async Task Fit(string view) => await (await Module()).InvokeVoidAsync("fit", view);

    public async Task Download(string fileName, byte[] data) => await (await Module()).InvokeVoidAsync("download", fileName, data);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}

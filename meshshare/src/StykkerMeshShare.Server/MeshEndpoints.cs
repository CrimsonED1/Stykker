using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Was die anderen PCs abfragen: wer bin ich, was hat sich geändert, die Dateien, und eine lange Abfrage „sag Bescheid,
// wenn sich etwas ändert“. Nur lesend; geschrieben wird hier nie etwas.
public static class MeshEndpoints
{
    public static void Map(WebApplication app, MeshNode node)
    {
        app.MapGet("/mesh/hello", () => Results.Json(new Hello("StykkerMeshShare", node.NodeId, node.NodeName, node.Seq)));
        app.MapGet("/mesh/changes", (long since) => Results.Json(node.GetChanges(since)));
        app.MapGet("/mesh/file", (string path, string hash) =>
        {
            if (!PathRules.IsHash(hash)) return Results.NotFound();
            var stream = node.OpenFile(path, hash);
            return stream == null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
        });
        app.MapGet("/mesh/wait", async (long since, int? seconds, CancellationToken ct) =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 25, 1, 60));
            try
            {
                var seq = await node.WaitForChangeAsync(since, timeout, ct);
                return Results.Json(new Hello("StykkerMeshShare", node.NodeId, node.NodeName, seq));
            }
            catch (OperationCanceledException) { return Results.Empty; }
        });
    }
}

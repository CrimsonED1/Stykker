using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Was die anderen PCs abfragen: wer bin ich, was hat sich geändert, die Dateien, und eine lange Abfrage „sag Bescheid,
// wenn sich etwas ändert“. Nur lesend; geschrieben wird hier nie etwas.
public sealed record MeshPauseRequest(bool Paused, string? By);

public static class MeshEndpoints
{
    public static void Map(WebApplication app, MeshNode node)
    {
        app.MapGet("/mesh/hello", () => Results.Json(Hello.Of(node, node.Seq)));
        // Angehalten (Sync-Pause): nichts herausgeben. Neuere PCs fragen gar nicht erst (sie sehen es im Hallo).
        app.MapGet("/mesh/changes", (long since) => node.TransfersPaused
            ? Results.Text("Sync paused on this PC.", statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(node.GetChanges(since)));
        app.MapGet("/mesh/file", (string path, string hash) =>
        {
            if (!PathRules.IsHash(hash)) return Results.NotFound();
            if (node.TransfersPaused) return Results.Text("Sync paused on this PC.", statusCode: StatusCodes.Status503ServiceUnavailable);
            var stream = node.OpenFile(path, hash);
            return stream == null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream", enableRangeProcessing: true);
        });
        // Sync-Pause von einem anderen PC aus. Nur mit dem Kopf X-Stykker-Mesh (ein Browser darf den ohne CORS nicht
        // setzen) und mit JSON; der Zugang ist wie für alles andere auf Heimnetz und Tailscale beschränkt.
        app.MapPost("/mesh/pause", (HttpContext ctx, MeshPauseRequest body) =>
        {
            if (ctx.Request.Headers["X-Stykker-Mesh"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
            node.SetPaused(body.Paused, body.By);
            return Results.Json(new Stykker.Shared.ActionResult(true, body.Paused ? "Sync paused." : "Sync continues."));
        });
        app.MapGet("/mesh/wait", async (long since, int? seconds, CancellationToken ct) =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 25, 1, 60));
            try
            {
                var seq = await node.WaitForChangeAsync(since, timeout, ct);
                return Results.Json(Hello.Of(node, seq));
            }
            catch (OperationCanceledException) { return Results.Empty; }
        });
    }
}

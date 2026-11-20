using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Stykker.Shared.Web;
using StykkerMeshShare.Core;
using Result = Stykker.Shared.ActionResult;

namespace StykkerMeshShare.Server;

// Was die Seite braucht. Zwei Sorten Aktionen:
//  - nur am PC selbst (IsLocal): Ordner, Gegenstellen, Löschungen bestätigen, Explorer. Bis zur Kopplung darf das
//    niemand aus dem Netz.
//  - von der eigenen Seite, auch am Handy (Guarded: gleiche Herkunft): hochladen, Reihenfolge, pausieren,
//    überspringen, Papierkorb.
public static class UiEndpoints
{
    // Inhalte, die im Browser geöffnet werden dürfen (statt herunterzuladen). Kein HTML, kein SVG: die liefen sonst als
    // Seite dieses Servers und könnten seine Aktionen auslösen.
    private static readonly HashSet<string> Viewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".avif",
        ".pdf", ".txt", ".log", ".md", ".csv",
        ".mp3", ".m4a", ".wav", ".aac", ".flac", ".mp4", ".m4v", ".mov", ".webm",
    };
    private static readonly FileExtensionContentTypeProvider Types = new();

    public static void Map(WebApplication app, MeshNode node, ServerSettings settings, int port, bool localOnly, string version)
    {
        app.MapGet("/api/ping", () => Results.Json(new { app = "StykkerMeshShare", module = "meshshare", version }));

        app.MapGet("/api/status", (HttpContext ctx, PeerManager peers) => Results.Json(Status.Build(node, peers, NetworkAccess.IsLocal(ctx), localOnly)));

        app.MapGet("/api/files", () =>
        {
            var conflict = " (conflict ";
            return Results.Json(node.Files.Select(f => new FileDto(f.Path, f.Size, new DateTime(f.MtimeTicks, DateTimeKind.Utc), f.OriginName,
                f.Path.Contains(conflict, StringComparison.Ordinal), Viewable.Contains(Path.GetExtension(f.Path)))));
        });

        // Herunterladen, oder mit inline=1 im Browser ansehen (nur sichere Arten, ohne Skripte).
        app.MapGet("/api/download", (HttpContext ctx, string path, int? inline) =>
        {
            var stream = node.OpenForDownload(path);
            if (stream == null) return Results.NotFound();
            var name = Path.GetFileName(stream.Name);
            if (inline == 1 && Viewable.Contains(Path.GetExtension(name)))
            {
                if (!Types.TryGetContentType(name, out var type)) type = "application/octet-stream";
                if (type.StartsWith("text/", StringComparison.Ordinal)) type = "text/plain; charset=utf-8";
                var headers = ctx.Response.Headers;
                headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();
                headers.XContentTypeOptions = "nosniff";
                headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'";
                return Results.Stream(stream, type, enableRangeProcessing: true);
            }
            return Results.File(stream, "application/octet-stream", name, enableRangeProcessing: true);
        });

        // Hochladen: eine Datei je Anfrage, der Rumpf ist der Inhalt. path ist der Pfad im Ordner (mit Unterordnern beim
        // Ablegen eines Ordners). Nur von der eigenen Seite (Origin), damit keine fremde Seite Dateien hineinlegt.
        app.MapPost("/api/upload", async (HttpContext ctx, string path, FolderWatcher watcher, CancellationToken ct) =>
        {
            if (!LocalRequests.SameOrigin(ctx)) return Results.Json(new { ok = false, message = "Refused: that request did not come from this page." }, statusCode: 403);
            // Platte fast voll: nichts annehmen (auch nicht, wenn die Datei die Grenze erst unterschreiten würde).
            if (node.Disk is { } disk && node.MinFreeBytes > 0 && disk.Free - (ctx.Request.ContentLength ?? 0) < node.MinFreeBytes)
                return Results.Json(new { ok = false, message = $"Not enough free space on this PC: {Gb(disk.Free)} free, it keeps at least {Gb(node.MinFreeBytes)} free." }, statusCode: StatusCodes.Status507InsufficientStorage);
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = null;   // nur hier: große Dateien
            var name = await node.AddFileAsync(path, ctx.Request.Body, ct);
            if (name == null) return Results.Json(new { ok = false, message = "This name is not allowed in the shared folder." }, statusCode: 400);
            watcher.Soon(200);
            return Results.Json(new { ok = true, path = name });
        });

        // ── Nur am PC selbst ──

        // Ordner wechseln: move = true zieht die Dateien mit um, sonst fängt der neue Ordner leer an und holt alles von
        // den anderen PCs. Der neue Ordner wird in settings.json gemerkt.
        app.MapPost("/api/folder", (HttpContext ctx, FolderRequest body) => LocalOnly(ctx, "The folder can only be changed on the PC itself.", () =>
        {
            var problem = node.ChangeFolder(body.Path?.Trim() ?? "", body.Move);
            if (problem != null) return new Result(false, problem);
            settings.SetFolder(node.Folder);
            return new Result(true, body.Move ? $"Moved to {node.Folder}." : $"Now using {node.Folder}. The files come from the other PCs.");
        }));
        // Beim Tippen: taugt der Ordner?
        app.MapPost("/api/folder/check", (HttpContext ctx, FolderRequest body) => LocalOnly(ctx, "Only on the PC itself.", () =>
            node.CheckFolder(body.Path?.Trim() ?? "") is { } problem ? new Result(false, problem) : new Result(true, "This folder works.")));
        // Grenze für den freien Platz (in GB, 0 = aus). Wird in settings.json gemerkt.
        app.MapPost("/api/settings/min-free", (HttpContext ctx, MinFreeRequest body) => LocalOnly(ctx, "Change this on the PC itself.", () =>
        {
            if (body.Gb is not (>= 0 and <= 10_000)) return new Result(false, "Enter a number of GB between 0 and 10000.");
            node.MinFreeBytes = (long)(body.Gb.Value * 1024 * 1024 * 1024);
            settings.SetMinFreeGb(body.Gb.Value);
            return new Result(true, body.Gb.Value == 0 ? "No limit: receives until the disk is full." : $"Receives only while at least {body.Gb.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} GB stay free.");
        }));
        app.MapPost("/api/allow-deletes", (HttpContext ctx) => LocalOnly(ctx, "Confirm deletions on the PC itself.", () =>
        {
            node.AllowHeldDeletes();
            return new Result(true, "Deletions sent to the other PCs.");
        }));
        app.MapPost("/api/peers", (HttpContext ctx, PeerRequest body, PeerManager peers) => LocalOnly(ctx, "PCs are added on the PC itself.", () =>
        {
            var address = ServerSettings.NormalizeAddress(body.Address, port);
            if (address == null) return new Result(false, "Enter a name or an address, for example pc.example.ts.net or 192.0.2.10.");
            peers.AddManual(address);
            return new Result(true, $"Added {address}.");
        }));
        app.MapPost("/api/peers/remove", (HttpContext ctx, PeerRequest body, PeerManager peers) => LocalOnly(ctx, "PCs are removed on the PC itself.", () =>
            peers.Remove(body.Address ?? "") ? new Result(true, "Removed.") : new Result(false, "Not in the list.")));
        app.MapPost("/api/open-folder", (HttpContext ctx) => LocalOnly(ctx, "The Explorer opens on the PC. Use this from the PC itself.", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return new Result(false, ex.Message); }
            return new Result(true, "Opened in the Explorer.");
        }));

        // ── Von der eigenen Seite, auch am Handy ──

        // Reihenfolge der Warteschlange (Ziehen und Ablegen auf der Seite): die Pfade in der gewünschten Reihenfolge.
        app.MapPost("/api/queue/order", (HttpContext ctx, OrderRequest body) => LocalRequests.Guarded(ctx, () =>
        {
            node.SetOrder(body.Paths ?? []);
            return new Result(true, "Order saved.");
        }));
        // Sync-Pause: für diesen PC (address leer) oder einen anderen im Mesh. Von jeder Seite aus, auch vom Handy.
        app.MapPost("/api/sync-pause", async (HttpContext ctx, SyncPauseRequest body, PeerManager peers, CancellationToken ct) =>
        {
            if (!LocalRequests.SameOrigin(ctx))
                return Results.Json(new Result(false, "Refused: that request did not come from this page."), statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrEmpty(body.Address))
            {
                var by = NetworkAccess.IsLocal(ctx) ? node.NodeName : $"a browser at {ctx.Connection.RemoteIpAddress}";
                node.SetPaused(body.Paused, by);
                return Results.Json(new Result(true, body.Paused ? "Sync paused on this PC." : "Sync continues on this PC."));
            }
            var (ok, message) = await peers.SetRemotePausedAsync(body.Address, body.Paused, ct);
            var result = ok ? new Result(true, body.Paused ? "Sync paused on that PC." : "Sync continues on that PC.") : new Result(false, message);
            return Results.Json(result, statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/queue/skip", (HttpContext ctx, SkipRequest body) => LocalRequests.Guarded(ctx, () =>
        {
            if (string.IsNullOrEmpty(body.Path)) return new Result(false, "No file given.");
            node.Skip(body.Path, body.Skip);
            return new Result(true, body.Skip ? "This file will not be loaded." : "This file will be loaded.");
        }));
        app.MapGet("/api/trash", () => Results.Json(node.TrashItems().Select(t => new TrashDto(t.Id, t.Path, t.Removed, t.Size))));
        app.MapPost("/api/trash/restore", (HttpContext ctx, RestoreRequest body) => LocalRequests.Guarded(ctx, () =>
            node.Restore(body.Id ?? "", out var name) is { } problem ? new Result(false, problem) : new Result(true, $"Restored as {name}.")));
    }

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";

    private static IResult LocalOnly(HttpContext ctx, string refusal, Func<Result> action) => NetworkAccess.IsLocal(ctx)
        ? LocalRequests.Guarded(ctx, action)
        : Results.Json(new Result(false, refusal), statusCode: StatusCodes.Status403Forbidden);
}

internal sealed record PeerRequest(string? Address);
internal sealed record OrderRequest(string[]? Paths);
internal sealed record FolderRequest(string? Path, bool Move);
internal sealed record SyncPauseRequest(string? Address, bool Paused);
internal sealed record SkipRequest(string? Path, bool Skip);
internal sealed record RestoreRequest(string? Id);
internal sealed record MinFreeRequest(double? Gb);
internal sealed record FileDto(string Path, long Size, DateTime Modified, string? From, bool Conflict, bool Viewable);
internal sealed record TrashDto(string Id, string Path, DateTime Removed, long Size);

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Photino.NET;
using Stykker.Pairing;

namespace StykkerMeshShare.UI;

// StykkerMeshShare: das Fenster um die Oberfläche des Servers (Photino: unter Windows WebView2). Ablauf:
//   1. Antwortet der Server auf dem Port? Sonst starten (StykkerMeshShare-Server.exe mit Tray-Symbol) und warten.
//   2. Die Seite laden – ab da ist es dieselbe Oberfläche wie im Browser, ohne eigenen Zeichencode.
// Anders als bei StykkerSYS läuft der Server nach dem Schließen des Fensters weiter: der Abgleich soll nicht mit dem
// Fenster enden. Beenden über das Tray-Symbol („Shut down“).
// Das Fenster zeichnet wie die anderen Werkzeuge ohne Grafikkarte (--disable-gpu); mit --gpu über die Grafikkarte.
// Wie der Starter ist es randlos: die Titelleiste ist die Kopfzeile der Seite (?window=1), mit Marke, Thema und eigenen
// Knöpfen. Die Seite schickt dafür einfache Nachrichten (window:minimize/maximize/restore/close, window:move dx,dy),
// das Fenster meldet den Zustand zurück (window:state maximized|restored).
internal static class Program
{
    private const string IconFile = "StykkerMeshShare.ico";

    [STAThread]
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (!opt.Gpu && OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

        var url = $"http://127.0.0.1:{opt.Port}/?window=1" + (opt.Gpu ? "" : "&shell=1");
        // Startet der Server nicht, zeigt das Fenster den Grund, statt ohne Meldung zu verschwinden.
        string? startError = null;
        try { EnsureServer(opt.Port, opt.Server); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { startError = ex.Message; }

        var window = new PhotinoWindow()
            .SetTitle("StykkerMeshShare")
            .SetUseOsDefaultSize(false)
            .SetUseOsDefaultLocation(false)
            .SetSize(new System.Drawing.Size(1100, 820))
            .Center()
            .SetChromeless(true)
            .SetTemporaryFilesPath(Path.Combine(CacheDir(), "web-shell"));
        var icon = Path.Combine(AppContext.BaseDirectory, IconFile);
        if (File.Exists(icon)) window.SetIconFile(icon);
        window.RegisterWebMessageReceivedHandler((_, message) => OnMessage(window, message));
        window.RegisterMaximizedHandler((_, _) => SendState(window, true));
        window.RegisterRestoredHandler((_, _) => SendState(window, false));
        if (startError == null) window.Load(new Uri(url));
        else window.LoadRawString(ErrorPage(startError));
        window.WaitForClose();
        return 0;
    }

    // Nachrichten der Titelleiste (einfache Zeichenketten). Läuft auf dem Fensterthread.
    private static void OnMessage(PhotinoWindow window, string message)
    {
        try
        {
            switch (message)
            {
                case "window:minimize": window.SetMinimized(true); return;
                case "window:maximize": window.SetMaximized(true); return;
                case "window:restore": window.SetMaximized(false); return;
                case "window:close": window.Close(); return;
                case "window:ready": SendState(window, window.Maximized); return;
            }
            if (message.StartsWith("window:move ", StringComparison.Ordinal))
            {
                var parts = message["window:move ".Length..].Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var dx) && int.TryParse(parts[1], out var dy) && !window.Maximized)
                    window.Offset(new System.Drawing.Point(dx, dy));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static void SendState(PhotinoWindow window, bool maximized)
    {
        try { window.SendWebMessage("window:state " + (maximized ? "maximized" : "restored")); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // Randlos hat das Fenster keine Windows-Leiste: die Fehlerseite bringt eine eigene mit (Ziehen, Schließen).
    private static string ErrorPage(string message) =>
        "<!doctype html><meta charset=\"utf-8\"><body style=\"font-family:'Segoe UI',sans-serif;margin:0;background:#060916;color:#e6e9f5\">" +
        "<div id=\"bar\" style=\"display:flex;align-items:center;height:40px;padding-left:14px;background:#0e1430;border-bottom:1px solid #1d2547;cursor:grab;user-select:none\">" +
        "<b style=\"font-family:Consolas,monospace;letter-spacing:.14em;font-size:12px\">STYKKER <span style=\"color:#38e8ff\">MESHSHARE</span></b>" +
        "<span style=\"flex:1\"></span><button onclick=\"window.external.sendMessage('window:close')\" style=\"width:44px;height:40px;border:0;background:none;color:inherit;cursor:pointer\">✕</button></div>" +
        "<div style=\"margin:32px\"><h2>StykkerMeshShare could not start its server</h2><p>" + WebUtility.HtmlEncode(message) + "</p>" +
        "<p>Build the solution first, or start with <code>--server &lt;path&gt;</code>.</p></div>" +
        "<script>var d=false,x=0,y=0,b=document.getElementById('bar');b.onmousedown=function(e){if(e.target.closest('button'))return;d=true;x=e.screenX;y=e.screenY;};" +
        "document.onmousemove=function(e){if(!d)return;var dx=e.screenX-x,dy=e.screenY-y;x=e.screenX;y=e.screenY;if(dx||dy)window.external.sendMessage('window:move '+dx+','+dy);};document.onmouseup=function(){d=false;};</script></body>";

    private static string CacheDir()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StykkerMeshShare");
        Directory.CreateDirectory(root);
        return root;
    }

    // Läuft schon ein Server auf dem Port, wird er benutzt; sonst gestartet und bis zu 30 s abgewartet.
    // Der gestartete Server bleibt nach dem Schließen des Fensters stehen (Tray-Symbol).
    private static void EnsureServer(int port, string? serverPath)
    {
        if (WaitForPort(port, TimeSpan.FromMilliseconds(600)).GetAwaiter().GetResult()) return;

        var exe = FindServer(serverPath)
            ?? throw new InvalidOperationException("StykkerMeshShare-Server.exe not found – build the solution first, or pass --server <path>.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var arg in new[] { "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-browser" })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("the server did not start");
        if (!WaitForPort(port, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"the server did not answer on port {port}");
        }
    }

    private static async Task<bool> WaitForPort(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        var end = DateTime.Now + timeout;
        while (true)
        {
            try
            {
                // Nur unser Server zählt: ein anderes Programm auf dem Port bekäme sonst das Fenster.
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/ping").ConfigureAwait(false);
                if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("\"app\":\"StykkerMeshShare\"", StringComparison.Ordinal)) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
            if (DateTime.Now >= end) return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    // Der Server liegt veröffentlicht neben dem Fenster; beim Entwickeln im eigenen bin-Ordner nebenan.
    private static string? FindServer(string? explicitPath)
    {
        const string name = "StykkerMeshShare-Server.exe";
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;

        var here = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(here)) return here;

        // Beim Entwickeln: nur innerhalb des Repos suchen (bis zum Ordner mit der .slnx), nie hinauf bis C:\.
        int level = 0;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && level < 8; dir = dir.Parent, level++)
        {
            var project = Path.Combine(dir.FullName, "StykkerMeshShare.Server");
            if (Directory.Exists(project))
            {
                var found = Directory.GetFiles(project, name, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            if (File.Exists(Path.Combine(dir.FullName, "StykkerMeshShare.slnx"))) break;   // Wurzel des Werkzeugs erreicht
        }
        return null;
    }
}

internal sealed record Options(int Port, string? Server, bool Gpu)
{
    // StykkerMeshShare [--port 17404] [--server <Pfad>] [--gpu]
    public static Options Parse(string[] args)
    {
        int port = StykkerPorts.MeshShare;
        string? server = null;
        bool gpu = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--gpu": gpu = true; break;
            }
        }
        return new Options(port, server, gpu);
    }
}

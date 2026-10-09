using System.Diagnostics;
using System.Net.Http;
using Photino.NET;

namespace StykkerHud.UI;

// StykkerHUD: das Fenster um die Weboberfläche des Servers (Photino: unter Windows WebView2). Ablauf:
//   1. Antwortet der Server auf dem Port? Sonst starten (StykkerHUD-Server.exe --no-tray) und warten.
//   2. Die Seite laden – ab da ist es dieselbe Oberfläche wie im Browser, ohne eigenen Zeichencode.
// Das Fenster zeichnet ohne Grafikkarte (--disable-gpu): ein Monitor soll die GPU messen, nicht benutzen.
// Mit dem Schalter --gpu zeichnet es über die Grafikkarte.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (!opt.Gpu && OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-gpu --disable-gpu-compositing");

        var url = $"http://127.0.0.1:{opt.Port}/" + (opt.Gpu ? "" : "?shell=1");
        var started = EnsureServer(opt.Port, opt.Server);

        var window = new PhotinoWindow()
            .SetTitle("StykkerHUD")
            .SetUseOsDefaultSize(false)
            .SetSize(new System.Drawing.Size(1280, 880))
            .Center()
            .SetTemporaryFilesPath(Path.Combine(CacheDir(), "web-shell"));
        try
        {
            window.Load(new Uri(url));
            window.WaitForClose();
        }
        finally
        {
            if (started != null)
            {
                try { if (!started.HasExited) started.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return 0;
    }

    private static string CacheDir()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StykkerHUD");
        Directory.CreateDirectory(root);
        return root;
    }

    // Läuft schon ein Server auf dem Port, wird er benutzt; sonst gestartet und bis zu 30 s abgewartet.
    // Der Rückgabewert ist der gestartete Prozess – nur der wird beim Schließen wieder beendet.
    private static Process? EnsureServer(int port, string? serverPath)
    {
        if (WaitForPort(port, TimeSpan.FromMilliseconds(600)).GetAwaiter().GetResult()) return null;

        var exe = FindServer(serverPath)
            ?? throw new InvalidOperationException("StykkerHUD-Server.exe not found – build the solution first, or pass --server <path>.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var arg in new[] { "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-tray", "--no-browser" })
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("the server did not start");
        if (!WaitForPort(port, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"the server did not answer on port {port}");
        }
        return process;
    }

    private static async Task<bool> WaitForPort(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
        var end = DateTime.Now + timeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/snapshot").ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
            if (DateTime.Now >= end) return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    // Der Server liegt veröffentlicht neben dem Fenster; beim Entwickeln im eigenen bin-Ordner nebenan.
    private static string? FindServer(string? explicitPath)
    {
        const string name = "StykkerHUD-Server.exe";
        if (!string.IsNullOrEmpty(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;

        var here = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(here)) return here;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "StykkerHud.Server");
            if (!Directory.Exists(project)) continue;
            var found = Directory.GetFiles(project, name, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found != null) return found;
        }
        return null;
    }
}

internal sealed record Options(int Port, string? Server, bool Gpu)
{
    // StykkerHUD [--port 8079] [--server <Pfad>] [--gpu]
    public static Options Parse(string[] args)
    {
        int port = 8079;
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
using System.Diagnostics;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Cli;

// Der Weg zum Server (S2/S3/S5): stykker web startet ihn, stykker qr zeigt den Code zum Anmelden vom Telefon,
// stykker remote schaltet das Heimnetz. Läuft der Server, holen status/list/show ihren Zustand von ihm und
// start/stop/unload schicken ihre Aktionen an ihn – dann misst nur noch eine Engine.
public static partial class ServerCommand
{
    public const int DefaultPort = 8078;

    // ── Verbindung ──

    // Läuft der Server für diesen Datenordner? (Schlüssel aus access lesen und /api/ping fragen)
    public static ServerClient? TryConnect(CliArgs a, bool quiet = true)
    {
        if (a.Sim) return null;
        try
        {
            var paths = a.DataDir != null ? new AppPaths(Path.GetFullPath(a.DataDir)) : AppPaths.Default();
            var platform = OperatingSystem.IsWindows() ? (IPlatform)new WindowsPlatform() : new BasicPlatform();
            var key = ServerClient.ReadKey(paths, platform);
            if (key == null) return null;   // der Server lief noch nie mit diesem Datenordner
            var client = new ServerClient(ServerClient.DefaultUrl(a.Port), key);
            if (client.PingAsync().GetAwaiter().GetResult()) return client;
            client.Dispose();
        }
        catch { /* kein Server, kein Schlüssel, kein Netz: dann lokal messen */ }
        return null;
    }

    // Lokale Daten des Servers lesen, auch wenn er gerade nicht läuft (Zugangscode, Geräteliste)
    private static (AppPaths Paths, AccessControl Access) Local(CliArgs a)
    {
        var paths = a.DataDir != null ? new AppPaths(Path.GetFullPath(a.DataDir)) : AppPaths.Default();
        var platform = OperatingSystem.IsWindows() ? (IPlatform)new WindowsPlatform() : new BasicPlatform();
        return (paths, new AccessControl(paths, platform));
    }

    // ── stykker web ──

    public static async Task<int> WebAsync(CliArgs a, CancellationToken ct)
    {
        if (await ServerClient.IsRunningAsync(a.Port, ct).ConfigureAwait(false))
        {
            var (paths, access) = Local(a);
            var url = OpenUrl(a.Port, access.Code);
            Console.Out.WriteLine($"{Out.Green("running")}  {url}");
            OpenBrowser(url, a);
            return Commands.Ok;
        }

        var exe = ServerLocator.Find();
        if (exe == null)
        {
            Out.Error("StykkerLLM-Server.exe was not found next to stykker.exe (this is a development build)");
            return Commands.Error;
        }
        var dataDir = a.DataDir ?? AppPaths.Default().Root;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            // Der Server bringt keine eigene Oberfläche mit; ein zweites schwarzes Konsolenfenster
            // neben dem Terminal wäre nur ein Irrtum (genau wie beim Start aus dem Fenster).
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(a.Port.ToString(Strings.Inv));
        if (a.Words.Contains("no-browser")) psi.ArgumentList.Add("--no-browser");
        if (a.Words.Contains("no-tray")) psi.ArgumentList.Add("--no-tray");   // Skripte und Server ohne Fenster
        var proc = Process.Start(psi);
        if (proc == null) { Out.Error("the server could not be started"); return Commands.Error; }

        // warten, bis er antwortet, dann den Browser mit dem Code öffnen
        var end = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            if (await ServerClient.IsRunningAsync(a.Port, ct).ConfigureAwait(false))
            {
                var (_, access2) = Local(a);
                var url = OpenUrl(a.Port, access2.Code);
                Console.Out.WriteLine($"{Out.Green("started")}  {url}");
                if (!a.Words.Contains("no-browser")) OpenBrowser(url, a);
                return Commands.Ok;
            }
            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        Out.Error("the server did not answer on port " + a.Port);
        return Commands.Error;
    }

    private static string OpenUrl(int port, string code) => $"{ServerClient.DefaultUrl(port)}/pair?code={Uri.EscapeDataString(code)}";

    private static void OpenBrowser(string url, CliArgs a)
    {
        if (a.NoBrowser || a.Words.Contains("no-browser")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* kein Browser: die Adresse steht oben */ }
    }

    // ── stykker qr ──

    public static async Task<int> QrAsync(CliArgs a, CancellationToken ct)
    {
        using var client = TryConnect(a);
        string code;
        string lan;
        int port = DefaultPort;
        var devices = new List<(string Name, string Address, DateTime? Seen)>();
        DateTime? expires;
        var requests = new List<RemotePairRequest>();
        if (client?.State is { } state)
        {
            code = state.Access.Code;
            expires = state.Access.CodeExpires;
            requests.AddRange(state.Access.Requests);
            lan = state.Access.LanAddress;
            port = state.ServerPort == 0 ? a.Port : state.ServerPort;
            foreach (var d in state.Access.Devices) devices.Add((d.Name, d.Address, d.LastSeen));
        }
        else
        {
            var (_, access) = Local(a);
            code = access.Code;
            expires = access.CodeExpires;
            port = a.Port;
            lan = NetInfo.LanAddress() ?? "";
            foreach (var d in access.Devices) devices.Add((d.Name, d.Address ?? "", d.LastSeen));
        }
        var url = string.IsNullOrEmpty(lan) ? $"{NetAddr.Url("127.0.0.1", port)}/pair?code={code}" : NetInfo.PairUrl(port, code, lan);
        if (a.Json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { code, lan, url, devices = devices.Select(d => new { d.Name, d.Address, seen = d.Seen }) },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return Commands.Ok;
        }
        if (!a.Ascii && !Console.IsOutputRedirected)
        {
            // QR für die Handykamera (Blockzeichen; mit --ascii zwei Rauten)
            var qr = QrCode.Encode(url, QrCode.Ecc.M);
            string on = a.Ascii ? "##" : "██", off = "  ";
            var sb = new System.Text.StringBuilder();
            for (int y = -1; y <= qr.Size; y++)
            {
                var line = new System.Text.StringBuilder();
                for (int x = -1; x <= qr.Size; x++)
                    line.Append(y >= 0 && y < qr.Size && x >= 0 && x < qr.Size && qr[x, y] ? Out.Bold(on) : off);
                sb.AppendLine(line.ToString().TrimEnd());
            }
            Console.Out.WriteLine(sb.ToString());
        }
        Console.Out.WriteLine($"  {Out.Bold("code")}  {Out.Bold(AccessControl.Pretty(code))}   {Out.Dim(expires is { } exp ? Strings.CodeValidFor(exp - DateTime.Now) : "")}");
        Console.Out.WriteLine($"  {Out.Dim("url")}   {url}");
        foreach (var r in requests)
            Console.Out.WriteLine($"  {Out.Yellow(Strings.PairPending)}  {Strings.PairPendingLine(r.Name, r.Address, r.Kind)}   {Out.Dim("stykker approve <code>")}");
        if (devices.Count == 0) Console.Out.WriteLine("  " + Out.Dim("no device has signed in yet (switch Home/VPN on in the window or '/remote on')"));
        else
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {Out.Bold("devices")}");
            foreach (var (name, address, seen) in devices)
                Console.Out.WriteLine($"    {name}  {Out.Dim(address)}  {Out.Dim(seen?.ToString("g") ?? "-")}");
        }
        return Commands.Ok;
    }

    // ── stykker remote [on|off] ──

    public static async Task<int> RemoteAsync(CliArgs a, CancellationToken ct)
    {
        var what = string.Join(' ', a.Words).ToLowerInvariant();
        bool? want = what switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => null };
        if (want is null && what.Length > 0) { Out.Error("usage: stykker remote [on|off]"); return Commands.Usage; }

        using var client = TryConnect(a);
        if (client == null)
        {
            Out.Error(Strings.ServerNotRunning);
            return Commands.Error;
        }
        if (want is bool on)
        {
            var result = await client.SendAsync("remote.set", flag: on, ct: ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Ok ? $"{Out.Green(Strings.On)}  {client.BaseUrl}/phone" : Out.Red(result.Message));
            return result.Ok ? Commands.Ok : Commands.Error;
        }
        var state = client.State;
        if (state == null) { Out.Error(Strings.ServerNotRunning); return Commands.Error; }
        Console.Out.WriteLine(state.Access.Remote
            ? $"{Out.Green(Strings.On)}  {NetAddr.Url(state.Access.LanAddress, state.ServerPort)}   code {state.Access.Code}"
            : $"{Out.Dim(Strings.RemoteThisPcOnly)}  {NetAddr.Url("127.0.0.1", state.ServerPort)}   code {state.Access.Code}");
        Console.Out.WriteLine(Out.Dim("  devices: " + (state.Access.Devices.Count == 0 ? "none" : string.Join(", ", state.Access.Devices.Select(d => d.Name)))));
        return Commands.Ok;
    }

    // ── stykker devices [id zum Entfernen] ──

    public static async Task<int> DevicesAsync(CliArgs a, CancellationToken ct)
    {
        using var client = TryConnect(a);
        var remove = string.Join(' ', a.Words);
        if (client == null) { Out.Error(Strings.ServerNotRunning); return Commands.Error; }
        if (remove.Length > 0)
        {
            var result = await client.SendAsync("device.remove", remove, ct: ct).ConfigureAwait(false);
            Console.Out.WriteLine(result.Ok ? $"{Out.Green("removed")} {remove}" : Out.Red(result.Message));
            return result.Ok ? Commands.Ok : Commands.Error;
        }
        var devices = client.State?.Access.Devices ?? new List<RemoteDevice>();
        if (devices.Count == 0) { Console.Out.WriteLine(Out.Dim(Strings.RemoteNoDevices)); return Commands.Ok; }
        foreach (var d in devices)
            Console.Out.WriteLine($"{d.Id}  {d.Name}  {RoleColor(d.Role)}  {Out.Dim(string.IsNullOrEmpty(d.Address) ? "-" : d.Address)}  {Out.Dim(d.LastSeen?.ToString("g") ?? "-")}");
        return Commands.Ok;
    }

    // ── stykker role <id> viewer|admin ──

    public static async Task<int> RoleAsync(CliArgs a, CancellationToken ct)
    {
        var words = a.Words;
        if (words.Count < 2) { Out.Error("usage: stykker role <id> viewer|admin"); return Commands.Usage; }
        var role = words[1].Trim().ToLowerInvariant();
        if (!AccessRole.IsValid(role)) { Out.Error(Strings.RoleUnknown(role)); return Commands.Usage; }
        using var client = TryConnect(a);
        if (client == null) { Out.Error(Strings.ServerNotRunning); return Commands.Error; }
        var result = await client.SendAsync("device.role", words[0], role, ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(result.Ok ? Out.Green(result.Message) : Out.Red(result.Message));
        return result.Ok ? Commands.Ok : Commands.Error;
    }

    // ── stykker approve <code> [viewer] ── (das neue Gerät zeigt die sechs Ziffern)

    public static async Task<int> ApproveAsync(CliArgs a, CancellationToken ct)
    {
        var words = a.Words.ToList();
        bool viewer = words.RemoveAll(w => w.Equals("viewer", StringComparison.OrdinalIgnoreCase)) > 0;
        var code = string.Concat(words);
        using var client = TryConnect(a);
        if (client == null) { Out.Error(Strings.ServerNotRunning); return Commands.Error; }
        if (code.Length == 0)
        {
            var pending = client.State?.Access.Requests ?? new List<RemotePairRequest>();
            Console.Out.WriteLine(Out.Dim(Strings.PairApproveHint));
            foreach (var r in pending)
                Console.Out.WriteLine($"  {Out.Yellow(Strings.PairPending)}  {Strings.PairPendingLine(r.Name, r.Address, r.Kind)}  {Out.Dim(Strings.CodeValidFor(r.Expires - DateTime.Now))}");
            Console.Out.WriteLine(Out.Dim("usage: stykker approve <6 digits> [viewer]"));
            return pending.Count == 0 ? Commands.Ok : Commands.Usage;
        }
        var result = await client.SendAsync("pair.approve", code, viewer ? AccessRole.Viewer : AccessRole.Admin, ct: ct).ConfigureAwait(false);
        Console.Out.WriteLine(result.Ok ? Out.Green(result.Message) : Out.Red(result.Message));
        return result.Ok ? Commands.Ok : Commands.Error;
    }

    private static string RoleColor(string role) =>
        role == AccessRole.Viewer ? Out.Dim(Strings.RoleViewer) : role == AccessRole.Hub ? Out.Yellow(Strings.RoleHub) : Out.Cyan(Strings.RoleAdmin);
}

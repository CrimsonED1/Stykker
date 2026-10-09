using System.Diagnostics;
using StykkerLlm.Core;
using StykkerLlm.Platform.Windows;

namespace StykkerLlm.Cli;

// Der Weg zum Server: verbinden (Schlüssel aus dem Datenordner), bei Bedarf starten, Browser öffnen, beenden.
// Die TUI und die Einzelbefehle messen nie selbst – der Server ist der Kern.
public static class ServerLink
{
    public static IPlatform Platform() => OperatingSystem.IsWindows() ? new WindowsPlatform() : new BasicPlatform();

    // null, wenn kein Server läuft (oder der Datenordner noch keinen Schlüssel hat)
    public static async Task<ServerClient?> ConnectAsync(CliArgs a, CancellationToken ct = default)
    {
        if (!await ServerClient.IsRunningAsync(a.Port, ct).ConfigureAwait(false)) return null;
        var key = ServerClient.ReadKey(a.Paths.Get(), Platform());
        if (key == null) return null;
        var client = new ServerClient(ServerClient.DefaultUrl(a.Port), key);
        if (await client.PingAsync(ct).ConfigureAwait(false)) return client;
        client.Dispose();
        return null;
    }

    // Läuft keiner, starten (ohne Browser, ohne Tray) und warten, bis er antwortet. Gibt er auf (Port belegt …), gleich sagen.
    public static async Task<ServerClient> EnsureAsync(CliArgs a, Action<string> status, CancellationToken ct = default)
    {
        if (await ConnectAsync(a, ct).ConfigureAwait(false) is { } running) return running;
        status(Strings.ShellStarting);
        var exe = ServerLocator.Find() ?? throw new InvalidOperationException(Strings.ShellNoServer.Replace("StykkerUI", "stykker"));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        foreach (var x in new[] { "--data-dir", a.Paths.Get().Root, "--port", a.Port.ToString(Strings.Inv), "--no-browser", "--no-tray" }) psi.ArgumentList.Add(x);
        using var proc = Process.Start(psi);
        AppLog.Write("server: not answering, started it");
        var end = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < end)
        {
            ct.ThrowIfCancellationRequested();
            if (await ConnectAsync(a, ct).ConfigureAwait(false) is { } c) return c;
            if (proc?.HasExited == true)
                throw new InvalidOperationException(proc.ExitCode == 1 ? Strings.ServerPortBusy(a.Port, Strings.ShellSeeServerLog) : Strings.ServerNotRunning);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(Strings.ServerNotRunning);
    }

    // Adresse für den Browser, mit dem Zugangscode: der Browser ist damit gleich angemeldet
    public static string PairUrl(int port, string code) =>
        code.Length > 0 ? $"{ServerClient.DefaultUrl(port)}/pair?code={Uri.EscapeDataString(code)}" : ServerClient.DefaultUrl(port);

    public static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* kein Browser: die Adresse steht da */ }
    }
}

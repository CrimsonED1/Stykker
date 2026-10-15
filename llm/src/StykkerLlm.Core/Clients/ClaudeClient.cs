namespace StykkerLlm.Core;

// Claude Desktop und Claude Code als Proxy-Client. Der Schalter selbst bleibt in ClaudeDesktop (der Adapter ändert nichts
// daran); hier kommen dazu: die Erkennung der Startvarianten und das Starten.
public sealed class ClaudeClient : IProxyClient
{
    public const string ClientId = "claude";

    // Kennungen der Startvarianten (client.launch)
    public const string KeyDesktop = "desktop", KeyDesktopExe = "desktop-exe", KeyCliProxy = "cli-proxy", KeyCliDirect = "cli-direct";

    // Ordner des Store-Pakets beginnt so; der Name des Ordners ist die Package Family Name
    private const string PackagePattern = "Claude_*";
    // Die App-Id im Paket (shell:AppsFolder\<PFN>!Claude)
    private const string AppId = "Claude";

    private readonly ClaudeDesktop _desktop;
    private readonly Func<bool> _proxyRunning;
    private readonly Func<int> _port;
    private readonly ClientProbe _probe;
    private readonly IClientStarter _starter;

    public ClaudeClient(ClaudeDesktop desktop, Func<bool> proxyRunning, Func<int> port, ClientProbe probe, IClientStarter starter)
    {
        _desktop = desktop; _proxyRunning = proxyRunning; _port = port; _probe = probe; _starter = starter;
    }

    public string Id => ClientId;
    public string Title => "Claude";
    public string IconName => "claude";
    public bool Supported => _desktop.Supported;
    public bool Enabled => _desktop.Enabled;
    public string SwitchHint => Strings.ClaudeOffHint;
    public bool Running => _desktop.Running;
    public int RunningCount => _desktop.RunningCount;

    public string Enable(out string? error) => _desktop.Enable(out error);
    public string Disable(out string? error) => _desktop.Disable(out error);
    public int CloseRunning(out string? error) => _desktop.CloseRunning(out error);

    // Erkennung: Desktop (Store-Paket, sonst klassische Installation), CLI über den Proxy und CLI direkt
    public IReadOnlyList<ClientInstall> Installs
    {
        get
        {
            var list = new List<ClientInstall> { DesktopInstall(), CliInstall(proxy: true), CliInstall(proxy: false) };
            AppLog.Write("claude client detect: " + string.Join(", ", list.Select(i => $"{i.Key}={(i.Available ? "yes" : "no")}")));
            return list;
        }
    }

    public string Launch(ClientInstall install, out string? error)
    {
        error = null;
        if (!Supported) { error = Strings.ClaudeUnsupported; return ""; }
        if (!install.Available || install.Path is not { Length: > 0 } path)
        {
            error = Strings.ClientStartNotInstalled(Title);
            return "";
        }

        string file, workDir = "";
        IReadOnlyList<string> args;
        var env = new Dictionary<string, string>();
        switch (install.Key)
        {
            case KeyDesktop:
                // Das Store-Paket startet über die App-Id: explorer.exe shell:AppsFolder\<PFN>!Claude
                file = "explorer.exe";
                args = new[] { @"shell:AppsFolder\" + Path.GetFileName(path) + "!" + AppId };
                break;
            case KeyDesktopExe:
                file = path;
                args = Array.Empty<string>();
                workDir = Path.GetDirectoryName(path) ?? "";
                break;
            case KeyCliProxy:
            case KeyCliDirect:
                if (install.Key == KeyCliProxy)
                {
                    if (!_proxyRunning())
                    {
                        error = Strings.ClientProxyOff;
                        AppLog.Write($"claude client launch {install.Key} refused: proxy is off");
                        return "";
                    }
                    // Dieselben Angaben wie für die Desktop-App: Adresse des Proxys und der statische Schlüssel
                    env["ANTHROPIC_BASE_URL"] = NetAddr.Url("127.0.0.1", _port());
                    env["ANTHROPIC_AUTH_TOKEN"] = ClaudeDesktop.ApiKey;
                    // Die Sitzung läuft über claude-stykker (Standardziel); andere Claude-Namen (Sicherheitsprüfung im
                    // Modus „auto“) gehen an das eingestellte Prüf-Ziel
                    env["ANTHROPIC_MODEL"] = ClaudeDesktop.ModelAlias;
                }
                (file, args) = ConsoleCommand(path);
                workDir = _probe.UserProfile is { Length: > 0 } home ? home : Path.GetDirectoryName(path) ?? "";
                break;
            default:
                error = Strings.ClientUnknownVariant(install.Key);
                return "";
        }

        bool ok = _starter.Start(file, args, workDir, env, out var startError);
        if (!ok)
        {
            error = Strings.ClientLaunchFailed(startError ?? "?");
            AppLog.Write($"claude client launch {install.Key} failed: {startError}");
            return "";
        }
        AppLog.Write($"claude client launch {install.Key} started{(env.Count > 0 ? " (via proxy)" : "")}");
        return Strings.ClientLaunched(install.Label);
    }

    // Ein neues Konsolenfenster mit der CLI: „cmd /k“ über „start“ (das öffnet immer ein eigenes Fenster; unter Windows 11
    // im eingestellten Standard-Terminal). Nicht wt.exe: läuft Windows Terminal schon, öffnet es den Tab im vorhandenen
    // Prozess, und die Umgebungsvariablen dieses Starts kämen dort nicht sicher an.
    private (string File, IReadOnlyList<string> Args) ConsoleCommand(string cliPath)
    {
        var home = _probe.UserProfile is { Length: > 0 } h ? h : Path.GetDirectoryName(cliPath) ?? "";
        return ("cmd.exe", new[] { "/c", "start", "", "/D", home, "cmd.exe", "/k", cliPath });
    }

    private ClientInstall DesktopInstall()
    {
        var local = _probe.LocalAppData;
        // Store-Paket: der Ordner heißt wie die Package Family Name
        var pkg = ClientProbe.SubDirs(local is null ? null : Path.Combine(local, "Packages"), PackagePattern).FirstOrDefault();
        if (pkg != null) return new ClientInstall(KeyDesktop, ClientInstallKind.Desktop, Strings.ClientStartDesktop, pkg, true);
        // Klassische Installation
        if (local is { Length: > 0 })
            foreach (var name in new[] { "claude.exe", "Claude.exe" })
            {
                var exe = Path.Combine(local, "AnthropicClaude", name);
                if (File.Exists(exe)) return new ClientInstall(KeyDesktopExe, ClientInstallKind.Desktop, Strings.ClientStartDesktop, exe, true);
            }
        return new ClientInstall(KeyDesktop, ClientInstallKind.Desktop, Strings.ClientStartDesktop, null, false);
    }

    private ClientInstall CliInstall(bool proxy)
    {
        var path = FindCli();
        return new ClientInstall(proxy ? KeyCliProxy : KeyCliDirect, ClientInstallKind.Cli,
            proxy ? Strings.ClientStartCliProxy : Strings.ClientStartCliDirect, path, path != null);
    }

    // claude.exe, claude.cmd oder claude im PATH; sonst %USERPROFILE%\.local\bin\claude.exe; sonst %APPDATA%\npm\claude.cmd
    private string? FindCli()
    {
        if (_probe.FindOnPath("claude.exe", "claude.cmd", "claude") is { } onPath) return onPath;
        if (_probe.UserProfile is { Length: > 0 } home && File.Exists(Path.Combine(home, ".local", "bin", "claude.exe")))
            return Path.Combine(home, ".local", "bin", "claude.exe");
        if (_probe.AppData is { Length: > 0 } roaming && File.Exists(Path.Combine(roaming, "npm", "claude.cmd")))
            return Path.Combine(roaming, "npm", "claude.cmd");
        return null;
    }
}

using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N70 – Die Proxy-Clients (Claude Desktop, Claude Code): Erkennung der Startvarianten, Start über den Proxy, Start der
// Store-App, die Registry und die Aktionen client.*. Nichts wird wirklich gestartet: ein Test-Starter nimmt die Aufrufe
// auf, die Ordner sind Temp-Ordner, die Einstellung der App landet im Temp-Ordner.
[TestClass]
public class N70_ClientTests
{
    private const string StorePackage = "Claude_pzs8sxrjxfjjc";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Nimmt die Starts auf, ohne etwas zu starten
    private sealed class RecordingStarter : IClientStarter
    {
        public List<(string File, string[] Args, string? Dir, Dictionary<string, string> Env)> Calls { get; } = new();
        public bool Fail { get; set; }

        public bool Start(string file, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string> env, out string? error)
        {
            Calls.Add((file, args.ToArray(), workDir, env.ToDictionary(kv => kv.Key, kv => kv.Value)));
            error = Fail ? "test failure" : null;
            return !Fail;
        }
    }

    // Ein Aufbau mit Temp-Ordnern für %LOCALAPPDATA%, %APPDATA%, %USERPROFILE% und den PATH
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = TempDir();
        public string Local => Path.Combine(Root, "local");
        public string Roaming => Path.Combine(Root, "roaming");
        public string Home => Path.Combine(Root, "home");
        public List<string> PathDirs { get; } = new();
        public RecordingStarter Starter { get; } = new();
        public bool ProxyRunning { get; set; } = true;

        public ClientProbe Probe => new() { LocalAppData = Local, AppData = Roaming, UserProfile = Home, PathDirs = PathDirs.ToList() };

        // Claude-Client mit eigener Ablage der App (Temp-Ordner), damit Ein- und Ausschalten nie die echte Ablage trifft
        public ClaudeClient Client() => new(
            new ClaudeDesktop(new FakePlatform(), () => 17500, Path.Combine(Root, "data"), Path.Combine(Root, "layer")),
            () => ProxyRunning, () => 17500, Probe, Starter);

        public string File(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            System.IO.File.WriteAllText(path, "");
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ClientInstall Find(IProxyClient client, string key) =>
        client.Installs.Single(i => i.Key == key);

    [TestMethod]
    public void Detection_NothingInstalled_AllVariantsUnavailable_WithoutError()
    {
        using var f = new Fixture();
        var client = f.Client();

        var installs = client.Installs;

        Assert.AreEqual(3, installs.Count, "Desktop, CLI über den Proxy, CLI direkt");
        CollectionAssert.AreEqual(new[] { "desktop", "cli-proxy", "cli-direct" }, installs.Select(i => i.Key).ToArray());
        Assert.IsTrue(installs.All(i => !i.Available && i.Path == null));
        Assert.AreEqual(ClientInstallKind.Desktop, installs[0].Kind);
        Assert.AreEqual(ClientInstallKind.Cli, installs[1].Kind);

        var msg = client.Launch(installs[1], out var error);
        Assert.IsNotNull(error);
        Assert.AreEqual("", msg);
        StringAssert.Contains(error, "not installed");
        Assert.AreEqual(0, f.Starter.Calls.Count, "nicht gefunden heißt: nichts starten");
    }

    [TestMethod]
    public void Detection_StorePackage_ChoosesTheMsixDesktop()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Local, "Packages", StorePackage));

        var desktop = Find(f.Client(), "desktop");

        Assert.IsTrue(desktop.Available);
        Assert.AreEqual(Path.Combine(f.Local, "Packages", StorePackage), desktop.Path);
    }

    [TestMethod]
    public void Detection_ClassicInstall_ChoosesTheExe()
    {
        using var f = new Fixture();
        var exe = f.File(Path.Combine(f.Local, "AnthropicClaude"), "claude.exe");

        var desktop = Find(f.Client(), "desktop-exe");

        Assert.IsTrue(desktop.Available);
        Assert.AreEqual(exe, desktop.Path);
        Assert.IsFalse(f.Client().Installs.Any(i => i.Key == "desktop" && i.Available), "ohne Store-Paket gibt es keine MSIX-Variante");
    }

    [TestMethod]
    public void Detection_CliOnPath_WinsOverTheFallbacks()
    {
        using var f = new Fixture();
        var onPath = f.File(Path.Combine(f.Root, "bin"), "claude.exe");
        f.PathDirs.Add(Path.Combine(f.Root, "bin"));
        f.File(Path.Combine(f.Home, ".local", "bin"), "claude.exe");

        var proxy = Find(f.Client(), "cli-proxy");
        var direct = Find(f.Client(), "cli-direct");

        Assert.IsTrue(proxy.Available && direct.Available);
        Assert.AreEqual(onPath, proxy.Path);
        Assert.AreEqual(onPath, direct.Path);
    }

    [TestMethod]
    public void Detection_CliFallbacks_LocalBinAndNpm()
    {
        using var f = new Fixture();
        var localBin = f.File(Path.Combine(f.Home, ".local", "bin"), "claude.exe");
        Assert.AreEqual(localBin, Find(f.Client(), "cli-direct").Path);

        using var g = new Fixture();
        var npm = g.File(Path.Combine(g.Roaming, "npm"), "claude.cmd");
        Assert.AreEqual(npm, Find(g.Client(), "cli-direct").Path);
    }

    [TestMethod]
    public void Launch_CliViaProxy_PassesProgramArgumentsAndTheTwoVariables()
    {
        using var f = new Fixture();
        var bin = Path.Combine(f.Root, "bin");
        var wt = f.File(bin, "wt.exe");
        var cli = f.File(bin, "claude.exe");
        f.PathDirs.Add(bin);
        var client = f.Client();

        var msg = client.Launch(Find(client, "cli-proxy"), out var error);

        Assert.IsNull(error, error);
        StringAssert.Contains(msg, "CLI via the Stykker-Proxy");
        Assert.AreEqual(1, f.Starter.Calls.Count);
        var call = f.Starter.Calls[0];
        Assert.AreEqual(wt, call.File, "Windows Terminal, wenn vorhanden");
        CollectionAssert.AreEqual(new[] { "-d", f.Home, "cmd.exe", "/k", cli }, call.Args);
        Assert.AreEqual(f.Home, call.Dir, "Arbeitsordner: der Benutzerordner");
        Assert.AreEqual(2, call.Env.Count);
        Assert.AreEqual("http://127.0.0.1:17500", call.Env["ANTHROPIC_BASE_URL"]);
        Assert.AreEqual("stykker", call.Env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [TestMethod]
    public void Launch_CliViaProxy_WithoutWindowsTerminal_OpensCmdThroughStart()
    {
        using var f = new Fixture();
        var cli = f.File(Path.Combine(f.Root, "bin"), "claude.exe");
        f.PathDirs.Add(Path.Combine(f.Root, "bin"));

        var client = f.Client();
        client.Launch(Find(client, "cli-proxy"), out var error);

        Assert.IsNull(error, error);
        var call = f.Starter.Calls.Single();
        Assert.AreEqual("cmd.exe", call.File);
        CollectionAssert.Contains(call.Args, "start", "start öffnet ein eigenes Konsolenfenster");
        CollectionAssert.Contains(call.Args, "/k");
        Assert.AreEqual(cli, call.Args[^1]);
        Assert.AreEqual("http://127.0.0.1:17500", call.Env["ANTHROPIC_BASE_URL"]);
    }

    [TestMethod]
    public void Launch_CliViaProxy_ProxyOff_ReportsAndStartsNothing()
    {
        using var f = new Fixture();
        f.File(Path.Combine(f.Root, "bin"), "claude.exe");
        f.PathDirs.Add(Path.Combine(f.Root, "bin"));
        f.ProxyRunning = false;
        var client = f.Client();

        client.Launch(Find(client, "cli-proxy"), out var error);

        Assert.AreEqual(Strings.ClientProxyOff, error);
        Assert.AreEqual(0, f.Starter.Calls.Count, "kein stilles Scheitern und kein Start ohne Proxy");
    }

    [TestMethod]
    public void Launch_CliDirect_HasNoVariables()
    {
        using var f = new Fixture();
        f.File(Path.Combine(f.Root, "bin"), "claude.exe");
        f.PathDirs.Add(Path.Combine(f.Root, "bin"));
        f.ProxyRunning = false;
        var client = f.Client();

        client.Launch(Find(client, "cli-direct"), out var error);

        Assert.IsNull(error, "der Proxy wird für die direkte CLI nicht gebraucht");
        var call = f.Starter.Calls.Single();
        Assert.AreEqual(0, call.Env.Count);
        Assert.IsFalse(call.Args.Any(a => a.Contains("ANTHROPIC")));
    }

    [TestMethod]
    public void Launch_DesktopStore_StartsThroughTheAppId()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Local, "Packages", StorePackage));
        var client = f.Client();

        client.Launch(Find(client, "desktop"), out var error);

        Assert.IsNull(error, error);
        var call = f.Starter.Calls.Single();
        Assert.AreEqual("explorer.exe", call.File);
        CollectionAssert.AreEqual(new[] { @"shell:AppsFolder\" + StorePackage + "!Claude" }, call.Args);
        Assert.AreEqual(0, call.Env.Count);
    }

    [TestMethod]
    public void Launch_DesktopClassic_StartsTheExeInItsFolder()
    {
        using var f = new Fixture();
        var exe = f.File(Path.Combine(f.Local, "AnthropicClaude"), "claude.exe");
        var client = f.Client();

        client.Launch(Find(client, "desktop-exe"), out var error);

        Assert.IsNull(error, error);
        var call = f.Starter.Calls.Single();
        Assert.AreEqual(exe, call.File);
        Assert.AreEqual(0, call.Args.Length);
        Assert.AreEqual(Path.GetDirectoryName(exe), call.Dir);
    }

    [TestMethod]
    public void Launch_StarterFails_ReturnsTheReason()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Local, "Packages", StorePackage));
        f.Starter.Fail = true;
        var client = f.Client();

        client.Launch(Find(client, "desktop"), out var error);

        StringAssert.Contains(error, "Could not start");
        StringAssert.Contains(error, "test failure");
    }

    [TestMethod]
    public void Launch_UnknownVariant_IsRefused()
    {
        using var f = new Fixture();
        var client = f.Client();

        client.Launch(new ClientInstall("nope", ClientInstallKind.Cli, "x", "C:\\x\\claude.exe", true), out var error);

        StringAssert.Contains(error, "Unknown start option");
        Assert.AreEqual(0, f.Starter.Calls.Count);
    }

    [TestMethod]
    public void Registry_HoldsClaude_WithUniqueIds()
    {
        using var f = new Fixture();
        var claude = f.Client();
        var registry = new ProxyClients(new IProxyClient[] { claude });

        Assert.AreEqual(1, registry.All.Count);
        Assert.AreSame(claude, registry.Get("claude"));
        Assert.AreSame(claude, registry.Get("CLAUDE"), "Groß- und Kleinschreibung egal");
        Assert.IsNull(registry.Get("nope"));
        Assert.IsNull(registry.Get(null));
        Assert.ThrowsException<InvalidOperationException>(() => new ProxyClients(new IProxyClient[] { claude, claude }));
    }

    [TestMethod]
    public void Adapter_SwitchesClaudeDesktopOnAndOff()
    {
        using var f = new Fixture();
        var client = f.Client();
        Assert.IsFalse(client.Enabled);

        client.Enable(out var error);
        Assert.IsNull(error, error);
        Assert.IsTrue(client.Enabled);
        Assert.IsTrue(File.Exists(Path.Combine(f.Root, "layer", "configLibrary", "_meta.json")), "die Ablage liegt im Temp-Ordner");

        client.Disable(out error);
        Assert.IsNull(error, error);
        Assert.IsFalse(client.Enabled);
    }

    [TestMethod]
    public async Task ActionApi_UnknownClientOrVariant_IsRefusedCleanly()
    {
        var dir = TempDir();
        try
        {
            // Die Engine liest die echte Ablage nur; die Aktionen unten erreichen mit diesen Angaben nie das Umschalten oder Starten
            using var engine = new MonitorEngine(new FakePlatform(), new AppPaths(dir), new AppSettings(), RequestProxy.CreateClient());
            var ctx = new ActionContext { Engine = engine, Launcher = new LaunchCoordinator(engine, new RemotePrompt()) };

            var set = await ActionApi.ExecuteAsync(new ActionRequest { Action = "client.set", Arg = "nope", Flag = true }, ctx, new RemotePrompt());
            Assert.IsFalse(set.Ok);
            StringAssert.Contains(set.Message, "nope");

            var close = await ActionApi.ExecuteAsync(new ActionRequest { Action = "client.close", Arg = "nope" }, ctx, new RemotePrompt());
            Assert.IsFalse(close.Ok);

            var unknownClient = await ActionApi.ExecuteAsync(new ActionRequest { Action = "client.launch", Arg = "nope", Arg2 = "desktop" }, ctx, new RemotePrompt());
            Assert.IsFalse(unknownClient.Ok);
            StringAssert.Contains(unknownClient.Message, "nope");

            var unknownVariant = await ActionApi.ExecuteAsync(new ActionRequest { Action = "client.launch", Arg = "claude", Arg2 = "nope" }, ctx, new RemotePrompt());
            Assert.IsFalse(unknownVariant.Ok);
            StringAssert.Contains(unknownVariant.Message, "Unknown start option");
        }
        finally { Directory.Delete(dir, true); }
    }
}

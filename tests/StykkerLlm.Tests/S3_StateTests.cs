using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Tests;

// S3: Zustand als JSON (Server → Fenster/TUI/Telefon) und Aktionen zurück. Der Test baut eine Engine aus dem
// Simulator, schreibt den Zustand mehrfach und liest ihn wieder – so fallen Fehler wie ein Name im Array sofort auf.
[TestClass]
public class S3_StateTests
{
    private static List<SimServerSpec> Servers() => new()
    {
        new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "sim-a", Model = "model-a", Context = 8192, Slots = 2, TpsMin = 200, TpsMax = 300, RequestsPerMinute = 300, ModelGb = 4 },
        new SimServerSpec { Kind = BackendKind.Ollama, Name = "Ollama", Model = "llama3.2:3b", Context = 4096, Slots = 1, ModelGb = 2 },
    };

    [TestMethod]
    public async Task State_IsValidJson_AndSurvivesTheRoundTrip()
    {
        using var host = new SimHost(Servers(), seed: 5, autoStep: false);
        var e = host.Engine;
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "slm-s3-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.Root);
        try
        {
            var access = new AccessControl(paths, new FakePlatform());
            var queue = new EvalQueue(paths);
            queue.Models.Add(new EvalModelDef { Name = "sim", Exe = @"C:\llama\llama-server.exe", Port = 8110, ModelFile = @"C:\m\a.gguf", SizeGb = 4 });

            // ein paar Takte messen lassen, damit Verlauf und Werte im Zustand stehen
            for (int i = 0; i < 4; i++)
            {
                host.World.Step(DateTime.Now.AddSeconds(30 + i));
                await e.TickAsync();
            }

            string json = StateJson.WriteText(e, access, queue, 17400, DateTimeOffset.Now);
            var state = StateSnapshot.Parse(json);

            Assert.AreEqual(StateJson.Schema, state.Schema);
            Assert.AreEqual(17400, state.ServerPort);
            Assert.IsTrue(state.Ticks > 0, "der Server schreibt mit, wie viele Takte er schon hatte");
            Assert.IsTrue(state.Servers.Count >= 2, $"Server im Zustand: {state.Servers.Count}");

            var llama = state.Servers.FirstOrDefault(s => s.Backend == "llama.cpp");
            Assert.IsNotNull(llama, "der llama.cpp-Server des Simulators");
            Assert.AreEqual("sim-a", llama!.Name);
            Assert.IsTrue(llama.Slots.Count >= 1, "Slots");
            Assert.IsTrue(llama.HistoryCount > 0, "Verlauf");
            Assert.AreEqual(llama.HistoryCount, llama.History.Take(llama.HistoryCount).Count());
            Assert.IsTrue(llama.Slots.Count > 0, "Slots");
            Assert.IsTrue(llama.Props != null && llama.Props!.NCtx > 0, "Angaben aus /props");
            StringAssert.Contains(json, "\"history\":[", "der Verlauf steht als Zahlenliste im JSON");

            var ollama = state.Servers.FirstOrDefault(s => s.Backend == "ollama");
            Assert.IsNotNull(ollama, "Ollama");

            // Zugang: Code und Geräteliste stehen im Zustand, der Hash des Cookies nie
            var paired = access.Pair(access.Code, "Handy", "192.168.1.9")!.Value;
            state = StateSnapshot.Parse(StateJson.WriteText(e, access, queue, 17400, DateTimeOffset.Now));
            Assert.AreEqual(access.Code, state.Access.Code);
            StringAssert.Contains(state.Access.PairUrl, "/pair?code=" + access.Code);
            Assert.AreEqual(1, state.Access.Devices.Count);
            Assert.AreEqual("Handy", state.Access.Devices[0].Name);
            Assert.IsFalse(json.Contains(paired.Token), "das Gerätecookie gehört nicht in den Zustand");
            Assert.IsFalse(json.Contains(AccessControl.Hash(paired.Token)), "auch nicht der Hash");

            // Warteschlange
            Assert.IsNotNull(state.Eval);
            Assert.AreEqual(1, state.Eval!.Models.Count);
            Assert.AreEqual("sim", state.Eval.Models[0].Name);

            // und noch einmal mit Verlauf aus (der Fehler, den nur der zweite Takt zeigte)
            string indented = StateJson.Write(e, access, queue, 17400, DateTimeOffset.Now, withHistory: true, indented: true);
            Assert.IsTrue(indented.Contains('\n'), "eingerückte Fassung für den Blick auf die Datei");
            Assert.AreEqual(state.Servers.Count, StateSnapshot.Parse(indented).Servers.Count);
        }
        finally
        {
            try { Directory.Delete(paths.Root, true); } catch { }
        }
    }

    [TestMethod]
    public void State_ParsesWhatIsMissing()
    {
        // Ein alter Server oder ein gekappter Strom: fehlende Felder dürfen keinen Absturz machen
        var state = StateSnapshot.Parse("""{"schema":1,"ticks":3}""");
        Assert.AreEqual(3, state.Ticks);
        Assert.AreEqual(1000, state.IntervalMs);
        Assert.AreEqual(0, state.Servers.Count);
        Assert.AreEqual(0, state.Launches.Count);      // vor dem Start stand das Feld noch nicht im Zustand
        Assert.IsNull(state.Gpu);
        Assert.IsFalse(state.Access.Remote);
        Assert.IsTrue(state.Proxy.Port == 0);
        Assert.AreEqual("", state.Access.Code);
    }

    // S4: Das Fenster zeichnet alles aus diesem Zustand, auch die Karte "starting …" und den fehlgeschlagenen Start.
    // Der Test startet dafür zwei echte (kurzlebige) Prozesse über die Registry des Motors.
    [TestMethod]
    public async Task State_CarriesLaunches()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        using var e = new MonitorEngine(new FakePlatform(), new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var log = Path.Combine(dir, "failed.log");
        File.WriteAllText(log, "llama_model_loader: failed to load model\nmain: error: something went wrong\n");
        var broken = e.Registry.Launch(new LaunchPlan("broken", cmd, new[] { "/c", "exit", "3" }, null, 8097, "127.0.0.1", null, log, null));
        var waiting = e.Registry.Launch(new LaunchPlan("waiting", cmd, new[] { "/c", "ping", "-n", "8", "127.0.0.1" }, null, 8098, "127.0.0.1", null, Path.Combine(dir, "waiting.log"), null));
        try
        {
            var end = DateTime.Now.AddSeconds(10);
            while (broken.State != LaunchState.Failed && DateTime.Now < end) await Task.Delay(50);
            Assert.AreEqual(LaunchState.Failed, broken.State, "der Prozess ist sofort fertig, also gescheitert");

            var state = StateSnapshot.Parse(StateJson.WriteText(e, null, null, 17400, DateTimeOffset.Now));
            Assert.AreEqual(2, state.Launches.Count);

            var f = state.Launches.Single(l => l.Name == "broken");
            Assert.IsTrue(f.Failed);
            Assert.AreEqual("failed", f.State);
            Assert.AreEqual(3, f.ExitCode);
            Assert.AreEqual("http://127.0.0.1:8097", f.Url);
            Assert.AreEqual(log, f.LogFile);
            Assert.IsTrue(f.Pid > 0);
            Assert.IsFalse(f.StillRunning);
            Assert.AreEqual("llama_model_loader: failed to load model", f.LogTail[0]);
            Assert.IsTrue(f.LogTail.Any(l => l.Contains("something went wrong")), "das Logende wird mitgeschickt");
            Assert.AreNotEqual(default, f.Started);
            Assert.AreEqual(32, f.Id.Length, "die Kennung ohne Bindestriche, wie sie im Zustand steht");
            Assert.AreEqual(broken.Id, Guid.Parse(f.Id));

            var w = state.Launches.Single(l => l.Name == "waiting");
            Assert.IsFalse(w.Failed);
            Assert.AreEqual("starting", w.State);
            Assert.IsNull(w.ExitCode);
            Assert.IsTrue(w.Pid > 0);
            Assert.AreEqual(0, w.LogTail.Count);
        }
        finally
        {
            foreach (var l in new[] { broken, waiting }) { try { l.Proc?.Kill(); } catch { } }
            TryDelete(dir);
        }
    }

    // Umgebung und Kontext stehen je Server und je Verlaufseintrag im Zustand, damit Fenster und Web dieselben Angaben
    // zeigen können, ohne die Bibliothek des Servers zu lesen.
    [TestMethod]
    public async Task State_CarriesEnvironmentAndContext()
    {
        var dir = NewDir();
        var paths = new AppPaths(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(paths.LibraryFile, """
            {"Version":1,"Profiles":[],"History":[{"Key":"k1","Program":"C:\\bin\\llama-server.exe",
             "Args":["-m","m.gguf","--port","8099","-c","16384","--api-key","***"],"Name":"NanoCut","Ctx":16384,
             "HasSecrets":true,"Env":{"GGML_CUDA_K_QUANT":"8"},"WorkingDir":"C:\\work","ModelPath":"m.gguf","Port":8099,
             "Runs":3,"TotalSeconds":120,"BestTps":55.5,"TpsSum":166.5,"TpsCount":3,"MaxVramGb":12.5}]}
            """);
        var p = new FakePlatform();
        p.AddServer(30, @"C:\bin\llama-server.exe", @"C:\bin\llama-server.exe -m m.gguf --port 8095 -c 8192", 8095, "127.0.0.1",
            env: new Dictionary<string, string> { ["GGML_CUDA_K_QUANT"] = "8", ["PATH"] = @"C:\Windows" });
        using var e = new MonitorEngine(p, paths, new AppSettings(), new HttpClient(new FakeHandler()));
        await e.Registry.RefreshNowAsync();

        var state = StateSnapshot.Parse(StateJson.WriteText(e, null, null, 17400, DateTimeOffset.Now));
        var sv = state.Servers.Single(s => s.Port == 8095);
        Assert.AreEqual("8", sv.Env["GGML_CUDA_K_QUANT"]);
        Assert.IsFalse(sv.Env.ContainsKey("PATH"), "nur die gemerkten Variablen (Whitelist)");

        var h = state.History.Single();
        Assert.AreEqual(16384, h.Ctx);
        Assert.AreEqual("8", h.Env["GGML_CUDA_K_QUANT"]);
        Assert.IsTrue(h.HasSecrets);

        // und daraus lässt sich der Details-Dialog bauen, ohne die Bibliothek zu lesen
        var runs = HistoryDetails.Runs(h);
        Assert.AreEqual(Strings.N0(16384), runs.First(f => f.Key == Strings.ColContext).Value);
        Assert.AreEqual(Strings.SecretsNote, runs.First(f => f.Key == Strings.HistSecrets).Value);
        Assert.AreEqual(Strings.N1(55.5), runs.First(f => f.Key == Strings.RowTpsBest).Value);
        var pars = HistoryDetails.Parameters(h);
        Assert.AreEqual("16384", pars.First(f => f.Key == "-c").Value);
        Assert.AreEqual("***", pars.First(f => f.Key == "--api-key").Value);
        Assert.AreEqual("8", pars.First(f => f.Key == Strings.EnvRow("GGML_CUDA_K_QUANT")).Value);
        Assert.AreEqual(@"C:\bin\llama-server.exe -m m.gguf --port 8099 -c 16384 --api-key ***", HistoryDetails.CommandLine(h));
        TryDelete(dir);
    }

    // S4: Der Dialog „Details“ braucht die Laufzeit (Startzeit des Prozesses), die GPU-Details die Auslastung je Prozess –
    // beides kommt aus dem Zustand, damit das Fenster nichts selbst misst.
    [TestMethod]
    public async Task State_CarriesStartTicksAndGpuUtil()
    {
        var dir = NewDir();
        var p = new FakePlatform { GpuUtilTop = new[] { ("llama-server.exe", 37.5), ("python.exe", 4.0) } };
        p.AddServer(30, @"C:\bin\llama-server.exe", @"C:\bin\llama-server.exe -m m.gguf --port 8090", 8090);
        using var e = new MonitorEngine(p, new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        await e.Registry.RefreshNowAsync();
        await e.TickAsync();
        for (int i = 0; i < 50 && e.GpuUtilTop.Count == 0; i++) await Task.Delay(20);   // der erste Takt liest im Hintergrund

        var state = StateSnapshot.Parse(StateJson.WriteText(e, null, null, 17400, DateTimeOffset.Now));
        Assert.AreEqual(111, state.Servers.Single(s => s.Port == 8090).StartTicks, "Startzeit des Prozesses für die Laufzeit im Dialog");
        CollectionAssert.AreEqual(new[] { "llama-server.exe", "python.exe" }, state.GpuUtilTop.Select(x => x.Name).ToArray());
        Assert.AreEqual(37.5, state.GpuUtilTop[0].Percent);
        Assert.AreEqual(4.0, state.GpuUtilTop[1].Percent);

        // Von Hand gelesen: ein älterer Server ohne diese Felder bleibt lesbar, die Felder fehlen dann einfach
        var alt = StateSnapshot.Parse("""{"schema":1,"servers":[{"key":"k","port":8090}],"gpuUtilTop":[{"name":"a.exe","pct":12}]}""");
        Assert.AreEqual(0, alt.Servers[0].StartTicks);
        Assert.AreEqual(12, alt.GpuUtilTop[0].Percent);
        Assert.AreEqual(0, StateSnapshot.Parse("""{"schema":1}""").GpuUtilTop.Count);
        TryDelete(dir);
    }

    // Die Karte "starting …" und der gescheiterte Start lassen sich vom Fenster wegklicken bzw. beenden
    [TestMethod]
    public async Task LaunchActionsDismissAndStop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = NewDir();
        var p = new FakePlatform();
        using var e = new MonitorEngine(p, new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler()));
        var prompt = new RemotePrompt();
        var ctx = new ActionContext { Engine = e, Launcher = new LaunchCoordinator(e, prompt) };
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        LaunchedServer Start(string name) =>
            e.Registry.Launch(new LaunchPlan(name, cmd, new[] { "/c", "ping", "-n", "8", "127.0.0.1" }, null, 8098, "127.0.0.1", null, Path.Combine(dir, name + ".log"), null));

        try
        {
            // Wegklicken: der Prozess läuft weiter, der Eintrag ist aus der Liste weg
            var ls = Start("t1");
            p.Processes[ls.Pid] = new ProcessDetails(ls.Pid, 0, 0, cmd, null, null, new Dictionary<string, string>());
            var dismissed = await ActionApi.ExecuteAsync(new ActionRequest { Action = "launch.dismiss", Arg = ls.Id.ToString() }, ctx, prompt);
            Assert.IsTrue(dismissed.Ok, dismissed.Message);
            Assert.AreEqual(0, e.Registry.Launches.Count);
            Assert.AreEqual(0, p.Terminated.Count);
            Assert.IsTrue(p.Processes.ContainsKey(ls.Pid), "wegklicken beendet den Prozess nicht");
            try { ls.Proc!.Kill(); } catch { }

            // Beenden: mit Rückfrage beim Nutzer, danach ist der Eintrag weg und der Prozess beendet
            var again = Start("t2");
            p.Processes[again.Pid] = new ProcessDetails(again.Pid, 0, 0, cmd, null, null, new Dictionary<string, string>());
            var stopped = await ActionApi.ExecuteAsync(new ActionRequest { Action = "launch.stop", Arg = again.Id.ToString() }, ctx, prompt);
            Assert.IsTrue(stopped.Ok, stopped.Message);
            Assert.IsTrue(prompt.Messages.Any(m => m.Contains("t2")), "das Beenden wird mit Name und PID bestätigt");
            Assert.AreEqual(0, e.Registry.Launches.Count);
            Assert.AreEqual(1, p.Terminated.Count);
            Assert.IsFalse(p.Processes.ContainsKey(again.Pid));

            // Unbekannte Kennung und kaputte Kennung: Nein sagen, nicht abstürzen
            Assert.IsFalse((await ActionApi.ExecuteAsync(new ActionRequest { Action = "launch.dismiss", Arg = "kein-guid" }, ctx, prompt)).Ok);
            Assert.IsFalse((await ActionApi.ExecuteAsync(new ActionRequest { Action = "launch.stop", Arg = Guid.NewGuid().ToString() }, ctx, prompt)).Ok);
        }
        finally { TryDelete(dir); }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-s3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* ein Virenscanner hält die Datei: bleibt im Temp-Ordner */ }
    }

    [TestMethod]
    public void Action_ReadsJsonAndHelpers()
    {
        var req = ActionRequest.Parse("""{"action":"eval.enqueue","ids":["a","b"],"flag":true,"number":3,"values":{"chat":"1","genTokens":"256"}}""");
        Assert.AreEqual("eval.enqueue", req.Action);
        Assert.AreEqual(2, req.Ids.Count);
        Assert.IsTrue(req.Flag);
        Assert.AreEqual(3, req.Number);
        Assert.AreEqual("1", req.Get("chat"));
        Assert.AreEqual(256, req.GetInt("genTokens"));
        Assert.IsTrue(req.GetBool("chat"));
        Assert.IsNull(req.Get("fehlt"));
        Assert.AreEqual("http://x/?a=1&b=2", new ActionRequest { Values = { ["url"] = "http://x/?a=1&b=2" } }.Get("url"));

        var kaputt = ActionRequest.Parse("kein json");
        Assert.AreEqual("", kaputt.Action);
        Assert.AreEqual(0, kaputt.Ids.Count);
    }

    [TestMethod]
    public void Actions_AreRejectedOnAReadOnlyEngine()
    {
        using var host = new SimHost(Servers(), seed: 1, autoStep: false);
        var ctx = new ActionContext
        {
            Engine = new MonitorEngine(new FakePlatform(), new AppPaths(Path.Combine(Path.GetTempPath(), "slm-s3ro")), new AppSettings(), readOnly: true),
            Launcher = new LaunchCoordinator(host.Engine, new RemotePrompt()),
        };
        var result = ActionApi.ExecuteAsync(new ActionRequest { Action = "remote.set", Flag = true }, ctx, new RemotePrompt()).Result;
        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Message, Strings.ReadOnlyEngine);
    }

    // Der Server des Fensters ist die Engine und schreibt immer: seine Engine wird nicht als read-only
    // angelegt. Vorher teilten Fenster und Server den Single-Instance-Mutex – je nachdem, wer zuerst kam,
    // war entweder der Server nur lesend (Proxy tat nichts) oder das Fenster beendete sich lautlos.
    // Geprüft wird hier die Wirkung; dass der Server den Mutex nicht mehr nimmt, steht in EngineHost.
    [TestMethod]
    public void Actions_WorkOnTheServersEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-s3own-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var settings = AppSettings.Load(paths.SettingsFile);
            using var engine = new MonitorEngine(new FakePlatform(), paths, settings, readOnly: false);
            Assert.IsFalse(engine.ReadOnly);
            var ctx = new ActionContext { Engine = engine, Launcher = new LaunchCoordinator(engine, new RemotePrompt()) };
            var result = ActionApi.ExecuteAsync(new ActionRequest { Action = "settings.set", Values = { ["intervalMs"] = "2000" } }, ctx, new RemotePrompt()).Result;
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(2000, settings.IntervalMs);
        }
        finally { Directory.Delete(dir, true); }
    }
}

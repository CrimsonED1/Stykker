using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Strata: Live-Werte aus /metrics (JSON), weil /slots nur is_processing meldet
[TestClass]
public class StrataTests
{
    private const string Health = "{\"status\": \"ok\", \"max_context\": 65536, \"model\": \"qwen-coder\", \"loaded\": true, \"service\": \"strata\"}";
    private const string Slots = "[{\"id\": 0, \"n_ctx\": 65536, \"is_processing\": true}]";
    private const string Old = "{\"time\": 1000.5, \"duration_s\": 12.6, \"finish\": \"length\", \"prompt_tokens\": 65, \"prompt_ms\": 1250.0, \"output_tokens\": 600, \"decode_tok_s\": 53.1}";
    private const string New = "{\"time\": 2000.0, \"duration_s\": 5.0, \"finish\": \"stop\", \"prompt_tokens\": 1000, \"prompt_ms\": 500.0, \"output_tokens\": 200, \"decode_tok_s\": 50.0}";

    private static string Metrics(string live, params string[] requests) =>
        "{\"engine\": {\"context\": 65536}, \"live\": " + live + ", \"requests\": [" + string.Join(",", requests) + "], \"totals\": {\"requests\": " + requests.Length + "}}";

    private const string Generating = "{\"state\": \"generating\", \"phase\": \"thinking\", \"prompt_tokens\": 8067, \"prompt_read\": null, \"prompt_total\": null, \"generated\": 142, \"max_tokens\": 800, \"tok_s\": 50.3}";
    private const string Idle = "{\"state\": \"idle\", \"phase\": null, \"prompt_tokens\": null, \"generated\": null, \"tok_s\": null}";

    [TestMethod]
    public void Parse_LiveAndRequests()
    {
        var m = StrataApi.Parse(Metrics(Generating, Old))!;
        Assert.IsTrue(m.Live.Busy);
        Assert.IsFalse(m.Live.Reading);
        Assert.IsTrue(m.Live.Thinking);
        Assert.AreEqual(142, m.Live.Generated);
        Assert.AreEqual(8067, m.Live.PromptTokens);
        Assert.AreEqual(50.3, m.Live.Tps, 1e-9);
        Assert.AreEqual(65536, m.MaxContext);
        Assert.AreEqual(1, m.Requests.Count);
        Assert.AreEqual(ReqStatus.Truncated, StrataApi.Status(m.Requests[0].Finish));
        Assert.IsNull(StrataApi.Parse("# HELP llamacpp:prompt_tokens_total\n"));   // Prometheus-Text eines llama-server
        Assert.IsTrue(StrataApi.IsStrataHealth(Health));
        Assert.IsFalse(StrataApi.IsStrataHealth("{\"status\":\"ok\"}"));
    }

    [TestMethod]
    public async Task Simulator_StrataServer_LiveValuesAndEngineVram()
    {
        var world = new SimWorld(seed: 3);
        var s = world.Add(new SimServerSpec { Kind = BackendKind.LlamaCpp, Strata = true, Name = "strata-sim", Model = "qwen-coder", Context = 65536, Slots = 4, TpsMin = 40, TpsMax = 60, RequestsPerMinute = 600 });
        Assert.AreEqual(1, s.Slots.Length);   // Strata hat immer genau einen Slot
        Assert.IsNull(s.LogPath);
        var platform = new SimPlatform(world);
        Assert.AreEqual("strata", platform.ChildProcesses(s.Pid).Single().Name);

        world.Start();
        var t = DateTime.Now;
        for (int i = 0; i < 400 && !(s.Slots[0].Busy && !s.Slots[0].Prompting && s.Slots[0].Gen > 5); i++) world.Step(t = t.AddSeconds(0.25));
        Assert.IsTrue(s.Slots[0].Busy && !s.Slots[0].Prompting, "simulierte Anfrage erzeugt gerade");

        var info = new ServerInfo { Key = s.Key, Host = "127.0.0.1", Port = s.Port, Pid = s.Pid, StartTicks = s.StartTicks, Backend = BackendKind.LlamaCpp };
        using var w = new ServerWatcher(info, new HttpClient(new SimHandler(world)), platform, () => NetSnapshot.Empty, new ClientNamer(platform));
        await w.PollAsync();
        Assert.AreEqual(true, w.IsStrata);
        Assert.IsTrue(w.Current > 0, "t/s aus /metrics");
        Assert.AreEqual(65536, w.Slots.Single().CtxMax);
        for (int i = 0; i < 50 && w.VramGb is not > 0; i++) { await Task.Delay(100); typeof(ServerWatcher).GetField("_lastMemQuery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, DateTime.MinValue); await w.PollAsync(); }
        Assert.IsTrue(w.VramGb > 4, "VRAM kommt vom Engine-Kindprozess strata.exe");
    }

    [TestMethod]
    public void ProxyOfAnotherMonitorInstance_IsNotProbed()
    {
        foreach (var image in new[] { @"C:\x\StykkerLLM.exe", @"C:\x\stykker.exe", @"C:\x\StykkerLLM-Maui.exe" })
        {
            var e = new ServerDiscovery.ProcEntry { Details = new ProcessDetails(99, 1, 4, image, null, null, new Dictionary<string, string>()), ExeName = ProcPath.Stem(image) };
            Assert.IsTrue(ServerDiscovery.IsExcludedFromProbe(e), image);
        }
    }

    [TestMethod]
    public async Task Watcher_UsesMetrics_ForTpsAndFinishedRequests()
    {
        var h = new FakeHandler();
        string metrics = Metrics(Generating, Old);
        h.Responder = (key, _) => key.EndsWith("/metrics") ? (HttpStatusCode.OK, metrics) : null;
        h.Routes["127.0.0.1:8082/slots"] = (HttpStatusCode.OK, Slots);
        h.Routes["127.0.0.1:8082/health"] = (HttpStatusCode.OK, Health);
        h.Routes["127.0.0.1:8082/props"] = (HttpStatusCode.OK, "{\"default_generation_settings\": {\"n_ctx\": 65536}, \"total_slots\": 1, \"model_alias\": \"qwen-coder\"}");
        h.Routes["127.0.0.1:8082/v1/models"] = (HttpStatusCode.OK, "{\"data\":[{\"id\":\"qwen-coder\"}]}");
        var info = new ServerInfo { Key = "127.0.0.1:8082", Host = "127.0.0.1", Port = 8082, Backend = BackendKind.LlamaCpp };
        using var w = new ServerWatcher(info, new HttpClient(h), new FakePlatform(), () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));
        var seen = new List<FinishedRequest>();
        w.RequestFinished += seen.Add;

        await w.PollAsync();
        Assert.AreEqual(true, w.IsStrata);
        Assert.AreEqual(50.3, w.Current, 1e-9);
        var s = w.Slots.Single();
        Assert.IsTrue(s.Busy);
        Assert.AreEqual(142, s.Generated);
        Assert.AreEqual(8067 + 142, s.CtxUsed);
        Assert.AreEqual(65536, s.CtxMax);
        Assert.AreEqual(800, s.MaxTokens);
        Assert.AreEqual(1, w.Finished.Count);       // alte Anfrage beim ersten Lesen: in der Liste, aber kein Ereignis
        Assert.IsNull(w.Finished[0].Seen);
        Assert.AreEqual(0, seen.Count);

        metrics = Metrics(Idle, New, Old);
        await w.PollAsync();
        Assert.AreEqual(0, w.Current);
        Assert.IsFalse(w.Slots.Single().Busy);
        Assert.AreEqual(1, seen.Count);             // nur die neue Anfrage
        Assert.AreEqual(200, seen[0].GenTokens);
        Assert.AreEqual(2000.0, seen[0].PromptTps, 1e-9);
        Assert.AreEqual(ReqStatus.Done, seen[0].Status);
        Assert.AreEqual(200, w.GeneratedTotal);
    }
}

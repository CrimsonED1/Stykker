using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N78 – Wiederverwendeter Prompt (Prompt-Cache): Strata-Zeile, llama-Fall, CSV-Spalte am Ende, Anzeigeformat, Lesephasen
[TestClass]
public class N78_PromptReuseTests
{
    private const string Strata8067 = "strata serve: prompt 8067 tokens = 0 reused + 8067 read in 4123 ms (1956.8 tok/s), 800 generated in 17997 ms (44.5 tok/s), drafts accepted 382 of 584, 1 checkpoints";
    private const string Strata85 = "strata serve: prompt 85 tokens = 0 reused + 85 read in 1236 ms (68.8 tok/s), 207 generated in 4085 ms (50.7 tok/s), drafts accepted 135 of 154, 1 checkpoints";
    private const string Strata44k = "strata serve: prompt 44120 tokens = 44000 reused + 120 read in 3000 ms (40.0 tok/s), 90 generated in 2000 ms (45.0 tok/s), drafts accepted 1 of 2, 1 checkpoints";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n78-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServerWatcher Watcher(FakeHandler h, int port, string? manualLog = null) => new(
        new ServerInfo { Key = "127.0.0.1:" + port, Host = "127.0.0.1", Port = port, Manual = true, ManualName = "n78", ManualLog = manualLog, Backend = BackendKind.LlamaCpp },
        new HttpClient(h), new FakePlatform(), () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));

    private static string Metrics(params string[] requests) =>
        "{\"engine\": {\"context\": 65536}, \"live\": {\"state\": \"idle\"}, \"requests\": [" + string.Join(", ", requests) +
        "], \"totals\": {\"requests\": " + requests.Length + "}}";

    private static string Done(double time, int promptTokens, double promptMs, int output) =>
        "{\"time\": " + time.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"duration_s\": 0.0, \"finish\": \"stop\", \"prompt_tokens\": " + promptTokens +
        ", \"prompt_ms\": " + promptMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"output_tokens\": " + output + ", \"decode_tok_s\": 45.0}";

    [TestMethod]
    public void StrataLine_IsParsed_WithAndWithoutReuse()
    {
        Assert.AreEqual(new StrataApi.PromptLine(8067, 0, 8067, 4123), StrataApi.ParsePromptLine(Strata8067));
        Assert.AreEqual(new StrataApi.PromptLine(85, 0, 85, 1236), StrataApi.ParsePromptLine(Strata85));
        Assert.AreEqual(new StrataApi.PromptLine(44120, 44000, 120, 3000), StrataApi.ParsePromptLine(Strata44k));
        Assert.IsNull(StrataApi.ParsePromptLine("strata serve: 800 generated in 17997 ms (44.5 tok/s)"), "keine Prompt-Zeile");
        Assert.IsNull(StrataApi.ParsePromptLine(""));
        Assert.IsNull(StrataApi.ParsePromptLine(null));
    }

    [TestMethod]
    public async Task Strata_ReuseComesFromThePlanLog_AndReadTokensAreWhatWasRead()
    {
        var dir = TempDir();
        try
        {
            var log = Path.Combine(dir, "Strata-8097.log");
            File.WriteAllText(log, "");
            var h = new FakeHandler();
            h.Routes["127.0.0.1:8097/slots"] = (HttpStatusCode.OK, "[]");
            h.Routes["127.0.0.1:8097/health"] = (HttpStatusCode.OK, "{\"status\": \"ok\", \"service\": \"strata\"}");
            h.Routes["127.0.0.1:8097/metrics"] = (HttpStatusCode.OK, Metrics());
            var w = Watcher(h, 8097);
            w.UsePlanLog(log);
            var got = new List<FinishedRequest>();
            w.RequestFinished += got.Add;
            await w.PollAsync();   // erster Durchlauf: noch keine Anfragen

            File.AppendAllText(log, Strata8067 + "\n" + Strata44k + "\n");
            h.Routes["127.0.0.1:8097/metrics"] = (HttpStatusCode.OK, Metrics(
                Done(1000.5, 8067, 4123, 800), Done(2000, 44120, 3000, 90), Done(3000, 999, 500, 10)));
            await w.PollAsync();

            Assert.AreEqual(3, got.Count);
            var whole = got[0];
            Assert.AreEqual(8067, whole.PromptTokens, "ganz gelesen: gelesen = ganzer Prompt");
            Assert.AreEqual(0, whole.PromptReused);
            Assert.AreEqual(8067, whole.PromptTotal);
            var cached = got[1];
            Assert.AreEqual(120, cached.PromptTokens, "gelesen = Prompt ohne den Cache");
            Assert.AreEqual(44000, cached.PromptReused);
            Assert.AreEqual(44120, cached.PromptTotal);
            Assert.AreEqual(40.0, cached.PromptTps, 0.01, "Lesegeschwindigkeit über die gelesenen Token");
            Assert.AreEqual(3.0, cached.PromptSec, 1e-6, "Lesedauer ist die Lesezeit der Zeile");
            var unknown = got[2];
            Assert.AreEqual(-1, unknown.PromptReused, "keine Zeile im Log: unbekannt");
            Assert.AreEqual(999, unknown.PromptTokens);
            w.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task Strata_WithoutPlanLog_ReuseStaysUnknown()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:8098/slots"] = (HttpStatusCode.OK, "[]");
        h.Routes["127.0.0.1:8098/health"] = (HttpStatusCode.OK, "{\"status\": \"ok\", \"service\": \"strata\"}");
        h.Routes["127.0.0.1:8098/metrics"] = (HttpStatusCode.OK, Metrics());
        var w = Watcher(h, 8098);
        var got = new List<FinishedRequest>();
        w.RequestFinished += got.Add;
        await w.PollAsync();
        h.Routes["127.0.0.1:8098/metrics"] = (HttpStatusCode.OK, Metrics(Done(1000, 8067, 4123, 800)));
        await w.PollAsync();
        var f = got.Single();
        Assert.AreEqual(8067, f.PromptTokens);
        Assert.AreEqual(-1, f.PromptReused, "von Hand gestarteter Server: kein Log, also unbekannt");
        Assert.IsNull(w.PlanLog);
        w.Dispose();
    }

    [TestMethod]
    public async Task Llama_ReusedTokens_AreThePromptMinusTheReadOnes()
    {
        var dir = TempDir();
        try
        {
            var log = Path.Combine(dir, "llama.log");
            File.WriteAllText(log, "");
            var h = new FakeHandler();
            h.Routes["127.0.0.1:8096/slots"] = (HttpStatusCode.OK,
                "[{\"id\": 0, \"is_processing\": true, \"id_task\": 5, \"n_prompt_tokens\": 1000, \"next_token\": {\"n_decoded\": 0}, \"params\": {\"max_tokens\": 0}}]");
            var w = Watcher(h, 8096, log);
            var got = new List<FinishedRequest>();
            w.RequestFinished += got.Add;
            await w.PollAsync();   // Anfrage 5 liest seinen 1000-Token-Prompt

            File.AppendAllText(log,
                "slot print_timing: id  0 | task 5 |  prompt eval time = 150.00 ms / 200 tokens (0.75 ms per token, 1333.33 tokens per second)\n" +
                "slot print_timing: id  0 | task 5 |        eval time = 500.00 ms / 50 tokens (10.00 ms per token, 100.00 tokens per second)\n" +
                "slot print_timing: id  0 | task 5 |       total time = 650.00 ms / 250 tokens\n" +
                "srv  update_slots: release: id  0 | task 5 | stop processing: n_tokens = 1050, truncated = 0\n");
            h.Routes["127.0.0.1:8096/slots"] = (HttpStatusCode.OK, "[]");
            await w.PollAsync();

            var f = got.Single();
            Assert.AreEqual(200, f.PromptTokens);
            Assert.AreEqual(800, f.PromptReused, "1000 im Kontext, 200 gelesen");
            Assert.AreEqual(1000, f.PromptTotal);
            w.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task ReadPhase_IsListedWithItsSamples_AndCarriesTheReuse()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:8099/slots"] = (HttpStatusCode.OK, "[]");
        h.Routes["127.0.0.1:8099/health"] = (HttpStatusCode.OK, "{\"status\": \"ok\", \"service\": \"strata\"}");
        h.Routes["127.0.0.1:8099/metrics"] = (HttpStatusCode.OK, Metrics());
        var dir = TempDir();
        try
        {
            var log = Path.Combine(dir, "strata.log");
            File.WriteAllText(log, Strata44k + "\n");
            var w = Watcher(h, 8099);
            w.UsePlanLog(log);
            await w.PollAsync();
            h.Routes["127.0.0.1:8099/metrics"] = (HttpStatusCode.OK, Metrics(Done(1000, 44120, 3000, 90)));
            await w.PollAsync();

            var phase = w.ReadPhases().Single();
            Assert.AreEqual(120, phase.Tokens);
            Assert.AreEqual(44000, phase.Reused);
            Assert.AreEqual(3.0, phase.Seconds, 1e-6);
            Assert.AreEqual(w.HistoryCount - 1, phase.To, "die Lesephase endet bei der letzten Probe (sie läuft noch dort)");
            Assert.IsTrue(phase.From >= 0 && phase.From <= phase.To);
            w.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void Csv_HasPromptReusedAsLastColumn()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "requests.csv");
            var log = new RequestLog(path);
            var seen = new DateTime(2026, 1, 1, 1, 2, 3);
            log.Append(new FinishedRequest(1, "s", "m", 0, 0, 120, 40.0, 45.0, 90, 2.0, seen, ReqStatus.Done, "Aider", 44120, "k", 44000));
            log.Append(new FinishedRequest(2, "s", "m", 0, 0, 999, 100.0, 45.0, 10, 1.0, seen, ReqStatus.Done, "", 0, "k"));
            var lines = File.ReadAllLines(path);
            Assert.AreEqual(3, lines.Length);
            var header = lines[0].TrimStart('﻿');
            Assert.AreEqual(RequestLog.Header, header);
            Assert.IsTrue(header.EndsWith(",Status,PromptReused"), header);
            Assert.IsTrue(lines[1].EndsWith(",done,44000"), lines[1]);
            Assert.IsTrue(lines[2].EndsWith(",done,-1"), lines[2]);
            Assert.AreEqual(header.Split(',').Length, lines[1].Split(',').Length, "gleich viele Spalten");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void CacheCell_ShowsTheSavedTokens_DashWhenNothingReused_EmptyWhenUnknown()
    {
        Assert.AreEqual("44.9k ↺", RequestRows.CacheText(44912));
        Assert.AreEqual("512 ↺", RequestRows.CacheText(512));
        Assert.AreEqual("1.2M ↺", RequestRows.CacheText(1_200_000));
        Assert.AreEqual("—", RequestRows.CacheText(0));
        Assert.AreEqual("", RequestRows.CacheText(-1));
    }

    [TestMethod]
    public void CacheTip_GivesThePercentageAndTheNumbers()
    {
        var tip = RequestRows.CacheTip(44000, 4000);
        Assert.IsTrue(tip.StartsWith("92% of the prompt reused"), tip);
        Assert.AreEqual(Strings.ReqCacheNone, RequestRows.CacheTip(0, 500));
        Assert.AreEqual(Strings.ReqCacheUnknown, RequestRows.CacheTip(-1, 500));
    }

    [TestMethod]
    public void ReadPhaseTip_NamesTokensSecondsAndReuse()
    {
        Assert.IsTrue(Strings.ReadPhaseTip(120, 3.04, 44000).StartsWith("read 120 tokens in 3"));
        Assert.IsTrue(Strings.ReadPhaseTip(120, 3.04, 44000).EndsWith("reused)"));
        Assert.IsTrue(Strings.ReadPhaseTip(8067, 4.1, -1).StartsWith("read 8"));
        Assert.IsFalse(Strings.ReadPhaseTip(8067, 4.1, -1).Contains("reused"));
    }
}

using System.Net;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N100 – Lesen des Prompts (Prefill): Fortschritt über die neu zu lesenden Token (Prompt ohne Cache), Restzeit, Texte
[TestClass]
public class N100_PromptReadingTests
{
    private static SlotView Reading(int prompt, int cached, int done, double tps) =>
        new(0, true, true, 0, 0, prompt, 200000, 0, done, -1, 7, cached, tps);

    [TestMethod]
    public void Progress_counts_only_the_tokens_that_are_not_in_the_cache()
    {
        var r = PromptReading.Of(Reading(52633, 49094, 318, 0))!;

        Assert.AreEqual(3539, r.ToRead);
        Assert.AreEqual(318.0 / 3539, r.Frac, 1e-9);
        Assert.IsNull(r.SecondsLeft, "ohne Geschwindigkeit keine Restzeit");
        Assert.AreEqual("9 %", Strings.CardReading(r));
        StringAssert.Contains(Strings.CardReadingTip(r), "318 of 3,539 new tokens (49,094 from the cache)");
    }

    [TestMethod]
    public void With_a_speed_the_time_left_follows()
    {
        var r = PromptReading.Of(Reading(10000, 0, 2000, 200))!;

        Assert.AreEqual(40, r.SecondsLeft!.Value, 1e-9);
        Assert.AreEqual("20 % · 40 s", Strings.CardReading(r));
        StringAssert.Contains(Strings.CardReadingTip(r), "200 t/s · about 40 s left");
    }

    [TestMethod]
    public void Only_a_reading_slot_with_a_known_cache_has_a_reading()
    {
        Assert.IsNull(PromptReading.Of(Reading(1000, -1, 10, 5)), "Server meldet keinen Cache");
        Assert.IsNull(PromptReading.Of(Reading(1000, 0, 10, 5) with { ReadingPrompt = false }), "schreibt schon");
        Assert.IsNull(PromptReading.Of(Reading(1000, 0, 10, 5) with { Busy = false }), "wartet");
    }

    [TestMethod]
    public async Task The_watcher_reads_the_cache_and_measures_against_the_new_tokens()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:8095/slots"] = (HttpStatusCode.OK,
            "[{\"id\": 0, \"n_ctx\": 200192, \"is_processing\": true, \"id_task\": 9, \"n_prompt_tokens\": 52633, " +
            "\"n_prompt_tokens_processed\": 318, \"n_prompt_tokens_cache\": 49094, \"next_token\": [{\"n_decoded\": 0, \"n_remain\": 16384}], " +
            "\"params\": {\"max_tokens\": 16384}}]");
        using var w = new ServerWatcher(
            new ServerInfo { Key = "127.0.0.1:8095", Host = "127.0.0.1", Port = 8095, Manual = true, ManualName = "n100", Backend = BackendKind.LlamaCpp },
            new HttpClient(h), new FakePlatform(), () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));

        await w.PollAsync();

        var s = w.Slots.Single();
        Assert.IsTrue(s.ReadingPrompt);
        Assert.AreEqual(49094, s.PromptCached);
        Assert.AreEqual(318.0 / 3539, s.PromptProgress, 1e-9, "nicht 318 / 52633 und nicht der Log-Wert mit Cache");
        Assert.AreEqual(3539, PromptReading.Of(s)!.ToRead);
    }

    [TestMethod]
    public async Task An_older_server_without_the_cache_field_keeps_the_old_behaviour()
    {
        var h = new FakeHandler();
        h.Routes["127.0.0.1:8094/slots"] = (HttpStatusCode.OK,
            "[{\"id\": 0, \"is_processing\": true, \"id_task\": 5, \"n_prompt_tokens\": 1000, \"n_prompt_tokens_processed\": 100, \"next_token\": {\"n_decoded\": 0}, \"params\": {\"max_tokens\": 0}}]");
        using var w = new ServerWatcher(
            new ServerInfo { Key = "127.0.0.1:8094", Host = "127.0.0.1", Port = 8094, Manual = true, ManualName = "n100b", Backend = BackendKind.LlamaCpp },
            new HttpClient(h), new FakePlatform(), () => NetSnapshot.Empty, new ClientNamer(new FakePlatform()));

        await w.PollAsync();

        var s = w.Slots.Single();
        Assert.AreEqual(-1, s.PromptCached);
        Assert.AreEqual(-1, s.PromptProgress, 1e-9, "ohne Log-Zeile und ohne Cache-Angabe: unbekannt");
        Assert.IsNull(PromptReading.Of(s));
    }
}

using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Spekulatives Decoding: die Zähler eines llama-server. Die Namen sind aus den Quellen des
// llama.cpp auf diesem Rechner (tools/server/server-task.cpp, Stand 2026-10-05), nicht geraten.
[TestClass]
public class SpecDecodeTests
{
    // Auszug aus einem echten /metrics mit laufendem Entwurfsmodell
    private const string Metrics = """
        # HELP llamacpp:spec_decode_num_draft_tokens_total Speculative: Total draft tokens generated
        # TYPE llamacpp:spec_decode_num_draft_tokens_total counter
        llamacpp:spec_decode_num_draft_tokens_total 1468
        # TYPE llamacpp:spec_decode_num_accepted_tokens_total counter
        llamacpp:spec_decode_num_accepted_tokens_total 1204
        # TYPE llamacpp:spec_decode_num_drafts_total counter
        llamacpp:spec_decode_num_drafts_total 977
        # TYPE llamacpp:spec_decode_num_accepted_tokens_per_pos_total counter
        llamacpp:spec_decode_num_accepted_tokens_per_pos_total{position="0"} 601
        llamacpp:spec_decode_num_accepted_tokens_per_pos_total{position="1"} 603
        llamacpp:requests_deferred 2
        """;

    // Ohne Entwurfsmodell schreibt llama-server diese Zeilen nicht
    private const string OhneDraft = """
        # TYPE llamacpp:requests_processing gauge
        llamacpp:requests_processing 1
        llamacpp:requests_deferred 0
        """;

    [TestMethod]
    public void Spec_ReadsTheThreeCountersAndIgnoresTheLabelledOnes()
    {
        var s = LlamaSpecApi.Parse(Metrics);
        Assert.AreEqual(1468, s.Drafted);
        Assert.AreEqual(1204, s.Accepted);
        Assert.AreEqual(977, s.Steps);
        // Die gelabelte Reihe je Position darf nicht mitgezählt werden (sonst zählt jedes Token mehrfach)
        Assert.AreEqual(0.82, LlamaSpecApi.Rate(s), 0.005, "1204 von 1468 Entwürfen angenommen");
        Assert.AreEqual(2.23, LlamaSpecApi.MeanAccepted(s), 0.02, "mean len wie llama.cpp: 1 + angenommen/Schritte");
    }

    [TestMethod]
    public void Spec_WithoutADraftModelReadsZero()
    {
        var s = LlamaSpecApi.Parse(OhneDraft);
        Assert.AreEqual(0, s.Drafted);
        Assert.AreEqual(0.0, LlamaSpecApi.Rate(s));
        Assert.AreEqual(0.0, LlamaSpecApi.MeanAccepted(s));
        Assert.AreEqual(0.0, LlamaSpecApi.Rate(new LlamaSpecApi.Spec(0, 5, 3)), "ohne Entwürfe keine Quote (nicht 5/0)");
        Assert.AreEqual(1.0, LlamaSpecApi.Rate(new LlamaSpecApi.Spec(100, 100, 50)), "100 % sind 100 %");
    }

    [TestMethod]
    public void Spec_TextNamesBothNumbersAndTheShare()
    {
        var text = Strings.DraftRate(1204, 1468);
        StringAssert.Contains(text, Strings.N0(1204));
        StringAssert.Contains(text, Strings.N0(1468));
        StringAssert.Contains(text, "82 %");
    }

    // Ende zu Ende über den Simulator: ein Server mit Entwurfsmodell zählt, ohne zählt nicht – und der Zustand
    // des Clients trägt beide Zahlen (additiv, Schema 1).
    [TestMethod]
    public async Task Spec_SimulatedServerCountsDrafts_AndTheStateCarriesThem()
    {
        var mit = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "draft", Model = "m", Context = 8192, Slots = 1, TpsMin = 60, TpsMax = 80, RequestsPerMinute = 120, DraftModel = 4 };
        var ohne = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "normal", Model = "m2", Context = 8192, Slots = 1, TpsMin = 60, TpsMax = 80, RequestsPerMinute = 120 };
        using var host = new SimHost(new List<SimServerSpec> { mit, ohne }, seed: 3, autoStep: false);
        host.World.Start();
        for (int i = 0; i < 30; i++)
        {
            host.World.Step(DateTime.Now.AddSeconds(2 * (i + 1)));
            await host.Engine.Registry.RefreshNowAsync();
            foreach (var s in host.Engine.Servers) { s.ExternalEverySeconds = 0; s.MetricsEverySeconds = 0; }
            await host.Engine.TickAsync();
            await Task.Delay(10);
        }

        var draft = host.Engine.Servers.First(s => s.Name == "draft");
        var plain = host.Engine.Servers.First(s => s.Name == "normal");
        Assert.IsTrue(draft.SpecActive, "der Server mit Entwurfsmodell zählt");
        Assert.IsTrue(draft.Spec.Drafted > 0 && draft.Spec.Accepted > 0);
        Assert.IsTrue(draft.Spec.Accepted <= draft.Spec.Drafted, "nicht mehr angenommen als entworfen");
        Assert.IsFalse(plain.SpecActive, "ohne Entwurfsmodell bleibt alles null");

        var json = StateJson.WriteText(host.Engine, null, null, 8078, DateTimeOffset.Now);
        var state = StateSnapshot.Parse(json);
        var mitImZustand = state.Servers.First(s => s.Name == "draft");
        var ohneImZustand = state.Servers.First(s => s.Name == "normal");
        Assert.AreEqual(draft.Spec.Drafted, mitImZustand.SpecDrafted, "Zustand nennt die Entwürfe");
        Assert.AreEqual(draft.Spec.Accepted, mitImZustand.SpecAccepted);
        Assert.IsNull(ohneImZustand.SpecDrafted, "ohne Entwurfsmodell: null, damit die Oberfläche nichts zeigt");
        StringAssert.Contains(json, "\"specDrafted\":");
    }
}
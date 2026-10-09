using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class HistoryDetailsTests
{
    private static HistoryEntry Entry(string? model = "m.gguf") => new()
    {
        Key = "k", Program = @"C:\bin\llama-server.exe",
        Args = new List<string> { "-m", model!, "--port", "8099", "-c", "8192", "-ngl", "99", "extra" },
        WorkingDir = @"C:\work", ModelPath = model, Name = "NanoCut", Port = 8099, Ctx = 8192,
        Env = new Dictionary<string, string> { ["GGML_X"] = "1" },
        FirstSeen = new DateTime(2026, 10, 1, 8, 0, 0), LastSeen = new DateTime(2026, 10, 3, 9, 15, 0),
        Runs = 7, TotalSeconds = 3600, BestTps = 62.5, TpsSum = 300, TpsCount = 6, MaxVramGb = 14.2,
    };

    private static string Value(IReadOnlyList<HistoryDetails.Fact> facts, string key) =>
        facts.Where(f => f.Key == key).Select(f => f.Value).FirstOrDefault() ?? "";

    [TestMethod]
    public void CommandLineStartsWithTheProgram()
    {
        Assert.AreEqual(@"C:\bin\llama-server.exe -m m.gguf --port 8099 -c 8192 -ngl 99 extra", HistoryDetails.CommandLine(Entry()));
    }

    [TestMethod]
    public void ParametersListArgumentsAndEnvironment()
    {
        var facts = HistoryDetails.Parameters(Entry());
        Assert.AreEqual("m.gguf", Value(facts, "-m"));      // Parameter stehen so da, wie sie geschrieben wurden
        Assert.AreEqual("8099", Value(facts, "--port"));
        Assert.AreEqual("8192", Value(facts, "-c"));
        Assert.AreEqual("99", Value(facts, "-ngl"));
        Assert.AreEqual("extra", Value(facts, Strings.RowArgument));   // Positionsargument ohne Namen
        Assert.AreEqual("1", Value(facts, Strings.EnvRow("GGML_X")));
    }

    [TestMethod]
    public void RunsShowsMeasuredValuesOnly()
    {
        var h = Entry();
        var facts = HistoryDetails.Runs(h);
        Assert.AreEqual(h.Program, Value(facts, Strings.HistProgram));
        Assert.AreEqual(@"C:\work", Value(facts, Strings.ColWorkingDir));
        Assert.AreEqual("8099", Value(facts, Strings.ColPort));
        Assert.AreEqual(Strings.N0(8192), Value(facts, Strings.ColContext));
        Assert.AreEqual("2026-10-01 08:00", Value(facts, Strings.RowFirstSeen));
        Assert.AreEqual("2026-10-03 09:15", Value(facts, Strings.ColLastSeen));
        Assert.AreEqual("7", Value(facts, Strings.ColRuns));
        Assert.AreEqual(Strings.N1(62.5), Value(facts, Strings.RowTpsBest));
        Assert.AreEqual(Strings.N1(50.0), Value(facts, Strings.RowTpsAverage));
        Assert.AreEqual(Strings.N1(14.2) + " GB", Value(facts, Strings.RowGpuMax));
        Assert.IsFalse(facts.Any(f => f.Key == Strings.HistSecrets));     // keine Secrets -> keine Zeile

        h.BestTps = 0; h.TpsCount = 0; h.MaxVramGb = 0; h.Port = null; h.Ctx = null;
        var sparse = HistoryDetails.Runs(h);
        Assert.IsFalse(sparse.Any(f => f.Key == Strings.RowTpsBest || f.Key == Strings.RowTpsAverage || f.Key == Strings.RowGpuMax
            || f.Key == Strings.ColPort || f.Key == Strings.ColContext));

        Assert.AreEqual(Strings.SecretsNote, Value(HistoryDetails.Runs(new HistoryEntry { HasSecrets = true }), Strings.HistSecrets));
    }

    [TestMethod]
    public void ModelPathResolvesRelativeToTheWorkingFolder()
    {
        Assert.AreEqual(Path.Combine(@"C:\work", "m.gguf"), HistoryDetails.ModelPath(Entry()));
        Assert.AreEqual("C:\\abs\\x.gguf", HistoryDetails.ModelPath(Entry(@"C:\abs\x.gguf")));
        Assert.IsNull(HistoryDetails.ModelPath(Entry(null)));
    }

    [TestMethod]
    public void ModelFileFactsReadTheGgufHeaderAndEstimateVram()
    {
        var path = WriteGguf();
        try
        {
            var facts = HistoryDetails.ModelFileFacts(Entry(path), path);
            Assert.AreEqual("Test Model", Value(facts, Strings.ColName));
            Assert.AreEqual("qwen35", Value(facts, Strings.HistArchitecture));
            Assert.AreEqual("Q4_K_M", Value(facts, Strings.HistQuantization));
            Assert.AreEqual("1050", Value(facts, Strings.HistParameters));
            Assert.AreEqual("64", Value(facts, Strings.HistLayers));
            Assert.AreEqual(Strings.N0(262144), Value(facts, Strings.HistTrainedContext));
            Assert.IsTrue(Value(facts, Strings.HistFileSize).EndsWith(" GB"));
            Assert.IsTrue(Value(facts, Strings.HistVramEstimate).StartsWith(Strings.N1(VramEstimate.Estimate(
                Gguf.TryRead(path)!, 8192, null, null, 99).TotalGb) + " GB"));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ModelFileFactsWithoutAReadableFile()
    {
        Assert.AreEqual(0, HistoryDetails.ModelFileFacts(Entry(null)).Count);            // kein Modellpfad: der Pfad steht schon in der Oberfläche

        var missing = Path.Combine(Path.GetTempPath(), "slm-no-" + Guid.NewGuid().ToString("N") + ".gguf");
        var facts = HistoryDetails.ModelFileFacts(Entry(missing), missing);
        Assert.AreEqual(Strings.ModelFileMissing, Value(facts, Strings.HistFile));
        Assert.IsFalse(facts.Any(f => f.Key == Strings.HistFileSize));

        var h = Entry(missing); h.ModelSizeGb = 9.8;
        Assert.AreEqual(Strings.N1(9.8) + " GB", Value(HistoryDetails.ModelFileFacts(h, missing), Strings.HistFileSizeWhenRan));
    }

    // Eine kleine GGUF-Datei mit Kopf, damit die Aufbereitung ohne echtes Modell geprüft werden kann
    private static string WriteGguf()
    {
        var path = Path.Combine(Path.GetTempPath(), "slm-hist-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(path, new GgufBuilder()
            .Text("general.architecture", "qwen35").Text("general.name", "Test Model")
            .U32("general.file_type", 15)
            .U32("qwen35.block_count", 64).U32("qwen35.embedding_length", 5120)
            .U32("qwen35.attention.head_count", 24).U32("qwen35.attention.head_count_kv", 4)
            .U32("qwen35.attention.key_length", 256).U32("qwen35.full_attention_interval", 4)
            .U64("qwen35.context_length", 262144)
            .Tensor("a", new long[] { 100, 10 }, 12).Tensor("b", new long[] { 50 }, 0)
            .Build());
        return path;
    }
}
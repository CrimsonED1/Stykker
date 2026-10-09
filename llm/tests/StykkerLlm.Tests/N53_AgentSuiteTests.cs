using System.Net;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;
using StykkerLlm.Core.Eval;

namespace StykkerLlm.Tests;

// Agent-Suite: Aufgaben mit echten Werkzeugen im Wegwerf-Ordner, Bewertung durch Prüfung des Ordners danach
[TestClass]
public class N53_AgentSuiteTests
{
    [TestMethod]
    public void Suite_IsBuiltIn_EveryTaskHasFilesAndAChecker()
    {
        var s = EvalSuites.BuiltIn("agent");
        Assert.AreEqual("agent", s.Name);
        Assert.IsTrue(s.Tasks.Count >= 10);
        foreach (var t in s.Tasks)
        {
            Assert.AreEqual("agent", t.Check.Type, t.Id);
            Assert.IsTrue(t.Files.Count > 0, t.Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(t.Check.Tests), t.Id);
        }
        Assert.IsTrue(EvalSuites.BuiltInNames.Contains("agent"));
    }

    // Ohne Arbeit des Modells darf keine Aufgabe bestanden sein (sonst misst sie nichts). Das Modell hier antwortet nur.
    [TestMethod]
    public async Task DoingNothing_PassesNoTask()
    {
        var python = PythonRunner.Find();
        if (python == null) Assert.Inconclusive("no Python");
        using var http = new HttpClient(new Model(_ => Answer("Done.")));
        var run = await EvalRunner.RunAsync(http, "http://x:1", EvalSuites.BuiltIn("agent"), new EvalOptions { Python = python }, CancellationToken.None);
        Assert.AreEqual(EvalSuites.BuiltIn("agent").Tasks.Count, run.Tasks.Count);
        foreach (var t in run.Tasks) Assert.AreEqual(0, t.Score, t.Id + ": " + t.Note);
    }

    // Ein Modell, das die Konfiguration richtig ändert (Werkzeug write_file), besteht genau diese Aufgabe
    [TestMethod]
    public async Task AModelThatDoesTheWork_Passes()
    {
        var python = PythonRunner.Find();
        if (python == null) Assert.Inconclusive("no Python");
        var suite = EvalSuites.BuiltIn("agent");
        suite.Tasks.RemoveAll(t => t.Id != "a-config-edit");
        const string ini = "; app settings\n[server]\nhost = 127.0.0.1\nport = 9090\nworkers = 4\n\n[log]\ndebug = false\nfile = app.log\n";
        int calls = 0;
        using var http = new HttpClient(new Model(_ => ++calls == 1
            ? ToolCall("write_file", JsonSerializer.Serialize(new { path = "app.ini", content = ini }))
            : Answer("Changed port and debug.")));
        var run = await EvalRunner.RunAsync(http, "http://x:1", suite, new EvalOptions { Python = python }, CancellationToken.None);
        var r = run.Tasks.Single();
        Assert.AreEqual(1, r.Score, r.Note);
        StringAssert.Contains(r.Answer, "write_file: app.ini");
        Assert.AreEqual(2, r.Rounds);
    }

    [TestMethod]
    public async Task WithoutPermissionForModelCode_AgentTasksAreSkipped()
    {
        using var http = new HttpClient(new Model(_ => Answer("x")));
        var run = await EvalRunner.RunAsync(http, "http://x:1", EvalSuites.BuiltIn("agent"), new EvalOptions { Python = null }, CancellationToken.None);
        Assert.IsTrue(run.Tasks.All(t => t.Skipped));
    }

    // Kontext des Servers zu klein (llama-server antwortet 400): nicht bewertet statt durchgefallen
    [TestMethod]
    public async Task ContextTooSmall_IsSkipped_NotFailed()
    {
        var python = PythonRunner.Find();
        if (python == null) Assert.Inconclusive("no Python");
        var suite = EvalSuites.BuiltIn("agent");
        suite.Tasks.RemoveAll(t => t.Id != "a-count-files");
        using var http = new HttpClient(new Model(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("{\"error\":{\"message\":\"request (4210 tokens) exceeds the available context size (4096 tokens), try increasing it\"}}") }));
        var run = await EvalRunner.RunAsync(http, "http://x:1", suite, new EvalOptions { Python = python }, CancellationToken.None);
        var r = run.Tasks.Single();
        Assert.IsTrue(r.Skipped, r.Note);
        StringAssert.Contains(r.Note, "context");
    }

    // Ältere Läufe ohne Rechner und Läufe mit Pfad als Modellname landen in derselben Zeile der Rangliste
    [TestMethod]
    public void OldRuns_GetTheMachineFromTheirUrl_AndPathsBecomeNames()
    {
        var old = EvalSuites.Normalize(new EvalRun { Model = "bonsai-2-27b", Url = "http://127.0.0.1:8080", Machine = "" });
        Assert.AreEqual(Environment.MachineName, old.Machine);
        var remote = EvalSuites.Normalize(new EvalRun { Model = "x", Url = "http://alexpc:8080" });
        Assert.AreEqual("alexpc", remote.Machine);
        var kept = EvalSuites.Normalize(new EvalRun { Model = "x", Url = "http://127.0.0.1:1", Machine = "ANDERER" });
        Assert.AreEqual("ANDERER", kept.Machine, "ein gespeicherter Rechner bleibt");
        Assert.AreEqual("Ornith-1.5-9B-Q8_0", EvalSuites.Normalize(new EvalRun { Model = @"D:\models\Ornith\Ornith-1.5-9B-Q8_0.gguf" }).Model);
    }

    private sealed class Model(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answer(await request.Content!.ReadAsStringAsync(ct));
    }

    private static HttpResponseMessage Sse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent("data: " + json + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Answer(string text) =>
        Sse("{\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "},\"finish_reason\":\"stop\"}]}");

    private static HttpResponseMessage ToolCall(string name, string args) =>
        Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":" + JsonSerializer.Serialize(name) +
            ",\"arguments\":" + JsonSerializer.Serialize(args) + "}}]},\"finish_reason\":\"tool_calls\"}]}");
}

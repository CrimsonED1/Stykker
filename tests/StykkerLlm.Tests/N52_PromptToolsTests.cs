using System.Net;
using System.Text;
using System.Text.Json;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Werkzeuge des Mini-Harness: im Arbeitsordner lesen/schreiben/ändern, Befehle, Freigabe, Schleife mit dem Modell
[TestClass]
public class N52_PromptToolsTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "slm-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static ToolCall Call(string name, object args) => new("c1", name, JsonSerializer.Serialize(args));

    [TestMethod]
    public async Task Files_ListReadWriteEdit_StayInsideTheFolder()
    {
        var dir = TempDir();
        var tools = new PromptTools(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ntwo\nthree\n");

        var list = await tools.RunAsync(Call("list_dir", new { path = "." }));
        StringAssert.Contains(list.Result, "a.txt");
        var read = await tools.RunAsync(Call("read_file", new { path = "a.txt", offset = 2, limit = 1 }));
        Assert.IsTrue(read.Ok);
        StringAssert.Contains(read.Result, "two");
        Assert.IsFalse(read.Result.Contains("three"));

        Assert.IsTrue((await tools.RunAsync(Call("write_file", new { path = "sub/b.txt", content = "hello" }))).Ok);
        Assert.AreEqual("hello", File.ReadAllText(Path.Combine(dir, "sub", "b.txt")));
        Assert.IsTrue((await tools.RunAsync(Call("edit_file", new { path = "sub/b.txt", old_text = "hello", new_text = "hallo" }))).Ok);
        Assert.AreEqual("hallo", File.ReadAllText(Path.Combine(dir, "sub", "b.txt")));
        var twice = await tools.RunAsync(Call("edit_file", new { path = "a.txt", old_text = "o", new_text = "0" }));
        Assert.IsFalse(twice.Ok, "nicht eindeutig");
        StringAssert.Contains(twice.Result, "2 times");

        foreach (var outside in new[] { "../x.txt", "..\\..\\x.txt", Path.Combine(Path.GetTempPath(), "x.txt"), "C:\\Windows\\win.ini" })
        {
            var r = await tools.RunAsync(Call("read_file", new { path = outside }));
            Assert.AreEqual(Strings.ToolOutside, r.Result, outside);
        }
        Assert.IsNull(tools.Resolve(dir + "-nebenan\\x.txt"), "ein Ordner mit gleichem Anfang ist nicht drinnen");
        Assert.AreEqual(Strings.ToolBadArguments, (await tools.RunAsync(new ToolCall("c", "read_file", "{kaputt"))).Result);
    }

    [TestMethod]
    public async Task Commands_RunInTheFolder_WithExitCode()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        var tools = new PromptTools(dir);
        var cmd = await tools.RunAsync(Call("run_command", new { command = "dir /b" }));
        Assert.IsTrue(cmd.Ok, cmd.Result);
        StringAssert.Contains(cmd.Result, "marker.txt");
        StringAssert.StartsWith(cmd.Result, "exit code 0");
        Assert.AreEqual("run_command", cmd.Call.Name, "das Ergebnis gehört zum Aufruf");

        var ps = await tools.RunAsync(Call("run_command", new { command = "Get-ChildItem -Name; exit 3", shell = "powershell" }));
        Assert.IsFalse(ps.Ok);
        StringAssert.StartsWith(ps.Result, "exit code 3");
        StringAssert.Contains(ps.Result, "marker.txt");
        Assert.IsTrue(Call("run_command", new { command = "x" }).NeedsApproval);
        Assert.IsFalse(Call("read_file", new { path = "x" }).NeedsApproval);
    }

    // Ein Modell, das in Runde 1 zwei Werkzeuge aufruft (gestreamt, Argumente in Stücken) und in Runde 2 antwortet
    private sealed class ToolModel : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            string sse = Bodies.Count == 1
                ? Ev("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"t1\",\"type\":\"function\",\"function\":{\"name\":\"list_dir\",\"arguments\":\"{\\\"pa\"}}]}}]}")
                  + Ev("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\".\\\"}\"}}]}}]}")
                  + Ev("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"t2\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"path\\\":\\\"out.txt\\\",\\\"content\\\":\\\"hi\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}")
                : Ev("{\"choices\":[{\"delta\":{\"content\":\"Done.\"},\"finish_reason\":\"stop\"}]}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
        private static string Ev(string json) => "data: " + json + "\n\n";
    }

    [TestMethod]
    public async Task Loop_RunsTools_AsksBeforeWriting_AndSendsResultsBack()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "readme.md"), "x");
        var model = new ToolModel();
        using var http = new HttpClient(model);
        var session = new PromptSession();
        session.Options.Tools = new PromptTools(dir);
        var asked = new List<string>();

        var r = await session.SendAsync(http, "http://x:1", "Look around and write out.txt", approve: c => { asked.Add(c.Name); return Task.FromResult(false); });
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual("Done.", r.Text);
        CollectionAssert.AreEqual(new[] { "write_file" }, asked, "nur Schreiben fragt, Auflisten nicht");
        Assert.IsFalse(File.Exists(Path.Combine(dir, "out.txt")), "abgelehnt heißt: nichts geschrieben");
        Assert.AreEqual(2, session.ToolLog.Count);
        Assert.IsTrue(session.ToolLog[1].Denied);
        StringAssert.Contains(session.ToolLog[0].Result, "readme.md");

        // user, assistant(tool_calls), tool, tool, assistant
        Assert.AreEqual(5, session.Turns.Count);
        using var second = JsonDocument.Parse(model.Bodies[1]);
        var msgs = second.RootElement.GetProperty("messages");
        Assert.AreEqual("t1", msgs[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.AreEqual("{\"path\":\".\"}", msgs[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString(), "Argumente aus den Stücken zusammengesetzt");
        Assert.AreEqual("tool", msgs[2].GetProperty("role").GetString());
        Assert.AreEqual("t2", msgs[3].GetProperty("tool_call_id").GetString());
        Assert.AreEqual(Strings.ToolDenied, msgs[3].GetProperty("content").GetString());
        Assert.IsTrue(second.RootElement.TryGetProperty("tools", out var defs) && defs.GetArrayLength() == 5);

        // Ohne Rückfrage wird geschrieben
        session.Clear();
        session.Options.AutoApprove = true;
        model.Bodies.Clear();
        await session.SendAsync(http, "http://x:1", "again");
        Assert.AreEqual("hi", File.ReadAllText(Path.Combine(dir, "out.txt")));
    }
}

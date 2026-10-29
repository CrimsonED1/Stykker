using System.Net;
using System.Net.Sockets;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Seite /prompt: Antworten als Markdown (ChatMarkdown), Fehlergründe in der Sprache der Oberfläche (PromptHarness.Reason),
// Messwerte je Antwort und der gestreamte Simulator
[TestClass]
public class N110_ChatMarkdownTests
{
    [TestMethod]
    public void Parse_CodeBlockWithLanguage()
    {
        var b = ChatMarkdown.Parse("Here:\n\n```python\ndef f():\n    return 1\n```\nDone.");
        Assert.AreEqual(3, b.Count);
        Assert.AreEqual(MdKind.Paragraph, b[0].Kind);
        Assert.AreEqual(MdKind.Code, b[1].Kind);
        Assert.AreEqual("python", b[1].Language);
        Assert.AreEqual("def f():\n    return 1", b[1].Text);
        Assert.IsFalse(b[1].Open);
        Assert.AreEqual("Done.", b[2].Spans.Single().Text);
    }

    [TestMethod]
    public void Parse_OpenCodeBlock_WhileStreaming()
    {
        var b = ChatMarkdown.Parse("```js\nconst a = 1;\nconst b");
        Assert.AreEqual(1, b.Count);
        Assert.IsTrue(b[0].Open, "die Antwort läuft noch: der Block reicht bis zum Ende");
        Assert.AreEqual("const a = 1;\nconst b", b[0].Text);
    }

    [TestMethod]
    public void Parse_CodeKeepsMarkdownLiteral()
    {
        var b = ChatMarkdown.Parse("```\n# not a heading\n- not a list **x**\n```");
        Assert.AreEqual("# not a heading\n- not a list **x**", b.Single().Text);
    }

    [TestMethod]
    public void Parse_HeadingsListsQuoteRule()
    {
        var b = ChatMarkdown.Parse("## Title\n\n- one\n- two\n  more\n  - nested\n\n3. three\n4. four\n\n> quoted\n\n---");
        Assert.AreEqual(MdKind.Heading, b[0].Kind);
        Assert.AreEqual(2, b[0].Level);
        Assert.AreEqual(MdKind.List, b[1].Kind);
        Assert.IsFalse(b[1].Ordered);
        Assert.AreEqual(3, b[1].Items.Count);
        Assert.AreEqual("two\nmore", b[1].Items[1].Spans.Single().Text);
        Assert.AreEqual(1, b[1].Items[2].Depth);
        Assert.IsTrue(b[2].Ordered);
        Assert.AreEqual(3, b[2].Start);
        Assert.AreEqual(MdKind.Quote, b[3].Kind);
        Assert.AreEqual(MdKind.Rule, b[4].Kind);
    }

    [TestMethod]
    public void Parse_Table()
    {
        var b = ChatMarkdown.Parse("| a | b |\n|---|:-:|\n| 1 | **2** |\n| 3 |").Single();
        Assert.AreEqual(MdKind.Table, b.Kind);
        Assert.AreEqual(2, b.Header.Count);
        Assert.AreEqual(2, b.Rows.Count);
        Assert.AreEqual(MdSpanKind.Bold, b.Rows[0][1].Single().Kind);
        Assert.AreEqual(0, b.Rows[1][1].Count, "fehlende Zelle bleibt leer");
    }

    [TestMethod]
    public void Inline_CodeBoldItalicLink()
    {
        var s = ChatMarkdown.Inline("Use `a*b*c` and **bold**, *it* or _it_, see [docs](https://example.org).");
        CollectionAssert.AreEqual(
            new[] { MdSpanKind.Text, MdSpanKind.Code, MdSpanKind.Text, MdSpanKind.Bold, MdSpanKind.Text, MdSpanKind.Italic, MdSpanKind.Text, MdSpanKind.Italic, MdSpanKind.Text, MdSpanKind.Link, MdSpanKind.Text },
            s.Select(x => x.Kind).ToArray());
        Assert.AreEqual("a*b*c", s[1].Text);
        Assert.AreEqual("https://example.org", s[9].Href);
    }

    [TestMethod]
    public void Inline_NoLinkForOtherSchemes_AndNoFalseItalic()
    {
        var s = ChatMarkdown.Inline("[x](javascript:alert(1)) 2 * 3 * 4 snake_case_name");
        Assert.IsTrue(s.All(x => x.Kind == MdSpanKind.Text), "nur http(s) wird zum Link, * mit Leerraum und _ im Wort bleiben Text");
        Assert.AreEqual("[x](javascript:alert(1)) 2 * 3 * 4 snake_case_name", string.Concat(s.Select(x => x.Text)));
    }

    [TestMethod]
    public void Parse_EmptyAndPlain()
    {
        Assert.AreEqual(0, ChatMarkdown.Parse("").Count);
        Assert.AreEqual(0, ChatMarkdown.Parse(null).Count);
        var p = ChatMarkdown.Parse("line one\nline two").Single();
        Assert.AreEqual("line one\nline two", p.Spans.Single().Text, "einfache Zeilenumbrüche bleiben");
    }

    [TestMethod]
    public void Conversation_AsMarkdown()
    {
        var md = ChatMarkdown.Conversation(new[]
        {
            new ChatTurn("user", "Hi"),
            new ChatTurn("assistant", "Hello **there**", Stats: "TTFT 10 ms"),
        });
        Assert.AreEqual("**You:**\nHi\n\n**Model:**\nHello **there**\n", md.Replace("\r\n", "\n"));
    }

    [TestMethod]
    public void Reason_RefusedConnection_InEnglishWithAddress()
    {
        var ex = new HttpRequestException(HttpRequestError.ConnectionError,
            "Es konnte keine Verbindung hergestellt werden, da der Zielcomputer die Verbindung verweigerte. (127.0.0.1:8081)",
            new SocketException((int)SocketError.ConnectionRefused));
        Assert.AreEqual(Strings.NetRefused + " (127.0.0.1:8081)", PromptHarness.Reason(ex));
    }

    [TestMethod]
    public void Reason_NameResolution_AndUnknown()
    {
        Assert.AreEqual(Strings.NetNoHost, PromptHarness.Reason(new HttpRequestException(HttpRequestError.NameResolutionError, "x")));
        Assert.AreEqual("something odd", PromptHarness.Reason(new HttpRequestException("something odd")));
    }

    [TestMethod]
    public async Task Send_ServerDown_NoTurnLeft_AndEnglishError()
    {
        var session = new PromptSession();
        using var http = new HttpClient(new Refusing());
        var r = await session.SendAsync(http, "http://127.0.0.1:8081", "hello");
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(0, session.Turns.Count, "die Frage steht nicht im Verlauf – die Seite legt sie zurück ins Feld");
        StringAssert.Contains(r.Error, Strings.NetRefused);
    }

    [TestMethod]
    public async Task Send_Timeout_IsAnError_NotACrash()
    {
        var session = new PromptSession();
        using var http = new HttpClient(new Slow()) { Timeout = TimeSpan.FromMilliseconds(50) };
        var r = await session.SendAsync(http, "http://127.0.0.1:8081", "hello");
        Assert.IsFalse(r.Ok);
        Assert.AreEqual(Strings.PromptTimedOut(TimeSpan.FromMilliseconds(50)), r.Error);
    }

    [TestMethod]
    public async Task Sim_StreamsMarkdown_WithStatsOnTheTurn()
    {
        using var sim = new SimHost(SimServerSpec.Defaults(), autoStep: false);
        var srv = sim.World.Servers.First(s => s.Spec.Kind == BackendKind.LlamaCpp);
        var session = new PromptSession();
        int deltas = 0;
        var r = await session.SendAsync(sim.Http, "http://127.0.0.1:" + srv.Port, "write python code", () => deltas++);
        Assert.IsTrue(r.Ok, r.Error);
        Assert.IsTrue(deltas > 10, "Stück für Stück, nicht auf einmal");
        Assert.AreEqual(MdKind.Code, ChatMarkdown.Parse(r.Text).First(b => b.Kind == MdKind.Code).Kind);
        Assert.AreEqual(2, session.Turns.Count);
        StringAssert.StartsWith(session.Turns[1].Stats, "TTFT ");
    }

    [TestMethod]
    public void SimAnswers_PiecesRebuildTheText()
    {
        var text = SimAnswers.For("hello");
        Assert.AreEqual(text, string.Concat(SimAnswers.Pieces(text)));
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused (127.0.0.1:8081)", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class Slow : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

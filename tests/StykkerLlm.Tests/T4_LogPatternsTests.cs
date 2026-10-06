using System.Globalization;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

[TestClass]
public class T4_LogPatternsTests
{
    private const string LiveLine = "slot update_slots: id  0 | task 17 | n_gen = 128, tg = 25.13 t/s, tg_3s = 24.50 t/s";
    private const string PromptLine = "print_timing: id  0 | task 17 | prompt eval time =   1052.31 ms /   512 tokens   (    2.06 tokens per second)";
    private const string EvalLine = "print_timing: id  0 | task 17 |        eval time =  10234.56 ms /   256 tokens   (   25.00 tokens per second)";
    private const string TotalLine = "print_timing: id  0 | task 17 |      total time =  11286.87 ms /   768 tokens";
    private const string ReleaseLine = "slot release: id  0 | task 17 | stop processing: n_tokens = 256, truncated = 0";
    private const string ProgressLine = "slot update_slots: id  0 | task 17 | prompt processing, n_tokens =  512, progress = 0.250000";
    private const string CancelLine = "main: cancel task, id_task = 17";

    [TestMethod]
    public void LiveLineIsParsed()
    {
        var m = LogPatterns.Live().Match(LiveLine);
        Assert.IsTrue(m.Success);
        Assert.AreEqual(0, LogPatterns.I(m, 1));
        Assert.AreEqual(17, LogPatterns.I(m, 2));
        Assert.AreEqual(128, LogPatterns.I(m, 3));
        Assert.AreEqual(25.13, LogPatterns.D(m, 4), 1e-9);
        Assert.AreEqual(24.50, LogPatterns.D(m, 5), 1e-9);
    }

    [TestMethod]
    public void PromptEvalAndTotalLinesAreParsed()
    {
        var p = LogPatterns.Prompt().Match(PromptLine);
        Assert.IsTrue(p.Success);
        Assert.AreEqual(17, LogPatterns.I(p, 1));
        Assert.AreEqual(1052.31, LogPatterns.D(p, 2), 1e-9);
        Assert.AreEqual(512, LogPatterns.I(p, 3));
        Assert.AreEqual(2.06, LogPatterns.D(p, 4), 1e-9);

        var e = LogPatterns.Eval().Match(EvalLine);
        Assert.IsTrue(e.Success);
        Assert.AreEqual(10234.56, LogPatterns.D(e, 2), 1e-9);
        Assert.AreEqual(256, LogPatterns.I(e, 3));
        Assert.AreEqual(25.00, LogPatterns.D(e, 4), 1e-9);

        var t = LogPatterns.Total().Match(TotalLine);
        Assert.IsTrue(t.Success);
        Assert.AreEqual(11286.87, LogPatterns.D(t, 2), 1e-9);
        Assert.AreEqual(768, LogPatterns.I(t, 3));
    }

    [TestMethod]
    public void EvalPatternDoesNotFireOnPromptLines()
    {
        Assert.IsFalse(LogPatterns.Eval().IsMatch(PromptLine));
        Assert.IsFalse(LogPatterns.Total().IsMatch(EvalLine));
        Assert.IsFalse(LogPatterns.Prompt().IsMatch(EvalLine));
    }

    [TestMethod]
    public void ReleaseProgressCancelAndUnifiedAreParsed()
    {
        var r = LogPatterns.Release().Match(ReleaseLine);
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0, LogPatterns.I(r, 1));
        Assert.AreEqual(17, LogPatterns.I(r, 2));
        Assert.AreEqual(256, LogPatterns.I(r, 3));
        Assert.AreEqual("0", r.Groups[4].Value);

        var pr = LogPatterns.Progress().Match(ProgressLine);
        Assert.IsTrue(pr.Success);
        Assert.AreEqual(512, LogPatterns.I(pr, 3));
        Assert.AreEqual(0.25, LogPatterns.D(pr, 4), 1e-9);

        Assert.AreEqual(17, LogPatterns.I(LogPatterns.Cancel().Match(CancelLine), 1));

        Assert.AreEqual("true", LogPatterns.Unified().Match("  kv_unified = 'true'").Groups[1].Value);
        Assert.AreEqual("false", LogPatterns.Unified().Match("kv_unified = 'false'").Groups[1].Value);
    }

    [TestMethod]
    public void PartialLinesDoNotMatch()
    {
        Assert.IsFalse(LogPatterns.Progress().IsMatch("slot update_slots: id  0 | task 17 | prompt processing, n_tokens = 5"));
        Assert.IsFalse(LogPatterns.Prompt().IsMatch("print_timing: id  0 | task 17 | prompt eval time =   1052.31 ms /   512 tokens   ("));
        Assert.IsFalse(LogPatterns.Live().IsMatch("slot update_slots: id  0 | task 17 | n_gen = 128, tg = 25.13 t/s,"));
        Assert.IsFalse(LogPatterns.Release().IsMatch("slot release: id  0 | task 17 | stop processing: n_tokens ="));
        Assert.IsFalse(LogPatterns.Total().IsMatch("print_timing: id  0 | task 17 |      total time ="));
    }

    [TestMethod]
    public void UnrelatedLinesDoNotMatch()
    {
        string[] lines =
        {
            "main: server is listening on http://127.0.0.1:8012 - starting the main loop",
            "system_info: n_threads = 8 | AVX = 1",
            "srv  update_slots: id  0 | task 17 | new prompt, n_ctx = 4096",
            "slot get_availabl... id  0 | task 1",
            "load_tensors: offloading 32 repeating layers to GPU",
        };
        foreach (var line in lines)
        {
            Assert.IsFalse(LogPatterns.Live().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Prompt().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Eval().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Total().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Release().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Progress().IsMatch(line), line);
            Assert.IsFalse(LogPatterns.Cancel().IsMatch(line), line);
        }
    }

    [TestMethod]
    public void CrlfLineEndingsAreTolerated()
    {
        foreach (var line in new[] { LiveLine, PromptLine, EvalLine, TotalLine, ReleaseLine, ProgressLine })
        {
            Assert.IsTrue(LogPatterns.Live().IsMatch(line + "\r\n") || LogPatterns.Prompt().IsMatch(line + "\r\n") ||
                          LogPatterns.Eval().IsMatch(line + "\r\n") || LogPatterns.Total().IsMatch(line + "\r\n") ||
                          LogPatterns.Release().IsMatch(line + "\r\n") || LogPatterns.Progress().IsMatch(line + "\r\n"), line);
        }
        Assert.IsTrue(LogPatterns.Cancel().IsMatch(CancelLine + "\r\n"));
    }

    [TestMethod]
    public void ParsingIsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // Dezimalkomma: "25.13" darf nicht 2513 werden
            var m = LogPatterns.Live().Match(LiveLine);
            Assert.IsTrue(m.Success);
            Assert.AreEqual(25.13, LogPatterns.D(m, 4), 1e-9);
            Assert.AreEqual(24.50, LogPatterns.D(m, 5), 1e-9);
            Assert.AreEqual(128, LogPatterns.I(m, 3));
            var p = LogPatterns.Prompt().Match(PromptLine);
            Assert.AreEqual(2.06, LogPatterns.D(p, 4), 1e-9);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [TestMethod]
    public void TimestampPrefixesDoNotDisturbParsing()
    {
        var m = LogPatterns.Live().Match("2026-01-01 12:00:00 [INFO] " + LiveLine);
        Assert.IsTrue(m.Success);
        Assert.AreEqual(17, LogPatterns.I(m, 2));
        Assert.AreEqual(25.13, LogPatterns.D(m, 4), 1e-9);
    }
}
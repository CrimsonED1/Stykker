using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N81 – Weg jeder Proxy-Anfrage (named, classifier, vision, default), Spalte Route in proxy.csv, Cloud-Zeilen und
// Kennzeichen in „Letzte Anfragen“ (keine Doppelzeilen für lokale Ziele)
[TestClass]
public class N81_RouteTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0);
    private static readonly ProxyTarget Main = new("main|m", "http://127.0.0.1:1", "main-model", BackendKind.LlamaCpp, true, false);
    private static readonly ProxyTarget Other = new("other|o", "http://127.0.0.1:2", "other-model", BackendKind.LlamaCpp, true, false);
    private static readonly ProxyTarget Cloud = new(ProxyTarget.CloudKey("https://cloud.example/v1", "big"), "https://cloud.example/v1", "big",
        BackendKind.LmStudio, true, false, Remote: true, PublicModel: "Cloud/big", DisplayName: "Cloud · big", Cloud: true);

    private static byte[] B(string json) => System.Text.Encoding.UTF8.GetBytes(json);

    private static readonly byte[] ImageBody = B("{\"model\":\"stykker\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\"}]}]}");

    [TestMethod]
    public void A_name_asked_for_explicitly_is_named_even_with_image_or_check()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Other };
        proxy.SetTargets(targets, Main.Key, classifierKey: Other.Key, visionKey: Other.Key);
        var (t, route) = proxy.Pick(targets, B("{\"model\":\"main-model\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\"}]}]}"));
        Assert.AreSame(Main, t);
        Assert.AreEqual(ProxyRoutes.Named, route);
    }

    [TestMethod]
    public void Claude_Codes_safety_check_goes_to_the_classifier_target()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Other };
        proxy.SetTargets(targets, Main.Key, classifierKey: Other.Key);
        var (t, route) = proxy.Pick(targets, B("{\"model\":\"claude-haiku-5-5\",\"messages\":[]}"));
        Assert.AreSame(Other, t);
        Assert.AreEqual(ProxyRoutes.Classifier, route);
        // Prüfung hinter dem Proxy: Gesprächsname, aber ohne Werkzeuge und mit dem Stoppzeichen der Prüfung
        (t, route) = proxy.Pick(targets, B("{\"model\":\"claude-stykker\",\"stop_sequences\":[\"</block>\"],\"messages\":[]}"));
        Assert.AreSame(Other, t);
        Assert.AreEqual(ProxyRoutes.Classifier, route);
    }

    [TestMethod]
    public void Requests_with_an_image_go_to_the_vision_target()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Other };
        proxy.SetTargets(targets, Main.Key, visionKey: Other.Key);
        var (t, route) = proxy.Pick(targets, ImageBody);
        Assert.AreSame(Other, t);
        Assert.AreEqual(ProxyRoutes.Vision, route);
    }

    [TestMethod]
    public void The_classifier_wins_over_the_vision_target()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Other, Cloud };
        proxy.SetTargets(targets, Main.Key, classifierKey: Other.Key, visionKey: Cloud.Key);
        var (t, route) = proxy.Pick(targets, B("{\"model\":\"claude-haiku-5-5\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image\"}]}]}"));
        Assert.AreSame(Other, t);
        Assert.AreEqual(ProxyRoutes.Classifier, route);
    }

    [TestMethod]
    public void Everything_else_is_default()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Other };
        proxy.SetTargets(targets, Main.Key, classifierKey: Other.Key, visionKey: Other.Key);
        var (t, route) = proxy.Pick(targets, B("{\"model\":\"stykker\",\"messages\":[]}"));
        Assert.IsNull(t);
        Assert.AreEqual(ProxyRoutes.Default, route);
        (t, route) = proxy.Pick(targets, B("{\"model\":\"unknown-model\",\"messages\":[]}"));
        Assert.IsNull(t);
        Assert.AreEqual(ProxyRoutes.Default, route);
        // Eine Claude-Sitzung unter dem eigenen Namen (claude-stykker) ist keine Prüfung
        (t, route) = proxy.Pick(targets, B("{\"model\":\"claude-stykker\",\"messages\":[]}"));
        Assert.IsNull(t);
        Assert.AreEqual(ProxyRoutes.Default, route);
    }

    [TestMethod]
    public void Only_the_two_special_routes_get_a_label()
    {
        Assert.AreEqual(Strings.ReqRouteAuto, ProxyRoutes.Label(ProxyRoutes.Classifier));
        Assert.AreEqual(Strings.ReqRouteImage, ProxyRoutes.Label(ProxyRoutes.Vision));
        Assert.AreEqual("", ProxyRoutes.Label(ProxyRoutes.Default));
        Assert.AreEqual("", ProxyRoutes.Label(ProxyRoutes.Named));
        Assert.AreEqual("", ProxyRoutes.Label(null));
        Assert.AreEqual("Claude Code's safety check (auto mode)", ProxyRoutes.Tip(ProxyRoutes.Classifier));
        Assert.AreEqual("request with an image", ProxyRoutes.Tip(ProxyRoutes.Vision));
    }

    [TestMethod]
    public void The_route_is_the_last_column_of_proxy_csv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n81-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ProxyLog(Path.Combine(dir, "proxy.csv"));
            log.Append(new ProxyRecord("k", "/v1/messages", T0, T0.AddSeconds(1), null, null, 0, 5, Array.Empty<string>(), "stop", "claude-cli/2",
                true, 200, false, "claude-haiku-5-5", null, ProxyRoutes.Classifier), "Cloud · big");
            log.Append(new ProxyRecord("k", "/v1/messages", T0, T0.AddSeconds(1), null, null, 0, 5, Array.Empty<string>(), "stop", "claude-cli/2",
                true, 200, false, "claude-stykker", null, ProxyRoutes.Vision), "x");
            var lines = File.ReadAllLines(Path.Combine(dir, "proxy.csv"));
            Assert.AreEqual(ProxyLog.Header, lines[0]);
            StringAssert.EndsWith(lines[0], ",Tools,Route");
            StringAssert.EndsWith(lines[1], ",-1,-1,,classifier");
            StringAssert.EndsWith(lines[2], ",-1,-1,,vision");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public void An_old_header_is_not_extended_but_a_v2_file_is_used()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n81-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "proxy.csv");
            const string old = "Time,Requested,Target,Path,Client,HttpStatus,Seconds,ReplyTokens,Aborted,MaxTokens,Messages,Tools";
            File.WriteAllText(path, old + "\r\n2026-10-14 09:00:00,m,t,/v1/messages,c,200,1.0,5,no,-1,-1,\r\n");
            var log = new ProxyLog(path);
            Assert.AreEqual(Path.Combine(dir, "proxy.v2.csv"), log.Path);
            log.Append(new ProxyRecord("k", "/v1/messages", T0, T0.AddSeconds(1), null, null, 0, 5, Array.Empty<string>(), "stop", "c",
                true, 200, false, "m", null, ProxyRoutes.Vision), "t");
            Assert.AreEqual(old, File.ReadAllLines(path)[0], "die alte Datei bleibt unverändert");
            Assert.AreEqual(2, File.ReadAllLines(path).Length);
            var v2 = File.ReadAllLines(Path.Combine(dir, "proxy.v2.csv"));
            Assert.AreEqual(ProxyLog.Header, v2[0]);
            StringAssert.EndsWith(v2[1], ",vision");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static FinishedRequest F(int seq, DateTime seen, string key = "127.0.0.1:8081") =>
        new(seq, "qwen", "qwen.gguf", seq, 0, 100, 500, 50, 200, 4, seen, ReqStatus.Done, "", 100, key);

    private static ProxyRecord Local(DateTime end, string route, string key = "127.0.0.1:8081") =>
        new(key, "/v1/chat/completions", end.AddSeconds(-4), end, end.AddSeconds(-3), end.AddSeconds(-1), 0, 100, Array.Empty<string>(), "stop",
            "opencode/0.9", true, 200, false, null, null, route);

    private static ProxyRecord CloudRec(DateTime end, string route, int status = 200, int reasoning = 0) =>
        new(ProxyTarget.CloudKey("https://cloud.example/v1", "big"), "/v1/chat/completions", end.AddSeconds(-4), end, end.AddSeconds(-3), null,
            reasoning, 100, Array.Empty<string>(), "stop", "claude-cli/2", true, status, false, "claude-haiku-5-5", null, route);

    [TestMethod]
    public void Cloud_requests_get_their_own_rows_with_name_model_and_label()
    {
        var cloud = CloudRec(T0, ProxyRoutes.Classifier);
        var rows = RequestRows.Build(Array.Empty<(FinishedRequest F, string? Host)>(), new[] { cloud },
            nameOf: k => k == cloud.ServerKey ? "Cloud · big" : k);
        var r = rows.Single();
        Assert.AreEqual("Cloud · big", r.Server);
        Assert.AreEqual("big", r.Model);
        Assert.AreEqual("cloud", r.Host);
        Assert.AreEqual(ProxyRoutes.Classifier, r.Route);
        Assert.AreEqual(Strings.ReqRouteAuto, ProxyRoutes.Label(r.Route));
        Assert.AreEqual(100, r.GenTokens);
        Assert.IsTrue(r.GenTps > 0, "t/s aus der Zeit bis zum ersten Token");
        Assert.AreEqual(4.0, r.Seconds, 0.01);
        Assert.AreEqual("ok", r.Result);
        Assert.AreEqual("agent", r.ClientKind);
    }

    [TestMethod]
    public void Cloud_tokens_and_speed_stay_empty_when_the_proxy_did_not_count_them()
    {
        var r = RequestRows.Build(Array.Empty<(FinishedRequest F, string? Host)>(), new[] { CloudRec(T0, ProxyRoutes.Classifier, reasoning: -1) }).Single();
        Assert.AreEqual(0, r.GenTokens, "unbekannt, nicht 0 als Zahl ausgegeben");
        Assert.AreEqual(0, r.GenTps);
        Assert.IsNull(r.ThinkTokens);
    }

    [TestMethod]
    public void Without_a_name_the_model_of_the_key_is_shown_and_errors_are_marked()
    {
        var rows = RequestRows.Build(Array.Empty<(FinishedRequest F, string? Host)>(), new[] { CloudRec(T0, ProxyRoutes.Vision, status: 502) });
        var r = rows.Single();
        Assert.AreEqual("big", r.Server);
        Assert.AreEqual("err", r.Result);
        Assert.AreEqual(ProxyRoutes.Vision, r.Route);
        Assert.AreEqual("", ProxyRoutes.Label(null));
    }

    [TestMethod]
    public void Local_requests_are_not_listed_twice_and_do_not_pick_up_cloud_records()
    {
        var rows = RequestRows.Build(new[] { (F(1, T0), (string?)null) },
            new[] { Local(T0.AddSeconds(1), ProxyRoutes.Default), CloudRec(T0.AddSeconds(2), ProxyRoutes.Classifier) });
        Assert.AreEqual(2, rows.Count, "eine lokale Zeile und eine Cloud-Zeile, keine Doppelzeile");
        var local = rows.Single(x => x.Host == null);
        Assert.AreEqual("127.0.0.1:8081", local.ServerKey);
        Assert.AreEqual(0, local.Tools ?? 0);
        Assert.AreEqual(ProxyRoutes.Classifier, rows.Single(x => x.Host == "cloud").Route);
    }

    [TestMethod]
    public void A_local_row_gets_its_route_only_when_the_match_is_unambiguous()
    {
        var one = RequestRows.Build(new[] { (F(1, T0), (string?)null) }, new[] { Local(T0.AddSeconds(1), ProxyRoutes.Classifier) });
        Assert.AreEqual(ProxyRoutes.Classifier, one.Single().Route);
        Assert.AreEqual(0, one.Single().Tools, "wie bisher: die Beobachtung gehört zur Zeile");

        // Zwei lokale Anfragen desselben Servers fast gleichzeitig: der Weg könnte an der falschen Zeile stehen
        var two = RequestRows.Build(new[] { (F(1, T0), (string?)null), (F(2, T0.AddSeconds(2)), (string?)null) },
            new[] { Local(T0.AddSeconds(1), ProxyRoutes.Classifier) });
        Assert.IsTrue(two.All(x => x.Route == null));
    }

    [TestMethod]
    public void Default_and_named_local_requests_show_no_route()
    {
        var rows = RequestRows.Build(new[] { (F(1, T0), (string?)null) }, new[] { Local(T0.AddSeconds(1), ProxyRoutes.Named) });
        Assert.AreEqual(ProxyRoutes.Named, rows.Single().Route);
        Assert.AreEqual("", ProxyRoutes.Label(rows.Single().Route));
    }
}

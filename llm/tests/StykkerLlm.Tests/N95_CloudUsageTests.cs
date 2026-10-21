using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N95 – Cloud-Verbrauch je Anbieter und Tag (CloudUsage): zählen, Tageswechsel, 90-Tage-Grenze, Speichern, Zustand.
// Ohne Netz: ein Temp-Ordner und eine feste Uhr.
[TestClass]
public class N95_CloudUsageTests
{
    private const string Url = "https://cloud.example.test/v1";
    private const string Other = "https://other.example.test/v1";
    private static readonly string Cloud = ProxyTarget.CloudKey(Url, "model-a");
    private static readonly DateTime Noon = new(2026, 10, 21, 12, 0, 0);

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "slm-cu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static ProxyRecord Rec(string key, DateTime end, int status = 200, int reasoning = 0, int content = 0, string path = "/v1/chat/completions") =>
        new(key, path, end.AddSeconds(-2), end, null, null, reasoning, content, Array.Empty<string>(), "stop", "client", false, status, false);

    [TestMethod]
    public void Counts_PerProvider_AndToday()
    {
        var cu = new CloudUsage(null, () => Noon);
        cu.Add(Rec(Cloud, Noon, content: 30, reasoning: 5));
        cu.Add(Rec(Cloud, Noon));
        cu.Add(Rec(ProxyTarget.CloudKey(Other, "m"), Noon, content: 10));

        var a = cu.Get(Url)!;
        Assert.AreEqual(2L, a.TodayRequests);
        Assert.AreEqual(35L, a.TodayTokens);
        Assert.AreEqual(2L, a.WeekRequests);
        Assert.AreEqual(1L, cu.Get(Other)!.TodayRequests);
        Assert.AreEqual(2, cu.Snapshot().Count);
    }

    [TestMethod]
    public void LocalTargets_AreIgnored()
    {
        var cu = new CloudUsage(null, () => Noon);
        cu.Add(Rec("127.0.0.1:8081", Noon, content: 50));
        cu.Add(Rec("model-host:8081", Noon, content: 50));

        CollectionAssert.AreEqual(Array.Empty<CloudUsage.Totals>(), cu.Snapshot().ToArray());
        Assert.IsNull(cu.Get(Url));
    }

    [TestMethod]
    public void ModelList_IsNotCounted()
    {
        var cu = new CloudUsage(null, () => Noon);
        cu.Add(Rec(Cloud, Noon, path: "/v1/models"));

        Assert.AreEqual(0, cu.Snapshot().Count);
    }

    [TestMethod]
    public void Errors_CountHttpStatusFrom400()
    {
        var cu = new CloudUsage(null, () => Noon);
        cu.Add(Rec(Cloud, Noon, status: 200, content: 4));
        cu.Add(Rec(Cloud, Noon, status: 429));
        cu.Add(Rec(Cloud, Noon, status: 500));

        var a = cu.Get(Url)!;
        Assert.AreEqual(3L, a.TodayRequests);
        Assert.AreEqual(2L, a.TodayErrors);
        Assert.AreEqual(2L, a.WeekErrors);
    }

    [TestMethod]
    public void UnknownTokens_AreNotCounted()
    {
        var cu = new CloudUsage(null, () => Noon);
        cu.Add(Rec(Cloud, Noon, reasoning: -1, content: -1));

        var a = cu.Get(Url)!;
        Assert.AreEqual(1L, a.TodayRequests);
        Assert.AreEqual(0L, a.TodayTokens);
    }

    [TestMethod]
    public void DayChange_MovesToday_AndWeekKeepsYesterday()
    {
        var now = Noon;
        var cu = new CloudUsage(null, () => now);
        cu.Add(Rec(Cloud, now, content: 10));

        now = Noon.AddDays(1);
        Assert.AreEqual(0L, cu.Get(Url)!.TodayRequests);
        Assert.AreEqual(1L, cu.Get(Url)!.WeekRequests);

        cu.Add(Rec(Cloud, now, content: 5));
        var a = cu.Get(Url)!;
        Assert.AreEqual(1L, a.TodayRequests);
        Assert.AreEqual(5L, a.TodayTokens);
        Assert.AreEqual(2L, a.WeekRequests);
        Assert.AreEqual(15L, a.WeekTokens);
    }

    [TestMethod]
    public void Week_IsSevenDays_IncludingToday()
    {
        var now = Noon;
        var cu = new CloudUsage(null, () => now);
        cu.Add(Rec(Cloud, Noon.AddDays(-6), content: 1));   // noch im Fenster (heute und 6 Tage zurück)
        Assert.AreEqual(1L, cu.Get(Url)!.WeekRequests);

        now = Noon.AddDays(1);                                // das Fenster wandert: der Tag vor 6 Tagen fällt heraus
        Assert.AreEqual(0L, cu.Get(Url)!.WeekRequests);
        Assert.IsFalse(cu.Get(Url)!.HasUse);
    }

    [TestMethod]
    public void NinetyDays_OldDaysAreDropped_OnAdd_AndOnLoad()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "cloud-usage.json");
            var cu = new CloudUsage(file, () => Noon);

            cu.Add(Rec(Cloud, Noon.AddDays(-90)));                 // 91 Kalendertage inklusive heute: fällt sofort weg
            Assert.AreEqual(0, cu.Snapshot().Count);

            cu.Add(Rec(Cloud, Noon.AddDays(-89)));                 // 90 Tage inklusive heute: bleibt
            cu.Flush();
            var text = File.ReadAllText(file);
            Assert.IsTrue(text.Contains("2026-07-24"));
            Assert.IsFalse(text.Contains("2026-07-23"));

            Assert.AreEqual(1, new CloudUsage(file, () => Noon).Snapshot().Count);
            Assert.AreEqual(0, new CloudUsage(file, () => Noon.AddDays(1)).Snapshot().Count);   // beim Laden fällt er einen Tag später weg
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void SaveAndLoad_KeepsTotals_AndStoresNoModelName()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "cloud-usage.json");
            var cu = new CloudUsage(file, () => Noon);
            cu.Add(Rec(Cloud, Noon, content: 30, reasoning: 5));
            cu.Add(Rec(Cloud, Noon, status: 503));
            cu.Flush();

            var back = new CloudUsage(file, () => Noon).Get(Url)!;
            Assert.AreEqual(2L, back.TodayRequests);
            Assert.AreEqual(35L, back.TodayTokens);
            Assert.AreEqual(1L, back.TodayErrors);
            Assert.AreEqual(2L, back.WeekRequests);

            var text = File.ReadAllText(file);
            Assert.IsFalse(text.Contains("model-a"), "Modellname darf nicht in der Datei stehen");
            Assert.IsTrue(text.Contains(Url));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void Save_IsThrottled_ButFlushWritesTheRest()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "cloud-usage.json");
            var now = Noon;
            var cu = new CloudUsage(file, () => now);

            cu.Add(Rec(Cloud, Noon, content: 1));                 // erste Speicherung sofort
            var first = File.ReadAllText(file);

            now = Noon.AddSeconds(5);
            cu.Add(Rec(Cloud, now, content: 1));                  // innerhalb von 30 s: nicht geschrieben
            Assert.AreEqual(first, File.ReadAllText(file));

            cu.Flush();                                           // beim Beenden: geschrieben
            Assert.AreEqual(2, new CloudUsage(file, () => now).Get(Url)!.TodayRequests);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void DamagedFile_StartsEmpty()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "cloud-usage.json");
            File.WriteAllText(file, "{ kaputt");
            var cu = new CloudUsage(file, () => Noon);
            Assert.AreEqual(0, cu.Snapshot().Count);

            cu.Add(Rec(Cloud, Noon, content: 2));                 // und ersetzt die Datei beim Speichern
            cu.Flush();
            Assert.AreEqual(1L, new CloudUsage(file, () => Noon).Get(Url)!.TodayRequests);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void FormatTokens_ReadableSteps()
    {
        Assert.AreEqual("0", CloudUsage.FormatTokens(0));
        Assert.AreEqual("999", CloudUsage.FormatTokens(999));
        Assert.AreEqual("1.2k", CloudUsage.FormatTokens(1234));
        Assert.AreEqual("34.5k", CloudUsage.FormatTokens(34500));
        Assert.AreEqual("210k", CloudUsage.FormatTokens(210000));
        Assert.AreEqual("1.5M", CloudUsage.FormatTokens(1500000));
    }

    [TestMethod]
    public void Line_Text_IsTheAgreedSentence()
    {
        Assert.AreEqual("today 12 req · 34.5k tokens · 7 days 80 req · 210k tokens", Strings.CloudUsageLine(12, 34500, 80, 210000));
    }

    [TestMethod]
    public void State_RoundTrips_ThroughStateJson()
    {
        var dir = TempDir();
        try
        {
            using (var engine = new MonitorEngine(new FakePlatform(), new AppPaths(dir), new AppSettings(), new HttpClient(new FakeHandler())))
            {
                engine.CloudUsage.Add(Rec(Cloud, DateTime.Now, content: 30));
                engine.CloudUsage.Add(Rec(Cloud, DateTime.Now, status: 500));
                engine.CloudUsage.Add(Rec("127.0.0.1:8081", DateTime.Now, content: 99));

                var snap = StateSnapshot.Parse(StateJson.WriteText(engine, null, null, 8078, DateTimeOffset.Now));
                var u = snap.Proxy.CloudUsage.Single();
                Assert.AreEqual(Url, u.Url);
                Assert.AreEqual(2L, u.TodayRequests);
                Assert.AreEqual(30L, u.TodayTokens);
                Assert.AreEqual(1L, u.TodayErrors);
                Assert.AreEqual(2L, u.WeekRequests);
                Assert.AreEqual(30L, u.WeekTokens);
                Assert.AreEqual(1L, u.WeekErrors);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

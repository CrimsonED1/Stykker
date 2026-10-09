using System.Globalization;
using System.Text.RegularExpressions;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Eigener Metrik-Endpunkt: Prometheus-Text für Starship, waybar, Grafana. Geprüft wird nicht nur
// "es steht etwas drin", sondern dass jede Zeile dem Format entspricht – ein doppelter Name ohne Label wäre für
// Prometheus ein Fehler, und genau der passiert leicht, wenn man je Prozess dieselbe Metrik ohne Kennung ausgibt.
[TestClass]
public class MetricsTextTests
{
    private static readonly Regex Zeile = new(@"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>[^}]*)\})? (?<wert>-?[0-9.eE+]+)$",
        RegexOptions.Compiled);

    private static List<(string Name, string Labels, double Wert)> Parse(string text)
    {
        var list = new List<(string, string, double)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var m = Zeile.Match(line);
            Assert.IsTrue(m.Success, "Zeile im Prometheus-Format: " + line);
            list.Add((m.Groups["name"].Value, m.Groups["labels"].Value, double.Parse(m.Groups["wert"].Value, CultureInfo.InvariantCulture)));
        }
        return list;
    }

    private static async Task<string> TextOfAsync(params SimServerSpec[] specs)
    {
        using var host = new SimHost(specs.Length == 0 ? SimServerSpec.Defaults() : specs, seed: 4, autoStep: false);
        host.World.Start();
        for (int i = 0; i < 4; i++)
        {
            host.World.Step(DateTime.Now.AddSeconds(10 * (i + 1)));
            await host.Engine.Registry.RefreshNowAsync();   // erst jetzt sind die simulierten Server erkannt
            foreach (var s in host.Engine.Servers) { s.ExternalEverySeconds = 0; s.MetricsEverySeconds = 0; }
            await host.Engine.TickAsync();                  // und gemessen
        }
        return MetricsText.Write(host.Engine, DateTimeOffset.Now,
            new List<(string, double)> { ("dwm", 0.52), ("chrome.exe", 0.1) },
            new List<(string, double)> { ("svchost", 1.48) },
            new List<(string, double)> { ("chrome", 6.1) });
    }

    private static SimServerSpec Sim(string name = "sim-a") => new()
    {
        Kind = BackendKind.LlamaCpp, Name = name, Model = "model-a", Context = 8192, Slots = 2, TpsMin = 200, TpsMax = 300,
        RequestsPerMinute = 300, ModelGb = 4, DraftModel = 4,
    };

    [TestMethod]
    public async Task EveryLineIsValidPrometheusAndEveryNameAppearsOnce()
    {
        var text = await TextOfAsync();
        var rows = Parse(text);
        Assert.IsTrue(rows.Count > 20, "es kommt etwas zusammen: " + rows.Count);

        // Genau eine Datenzeile je Name+Labels: zwei Zeilen mit gleichem Namen und ohne Label wären ein Fehler
        foreach (var group in rows.GroupBy(r => (r.Item1, r.Item2)))
            Assert.AreEqual(1, group.Count(), $"doppelte Reihe {group.Key.Item1}{{{group.Key.Item2}}}");

        // Die Grundwerte stehen drin, mit invarianten Zahlen (Punkt, nicht Komma)
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_up" && r.Item3 == 1));
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_servers"));
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_ticks_total"));
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_process_vram_bytes" && r.Item2.Contains("dwm")));
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_process_ram_bytes" && r.Item2.Contains("svchost")));
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_process_gpu_utilization_percent" && r.Item2.Contains("chrome")));
        // Zahlen mit Punkt, nicht mit Komma (die Kommas im Text stecken in den Labels)
        Assert.IsFalse(Regex.IsMatch(text, @"\d,\d"), "keine Dezimalkomma-Zahlen");
    }

    [TestMethod]
    public async Task ServersAndSlotsCarryTheirIdentity()
    {
        // Zwei llama-Server mit je zwei Slots und Entwurfsmodell – damit ist jede Zeile eindeutig zuordenbar
        var rows = Parse(await TextOfAsync(Sim("sim-a"), Sim("sim-b")));
        var server = rows.Where(r => r.Item1 == "stykker_server_tps").ToList();
        Assert.AreEqual(2, server.Count, "zwei simulierte Server");
        Assert.AreEqual(2, server.Select(r => r.Item2).Distinct().Count(), "zwei verschiedene Server-Kennungen");
        Assert.IsTrue(server.All(r => r.Item2.StartsWith("server=\"") && r.Item2.EndsWith("\"")));

        var slots = rows.Where(r => r.Item1 == "stykker_slot_tps").ToList();
        Assert.AreEqual(4, slots.Count, "zwei Slots je Server");
        Assert.IsTrue(slots.All(r => Regex.IsMatch(r.Item2, @"server=""[^""]+"",slot=""\d+""")),
            "Label mit Server und Slot, z. B. " + slots[0].Item2);

        // Entwürfe erscheinen nur mit Entwurfsmodell
        Assert.AreEqual(2, rows.Count(r => r.Item1 == "stykker_server_spec_draft_tokens_total"));
    }

    [TestMethod]
    public async Task NoDraftModel_NoDraftCounters()
    {
        var ohne = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "plain", Model = "m", Context = 4096, Slots = 1 };
        var rows = Parse(await TextOfAsync(ohne));
        Assert.IsFalse(rows.Any(r => r.Item1.Contains("spec_")), "ohne Entwurfsmodell keine Entwurfszähler");
    }

    [TestMethod]
    public async Task ServerLabelIsSafeForPrometheus()
    {
        // Ein Server mit Umlaut, Leerzeichen und Anführungszeichen im Namen darf die Zeile nicht zerreißen
        var weird = new SimServerSpec { Kind = BackendKind.LlamaCpp, Name = "Wirt's \"Test\"\nServer", Model = "m", Context = 4096, Slots = 1 };
        var text = await TextOfAsync(weird);
        var rows = Parse(text);
        Assert.IsTrue(rows.Any(r => r.Item1 == "stykker_server_online"));
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            Assert.IsTrue(Zeile.IsMatch(line), "Zeile bleibt parsebar: " + line);
        }
        Assert.IsFalse(text.Contains("Test\nServer"), "kein Zeilenumbruch im HELP");
    }

    [TestMethod]
    public async Task HeaderAndContentTypeAreTheOnesPrometheusExpects()
    {
        Assert.AreEqual("text/plain; version=0.0.4; charset=utf-8", MetricsText.ContentType);
        var text = await TextOfAsync();
        Assert.IsTrue(text.StartsWith("# HELP "), "Prometheus erwartet HELP vor TYPE vor Wert");
        // Jede Datenzeile hat ihren HELP-Block
        var names = Parse(text).Select(r => r.Item1).Distinct();
        foreach (var n in names)
        {
            StringAssert.Contains(text, "# HELP " + n + " ", "HELP zu " + n);
            StringAssert.Contains(text, "# TYPE " + n + " gauge", "TYPE zu " + n);
        }
    }
}
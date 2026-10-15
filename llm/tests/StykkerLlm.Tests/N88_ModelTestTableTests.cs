using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N88 – Tabellen der Modelltests im Proxy-Dialog: der letzte Lauf je Modell aus den CSV-Dateien (Temp-Dateien), das Überlagern
// durch den laufenden Lauf, die Freigabe der Knöpfe „Use as auto“ und „Use as vision“, das Umschalten „+ Models“ und die Gruppen.
[TestClass]
public class N88_ModelTestTableTests
{
    private const string Url = "https://cloud.example/v1";
    private const string ClassifierHeader = "Time,Provider,Model,Status,FirstByteSec,TotalSec,Format ok,Note";
    private const string VisionHeader = "Time,Provider,Model,Status,TotalSec,Ok,Note";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-n88-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── CSV lesen ──

    [TestMethod]
    public void A_csv_fields_come_back_as_they_were_written()
    {
        // Csv() schreibt Felder mit Komma oder Anführungszeichen in Anführungszeichen; das Lesen muss dasselbe ergeben
        foreach (var value in new[] { "plain", "with, comma", "with \"quotes\" inside", "" })
        {
            var line = string.Join(",", "x", ClassifierTest.Csv(value), "y");
            var fields = ModelTestTable.SplitCsvLine(line);
            Assert.AreEqual(3, fields.Count, line);
            Assert.AreEqual(value, fields[1]);
        }
    }

    [TestMethod]
    public void B_the_last_classifier_run_per_model_is_loaded_for_this_provider_only()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, ClassifierTest.CsvFile);
            File.WriteAllText(csv, string.Join("\r\n",
                ClassifierHeader,
                "2026-10-14 10:00:00,https://cloud.example/v1,alpha,200,0.50,1.00,no,\"first, not ok\"",
                "2026-10-15 09:00:00,https://cloud.example/v1,alpha,200,0.40,0.90,yes,",
                "2026-10-15 09:00:00,https://cloud.example/v1,beta,skipped,,,,\"Not asked: the name contains 'guard', x\"",
                "2026-10-15 09:00:00,https://other.example/v1,gamma,200,0.10,0.20,yes,",
                "not,a,valid,row") + "\r\n");

            var rows = ModelTestTable.LastClassifier(csv, Url).OrderBy(r => r.Model).ToList();

            Assert.AreEqual(2, rows.Count, "gamma gehört zu einem anderen Anbieter, die Kopfzeile und die kaputte Zeile fallen weg");
            Assert.AreEqual("alpha", rows[0].Model);
            Assert.IsTrue(rows[0].FormatOk is true, "die neuere Zeile gewinnt");
            Assert.AreEqual(new DateTime(2026, 10, 15, 9, 0, 0), rows[0].Time);
            Assert.AreEqual(0.40, rows[0].FirstByteSec);
            Assert.AreEqual(0.90, rows[0].TotalSec);
            Assert.AreEqual("beta", rows[1].Model);
            Assert.AreEqual(Strings.ClassifierStatusSkipped, rows[1].Status);
            Assert.IsNull(rows[1].FormatOk, "übersprungen: kein Format-Urteil");
            Assert.AreEqual("Not asked: the name contains 'guard', x", rows[1].Note, "ein Komma in der Notiz bleibt in einem Feld");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void C_a_later_line_with_the_same_time_wins_and_no_is_read_as_false()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, ClassifierTest.CsvFile);
            File.WriteAllText(csv, string.Join("\r\n",
                ClassifierHeader,
                "2026-10-15 09:00:00,https://cloud.example/v1,alpha,200,0.40,0.90,yes,",
                "2026-10-15 09:00:00,https://cloud.example/v1,alpha,200,0.40,0.95,no,\"The answer does not start with <block>\"") + "\r\n");

            var row = ModelTestTable.LastClassifier(csv, Url).Single();
            Assert.IsTrue(row.FormatOk is false);
            Assert.AreEqual(0.95, row.TotalSec);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void D_the_last_vision_run_per_model_is_loaded_and_yes_means_ok()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, VisionTest.CsvFile);
            File.WriteAllText(csv, string.Join("\r\n",
                VisionHeader,
                "2026-10-14 10:00:00,https://cloud.example/v1,vis-a,200,1.10,no,\"old\"",
                "2026-10-15 10:00:00,https://cloud.example/v1,vis-a,200,0.80,yes,",
                "2026-10-15 10:00:00,https://cloud.example/v1,vis-b,200,2.00,no,\"The answer does not name both colours (red and blue): green\"") + "\r\n");

            var rows = ModelTestTable.LastVision(csv, Url).OrderBy(r => r.Model).ToList();

            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows[0].Ok);
            Assert.AreEqual(0.80, rows[0].TotalSec);
            Assert.IsFalse(rows[1].Ok);
            StringAssert.Contains(rows[1].Note, "green");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void E_a_missing_or_empty_csv_gives_no_rows()
    {
        var dir = TempDir();
        try
        {
            Assert.AreEqual(0, ModelTestTable.LastClassifier(Path.Combine(dir, "none.csv"), Url).Count);
            Assert.AreEqual(0, ModelTestTable.LastVision(null, Url).Count);
            var empty = Path.Combine(dir, VisionTest.CsvFile);
            File.WriteAllText(empty, VisionHeader + "\r\n");
            Assert.AreEqual(0, ModelTestTable.LastVision(empty, Url).Count, "nur die Kopfzeile");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void F_saved_rows_are_overlaid_by_the_current_run_per_model()
    {
        var t = new DateTime(2026, 10, 15, 9, 0, 0);
        var saved = new[]
        {
            new ClassifierResult(t.AddDays(-1), "a", "200", 0.5, 1.0, true, ""),
            new ClassifierResult(t.AddDays(-1), "b", "200", 0.5, 1.0, true, ""),
        };
        var current = new[] { new ClassifierResult(t, "a", "200", 0.5, 2.0, false, "wrong") };

        var merged = ModelTestTable.Overlay(saved, current, r => r.Model).OrderBy(r => r.Model).ToList();

        Assert.AreEqual(2, merged.Count);
        Assert.IsTrue(merged[0].FormatOk is false, "der laufende Lauf ist neuer");
        Assert.AreEqual("wrong", merged[0].Note);
        Assert.IsTrue(merged[1].FormatOk is true, "ein Modell ohne neue Zeile bleibt mit dem gespeicherten Ergebnis");
    }

    // ── Knöpfe ──

    [TestMethod]
    public void G_use_as_auto_needs_a_format_that_passed_in_the_classifier_test()
    {
        var t = new DateTime(2026, 10, 15);
        var classifier = new[]
        {
            new ClassifierResult(t, "good", "200", 0.1, 0.2, true, ""),
            new ClassifierResult(t, "bad", "200", 0.1, 0.2, false, "format"),
            new ClassifierResult(t, "skip", Strings.ClassifierStatusSkipped, null, null, null, "Not asked"),
        };
        Assert.AreEqual(ModelTestTable.UseState.Usable, ModelTestTable.AsAuto(classifier, "good"));
        Assert.AreEqual(ModelTestTable.UseState.NotUsable, ModelTestTable.AsAuto(classifier, "bad"));
        Assert.AreEqual(ModelTestTable.UseState.NotTested, ModelTestTable.AsAuto(classifier, "skip"), "übersprungen heißt nicht getestet");
        Assert.AreEqual(ModelTestTable.UseState.NotTested, ModelTestTable.AsAuto(classifier, "unknown"));
    }

    [TestMethod]
    public void H_use_as_vision_needs_a_correct_description_in_the_image_test()
    {
        var t = new DateTime(2026, 10, 15);
        var vision = new[]
        {
            new VisionResult(t, "seen", "200", 0.9, true, ""),
            new VisionResult(t, "blind", "200", 0.9, false, "wrong"),
            new VisionResult(t, "skip", Strings.ClassifierStatusSkipped, null, false, "Not asked"),
        };
        Assert.AreEqual(ModelTestTable.UseState.Usable, ModelTestTable.AsVision(vision, "seen"));
        Assert.AreEqual(ModelTestTable.UseState.NotUsable, ModelTestTable.AsVision(vision, "blind"));
        Assert.AreEqual(ModelTestTable.UseState.NotTested, ModelTestTable.AsVision(vision, "skip"));
        Assert.AreEqual(ModelTestTable.UseState.NotTested, ModelTestTable.AsVision(vision, "unknown"));
    }

    [TestMethod]
    public void I_the_two_tests_are_judged_across_each_other()
    {
        // Ein Modell, das im Format besteht, aber im Bildtest nicht getestet wurde: „Use as auto“ aktiv, „Use as vision“ aus
        var t = new DateTime(2026, 10, 15);
        var classifier = new[] { new ClassifierResult(t, "text-only", "200", 0.1, 0.2, true, "") };
        var vision = Array.Empty<VisionResult>();
        Assert.AreEqual(ModelTestTable.UseState.Usable, ModelTestTable.AsAuto(classifier, "text-only"));
        Assert.AreEqual(ModelTestTable.UseState.NotTested, ModelTestTable.AsVision(vision, "text-only"), "„not tested for images“");
    }

    [TestMethod]
    public void J_plus_models_adds_and_takes_off_without_duplicates()
    {
        CollectionAssert.AreEqual(new[] { "x", "y", "z" }, ModelTestTable.WithOffered(new[] { "x", "y" }, "z", true));
        CollectionAssert.AreEqual(new[] { "y" }, ModelTestTable.WithOffered(new[] { "x", "y" }, "x", false));
        CollectionAssert.AreEqual(new[] { "x", "y" }, ModelTestTable.WithOffered(new[] { "x", "y" }, "y", true), "schon drin: nicht doppelt");
        CollectionAssert.AreEqual(new[] { "x", "y" }, ModelTestTable.WithOffered(new[] { "x", "y" }, "q", false), "nicht drin: nichts ändert sich");
        Assert.AreEqual(0, ModelTestTable.WithOffered(Array.Empty<string>(), "only", false).Length);
    }

    // ── Gruppen und Zähler ──

    [TestMethod]
    public void K_loaded_rows_sort_usable_fastest_first_then_failed_then_skipped()
    {
        var dir = TempDir();
        try
        {
            var csv = Path.Combine(dir, ClassifierTest.CsvFile);
            File.WriteAllText(csv, string.Join("\r\n",
                ClassifierHeader,
                "2026-10-15 09:00:00,https://cloud.example/v1,skipped-one,skipped,,,,\"Not asked\"",
                "2026-10-15 09:00:00,https://cloud.example/v1,ok-slow,200,1.00,2.50,yes,",
                "2026-10-15 09:00:00,https://cloud.example/v1,wrong-fast,200,0.10,0.20,no,\"format\"",
                "2026-10-15 09:00:00,https://cloud.example/v1,ok-fast,200,0.20,0.50,yes,",
                "2026-10-15 09:00:00,https://cloud.example/v1,timeout-one,timeout,,60.00,no,\"No complete answer\"") + "\r\n");

            var sorted = ClassifierTest.Sorted(ModelTestTable.LastClassifier(csv, Url)).Select(r => r.Model).ToArray();

            // Brauchbare zuerst (schnellste oben), dann die nicht brauchbaren nach Zeit, übersprungene ganz unten
            CollectionAssert.AreEqual(new[] { "ok-fast", "ok-slow", "wrong-fast", "timeout-one", "skipped-one" }, sorted);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void L_summary_counts_match_the_live_counts()
    {
        var t = new DateTime(2026, 10, 15);
        var rows = new[]
        {
            new ClassifierResult(t, "a", "200", 0.1, 0.2, true, ""),
            new ClassifierResult(t, "b", "200", 0.1, 0.2, false, ""),
            new ClassifierResult(t, "c", Strings.ClassifierStatusSkipped, null, null, null, ""),
        };
        Assert.AreEqual((1, 1, 1), ClassifierTest.CountOf(rows));
        var vrows = new[]
        {
            new VisionResult(t, "a", "200", 0.1, true, ""),
            new VisionResult(t, "b", "200", 0.1, false, ""),
            new VisionResult(t, "c", Strings.ClassifierStatusSkipped, null, false, ""),
        };
        Assert.AreEqual((1, 1, 1), VisionTest.CountOf(vrows));
    }
}

using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N71 – Wo die Protokolle liegen: Anfrageprotokoll, Server-Logs, Aufnahmen und die Logs der Programme liegen im Protokollordner
// neben der exe (wenn dort geschrieben werden darf), sonst im Datenordner. Mit --data-dir bleibt alles im Datenordner.
// Nur Temp-Ordner: der "exe-Ordner" ist ein Temp-Ordner, nichts landet neben der echten exe oder im echten %APPDATA%.
[TestClass]
public class N71_LogLocationTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "slm-n71-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Clean(params string[] dirs)
    {
        foreach (var dir in dirs)
            try { Directory.Delete(dir, true); } catch { /* ein Virenscanner hält vielleicht noch eine Datei */ }
    }

    [TestMethod]
    public void LogsDir_IsTheAppFolder_WhenItIsWritable_AndSettingsStayInTheDataFolder()
    {
        var root = TempDir("data");
        var app = TempDir("exe");
        try
        {
            var paths = new AppPaths(root, isDefault: false, appDir: app);
            Assert.AreEqual(Path.Combine(app, "logs"), paths.LogsDir);
            Assert.AreEqual(Path.Combine(app, "logs", "requests.csv"), paths.DefaultCsv);
            Assert.AreEqual(Path.Combine(app, "logs", "recordings"), paths.RecordingsDir);
            Assert.AreEqual(Path.Combine(app, "logs", "StykkerUI.log"), AppLog.PickFile("StykkerUI", paths, app));
            Assert.AreEqual(Path.Combine(root, "settings.json"), paths.SettingsFile);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "logs")), "im Datenordner entsteht kein logs-Ordner");
        }
        finally { Clean(root, app); }
    }

    [TestMethod]
    public void LogsDir_FallsBackToTheDataFolder_WhenTheAppFolderIsBlocked()
    {
        var root = TempDir("data");
        var app = TempDir("exe");
        try
        {
            // "logs" bei der App ist eine Datei, kein Ordner: dort kann nichts hin – also der Datenordner
            File.WriteAllText(Path.Combine(app, "logs"), "x");
            var paths = new AppPaths(root, isDefault: false, appDir: app);
            Assert.AreEqual(Path.Combine(root, "logs"), paths.LogsDir);
            Assert.AreEqual(Path.Combine(root, "logs", "requests.csv"), paths.DefaultCsv);
            Assert.AreEqual(Path.Combine(root, "logs", "recordings"), paths.RecordingsDir);
            Assert.AreEqual(Path.Combine(root, "logs", "StykkerUI.log"), AppLog.PickFile("StykkerUI", paths, app));
        }
        finally { Clean(root, app); }
    }

    [TestMethod]
    public void DataDirOnly_KeepsEverythingInTheDataFolder()
    {
        var root = TempDir("data");
        try
        {
            var paths = new AppPaths(root);
            Assert.IsNull(paths.AppDir);
            Assert.AreEqual(Path.Combine(root, "logs"), paths.LogsDir);
            Assert.AreEqual(Path.Combine(root, "logs", "requests.csv"), paths.DefaultCsv);
            Assert.AreEqual(Path.Combine(root, "logs", "recordings"), paths.RecordingsDir);
            Assert.AreEqual(Path.Combine(root, "logs", "StykkerUI.log"), AppLog.PickFile("StykkerUI", paths, null));

            // --data-dir auf einen anderen Ordner als die Vorgabe: wie new AppPaths(…)
            var fromDir = AppPaths.FromDataDir(root);
            Assert.IsNull(fromDir.AppDir);
            Assert.AreEqual(Path.Combine(root, "logs"), fromDir.LogsDir);
        }
        finally { Clean(root); }
    }

    [TestMethod]
    public void InAppFolder_UsesTheProgramFolderForLogs()
    {
        // Nur der Ordner wird geprüft, nichts wird angelegt: die Protokolle lägen neben der (Test-)exe
        var paths = AppPaths.InAppFolder(Path.Combine(Path.GetTempPath(), "slm-n71-host-" + Guid.NewGuid().ToString("N")));
        Assert.AreEqual(AppContext.BaseDirectory, paths.AppDir);
        Assert.IsFalse(paths.IsDefault);
    }

    [TestMethod]
    public void CsvLog_AllowsTheLogsFolderAndTheDataFolder_NotAnythingElse()
    {
        var root = TempDir("data");
        var app = TempDir("exe");
        try
        {
            var paths = new AppPaths(root, isDefault: false, appDir: app);
            var inLogs = Path.Combine(app, "logs", "my.csv");
            Assert.AreEqual(inLogs, paths.ResolveCsvLog(inLogs, out var note)); Assert.IsNull(note);

            // alte settings.json mit einem Pfad im Datenordner bleibt gültig
            var old = Path.Combine(root, "old.csv");
            Assert.AreEqual(old, paths.ResolveCsvLog(old, out note)); Assert.IsNull(note);

            // außerhalb beider Ordner: Vorgabe im Protokollordner + Hinweis
            var evil = Path.Combine(Path.GetTempPath(), "slm-n71-evil-" + Guid.NewGuid().ToString("N") + ".csv");
            Assert.AreEqual(paths.DefaultCsv, paths.ResolveCsvLog(evil, out note));
            Assert.IsNotNull(note);
            StringAssert.Contains(note, paths.DefaultCsv);

            // der Protokollordner selbst ist keine Datei
            Assert.AreEqual(paths.DefaultCsv, paths.ResolveCsvLog(Path.Combine(app, "logs") + Path.DirectorySeparatorChar, out note));
            Assert.IsNotNull(note);
        }
        finally { Clean(root, app); }
    }

    [TestMethod]
    public void LogTail_ReadsTheLogsFolderAndTheDataFolder_NotOtherFolders()
    {
        var root = TempDir("data");
        var app = TempDir("exe");
        try
        {
            var paths = new AppPaths(root, isDefault: false, appDir: app);
            Directory.CreateDirectory(paths.LogsDir);
            var serverLog = Path.Combine(paths.LogsDir, "llama-8080.log");
            File.WriteAllText(serverLog, "hello\n");
            Assert.IsTrue(LogTail.Inside(paths, serverLog));
            Assert.AreEqual("hello", LogTail.Read(paths, serverLog)?.Trim());

            var dataLog = Path.Combine(root, "old.log");
            File.WriteAllText(dataLog, "old\n");
            Assert.IsTrue(LogTail.Inside(paths, dataLog));

            var other = Path.Combine(app, "setup.log");
            File.WriteAllText(other, "x");
            Assert.IsFalse(LogTail.Inside(paths, other));
            Assert.IsNull(LogTail.Read(paths, other));
        }
        finally { Clean(root, app); }
    }

    [TestMethod]
    public void IsProgramLog_RecognizesTheProgramsLogs()
    {
        foreach (var name in new[] { "StykkerLLM-Server.log", "StykkerUI.log", "stykker.log", "StykkerUI.1.log", "StykkerHost-service.log" })
            Assert.IsTrue(AppLog.IsProgramLog(name), name);
        foreach (var name in new[] { "llama-8080.log", "stykker-8080.log", "StykkerUI.txt", "my.log" })
            Assert.IsFalse(AppLog.IsProgramLog(name), name);
    }

    [TestMethod]
    public void PruneLogs_NeverDeletesTheProgramLogs_OnlyServerLogs()
    {
        var dir = TempDir("prune");
        try
        {
            // Die Programm-Logs liegen im selben Ordner wie die Server-Logs und zählen nicht mit
            File.WriteAllBytes(Path.Combine(dir, "StykkerUI.log"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(dir, "StykkerUI.1.log"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(dir, "llama-8080.log"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(dir, "llama-8081.log"), new byte[3000]);

            ServerLauncher.PruneLogs(dir, 1000, Array.Empty<string>(), int.MaxValue);

            Assert.IsTrue(File.Exists(Path.Combine(dir, "StykkerUI.log")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "StykkerUI.1.log")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "llama-8080.log")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "llama-8081.log")));
        }
        finally { Clean(dir); }
    }
}

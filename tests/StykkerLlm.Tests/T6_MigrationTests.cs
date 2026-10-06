using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// T6 – Datenordner-Migration (Umbenennen auf StykkerLLM): %APPDATA%\StykkerSLM -> %APPDATA%\StykkerLLM
[TestClass]
public class T6_MigrationTests
{
    [TestMethod]
    public void OldFolderIsRenamedWhenNewDoesNotExist()
    {
        var appData = Path.Combine(Path.GetTempPath(), "slm-t6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(appData, "StykkerSLM"));
        try
        {
            File.WriteAllText(Path.Combine(appData, "StykkerSLM", "settings.json"), "{\"Theme\":\"Obsidian\"}");
            File.WriteAllText(Path.Combine(appData, "StykkerSLM", "library.json"), "{\"Version\":1}");

            var root = LibraryAccess.Migrate(appData);

            Assert.AreEqual(Path.Combine(appData, "StykkerLLM"), root);
            Assert.IsTrue(Directory.Exists(root));
            Assert.IsFalse(Directory.Exists(Path.Combine(appData, "StykkerSLM")));
            Assert.AreEqual("Obsidian", AppSettings.Load(Path.Combine(root, "settings.json")).Theme);
        }
        finally { Directory.Delete(appData, true); }
    }

    [TestMethod]
    public void ExistingNewFolderIsNeverOverwritten()
    {
        var appData = Path.Combine(Path.GetTempPath(), "slm-t6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(appData, "StykkerSLM"));
        Directory.CreateDirectory(Path.Combine(appData, "StykkerLLM"));
        try
        {
            File.WriteAllText(Path.Combine(appData, "StykkerSLM", "settings.json"), "{\"Theme\":\"Obsidian\"}");
            File.WriteAllText(Path.Combine(appData, "StykkerLLM", "settings.json"), "{\"Theme\":\"Deep Sea\"}");

            var root = LibraryAccess.Migrate(appData);

            Assert.AreEqual(Path.Combine(appData, "StykkerLLM"), root);
            Assert.IsTrue(Directory.Exists(Path.Combine(appData, "StykkerSLM")));   // alt bleibt unangetastet
            Assert.AreEqual("Deep Sea", AppSettings.Load(Path.Combine(root, "settings.json")).Theme);
        }
        finally { Directory.Delete(appData, true); }
    }

    [TestMethod]
    public void FreshInstallGetsTheNewFolderWithoutMigration()
    {
        var appData = Path.Combine(Path.GetTempPath(), "slm-t6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appData);
        try
        {
            var root = LibraryAccess.Migrate(appData);
            Assert.AreEqual(Path.Combine(appData, "StykkerLLM"), root);
            Assert.IsFalse(Directory.Exists(Path.Combine(appData, "StykkerSLM")));
        }
        finally { Directory.Delete(appData, true); }
    }
}

// Kleiner Zugriffshelfer für die Tests: AppPaths.MigrateRoot ist internal
internal static class LibraryAccess
{
    public static string Migrate(string appData) => AppPaths.MigrateRoot(appData);
}
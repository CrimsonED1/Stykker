using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Ordner-Wähler der Prompt-Seite: der Server blättert seine eigenen Ordner durch, gezeigt werden nur Ordnernamen
[TestClass]
public class PromptFolderTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "slm-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [TestMethod]
    public void Browse_ListsOnlySubfolders_SortedIgnoringCase()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Beta"));
            Directory.CreateDirectory(Path.Combine(dir, "alpha"));
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");

            var l = FolderBrowser.Browse(dir);

            Assert.IsFalse(l.Failed);
            Assert.AreEqual(Path.GetFullPath(dir), l.Current);
            CollectionAssert.AreEqual(new[] { "alpha", "Beta" }, l.Folders.ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Browse_SubfolderHasTheFolderAboveAsParent()
    {
        var dir = TempDir();
        try
        {
            var sub = Path.Combine(dir, "child");
            Directory.CreateDirectory(sub);

            Assert.AreEqual(Path.GetFullPath(dir), FolderBrowser.Browse(sub).Parent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Browse_MissingFolder_FailsButStillOffersTheParent()
    {
        var dir = TempDir();
        try
        {
            var l = FolderBrowser.Browse(Path.Combine(dir, "gibt-es-nicht"));

            Assert.IsTrue(l.Failed);
            Assert.AreEqual(0, l.Folders.Count);
            Assert.AreEqual(Path.GetFullPath(dir), l.Parent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Browse_DriveRoot_ParentIsTheDriveList()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var l = FolderBrowser.Browse(root);

        Assert.IsFalse(l.Failed);
        Assert.AreEqual("", l.Parent);
    }

    [TestMethod]
    public void Browse_EmptyPath_ListsTheDrives()
    {
        var l = FolderBrowser.Browse("");

        Assert.AreEqual("", l.Current);
        Assert.IsNull(l.Parent);
        Assert.IsTrue(l.Drives.Count > 0);
    }

    [TestMethod]
    public void Browse_HiddenFolders_AreNotListed()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("versteckte Ordner gibt es nur unter Windows");
        var dir = TempDir();
        try
        {
            var hidden = Path.Combine(dir, "versteckt");
            Directory.CreateDirectory(hidden);
            new DirectoryInfo(hidden).Attributes |= FileAttributes.Hidden;

            Assert.AreEqual(0, FolderBrowser.Browse(dir).Folders.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}

using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Profil als Startskript herunterladen (ScriptExport.ForProfile): Dateiname, Art, Geheimnisse geschwärzt
[TestClass]
public class N106_ProfileScriptTests
{
    private static Profile Qwen() => new()
    {
        Name = "qwen3 8B / q4",
        Program = @"C:\llama\llama-server.exe",
        Args = new() { "-m", @"D:\models\qwen3-8b.gguf", "-c", "8192", "--api-key", "sk-geheim-123" },
        WorkingDir = @"C:\llama",
    };

    [TestMethod]
    public void ForProfile_PowerShell_NamedAfterProfile_SecretRedacted()
    {
        var (file, text) = ScriptExport.ForProfile(Qwen(), "ps1")!.Value;
        Assert.AreEqual("start-qwen3-8B-q4.ps1", file);
        StringAssert.Contains(text, "& 'C:\\llama\\llama-server.exe'");
        StringAssert.Contains(text, "'-c' '8192'");
        Assert.IsFalse(text.Contains("sk-geheim-123"), "ein Schlüssel landet nie in der Datei");
        StringAssert.Contains(text, CmdLine.Redacted);
        StringAssert.Contains(text, "qwen3 8B / q4");
    }

    [TestMethod]
    public void ForProfile_Cmd_DefaultAndUnknownKind()
    {
        var (file, text) = ScriptExport.ForProfile(Qwen(), "CMD")!.Value;
        Assert.AreEqual("start-qwen3-8B-q4.cmd", file);
        StringAssert.StartsWith(text, "@echo off");
        Assert.AreEqual("start-qwen3-8B-q4.ps1", ScriptExport.ForProfile(Qwen(), null)!.Value.FileName, "ohne Art: PowerShell");
        Assert.IsNull(ScriptExport.ForProfile(Qwen(), "sh"));
    }

    [TestMethod]
    public void FileStem_SafeForEveryFileSystem()
    {
        Assert.AreEqual("start-a-b-c", ScriptExport.FileStem("a:b\\c"));
        Assert.AreEqual("start-server", ScriptExport.FileStem("   "));
        Assert.AreEqual("start-server", ScriptExport.FileStem("///"));
        Assert.AreEqual("start-gemma-3_12b.q4", ScriptExport.FileStem("gemma 3_12b.q4"));
        Assert.IsTrue(ScriptExport.FileStem(new string('x', 200)).Length <= 64);
    }

    [TestMethod]
    public void ForProfile_WithoutName_UsesProgram()
    {
        var p = Qwen();
        p.Name = "";
        Assert.AreEqual("start-llama-server.ps1", ScriptExport.ForProfile(p, "ps1")!.Value.FileName);
    }

    [TestMethod]
    public void ForHistory_SameAsProfile()
    {
        var p = Qwen();
        var h = new HistoryEntry { Name = p.Name, Program = p.Program, Args = p.Args.ToList(), WorkingDir = p.WorkingDir };
        Assert.AreEqual(ScriptExport.ForProfile(p, "cmd"), ScriptExport.ForHistory(h, "cmd"));
        Assert.IsFalse(ScriptExport.ForHistory(h, "ps1")!.Value.Text.Contains("sk-geheim-123"));
    }
}

using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N101 – Die Serverkarte zeigt den Namen des gemerkten Profils, wenn Programm und Argumente genau passen (Library.FindProfile);
// --log-file zählt nicht, jedes andere Argument schon
[TestClass]
public class N101_CardProfileNameTests
{
    private const string Exe = @"C:\llm\llama-server.exe";
    private static readonly string[] Args = { "-m", @"C:\models\m.gguf", "--port", "8080", "-c", "200000" };

    private static ServerInfo Running(params string[] args) => new()
    {
        Key = "127.0.0.1:8080", Host = "127.0.0.1", Port = 8080, Backend = BackendKind.LlamaCpp,
        Program = Exe, Args = args.ToList(), WorkingDir = @"C:\llm",
    };

    private static Library WithProfile(string name)
    {
        var lib = new Library();
        var h = new HistoryEntry { Name = "m", Program = Exe, Args = Args.ToList(), WorkingDir = @"C:\llm", Port = 8080 };
        Assert.IsNotNull(lib.AddProfile(h, name, DateTime.Now));
        return lib;
    }

    [TestMethod]
    public void The_same_program_and_arguments_find_the_profile_even_with_a_log_file()
    {
        var lib = WithProfile("Bonsai Vision MTP");

        Assert.AreEqual("Bonsai Vision MTP", lib.FindProfile(Running(Args))?.Name);
        Assert.AreEqual("Bonsai Vision MTP", lib.FindProfile(Running(Args.Concat(new[] { "--log-file", @"C:\llm\server.log" }).ToArray()))?.Name);
    }

    [TestMethod]
    public void Another_argument_is_another_configuration()
    {
        var lib = WithProfile("Bonsai Vision MTP");

        Assert.IsNull(lib.FindProfile(Running(Args.Concat(new[] { "--spec-type", "draft-mtp" }).ToArray())),
            "zusätzliches Argument: anderer Schlüssel, die Karte zeigt den Alias");
    }

    [TestMethod]
    public void The_subtitle_names_the_model_and_what_it_runs_on()
    {
        Assert.AreEqual("llama.cpp-prism", ServerSubtitle.BackendOf(@"C:\__LLM\llama.cpp-prism\build\bin\llama-server.exe", BackendKind.LlamaCpp));
        Assert.AreEqual("llama.cpp", ServerSubtitle.BackendOf(@"C:\__LLM\llama.cpp\build\bin\llama-server.exe", BackendKind.LlamaCpp));
        Assert.AreEqual("llama.cpp", ServerSubtitle.BackendOf(@"D:\tools\llama-server.exe", BackendKind.LlamaCpp));
        Assert.AreEqual("python", ServerSubtitle.BackendOf(@"C:\Python312\python.exe", BackendKind.LlamaCpp));
        Assert.AreEqual("Ollama", ServerSubtitle.BackendOf(null, BackendKind.Ollama));

        Assert.AreEqual("Ternary-Bonsai-2-27B-PQ2_0-MTP · llama.cpp-prism",
            ServerSubtitle.Of("Bonsai Vision MTP", "llama.cpp-prism", "Ternary-Bonsai-2-27B-PQ2_0-MTP"));
        Assert.AreEqual("llama.cpp", ServerSubtitle.Of("gemma-4", "llama.cpp", "gemma-4", "–", null), "Name und Leeres nicht wiederholen");
    }

    [TestMethod]
    public void The_tooltip_names_profile_alias_and_model()
    {
        Assert.AreEqual("Saved profile \"Bonsai\" · bonsai-27b · Ternary-Bonsai-2-27B", Strings.CardProfileTip("Bonsai", "bonsai-27b", "Ternary-Bonsai-2-27B"));
        Assert.AreEqual("Saved profile \"Bonsai\" · bonsai-27b", Strings.CardProfileTip("Bonsai", "bonsai-27b", "bonsai-27b"));
        Assert.AreEqual("Saved profile \"Bonsai\" · bonsai-27b", Strings.CardProfileTip("Bonsai", "bonsai-27b", "–"));
    }
}

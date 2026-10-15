using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N72 – Modus „auto“ von Claude Code: Anfragen an andere Claude-Namen (die Sicherheitsprüfung) gehen an das eingestellte
// Prüf-Ziel, claude-stykker und alles andere wie bisher an das Standardziel.
[TestClass]
public class N72_ClassifierTargetTests
{
    private static readonly ProxyTarget Main = new("main|m", "http://127.0.0.1:1", "main-model", BackendKind.LlamaCpp, true, false);
    private static readonly ProxyTarget Check = new("provider:https://cloud.example/v1|c", "https://cloud.example/v1", "c", BackendKind.LmStudio, true, false,
        Remote: true, PublicModel: "Cloud/c", Cloud: true);

    [TestMethod]
    public void Other_claude_names_count_as_safety_checks()
    {
        Assert.IsTrue(RouterProxy.IsOtherClaudeModel("claude-sonnet-5"));
        Assert.IsTrue(RouterProxy.IsOtherClaudeModel("Claude-Haiku-5-5"));
        Assert.IsFalse(RouterProxy.IsOtherClaudeModel(ClaudeDesktop.ModelAlias));
        Assert.IsFalse(RouterProxy.IsOtherClaudeModel("stykker"));
        Assert.IsFalse(RouterProxy.IsOtherClaudeModel(null));
    }

    [TestMethod]
    public void A_set_check_target_takes_the_claude_names()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Check };
        proxy.SetTargets(targets, Main.Key, Check.Key);
        Assert.AreSame(Check, proxy.ClassifierTarget(targets, "claude-sonnet-6"));
        Assert.IsNull(proxy.ClassifierTarget(targets, ClaudeDesktop.ModelAlias), "die Sitzung bleibt beim Standardziel");
        Assert.IsNull(proxy.ClassifierTarget(targets, "main-model"));
    }

    [TestMethod]
    public void A_check_under_the_session_name_is_recognised_by_its_shape()
    {
        var check = "{\"model\":\"claude-stykker\",\"max_tokens\":2112,\"stop_sequences\":[\"</block>\"],\"messages\":[]}"u8.ToArray();
        var chat = "{\"model\":\"claude-stykker\",\"max_tokens\":32000,\"tools\":[{\"name\":\"Bash\"}],\"stop_sequences\":[\"</block>\"],\"messages\":[]}"u8.ToArray();
        Assert.IsTrue(RouterProxy.IsSafetyCheck(check));
        Assert.IsFalse(RouterProxy.IsSafetyCheck(chat), "ein Gespräch mit Werkzeugen ist keine Prüfung");
        Assert.IsFalse(RouterProxy.IsSafetyCheck("{\"model\":\"claude-stykker\",\"messages\":[]}"u8.ToArray()));

        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Check };
        proxy.SetTargets(targets, Main.Key, Check.Key);
        Assert.AreSame(Check, proxy.ClassifierTarget(targets, ClaudeDesktop.ModelAlias, check));
        Assert.IsNull(proxy.ClassifierTarget(targets, ClaudeDesktop.ModelAlias, chat));
    }

    [TestMethod]
    public void A_check_asks_the_target_not_to_think()
    {
        var body = "{\"model\":\"claude-stykker\",\"max_tokens\":2112,\"stop_sequences\":[\"</block>\"],\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}"u8.ToArray();
        var off = System.Text.Encoding.UTF8.GetString(AnthropicBridge.ToOpenAi(body, "m", noThinking: true));
        StringAssert.Contains(off, "\"enable_thinking\":false");
        StringAssert.Contains(off, "</block>");
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(AnthropicBridge.ToOpenAi(body, "m")).Contains("enable_thinking"));
    }

    [TestMethod]
    public void Without_a_check_target_nothing_changes()
    {
        var proxy = new RouterProxy(0);
        var targets = new[] { Main, Check };
        proxy.SetTargets(targets, Main.Key);
        Assert.IsNull(proxy.ClassifierTarget(targets, "claude-sonnet-5"));
        proxy.SetTargets(new[] { Main }, Main.Key, Check.Key);
        Assert.IsNull(proxy.ClassifierTarget(new[] { Main }, "claude-sonnet-5"), "Prüf-Ziel nicht da → Standardziel");
    }
}

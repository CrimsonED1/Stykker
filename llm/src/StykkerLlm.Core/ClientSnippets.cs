namespace StykkerLlm.Core;

// Die Blöcke „Connect a client“ in der Proxy-Karte des Monitors: fertige Zeilen für Claude Code, Qwen Code (OpenAI-kompatibel)
// und einen curl-Aufruf. Die Werte sind die des Proxys selbst: derselbe Schlüssel wie in Clients/ClaudeClient.cs
// (ClaudeDesktop.ApiKey, den der Proxy für örtliche Ziele nicht prüft), das Modell claude-stykker für Claude Code (wie
// ClaudeClient) und „stykker“ (RouterProxy.VirtualModel) für OpenAI-Clients. Core baut den Text, die Seite zeigt ihn nur.
public static class ClientSnippets
{
    public enum Shell { PowerShell, Bash }

    public sealed record Block(string Title, string Hint, string Text);

    // Basisadresse ohne „/v1“ am Ende (ProxyPanel.BaseUrl liefert sie mit „/v1“)
    public static string Root(string baseUrl)
    {
        var t = baseUrl.TrimEnd('/');
        return t.EndsWith("/v1", StringComparison.Ordinal) ? t[..^3] : t;
    }

    // Die drei Blöcke für eine Adresse. Die Shell-Form betrifft nur die Umgebungsvariablen; der curl-Block ist bash-Form.
    public static IReadOnlyList<Block> Build(string baseUrl, Shell shell)
    {
        var root = Root(baseUrl);
        var v1 = root + "/v1";
        return new[]
        {
            new Block(Strings.MxClaudeCode, Strings.MxClaudeCodeHint, Env(shell,
                ("ANTHROPIC_BASE_URL", root), ("ANTHROPIC_AUTH_TOKEN", ClaudeDesktop.ApiKey), ("ANTHROPIC_MODEL", ClaudeDesktop.ModelAlias))),
            new Block(Strings.MxOpenAi, Strings.MxOpenAiHint, Env(shell,
                ("OPENAI_BASE_URL", v1), ("OPENAI_API_KEY", ClaudeDesktop.ApiKey), ("OPENAI_MODEL", RouterProxy.VirtualModel))),
            new Block(Strings.MxCurl, Strings.MxCurlHint, Curl(v1)),
        };
    }

    // PowerShell: $env:NAME = "wert"; bash: export NAME="wert"
    private static string Env(Shell shell, params (string Name, string Value)[] vars) =>
        string.Join("\n", vars.Select(v => shell == Shell.PowerShell
            ? $"$env:{v.Name} = \"{v.Value}\""
            : $"export {v.Name}=\"{v.Value}\""));

    private static string Curl(string v1) =>
        $"curl {v1}/chat/completions -H \"Authorization: Bearer {ClaudeDesktop.ApiKey}\" -H \"Content-Type: application/json\" " +
        "-d '{\"model\":\"" + RouterProxy.VirtualModel + "\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}]}'";
}

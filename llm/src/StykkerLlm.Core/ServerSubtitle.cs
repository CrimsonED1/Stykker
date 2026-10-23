namespace StykkerLlm.Core;

// Unterzeile unter dem Namen auf Serverkarte und Profil-Kachel: welches Modell und womit es läuft,
// z. B. „Ternary-Bonsai-2-27B-PQ2_0-MTP · llama.cpp-prism“. Leere Angaben, „–“ und Wiederholungen (auch des Namens
// darüber) fallen weg.
public static class ServerSubtitle
{
    public static string Of(string title, string backend, params string?[] models)
    {
        var parts = new List<string>();
        foreach (var raw in models.Append(backend))
        {
            var m = raw?.Trim() ?? "";
            if (m.Length == 0 || m == "–" || m.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts.Contains(m, StringComparer.OrdinalIgnoreCase)) parts.Add(m);
        }
        return string.Join(" · ", parts);
    }

    // Womit ein Programm läuft: llama-server → „llama.cpp“, oder der Ordner des Forks, wenn er anders heißt
    // („…\llama.cpp-prism\build\bin\llama-server.exe“ → „llama.cpp-prism“); ein anderes Programm mit seinem Namen
    // (z. B. „python“); Ollama, LM Studio, vLLM mit ihrem Anzeigenamen.
    public static string BackendOf(string? program, BackendKind kind)
    {
        if (kind != BackendKind.LlamaCpp || string.IsNullOrWhiteSpace(program)) return BackendProbes.DisplayName(kind);
        var parts = program.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var file = parts.Length > 0 ? parts[^1] : program;
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        if (!file.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase)) return file;
        const string Base = "llama.cpp";
        var fork = parts.FirstOrDefault(p => p.StartsWith(Base, StringComparison.OrdinalIgnoreCase) && p.Length > Base.Length);
        return fork ?? BackendProbes.DisplayName(kind);
    }
}

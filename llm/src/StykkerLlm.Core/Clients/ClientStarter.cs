using System.Diagnostics;

namespace StykkerLlm.Core;

// Startet ein Programm für einen Client. Ein eigener Typ, damit Tests nichts wirklich starten.
public interface IClientStarter
{
    // env: zusätzliche Umgebungsvariablen für das Programm (leer = unverändert übernehmen)
    bool Start(string file, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string> env, out string? error);
}

// Der echte Start. Der Server läuft ohne sichtbare Konsole; deshalb öffnet ein Konsolenfenster der Aufrufer selbst
// (wt.exe bzw. „cmd /c start“), statt ein Konsolenprogramm direkt zu starten, das die unsichtbare Konsole erbt.
public sealed class ProcessClientStarter : IClientStarter
{
    public bool Start(string file, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string> env, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }
}

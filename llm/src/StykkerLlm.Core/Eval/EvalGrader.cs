using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StykkerLlm.Core.Eval;

// Ein Werkzeugaufruf aus der Antwort (Name und Argumente als JSON-Text)
public sealed record EvalToolCall(string Name, string Arguments, string Id = "");

// Bewertet eine Antwort nach EvalCheck. Liefert Punktanteil 0..1 und einen kurzen Grund bei Fehlschlag.
public static class EvalGrader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Denkteil entfernen (<think>…</think>, auch ohne öffnendes Tag am Anfang). Ein Denkteil ohne Ende (abgeschnitten)
    // ergibt "": dann gibt es keine Antwort, und Zahlen aus dem Denkteil dürfen nicht bewertet werden.
    public static string StripThinking(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline);
        int close = t.IndexOf("</think>", StringComparison.Ordinal);
        if (close >= 0) t = t[(close + 8)..];
        if (t.Contains("<think>", StringComparison.Ordinal)) return "";
        return t.Trim();
    }

    // Für "exact": Rand-Satzzeichen, Anführungszeichen, Markdown-Fett und Codeblöcke weg – so lange, bis sich nichts mehr ändert
    public static string Normalize(string s)
    {
        s = Regex.Replace(s.Trim(), @"^```\w*\s*|\s*```$", "");
        string prev;
        do
        {
            prev = s;
            s = s.Trim().Trim('*', '`', '"', '\'', '„', '“', '”', '_').TrimEnd('.', '!', ',', ';', ':');
        } while (s != prev);
        return s;
    }

    public static async Task<(double Score, string Note)> GradeAsync(EvalTask task, string answer, IReadOnlyList<EvalToolCall> tools,
        PythonRunner? python, CancellationToken ct, PythonRunner? pythonChecks = null)
    {
        try { return await GradeCoreAsync(task, answer, tools, python, pythonChecks ?? python, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (0, "grader error: " + Short(ex.Message, 100)); }   // z. B. ungültiges Pattern in einer eigenen Suite
    }

    private static async Task<(double, string)> GradeCoreAsync(EvalTask task, string answer, IReadOnlyList<EvalToolCall> tools,
        PythonRunner? python, PythonRunner? pythonChecks, CancellationToken ct)
    {
        var c = task.Check;
        string a = StripThinking(answer);
        switch (c.Type.ToLowerInvariant())
        {
            case "exact":
            {
                var n = Normalize(a);
                return c.Expected.Any(e => string.Equals(n, e.Trim(), StringComparison.OrdinalIgnoreCase)) ? (1, "") : (0, $"got \"{Short(n)}\"");
            }
            case "number":
            {
                // Tausendertrenner (1,815 / 1 815) entfernen; bei mehreren Zahlen zählt die letzte (Rechenweg vor dem Ergebnis)
                var cleaned = Regex.Replace(a, @"(?<=\d)[,\u202F\u00A0 ](?=\d{3}\b)", "");
                var ms = Regex.Matches(cleaned, @"-?\d+(?:\.\d+)?");
                if (ms.Count == 0) return (0, "no number in the answer");
                var last = ms[^1].Value;
                double got = double.Parse(last, Inv), want = double.Parse(c.Expected.FirstOrDefault() ?? "0", Inv);
                return Math.Abs(got - want) <= c.Tolerance + 1e-9 ? (1, "") : (0, $"got {last}, expected {c.Expected.FirstOrDefault()}");
            }
            case "regex":
                return Regex.IsMatch(a.Replace("\r\n", "\n"), c.Pattern ?? "", c.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(2))
                    ? (1, "") : (0, $"format not matched: \"{Short(a)}\"");
            case "contains":
                return Contains(c, a);
            case "json":
                return GradeJson(c, a);
            case "python":
                if (python == null) return (0, "skipped: Python code is not run");
                return await GradePythonAsync(c, a, python, ct).ConfigureAwait(false);
            case "tool":
                return GradeTool(c, tools);
            case "pycheck":
            {
                if (pythonChecks == null) return (0, "skipped: no Python for the answer checks");
                // Die Antwort als String-Literal (JSON-Escapes sind gültige Python-Escapes), danach die Prüfungen
                var nonce = "EVAL-OK-" + Guid.NewGuid().ToString("N")[..12];
                var script = "answer = " + JsonSerializer.Serialize(a) + "\n" + (c.Tests ?? "") + $"\nprint('{nonce}')\n";
                var (exit, output) = await pythonChecks.RunAsync(script, TimeSpan.FromSeconds(Math.Clamp(c.TimeoutSec, 1, 60)), ct).ConfigureAwait(false);
                if (exit == 0 && output.Contains(nonce, StringComparison.Ordinal)) return (1, "");
                var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "failed";
                return (0, Short(last + " · answer: " + a, 120));
            }
            case "toolchain":
            {
                // Reihenfolge der geforderten Werkzeuge muss als Teilfolge der Aufrufe vorkommen
                int i = 0;
                foreach (var call in tools)
                    if (i < c.Expected.Count && string.Equals(call.Name, c.Expected[i], StringComparison.OrdinalIgnoreCase)) i++;
                if (i < c.Expected.Count)
                    return (0, $"tool chain incomplete: called [{string.Join(", ", tools.Select(t => t.Name))}], expected [{string.Join(", ", c.Expected)}]");
                if (c.Pattern != null && !Regex.IsMatch(a, c.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
                    return (0.5, $"tools ok, final answer wrong: \"{Short(a)}\"");
                return (1, "");
            }
            case "notool":
                if (tools.Count > 0) return (0, $"called {tools[0].Name} although no tool was needed");
                if (c.Pattern != null && !Regex.IsMatch(a, c.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))) return (0, $"answer: \"{Short(a)}\"");
                return Contains(c, a);
            default:
                return (0, $"unknown check type '{c.Type}'");
        }
    }

    private static (double, string) Contains(EvalCheck c, string a)
    {
        var missing = c.Expected.Where(e => !a.Contains(e, StringComparison.OrdinalIgnoreCase)).ToList();
        return missing.Count == 0 ? (1, "") : (0, "missing: " + string.Join(", ", missing));
    }

    private static (double, string) GradeJson(EvalCheck c, string a)
    {
        var m = Regex.Match(a, @"\{.*\}", RegexOptions.Singleline);
        if (!m.Success) return (0, "no JSON object");
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (0, "not a JSON object");
            foreach (var key in c.Expected)
                if (!doc.RootElement.TryGetProperty(key, out _)) return (0, $"key '{key}' missing");
            foreach (var (key, want) in c.Values)
            {
                if (!doc.RootElement.TryGetProperty(key, out var v)) return (0, $"key '{key}' missing");
                var got = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                if (!string.Equals(got.Trim(), want.Trim(), StringComparison.OrdinalIgnoreCase)) return (0, $"{key} = {Short(got)}, expected {want}");
                if (v.ValueKind == JsonValueKind.String && double.TryParse(want, NumberStyles.Float, Inv, out _)) return (0.5, $"{key} is a string, expected a number");
            }
            // Inhalt stimmt; Teilpunkte, wenn Text um das JSON herum steht
            if (!Regex.IsMatch(a.Trim(), @"^(```(json)?\s*)?\{.*\}(\s*```)?$", RegexOptions.Singleline)) return (0.5, "JSON with extra text around it");
            return (1, "");
        }
        catch (JsonException) { return (0, "invalid JSON"); }
    }

    private static (double, string) GradeTool(EvalCheck c, IReadOnlyList<EvalToolCall> tools)
    {
        if (tools.Count == 0) return (0, "no tool call");
        var call = tools[0];
        if (!string.Equals(call.Name, c.Tool, StringComparison.OrdinalIgnoreCase)) return (0, $"called {call.Name}, expected {c.Tool}");
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            foreach (var (key, want) in c.Args)
            {
                if (!doc.RootElement.TryGetProperty(key, out var v)) return (0.5, $"argument '{key}' missing");
                var got = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                if (!got.Contains(want, StringComparison.OrdinalIgnoreCase)) return (0.5, $"{key} = {Short(got)}, expected {want}");
            }
            return (1, "");
        }
        catch (JsonException) { return (0.5, "arguments are not valid JSON"); }
    }

    // Code aus der Antwort: alle als Python markierten Blöcke (```python, ```Python, ```py, ```python3) hintereinander;
    // ohne Markierung der größte Block; ohne Codeblock die ganze Antwort
    public static string ExtractCode(string a)
    {
        var all = Regex.Matches(a, @"```([\w+-]*)[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);
        var py = all.Where(m => Regex.IsMatch(m.Groups[1].Value, @"^(python3?|py)$", RegexOptions.IgnoreCase)).Select(m => m.Groups[2].Value).ToList();
        if (py.Count > 0) return string.Join("\n\n", py);
        var plain = all.Where(m => m.Groups[1].Value.Length == 0).Select(m => m.Groups[2].Value).ToList();
        if (plain.Count > 0) return plain.OrderByDescending(b => b.Length).First();
        return a;
    }

    private static async Task<(double, string)> GradePythonAsync(EvalCheck c, string a, PythonRunner python, CancellationToken ct)
    {
        var code = ExtractCode(a);
        if (string.IsNullOrWhiteSpace(code)) return (0, "no code");
        // Bestanden nur mit Marker am Ende der Tests: ein sys.exit(0) im Modellcode vor den Tests besteht nicht
        var nonce = "EVAL-OK-" + Guid.NewGuid().ToString("N")[..12];
        var script = code + "\n\n# --- tests ---\n" + (c.Tests ?? "") + $"\nprint('{nonce}')\n";
        var (exit, output) = await python.RunAsync(script, TimeSpan.FromSeconds(Math.Clamp(c.TimeoutSec, 1, 120)), ct).ConfigureAwait(false);
        if (exit == 0 && output.Contains(nonce, StringComparison.Ordinal)) return (1, "");
        if (exit == 0) return (0, "exited before the tests ran");
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "failed";
        return (0, Short(last, 120));
    }

    internal static string Short(string s, int max = 60)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

// Führt von Modellen geschriebenen Python-Code aus: eigener temporärer Ordner, isolierter Modus (-I), bereinigte Umgebung
// (keine API-Schlüssel), Zeitlimit, Ausgabe höchstens 256 KB, unter Windows in einem Job-Objekt (Speicher höchstens 1,5 GB,
// alle Unterprozesse enden mit dem Job, niedrige Priorität). Das ist keine Sandbox – der Aufrufer muss vorher fragen.
public sealed class PythonRunner
{
    public const int MaxOutputChars = 256 * 1024;
    public const long MemoryLimitBytes = 1536L * 1024 * 1024;
    public string Executable { get; }

    private PythonRunner(string exe) { Executable = exe; }

    // Python suchen: ausdrücklich angegeben, "py -3", "python", "python3", übliche Installationsorte. Der Store-Platzhalter zählt nicht.
    public static PythonRunner? Find(string? configured = null)
    {
        var candidates = new List<(string Exe, string[] Prefix)>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add((configured, Array.Empty<string>()));
        if (OperatingSystem.IsWindows()) candidates.Add(("py", new[] { "-3" }));
        candidates.Add(("python", Array.Empty<string>()));
        candidates.Add(("python3", Array.Empty<string>()));
        // Übliche Installationsorte unter Windows (Python ist oft installiert, aber nicht im PATH); neueste Version zuerst
        if (OperatingSystem.IsWindows())
            foreach (var root in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\" })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root, "Python3*").OrderByDescending(d => d.Length).ThenByDescending(d => d))
                        if (File.Exists(Path.Combine(d, "python.exe"))) candidates.Add((Path.Combine(d, "python.exe"), Array.Empty<string>()));
                }
                catch { }
            }
        foreach (var (exe, prefix) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var p in prefix) psi.ArgumentList.Add(p);
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import sys;print(sys.executable)");
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                _ = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(8000)) { try { proc.Kill(true); } catch { } continue; }
                var outText = outTask.Result.Trim();
                if (proc.ExitCode == 0 && outText.Length > 0 && !outText.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return new PythonRunner(outText);
            }
            catch { }
        }
        return null;
    }

    // Liefert Exit-Code (-1 = Zeitlimit, Ausgabeflut oder Start fehlgeschlagen) und die Ausgabe (stdout + stderr, gekürzt)
    public async Task<(int Exit, string Output)> RunAsync(string code, TimeSpan timeout, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "stykker-eval-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Process? proc = null;
        using var job = OperatingSystem.IsWindows() ? JobObject.Create(MemoryLimitBytes) : null;
        try
        {
            var file = Path.Combine(dir, "task.py");
            await File.WriteAllTextAsync(file, code, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            // Bereinigte Umgebung: nur was Python zum Starten braucht (keine Tokens, Schlüssel, Proxys)
            var keep = new[] { "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "PATH", "PATHEXT", "COMSPEC", "SYSTEMDRIVE", "LANG", "HOME" };
            foreach (var k in psi.Environment.Keys.ToList())
                if (!keep.Contains(k, StringComparer.OrdinalIgnoreCase)) psi.Environment.Remove(k);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["TEMP"] = dir; psi.Environment["TMP"] = dir;
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add("-X");
            psi.ArgumentList.Add("utf8");
            psi.ArgumentList.Add(file);
            proc = Process.Start(psi);
            if (proc == null) return (-1, "python did not start");
            job?.Assign(proc);
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            proc.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var buf = new StringBuilder();
            bool flood = false;
            async Task Pump(StreamReader r)
            {
                var chunk = new char[4096];
                while (true)
                {
                    int n = await r.ReadAsync(chunk.AsMemory(), cts.Token).ConfigureAwait(false);
                    if (n <= 0) return;
                    lock (buf)
                    {
                        if (buf.Length + n > MaxOutputChars) { flood = true; cts.Cancel(); return; }
                        buf.Append(chunk, 0, n);
                    }
                }
            }
            var pumps = Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError));
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await pumps.WaitAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);   // Enkel mit geerbten Pipes: nicht ewig warten
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                ct.ThrowIfCancellationRequested();
                if (!proc.HasExited || flood) return (-1, flood ? $"output flood (more than {MaxOutputChars / 1024} KB)" : $"timeout after {timeout.TotalSeconds:0} s");
            }
            string output; lock (buf) output = buf.ToString();
            return (proc.ExitCode, output);
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(3000); } catch { }
                proc.Dispose();
            }
            job?.Dispose();   // beendet auch entkommene Unterprozesse (KILL_ON_JOB_CLOSE)
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(dir, true); break; } catch { Thread.Sleep(200); }
            }
        }
    }

    // Windows-Job-Objekt: Speichergrenze je Prozess und "alle beenden, wenn der Job geschlossen wird"
    private sealed class JobObject : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attr, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimits info, int size);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        private IntPtr _h;
        private JobObject(IntPtr h) { _h = h; }

        public static JobObject? Create(long memoryLimit)
        {
            try
            {
                var h = CreateJobObject(IntPtr.Zero, null);
                if (h == IntPtr.Zero) return null;
                var info = new ExtendedLimits();
                info.Basic.LimitFlags = 0x2000 | 0x100;   // KILL_ON_JOB_CLOSE | PROCESS_MEMORY
                info.ProcessMemoryLimit = (UIntPtr)(ulong)memoryLimit;
                SetInformationJobObject(h, 9, ref info, Marshal.SizeOf<ExtendedLimits>());   // JobObjectExtendedLimitInformation
                return new JobObject(h);
            }
            catch { return null; }
        }

        public void Assign(Process p) { try { AssignProcessToJobObject(_h, p.Handle); } catch { } }

        public void Dispose()
        {
            if (_h != IntPtr.Zero) { CloseHandle(_h); _h = IntPtr.Zero; }
        }
    }
}

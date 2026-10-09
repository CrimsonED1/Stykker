using System.Diagnostics;
using System.Text;

namespace StykkerLlm.Core;

public enum StopOutcome { Stopped, ProcessChanged, NotFound, Failed, NeedsConfirmation }

public enum LaunchSeverity { Error, Warning }

public sealed record LaunchIssue(LaunchSeverity Severity, string Code, string Message);

// Was gestartet werden soll. Args und Env enthalten nie Klartext-Geheimnisse; geschwärzte Werte ("***") müssen vorher ersetzt werden.
public sealed record LaunchSpec(string Name, string Program, IReadOnlyList<string> Args, string? WorkingDir,
    Guid? ProfileId = null, double? MeasuredVramGb = null, IReadOnlyDictionary<string, string>? Env = null);

// Ein geschwärzter Wert, der vor dem Start neu eingegeben werden muss: Option + Index in den Argumenten, bzw. EnvName bei einer Umgebungsvariable
public sealed record SecretSlot(string Option, int Index, string? EnvName = null);

// Fertiger Startplan: Argumente mit --log-file, ermittelter Port
public sealed record LaunchPlan(string Name, string Program, IReadOnlyList<string> Args, string? WorkingDir, int Port, string Host,
    string? ModelPath, string LogFile, Guid? ProfileId, IReadOnlyDictionary<string, string>? Env = null);

public enum LaunchState { Starting, Failed }

// Ringpuffer für die letzten Ausgabezeilen (stdout/stderr) eines gestarteten Prozesses: begrenzt in Zeilenzahl und Zeilenlänge.
public sealed class OutputRing
{
    private readonly Queue<string> _lines = new();
    private readonly int _maxLines, _maxLineLength;
    public OutputRing(int maxLines = 200, int maxLineLength = 1000) { _maxLines = maxLines; _maxLineLength = maxLineLength; }

    public void Add(string? line)
    {
        if (line == null) return;
        if (line.Length > _maxLineLength) line = line[.._maxLineLength] + "…";
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _maxLines) _lines.Dequeue();
        }
    }

    public string[] Tail(int count)
    {
        lock (_lines) return _lines.Skip(Math.Max(0, _lines.Count - count)).ToArray();
    }

    public int Count { get { lock (_lines) return _lines.Count; } }
}

// Ein vom Monitor gestarteter Serverprozess, solange er noch nicht lauscht (Starting) bzw. wenn er sich beendet hat oder zu lange braucht (Failed).
public sealed class LaunchedServer : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Program { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string? WorkingDir { get; init; }
    public int Port { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public string LogFile { get; init; } = "";
    public Guid? ProfileId { get; init; }
    public DateTime Started { get; init; } = DateTime.Now;
    public int Pid { get; internal set; }
    public long StartTicks { get; internal set; }
    public Process? Proc { get; internal set; }
    internal OutputRing Output { get; } = new();
    private volatile int _state;
    public LaunchState State => (LaunchState)_state;
    public int? ExitCode { get; private set; }
    // Warum der Eintrag fehlgeschlagen ist (Prozess beendet / lauscht nach der Frist nicht), sonst null
    public string? FailureReason { get; private set; }
    // Der Prozess läuft noch, obwohl der Eintrag fehlgeschlagen ist (Zeitüberschreitung): kann gestoppt werden
    public bool StillRunning { get; private set; }
    public IReadOnlyList<string> LogTail { get; private set; } = Array.Empty<string>();
    public string Url => NetAddr.Url(Host, Port);

    // Letzte Zeilen: bevorzugt das Log, sonst die Konsolenausgabe (z. B. wenn der Prozess schon vor dem ersten Logschreiben abstürzt)
    private IReadOnlyList<string> CollectTail()
    {
        var t = LogTailReader.Tail(LogFile, 12);
        return t.Length > 0 ? t : Output.Tail(12);
    }

    internal void MarkExited(int? code)
    {
        ExitCode = code;
        StillRunning = false;
        LogTail = CollectTail();
        _state = (int)LaunchState.Failed;
    }

    internal void MarkTimedOut(TimeSpan limit)
    {
        if (State != LaunchState.Starting) return;
        FailureReason = Strings.LaunchTimedOut((int)limit.TotalMinutes);
        StillRunning = true;
        LogTail = CollectTail();
        _state = (int)LaunchState.Failed;
    }

    public void Dispose() { try { Proc?.Dispose(); } catch { } Proc = null; }
}

// Letzte Zeilen einer (möglicherweise noch beschriebenen) Textdatei
public static class LogTailReader
{
    public static string[] Tail(string? path, int lines, int maxBytes = 64 * 1024)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length, start = Math.Max(0, len - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[len - start];
            int got = 0;
            while (got < buf.Length) { int r = fs.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
            var text = Encoding.UTF8.GetString(buf, 0, got);
            var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);   // angeschnittene erste Zeile
            while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            return all.Skip(Math.Max(0, all.Count - lines)).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}

// Start und Stopp von Serverprozessen. Gestartet wird nur, was der Nutzer gemerkt oder im Verlauf gewählt hat (lokale Datei).
public static class ServerLauncher
{
    public const int DefaultPort = 8080;
    public const long MaxLogBytes = 50L * 1024 * 1024;

    // ── Stoppen ──

    // Beendet den Prozess nur, wenn PID und Startzeit noch zu dem passen, was der Nutzer bestätigt hat. Die Plattform öffnet den Prozess
    // einmal, prüft die Startzeit und beendet über dasselbe Handle (kein Zeitfenster für eine wiederverwendete PID).
    // startTicks == 0 (Startzeit nicht lesbar): ohne ausdrückliche Bestätigung (allowUnverified) wird nichts beendet.
    public static StopOutcome Stop(IPlatform platform, int pid, long startTicks, out string? error, bool allowUnverified = false)
    {
        error = null;
        if (startTicks == 0 && !allowUnverified) return StopOutcome.NeedsConfirmation;
        return platform.Terminate(pid, startTicks, out error);
    }

    // ── Geheimnisse ──

    // Geschwärzte Werte in den Argumenten ("--api-key ***", "--hf-token=***") und in der Umgebung
    public static List<SecretSlot> FindSecretSlots(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var slots = new List<SecretSlot>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            int eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=') : -1;
            if (eq > 0 && a[(eq + 1)..] == CmdLine.Redacted) slots.Add(new SecretSlot(a[..eq], i));
            else if (i + 1 < args.Count && args[i + 1] == CmdLine.Redacted && IsSecretOption(a)) slots.Add(new SecretSlot(a, i + 1));
        }
        if (env != null)
            foreach (var (k, v) in env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                if (v == CmdLine.Redacted) slots.Add(new SecretSlot(k, -1, k));
        return slots;
    }

    private static bool IsSecretOption(string a) => a is "--api-key" or "--api-key-file" or "--hf-token" or "-hft";

    // Setzt die eingegebenen Werte ein; eine geschwärzte --api-key-file wird dabei zu --api-key mit dem Schlüssel selbst
    public static List<string> ApplySecrets(IReadOnlyList<string> args, IReadOnlyDictionary<int, string> valuesByIndex)
    {
        var result = args.ToList();
        foreach (var (idx, value) in valuesByIndex)
        {
            var a = result[idx];
            int eq = a.StartsWith("--", StringComparison.Ordinal) ? a.IndexOf('=') : -1;
            if (eq > 0)
            {
                var name = a[..eq] == "--api-key-file" ? "--api-key" : a[..eq];
                result[idx] = name + "=" + value;
            }
            else
            {
                result[idx] = value;
                if (idx > 0 && result[idx - 1] == "--api-key-file") result[idx - 1] = "--api-key";
            }
        }
        return result;
    }

    // Eingegebene Werte in die geschwärzten Umgebungsvariablen einsetzen
    public static Dictionary<string, string> ApplyEnvSecrets(IReadOnlyDictionary<string, string>? env, IReadOnlyDictionary<string, string> valuesByName)
    {
        var result = new Dictionary<string, string>(env ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in valuesByName) result[k] = v;
        return result;
    }

    // ── Bestätigung der Kommandozeile ──

    // Fingerabdruck dessen, was gestartet würde (Programm, Argumente, Arbeitsordner, Umgebung; alles noch geschwärzt). Ändert sich etwas,
    // muss der Nutzer die Kommandozeile erneut bestätigen. Gespeichert wird nicht dieser Hash, sondern Library.Fingerprint (HMAC mit
    // Geheimschlüssel): eine bearbeitete library.json kann so keine Bestätigung vortäuschen.
    public static string Fingerprint(LaunchSpec spec) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintPayload(spec))))[..24].ToLowerInvariant();

    internal static string FingerprintPayload(LaunchSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append(spec.Program.Trim().ToLowerInvariant()).Append('\u0001').Append(string.Join("\u0001", spec.Args)).Append('\u0001')
          .Append(spec.WorkingDir?.Trim().ToLowerInvariant() ?? "");
        if (spec.Env != null)
            foreach (var (k, v) in spec.Env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append('\u0002').Append(k.ToUpperInvariant()).Append('=').Append(v);
        return sb.ToString();
    }

    // Vollständige Kommandozeile zum Bestätigen (geschwärzt): Programm, Argumente, Arbeitsordner, Umgebung
    public static string DescribeCommand(LaunchSpec spec)
    {
        var (red, _) = CmdLine.Redact(spec.Args);
        var sb = new StringBuilder();
        sb.Append(CmdLine.Join(new[] { spec.Program }.Concat(red)));
        sb.Append("\n\n").Append(Strings.WorkingFolder).Append(": ").Append(string.IsNullOrEmpty(spec.WorkingDir) ? Strings.ProgramFolder : spec.WorkingDir);
        if (spec.Env is { Count: > 0 })
            sb.Append("\n").Append(Strings.Environment).Append(": ").Append(string.Join(", ", spec.Env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Key + "=" + (e.Value == CmdLine.Redacted || LooksSecretEnv(e.Key) ? CmdLine.Redacted : e.Value))));
        return sb.ToString();
    }

    private static bool LooksSecretEnv(string n) => n.Contains("KEY", StringComparison.OrdinalIgnoreCase) || n.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        n.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || n.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);

    // ── Prüfen ──

    // Nur ein vollständiger, absoluter Pfad zu einer vorhandenen Datei wird gestartet: kein Suchen im PATH, kein relatives Auflösen
    public static bool IsStartableProgram(string? program) =>
        !string.IsNullOrWhiteSpace(program) && Path.IsPathFullyQualified(program) && File.Exists(program);

    // Vorabprüfung: Fehler verhindern den Start, Warnungen lassen sich mit "trotzdem starten" übergehen
    public static List<LaunchIssue> Check(LaunchSpec spec, IPlatform platform, GpuSample? gpu, IReadOnlyList<ServerInfo> running) =>
        Check(spec, platform, gpu, running.Select(r => (r.Params?.Model, r.WorkingDir, r.Port)).ToList());

    public static List<LaunchIssue> Check(LaunchSpec spec, IPlatform platform, GpuSample? gpu, IReadOnlyList<(string? Model, string? WorkingDir, int Port)> running)
    {
        var issues = new List<LaunchIssue>();
        void Err(string code, string msg) => issues.Add(new LaunchIssue(LaunchSeverity.Error, code, msg));
        void Warn(string code, string msg) => issues.Add(new LaunchIssue(LaunchSeverity.Warning, code, msg));

        if (string.IsNullOrWhiteSpace(spec.Program)) { Err("program", "No program set."); return issues; }
        if (!Path.IsPathFullyQualified(spec.Program)) Err("program", $"The program must be a full path (e.g. C:\\llama\\llama-server.exe), not \"{spec.Program}\".");
        else if (!File.Exists(spec.Program)) Err("program", $"Program not found: {spec.Program}");
        if (!string.IsNullOrEmpty(spec.WorkingDir) && !Directory.Exists(spec.WorkingDir)) Err("cwd", $"Working folder not found: {spec.WorkingDir}");
        if (FindSecretSlots(spec.Args, spec.Env).Count > 0) Err("secret", "A secret value (API key or token) must be entered first.");

        var parsed = LlamaServerArgs.Parse(spec.Args);
        if (parsed.Mode != ServerMode.Normal) Err("mode", "Only servers with a local model file (-m) can be started from here.");

        var model = ResolveModel(parsed.Model, spec.WorkingDir);
        if (parsed.Mode == ServerMode.Normal && (model == null || !File.Exists(model))) Err("model", $"Model file not found: {parsed.Model}");

        int port = parsed.Port ?? DefaultPort;
        var listener = platform.ReadListeners().FirstOrDefault(l => l.Port == port);
        if (listener != null) Err("port", $"Port {port} is already in use (PID {listener.Pid}).");

        if (model != null)
        {
            var same = running.FirstOrDefault(r => r.Model is { } m && string.Equals(ResolveModel(m, r.WorkingDir), model, StringComparison.OrdinalIgnoreCase));
            if (same != default) Warn("same-model", $"The same model is already running on port {same.Port}.");
        }

        if (model != null && File.Exists(model))
        {
            var gguf = Gguf.TryRead(model);
            if (gpu != null)
            {
                double? need = null; string how = "estimate";
                if (spec.MeasuredVramGb is > 0) { need = spec.MeasuredVramGb; how = "measured earlier"; }
                else if (gguf is { } g)
                {
                    int ctx = parsed.Ctx is > 0 ? parsed.Ctx.Value : (int)Math.Min(g.ContextLength ?? 4096, 131072);
                    need = VramEstimate.Estimate(g, ctx, parsed.CacheTypeK, parsed.CacheTypeV, parsed.Ngl).TotalGb;
                }
                if (need is double n && n > gpu.MemFreeGb - 0.2)
                    Warn("vram", $"Needs about {Strings.N1(n)} GB VRAM ({how}), only {Strings.N1(gpu.MemFreeGb)} GB free.");
            }
            if (RamWarning(gguf, parsed, platform.ReadSystem()) is { } ram) Warn("ram", ram);
        }
        return issues;
    }

    // RAM-Warnung vor dem Start: bindend ist die CPU-Seite (Gewichte werden per mmap geteilt, der KV-Cache der CPU-Schichten
    // liegt im RAM). null = kein Hinweis; ohne GGUF-Metadaten oder ohne Systemwerte wird nichts gemeldet.
    public static string? RamWarning(GgufInfo? gguf, LlamaServerArgs parsed, SystemSample? sys)
    {
        if (gguf == null || sys == null) return null;
        int ctx = parsed.Ctx is > 0 ? parsed.Ctx.Value : (int)Math.Min(gguf.ContextLength ?? 4096, 131072);
        double cpuNeed = VramEstimate.CpuRamGb(gguf, ctx, parsed.CacheTypeK, parsed.CacheTypeV, parsed.Ngl);
        double freeRam = sys.RamTotalGb - sys.RamUsedGb;
        if (cpuNeed > freeRam - 1.0)
            return $"Needs about {Strings.N1(cpuNeed)} GB RAM on the CPU side, only {Strings.N1(freeRam)} GB free.";
        if (freeRam < 1.5)
            return $"Only {Strings.N1(freeRam)} GB system RAM free.";
        return null;
    }

    // Wie Check, aber nicht auf dem UI-Thread (Dateizugriffe, GGUF-Kopf lesen, Listener-Tabelle)
    public static Task<List<LaunchIssue>> CheckAsync(LaunchSpec spec, IPlatform platform, GpuSample? gpu, IReadOnlyList<ServerInfo> running) =>
        Task.Run(() => Check(spec, platform, gpu, running));

    // Dasselbe für einen Client, der den Zustand des Servers kennt: Fenster, TUI und Web prüfen so ohne eigene Engine
    public static Task<List<LaunchIssue>> CheckAsync(LaunchSpec spec, IPlatform platform, GpuSample? gpu, IReadOnlyList<RemoteServer> running) =>
        Task.Run(() => Check(spec, platform, gpu, running.Select(s => ((string?)s.Params.Model, (string?)s.WorkingDir, s.Port)).ToList()));

    public static string? ResolveModel(string? model, string? workingDir)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (Path.IsPathRooted(model) || string.IsNullOrEmpty(workingDir)) return model;
        return Path.GetFullPath(Path.Combine(workingDir, model));
    }

    // ── Plan und Start ──

    // Ergänzt --log-file (je Profil und Port unter dem Log-Ordner), falls keins gesetzt ist, und räumt alte Logs auf
    public static LaunchPlan BuildPlan(LaunchSpec spec, string logsDir, StorageLimits? limits = null)
    {
        limits ??= new StorageLimits();
        var args = spec.Args.ToList();
        var parsed = LlamaServerArgs.Parse(args);
        int port = parsed.Port ?? DefaultPort;
        string host = parsed.Host is { Length: > 0 } h && h != "0.0.0.0" && h != "::" ? h : "127.0.0.1";
        string log;
        if (parsed.LogFile is { Length: > 0 } lf)
            log = Path.IsPathRooted(lf) || string.IsNullOrEmpty(spec.WorkingDir) ? lf : Path.GetFullPath(Path.Combine(spec.WorkingDir, lf));
        else
        {
            log = Path.Combine(logsDir, SafeName(spec.Name) + "-" + port + ".log");
            args.Add("--log-file"); args.Add(log);
        }
        PruneLogs(logsDir, limits.MaxLogBytes, new[] { log }, limits.MaxLogFiles);
        return new LaunchPlan(spec.Name, spec.Program, args, spec.WorkingDir, port, host, ResolveModel(parsed.Model, spec.WorkingDir), log, spec.ProfileId, spec.Env);
    }

    public static string SafeName(string name)
    {
        var s = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).Trim('_', '.');
        return s.Length == 0 ? "server" : s.Length > 60 ? s[..60] : s;
    }

    public static void PruneLogs(string dir, long maxBytes, string? keep = null) =>
        PruneLogs(dir, maxBytes, keep == null ? Array.Empty<string>() : new[] { keep }, int.MaxValue);

    // Löscht die ältesten *.log im Ordner, bis alle zusammen unter dem Limit (Größe und Anzahl) liegen; die Dateien in keep bleiben
    public static void PruneLogs(string dir, long maxBytes, IEnumerable<string> keep, int maxFiles)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var keepSet = keep.Where(k => !string.IsNullOrEmpty(k)).Select(k => { try { return Path.GetFullPath(k); } catch { return k; } })
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new DirectoryInfo(dir).GetFiles("*.log").OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            int count = files.Count;
            foreach (var f in files)
            {
                if (total <= maxBytes && count <= maxFiles) break;
                if (keepSet.Contains(f.FullName)) continue;
                try { long len = f.Length; f.Delete(); total -= len; count--; } catch { }
            }
        }
        catch { }
    }

    // Startet den Prozess ohne Shell und ohne Fenster. Die Ausgabe (stdout/stderr) wird asynchron in einen begrenzten Ringpuffer gelesen
    // (für die Fehlermeldung bei einem frühen Absturz); der Server schreibt sein Log selbst über --log-file. Wirft bei Startfehlern.
    public static LaunchedServer Start(LaunchPlan plan, IPlatform? platform = null)
    {
        if (!IsStartableProgram(plan.Program)) throw new InvalidOperationException(
            Path.IsPathFullyQualified(plan.Program ?? "") ? $"Program not found: {plan.Program}" : "The program must be a full path to an existing file.");
        var dir = Path.GetDirectoryName(plan.LogFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(plan.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        if (plan.Env != null)
            foreach (var (k, v) in plan.Env)
                if (v != CmdLine.Redacted) psi.Environment[k] = v;
        if (!string.IsNullOrEmpty(plan.WorkingDir)) psi.WorkingDirectory = plan.WorkingDir;
        else if (Path.GetDirectoryName(plan.Program) is { Length: > 0 } pd) psi.WorkingDirectory = pd;

        var ls = new LaunchedServer
        {
            Name = plan.Name, Program = plan.Program, Args = plan.Args, WorkingDir = plan.WorkingDir, Port = plan.Port, Host = plan.Host,
            LogFile = plan.LogFile, ProfileId = plan.ProfileId,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => ls.Output.Add(e.Data);
        p.ErrorDataReceived += (_, e) => ls.Output.Add(e.Data);
        p.Exited += (_, _) =>
        {
            // Die letzten Ausgabezeilen eintreffen lassen, bevor der Fehlertext zusammengestellt wird
            try { p.WaitForExit(); } catch { }
            int? code = null;
            try { code = p.ExitCode; } catch { }
            ls.MarkExited(code);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        ls.Proc = p;
        ls.Pid = p.Id;
        ls.StartTicks = platform?.ProcessStartTicks(p.Id) ?? 0;
        return ls;
    }
}

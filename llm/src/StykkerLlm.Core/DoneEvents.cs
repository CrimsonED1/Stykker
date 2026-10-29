using StykkerLlm.Core.Eval;

namespace StykkerLlm.Core;

// Lange Läufe, die fertig geworden sind (Testwarteschlange, Benchmark): der neueste als Meldung für den Browser-Tab
// (TabStatus.Alert). Wer eine Stunde Modelltests startet, schaut nicht die ganze Zeit hin.
public sealed record DoneEvent(string Kind, string Title, string Body, DateTime When);

public sealed class DoneEvents
{
    private readonly object _gate = new();
    private DoneEvent? _last;
    private bool _queueWasRunning;
    private DateTime _queueSince;

    public DoneEvent? Last { get { lock (_gate) return _last; } }

    // Benchmark fertig (BenchmarkService.Finished): Kennzahl Chat-t/s, sonst die beste Kontextstufe; Rückgang dazu
    public void Bench(BenchResult r, BenchRegression? regression, DateTime now)
    {
        var steps = r.Steps.Where(s => s.Ok && s.GenTps > 0).ToList();
        double? tps = steps.FirstOrDefault(s => s.Kind == "chat")?.GenTps ?? (steps.Count > 0 ? steps.Max(s => s.GenTps) : null);
        var body = Strings.DoneBenchBody(r.DurationSec, tps, regression?.DropPct);
        Set(new DoneEvent("bench", Strings.DoneBenchTitle(r.Title), body, now));
    }

    // Testwarteschlange, jeden Takt: aus „läuft“ wird „läuft nicht“ → eine Meldung über die Aufträge, die seit dem Start
    // fertig wurden. Abgebrochene Aufträge zählen nicht als fertig; ohne fertigen Auftrag keine Meldung.
    public void Queue(bool running, IEnumerable<EvalJob> jobs, DateTime now)
    {
        bool ended;
        DateTime since;
        lock (_gate)
        {
            if (running && !_queueWasRunning) _queueSince = now;
            ended = _queueWasRunning && !running;
            _queueWasRunning = running;
            since = _queueSince;
        }
        if (!ended) return;
        var finished = jobs.Where(j => j.Finished is DateTime f && f >= since).ToList();
        int done = finished.Count(j => j.State == EvalJobState.Done), failed = finished.Count(j => j.State == EvalJobState.Failed);
        if (done + failed == 0) return;
        var scores = finished.Where(j => j.State == EvalJobState.Done).Select(j => j.Score).Where(s => s > 0).ToList();
        Set(new DoneEvent("tests", Strings.DoneTestsTitle, Strings.DoneTestsBody(done, failed, scores.Count > 0 ? scores.Average() : null), now));
    }

    // Jeder neue fertige Lauf (für die Ereignisliste ActivityLog)
    public event Action<DoneEvent>? Added;

    private void Set(DoneEvent e)
    {
        lock (_gate) _last = e;
        Added?.Invoke(e);
    }
}

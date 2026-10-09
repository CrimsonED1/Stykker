using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerLlm.Core.Eval;

// Test-Suite für lokale Modelle: Aufgaben (Coding, Rechnen/Logik, Formattreue, Werkzeugaufrufe, langer Kontext) mit
// automatischer Bewertung. Eine Suite ist eine JSON-Datei; die eingebaute liegt als Ressource im Core (suite-basic.json),
// eigene liegen im Datenordner unter eval\*.json. Ein Lauf wird unter eval-results\ gespeichert und lässt sich vergleichen.

public sealed class EvalSuite
{
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public double Temperature { get; set; } = 0.2;
    public int Seed { get; set; } = 42;
    public List<EvalTask> Tasks { get; set; } = new();
}

public sealed class EvalTask
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";       // coding | reasoning | format | tools | context (frei wählbar)
    public string Title { get; set; } = "";
    public string? System { get; set; }
    public string Prompt { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public double Points { get; set; } = 1;
    // Werkzeuge im OpenAI-Format (unverändert an den Server)
    public JsonElement? Tools { get; set; }
    // Langer Kontext: so viele Token Fülltext werden vor dem Prompt eingesetzt; {needle} im Fülltext an Position NeedleAt (0..1)
    public int FillerTokens { get; set; }
    public string? Needle { get; set; }
    public double NeedleAt { get; set; } = 0.5;
    // Mehrere Fakten im Fülltext (zusätzlich zu Needle), je mit Position 0..1 in NeedlesAt
    public List<string> Needles { get; set; } = new();
    public List<double> NeedlesAt { get; set; } = new();
    // Tool-Ketten: feste Ergebnisse je Werkzeugname; ruft das Modell ein Werkzeug auf, bekommt es dieses Ergebnis und darf weiterarbeiten
    public Dictionary<string, string> ToolResults { get; set; } = new();
    public int MaxRounds { get; set; } = 4;
    // Agent-Aufgaben: Dateien, die vor dem Lauf im Wegwerf-Ordner liegen (relativer Pfad → Inhalt)
    public Dictionary<string, string> Files { get; set; } = new();
    public EvalCheck Check { get; set; } = new();
}

// Bewertung einer Antwort. Typen:
//   exact    – Antwort (ohne Denkteil, Satzzeichen, Anführungszeichen, ** am Rand) gleich einem der Expected (ohne Groß/klein)
//   number   – erste Zahl der Antwort gleich Expected[0] (Toleranz Tolerance)
//   regex    – Pattern passt auf die Antwort (Optionen: Multiline, IgnoreCase wenn IgnoreCase)
//   contains – alle Expected kommen vor (ohne Groß/klein)
//   json     – Antwort ist ein JSON-Objekt (auch in ```json```), Keys vorhanden, Values (Text-Vergleich) stimmen
//   python   – Codeblock der Antwort + Tests ausführen (Python, Zeitlimit); bestanden, wenn der Prozess mit 0 endet
//   tool     – erster Werkzeugaufruf heißt Tool, Args (Teilmenge, Text enthält Wert, ohne Groß/klein) stimmen
//   notool   – kein Werkzeugaufruf; Pattern (falls gesetzt) passt, alle Expected kommen in der Antwort vor
//   pycheck  – Python-Tests prüfen die Antwort als Text: Variable `answer` (ohne Denkteil); bestanden, wenn alle asserts halten
//   toolchain – die Werkzeuge in Expected wurden in dieser Reihenfolge aufgerufen (ToolResults liefert die Antworten) und die
//              Schlussantwort passt auf Pattern
//   agent    – das Modell arbeitet mit echten Werkzeugen (read, list, write, edit, cmd/PowerShell) in einem Wegwerf-Ordner mit
//              Files; danach prüft Tests (Python, im Ordner) das Ergebnis. Braucht die Erlaubnis für Modell-Code (es führt Befehle aus)
public sealed class EvalCheck
{
    public string Type { get; set; } = "exact";
    public List<string> Expected { get; set; } = new();
    public string? Pattern { get; set; }
    public bool IgnoreCase { get; set; } = true;
    public double Tolerance { get; set; }
    public string? Tests { get; set; }
    public int TimeoutSec { get; set; } = 10;
    public string? Tool { get; set; }
    public Dictionary<string, string> Args { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();
}

public sealed class EvalTaskResult
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public double Points { get; set; } = 1;
    public double Score { get; set; }                 // 0..1
    public bool Passed => Score >= 0.999;
    public bool Skipped { get; set; }                 // nicht bewertet (z. B. Code ausführen abgelehnt, Kontext zu klein)
    public bool Error { get; set; }                   // Server-/Verbindungsfehler: zählt nicht in die Note (ist kein Fehler des Modells)
    public string FinishReason { get; set; } = "";
    public double PromptTps { get; set; }
    public string Note { get; set; } = "";            // kurzer Grund bei Fehlschlag
    public int PromptTokens { get; set; }
    public int GenTokens { get; set; }
    public double Seconds { get; set; }
    public double GenTps { get; set; }
    public int Rounds { get; set; } = 1;              // Anfragen bei Tool-Ketten
    public string Answer { get; set; } = "";          // Antwort ohne Denkteil, gekürzt (zum Nachsehen)
}

public sealed class EvalRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Started { get; set; }
    public double DurationSec { get; set; }
    public string Server { get; set; } = "";
    public string Url { get; set; } = "";
    // Rechner, auf dem das Modell lief (nicht der, von dem gestartet wurde) – damit Läufe verschiedener
    // Rechner vergleichbar sind. Bei einem entfernten Ziel der Host aus der URL, sonst dieser Rechner.
    public string Machine { get; set; } = "";
    public string Model { get; set; } = "";
    public string ModelFile { get; set; } = "";          // GGUF-Datei (stabiler Schlüssel für die Modell-Übersicht)
    public string Settings { get; set; } = "";           // z. B. "ctx 131072 · ngl 99 · fa on"
    public int Seed { get; set; }
    public string Gpu { get; set; } = "";
    public string Suite { get; set; } = "";
    public int SuiteVersion { get; set; }
    public double Temperature { get; set; }
    public bool Cancelled { get; set; }
    public int Repeat { get; set; } = 1;              // Nummer der Wiederholung (--repeat), 1 = erster Lauf
    public Guid Group { get; set; }                   // gleiche Gruppe = Wiederholungen desselben Aufrufs
    public List<EvalTaskResult> Tasks { get; set; } = new();

    // Prozent je Kategorie (nur bewertete Aufgaben, nach Punkten gewichtet)
    [JsonIgnore]
    public IReadOnlyList<(string Category, double Percent, int Passed, int Count)> Categories =>
        Tasks.Where(t => !t.Skipped && !t.Error).GroupBy(t => t.Category)
             .Select(g => (g.Key, g.Sum(t => t.Points) > 0 ? 100 * g.Sum(t => t.Score * t.Points) / g.Sum(t => t.Points) : 0, g.Count(t => t.Passed), g.Count()))
             .OrderBy(c => EvalSuites.CategoryOrder(c.Key)).ThenBy(c => c.Key).ToList();

    // Gesamtnote: Mittel der Kategorien (jede Kategorie zählt gleich viel)
    [JsonIgnore]
    public double Total => Categories.Count == 0 ? 0 : Categories.Average(c => c.Percent);

    [JsonIgnore]
    public string Title => string.IsNullOrEmpty(Model) ? Server : Model;
}

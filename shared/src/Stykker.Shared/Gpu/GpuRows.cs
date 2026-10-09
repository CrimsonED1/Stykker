namespace Stykker.Shared.Gpu;

// Eine Zeile eines Leistungsindikators für genau einen Prozess: Auslastung in Prozent oder Grafikspeicher in MB.
public sealed record GpuProcRow(int Pid, double Value);

// Die Auslastung einer Engine der Grafikkarte über alle Prozesse („GPU Engine" der Leistungsindikatoren,
// dieselbe Quelle wie die Anteile je Prozess). Engine ist die Art – „3D", „Compute", „Copy", „Video decode",
// „Video encode". Mehrere Engines können gleichzeitig laufen, die Summe darf also über 100 % liegen.
public sealed record GpuEngineRow(string Engine, double Percent);

// Ein Takt des GPU-Engine-Zählers: die Auslastung je Prozess und je Engine. Beides stammt aus derselben Abfrage
// und wird deshalb zusammen gelesen – eine zweite Abtastung desselben Raten-Zählers im selben Takt gäbe null.
public sealed record GpuUtilSample(IReadOnlyList<GpuProcRow> Processes, IReadOnlyList<GpuEngineRow> Engines);

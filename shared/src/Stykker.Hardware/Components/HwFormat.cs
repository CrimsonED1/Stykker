using System.Globalization;

namespace Stykker.Hardware.Components;

// Zahlen der Hardware-Seite: Größen in Zweierpotenzen, Raten je Sekunde, Prozent. Fehlt ein Wert, steht „–".
public static class HwFormat
{
    public const string Dash = "–";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Farben der Anteile je Programm (dieselben wie in StykkerSYS), aus den Tokens des Design-Systems.
    public static readonly string[] Palette =
    {
        "var(--acc)", "var(--second)", "var(--good)", "var(--warn)",
        "color-mix(in srgb,var(--acc),var(--bad))", "color-mix(in srgb,var(--second),var(--good))",
        "color-mix(in srgb,var(--warn) 60%,var(--bad))", "color-mix(in srgb,var(--acc) 60%,var(--ink))",
    };

    public static string Size(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return Dash;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(v >= 100 || u == 0 ? "0" : "0.0", Inv) + " " + units[u];
    }

    public static string Rate(double? bytesPerSec) => bytesPerSec is double b ? Size(b) + "/s" : Dash;

    public static string Pct(double? pct) => pct is double p ? p.ToString("0", Inv) + " %" : Dash;

    public static string Num(double value, string format = "0") => value.ToString(format, Inv);

    // Gigabyte: eine Nachkommastelle unter 10, sonst ganz
    public static string Gb(double? gb) => gb is double g ? g.ToString(g >= 10 ? "0" : "0.0", Inv) : Dash;

    // Speicher eines Programms (MB): ab 1 GB in GB, sonst in MB – als Zahl und Einheit getrennt (für hw-val)
    public static (string Value, string Unit) Mem(double mb) =>
        mb >= 1024 ? ((mb / 1024).ToString(mb >= 10240 ? "0" : "0.0", Inv), "GB") : (mb.ToString("0", Inv), "MB");

    public static string Mhz(double mhz) => mhz > 0 ? mhz.ToString("N0", Inv) + " MHz" : Dash;

    // Prozent für style (Punkt als Trennzeichen, egal welche Sprache der Server hat)
    public static string Css(double fraction) => (Math.Clamp(fraction, 0, 1) * 100).ToString("0.###", Inv) + "%";
}

using System.Globalization;

namespace StykkerCmd.UI.Text;

// Anzeigeformate in der Kultur des Systems.
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {Units[0]}"
            : $"{value.ToString("N1", CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static string Date(DateTimeOffset value)
        => value == DateTimeOffset.MinValue
            ? string.Empty
            : value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    public static string Count(int n, string singular, string plural)
        => $"{n} {(n == 1 ? singular : plural)}";
}

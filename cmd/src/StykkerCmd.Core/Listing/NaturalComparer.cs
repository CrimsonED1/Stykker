namespace StykkerCmd.Core.Listing;

// Vergleicht Namen ohne Groß-/Kleinschreibung und zählt Ziffernfolgen als Zahlen: "datei2" kommt vor "datei10".
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i, startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numberY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (numberX.Length != numberY.Length)
                    return numberX.Length.CompareTo(numberY.Length);

                int digits = numberX.SequenceCompareTo(numberY);
                if (digits != 0)
                    return digits;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0)
                    return c;
                i++;
                j++;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}

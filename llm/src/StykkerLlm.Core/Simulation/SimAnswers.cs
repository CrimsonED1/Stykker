namespace StykkerLlm.Core;

// Was die simulierten Server auf der Seite /prompt antworten (gestreamt): Markdown, wie echte Modelle es schreiben – mit
// Code-Block, wenn nach Code gefragt wird, sonst mit Liste, Tabelle und Hervorhebung. Nur für Debug-Builds mit --sim.
public static class SimAnswers
{
    public static string For(string ask)
    {
        var a = ask.ToLowerInvariant();
        if (a.Contains("code") || a.Contains("python") || a.Contains("function") || a.Contains("script") || a.Contains("c#"))
            return """"
                Here is a small **Python** function that reverses a string:

                ```python
                def reverse(text: str) -> str:
                    """Return text backwards."""
                    return text[::-1]


                print(reverse("stykker"))  # rekkyts
                ```

                How it works:

                1. `text[::-1]` is a slice with step `-1`, so it walks from the end to the start.
                2. Strings are immutable, so the slice returns a *new* string.

                For very long strings this is still `O(n)`.
                """";
        return """
            ## Why the sky looks blue

            Sunlight contains every colour. Air molecules scatter **short wavelengths** much more than long ones
            (Rayleigh scattering, roughly proportional to `1/λ⁴`).

            - Blue light (~450 nm) is scattered about **5×** more than red (~700 nm).
            - At sunset the light travels through more air, so the blue is scattered away and the sky turns *orange*.

            | Colour | Wavelength | Scattering |
            |---|---|---|
            | blue | 450 nm | strong |
            | red | 700 nm | weak |

            > Violet scatters even more, but our eyes are less sensitive to it.
            """;
    }

    // In Stücke von etwa einem Token: Wörter mit ihrem Leerraum, lange Wörter in Vierergruppen
    public static List<string> Pieces(string text)
    {
        var pieces = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && i - start < 4) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == start) i++;
            pieces.Add(text[start..i]);
        }
        return pieces;
    }
}

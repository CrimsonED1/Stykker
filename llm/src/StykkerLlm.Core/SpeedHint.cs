namespace StykkerLlm.Core;

// „Langsamer als sonst“ (docs/plan-ui-redesign.md, U11): der Schnitt dieses Laufs gegen den Schnitt aus dem Verlauf
// desselben Servers (gleiche Befehlszeile). Erst ab genug Messpunkten und einer deutlichen Abweichung, sonst flackert es.
public static class SpeedHint
{
    public const double Threshold = 0.15;      // ab 15 % langsamer
    public const int MinSamples = 20;          // Takte mit Last in diesem Lauf
    public const long MinUsualSamples = 60;    // Messpunkte im Verlauf

    // Wie viel Prozent langsamer (positiv), oder null, wenn kein Hinweis angebracht ist
    public static int? SlowerPercent(double runAvg, int runSamples, double usualAvg, long usualSamples)
    {
        if (runSamples < MinSamples || usualSamples < MinUsualSamples || usualAvg <= 0 || runAvg <= 0) return null;
        var drop = 1 - runAvg / usualAvg;
        return drop >= Threshold ? (int)Math.Round(drop * 100) : null;
    }
}

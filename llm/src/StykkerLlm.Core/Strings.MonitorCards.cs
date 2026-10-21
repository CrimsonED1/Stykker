namespace StykkerLlm.Core;

// Texte für die Kopfleiste und die Serverkarte (auftrag-llm-kopf-karten): Kurznamen unter den Kennzahlen,
// der Umschalter „Labels“ und der Kontext-Balken je Slot. Die übrigen Karten-Texte stehen in Strings.Card.cs.
public static partial class Strings
{
    // Kopfleiste: Einheit des freien RAM und Server-Zähler
    public const string HdrRamFree = "GB free";
    public static string HdrSrvChip(int n, int busy) => busy > 0 ? $"{n} srv · {busy} busy" : $"{n} srv";

    // Kurznamen unter den Kennzahlen der Serverkarte (nur mit dem Schalter „Labels“ sichtbar)
    public const string LblVram = "VRAM", LblRam = "RAM", LblCpu = "CPU", LblFile = "file", LblCtx = "ctx", LblSlots = "slots";
    public const string LblModels = "models", LblQueue = "queue", LblPeak = "peak t/s", LblAvg = "avg t/s", LblTokens = "tokens";
    public const string LblSpec = "spec", LblClients = "clients";

    // Umschalter im Menü ☰ → Ansicht
    public const string CardLabels = "Labels", CardLabelsTip = "Show a short name under each figure on the server cards";

    // Tooltip des Kontext-Balkens je Slot, z. B. „18.2k / 40k context used“
    public static string CtxFillTip(string used, string max) => $"{used} / {max} context used";
}

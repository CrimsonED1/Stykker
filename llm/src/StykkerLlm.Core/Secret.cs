namespace StykkerLlm.Core;

// Ein Geheimnis (API-Schlüssel), das nur im Speicher lebt und nur intern für HTTP-Anfragen gebraucht wird.
// ToString liefert nie den Wert, damit er nicht versehentlich in Texte, Logs oder JSON gerät.
public sealed class SecretValue
{
    private readonly string _value;
    public SecretValue(string value) => _value = value;
    public string Reveal() => _value;
    public override string ToString() => CmdLine.Redacted;
}

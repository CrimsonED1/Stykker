namespace StykkerCmd.Core.Model;

// Ein Datenträger für die Laufwerkstabs: Wurzelpfad ("C:\" bzw. "/") und Anzeigename (Datenträgername oder Typ).
public sealed record VolumeInfo(string Root, string Name);

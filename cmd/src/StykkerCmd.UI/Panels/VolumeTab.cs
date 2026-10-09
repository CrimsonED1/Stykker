namespace StykkerCmd.UI.Panels;

// Ein Laufwerkstab über dem Panel. IsCurrent markiert den Datenträger, auf dem der angezeigte Ordner liegt.
public sealed record VolumeTab(string Root, string Label, bool IsCurrent);

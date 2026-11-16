namespace StykkerFirewall.Service;

// Einstellungen aus appsettings.json, Umgebungsvariablen (STYKKERFW_…) oder der Befehlszeile (-backend simulator).
public sealed class ServiceOptions
{
    // Id aus dem BackendCatalog: "windows-firewall" (Standard) oder "simulator".
    public string Backend { get; set; } = "windows-firewall";

    // Leer = %ProgramData%\Stykker\Firewall
    public string? DataDir { get; set; }

    public string Pipe { get; set; } = Core.Ipc.PipeNames.Default;

    // Ruhezeit nach „Einmalig blockieren“, bevor dieselbe App wieder nachfragen darf.
    public int QuietSecondsAfterBlockOnce { get; set; } = 120;

    public string ResolveDataDir() => string.IsNullOrWhiteSpace(DataDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Stykker", "Firewall")
        : Path.GetFullPath(DataDir);
}

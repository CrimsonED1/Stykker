using Stykker.Pairing;

namespace StykkerLlm.Core;

// Die Kopplung von StykkerLLM an die gemeinsame Ablage (shared/Stykker.Pairing): eine pairing.dat für HUD, SYS und LLM.
// Hier entsteht der Dienst für einen Datenordner: die Ablage öffnen und die alten Dateien aus dem Datenordner (access.dat,
// hosts.dat) einmal übernehmen. Die Ablage merkt sich, was übernommen ist, so kommt nichts doppelt oder zurück.
public static class SharedPairing
{
    // Der Dienst für diesen Datenordner. file = Pfad aus --pairing-file (sonst AppPaths.PairingFile). now ist eine Uhr in
    // Ortszeit (Tests geben eine eigene hinein); der Dienst selbst rechnet in UTC.
    public static PairingService Open(AppPaths paths, IPlatform platform, string? file = null, Func<DateTime>? now = null)
    {
        var store = new PairingStore(paths.PairingFile(file));
        LegacyLlmImport.ImportFromFolder(store, paths.Root, new LegacyProtector(platform));
        Func<DateTime>? utc = now == null ? null : () => ToUtc(now());
        return new PairingService(store, utc);
    }

    // Zeiten: die Oberflächen rechnen in Ortszeit, die Ablage speichert UTC. Eine Zeit ohne Angabe der Zone gilt als
    // Ortszeit und kommt beim Zurückrechnen unverändert heraus.
    public static DateTime ToUtc(DateTime local) =>
        local.Kind == DateTimeKind.Utc ? local : DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    public static DateTime ToLocal(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local || utc == DateTime.MinValue) return utc;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
    }

    // Die alten Dateien hat StykkerLLM über die Plattform geschützt (DPAPI mit "StykkerLLM.confirm.v1")
    private sealed class LegacyProtector(IPlatform platform) : IDataProtector
    {
        public byte[]? Protect(byte[] data) => platform.ProtectForCurrentUser(data);
        public byte[]? Unprotect(byte[] data) => platform.UnprotectForCurrentUser(data);
    }
}

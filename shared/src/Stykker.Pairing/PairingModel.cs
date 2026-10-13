namespace Stykker.Pairing;

// Rollen eines Geräts in einem Modul: Admin darf alles wie das Fenster, Viewer sieht nur zu.
public static class PairingRoles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
    // Frühere Rolle eines koppelnden Hubs in StykkerLLM (entfernt): solche Geräte dürfen nur noch lesen
    public const string LegacyHub = "hub";

    // Ein Modul, für das ein Gerät keine Rolle hat, sieht es als Viewer: ein gekoppeltes Gerät darf in einem
    // neuen Modul zusehen, aber nicht automatisch schalten (in StykkerSYS etwa Prozesse beenden).
    public const string DefaultForNewModule = Viewer;

    public static bool IsValid(string? role) => role is Admin or Viewer;

    // Für Werte aus einer access.dat von StykkerLLM: fehlend bedeutet Admin (Dateien aus der Zeit vor den Rollen),
    // ein alter Hub nur Viewer.
    public static string NormalizeLegacy(string? role) => role switch { Viewer => Viewer, LegacyHub => Viewer, _ => Admin };

    public static bool CanWrite(string? role) => role == Admin;
}

public static class DeviceKinds
{
    public const string Browser = "browser";   // Browser, Handy, Fenster
    public const string Host = "host";         // ein Model-Host: meldet sich mit seinem Token, hat keine Rollen

    public static string Normalize(string? kind) => kind == Host ? Host : Browser;
}

// Ein gekoppeltes Gerät. Gespeichert wird nur der Hash des Tokens, nie das Token selbst: eine kopierte pairing.dat
// erlaubt keine Anmeldung.
public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = DeviceKinds.Browser;
    public DateTime Created { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? Address { get; set; }        // letzte IP, nur zur Anzeige
    // Modul-Id ("llm", "sys", …) -> Rolle. Fehlt ein Modul, gilt PairingRoles.DefaultForNewModule.
    public Dictionary<string, string> Roles { get; set; } = new();
    // Nur in pairing.dat, nie im Zustand für die Oberflächen
    public string Hash { get; set; } = "";

    public string RoleIn(string module) =>
        Roles.TryGetValue(module, out var role) && PairingRoles.IsValid(role) ? role : PairingRoles.DefaultForNewModule;
}

// Eine Anfrage "andersherum" (wie beim Fernseher): das neue Gerät zeigt sechs Ziffern, ein angemeldetes Gerät tippt
// sie ein und gibt es frei. In der Ablage stehen nur Hashes: der Hash des Secrets, mit dem das neue Gerät nachfragt,
// und der Hash des Tokens, das es bei Freigabe bekommt. Das Token selbst hält nur der Prozess, der die Anfrage
// angenommen hat. So kann ein anderer Prozess (die Zentrale) freigeben, ohne dass je ein Token auf der Platte liegt.
public sealed class PairRequest
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Module { get; set; } = "";
    public string? Address { get; set; }
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
    public string Status { get; set; } = PairStatus.Pending;
    public string? DeviceId { get; set; }
    public string SecretHash { get; set; } = "";
    public string TokenHash { get; set; } = "";
}

public static class PairStatus
{
    public const string Pending = "pending", Approved = "approved", Denied = "denied", Expired = "expired", Unknown = "unknown";
}

// Der Inhalt von pairing.dat.
public sealed class PairingState
{
    public int Version { get; set; } = 1;
    // Netzschalter "Home/VPN" aus StykkerLLM. Er zieht mit stykker.json in die Konfiguration um; bis dahin steht er hier.
    public bool RemoteEnabled { get; set; }
    public string Code { get; set; } = "";
    public DateTime CodeCreated { get; set; }     // der Code gilt CodeLifetime lang und nur für eine Anmeldung
    public int CodeWrongTries { get; set; }       // in der Ablage, damit mehrere Prozesse gemeinsam zählen
    public List<PairedDevice> Devices { get; set; } = new();
    public List<PairRequest> Requests { get; set; } = new();
    // Quellen, die schon übernommen wurden ("llm:access", "llm:hosts"): ein zentral entferntes Gerät kommt
    // beim nächsten Start nicht aus der alten Datei zurück.
    public List<string> Imported { get; set; } = new();
}

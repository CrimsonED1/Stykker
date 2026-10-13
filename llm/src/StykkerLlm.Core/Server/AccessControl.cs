using System.Text;
using Stykker.Pairing;

namespace StykkerLlm.Core;

// Die Namen aus der gemeinsamen Bibliothek, die hier doppelt vorkommen: Anfragen und Status gibt es in StykkerLLM
// und in Stykker.Pairing. Hier heißt die Anfrage der Bibliothek SharedRequest.
using SharedRequest = Stykker.Pairing.PairRequest;

// Die Rolle eines angemeldeten Geräts: Admin darf alles wie das Fenster, Viewer sieht nur zu.
// Vor den Rollen war jedes Gerät Admin, deshalb ist Admin auch der Wert für eine Datei ohne Rollenfeld.
public static class AccessRole
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
    // Frühere Rolle eines koppelnden Hubs (alte Node-Kopplung, entfernt): solche Geräte dürfen nur noch lesen
    public const string LegacyHub = "hub";

    public static bool IsValid(string? role) => role is Admin or Viewer;

    // Für Werte aus der Datei: fehlend bedeutet Admin (ältere Dateien), ein alter Hub nur Viewer
    public static string Normalize(string? role) => role switch { Viewer => Viewer, LegacyHub => Viewer, _ => Admin };

    public static bool CanWrite(string? role) => Normalize(role) != Viewer;
}

// Ein Gerät, das sich angemeldet hat (Browser im Heimnetz, Handy, Fenster), so wie StykkerLLM es zeigt: die Rolle in
// diesem Modul und Zeiten in Ortszeit. Den Hash des Cookies hält nur die Ablage.
public sealed class AccessDevice
{
    private string _role = AccessRole.Admin;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? Address { get; set; }        // letzte IP, nur zur Anzeige
    public string Role { get => _role; set => _role = AccessRole.Normalize(value); }
}

public static class PairKinds
{
    public const string Browser = "browser";
    public static string Normalize(string? kind) => Browser;
}

public static class PairStatus
{
    public const string Pending = "pending", Approved = "approved", Denied = "denied", Expired = "expired", Unknown = "unknown";
}

// Eine Kopplungsanfrage "andersherum" in der Sicht von StykkerLLM. Die Anfrage selbst liegt in der gemeinsamen Ablage;
// das Token holt sich das neue Gerät nur bei dem Prozess ab, der die Anfrage angenommen hat.
public sealed class PairRequest
{
    public string Id { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = PairKinds.Browser;
    public string? Address { get; init; }
    public DateTime Created { get; init; }
    public DateTime Expires { get; init; }
    public string Status { get; init; } = PairStatus.Pending;
    public string? DeviceId { get; init; }
}

// Zugang zu StykkerLLM: der Schalter "Home/VPN", der Zugangscode für Browser und die Geräte. Die Daten liegen in der
// gemeinsamen Ablage pairing.dat (Stykker.Pairing), die HUD und SYS mitbenutzen. Diese Klasse übersetzt nur: Ein Gerät
// hat je Modul eine Rolle, hier zählt die Rolle für "llm".
//
// Zwei Wege zur Anmeldung, beide mit sechs Ziffern und QR-Code:
//  1. Der PC zeigt den Code, das neue Gerät tippt oder scannt ihn (Pair).
//  2. Das neue Gerät zeigt einen Code, ein angemeldetes Gerät gibt ihn frei (RequestPairing → Approve → Poll).
public sealed class AccessControl
{
    // Name der alten Datei aus der Zeit vor der gemeinsamen Ablage (nur noch für die Übernahme gelesen)
    public const string FileName = LegacyLlmImport.AccessFile;
    public const int CodeLength = PairingCodes.CodeLength;
    public static readonly TimeSpan CodeLifetime = PairingCodes.CodeLifetime;
    public const int MaxWrongTries = PairingCodes.MaxWrongTries;
    public const int MaxPendingRequests = PairingCodes.MaxPendingRequests;

    private readonly PairingService _pairing;
    private readonly object _lock = new();
    private string _signature = "";

    public event Action? Changed;

    public AccessControl(AppPaths paths, IPlatform platform, Func<DateTime>? now = null)
        : this(SharedPairing.Open(paths, platform, null, now)) { }

    // Ein fertiger Dienst: Server und Hosts teilen sich einen, damit sie dieselbe Ablage benutzen
    public AccessControl(PairingService pairing)
    {
        _pairing = pairing;
        _pairing.Changed += () => Changed?.Invoke();
        _ = Code;   // beim ersten Start gibt es einen Code, damit der Browser sich anmelden kann
        _signature = Signature();
    }

    public string FilePath => _pairing.Store.FilePath;

    public bool RemoteEnabled => _pairing.RemoteEnabled;

    public string Code => Guard(() => _pairing.Code, "");

    // Bis wann der angezeigte Code gilt (Fenster, TUI und Web zeigen die Restzeit)
    public DateTime CodeExpires => SharedPairing.ToLocal(_pairing.CodeExpires);

    public IReadOnlyList<AccessDevice> Devices => _pairing.Devices.Select(ToDevice).ToList();

    public int DeviceCount => _pairing.DeviceCount;

    public static string NewCode() => PairingCodes.NewCode();

    // Eingaben wie "482 913" oder "482-913" gelten wie "482913"
    public static string CleanCode(string? code) => PairingCodes.CleanCode(code);

    // "482 913" zum Ablesen
    public static string Pretty(string? code) => PairingCodes.Pretty(code);

    public static string NewToken() => PairingCodes.NewToken();

    public static string Hash(string token) => PairingCodes.Hash(token);

    // Im Takt des Servers: abgelaufenen Code ersetzen, alte Kopplungsanfragen aufräumen. Hat ein anderer Prozess die Ablage
    // geändert (die Zentrale hat ein Gerät entfernt), meldet das Changed, damit die Seiten neu zeichnen.
    // Liefert true, wenn diese Sitzung etwas geändert hat.
    public bool Maintain()
    {
        bool changed = Guard(() => _pairing.Maintain(), false);
        var now = Signature();
        bool foreign;
        lock (_lock) { foreign = now != _signature; _signature = now; }
        if (foreign && !changed) Changed?.Invoke();
        return changed;
    }

    // Schalter "Home/VPN": aus = nur dieser PC (der Server nimmt dann nur Zugriffe von 127.0.0.1 an), an = ganzes Netz
    public void SetRemote(bool on) => Try(() => _pairing.SetRemote(on));

    // Neuer Code (abgelaufen, verbraucht, zu oft falsch, oder jemand hat den QR-Code gesehen). Angemeldete Geräte
    // bleiben angemeldet – wer sie nicht mehr haben will, entfernt sie in der Geräteliste.
    public string RotateCode() => Guard(() => _pairing.RotateCode(), Code);

    // Anmeldung mit dem Code: liefert das Cookie (Token) für das Gerät, oder null bei falschem oder abgelaufenem Code.
    // Der Code ist danach verbraucht (der nächste steht sofort in Fenster, TUI und Web). kind wird nicht mehr gebraucht:
    // jede Anmeldung ist ein Browser.
    public (string Token, AccessDevice Device)? Pair(string? code, string? name, string? address, string? kind = null)
    {
        var paired = Guard<(string Token, PairedDevice Device)?>(() => _pairing.Pair(code, name, address, LegacyLlmImport.Module), null);
        return paired is { } p ? (p.Token, ToDevice(p.Device)) : null;
    }

    // Ein Gerät ohne Code anlegen – nur für Aufrufer, die den Schlüssel des Datenordners haben (die Fenster-Hülle auf
    // diesem PC). Liefert das Token einmal; gespeichert wird nur sein Hash. null = die Ablage ist gerade gesperrt.
    public (string Token, AccessDevice Device)? AddLocalDevice(string? name, string? address)
    {
        var added = Guard<(string Token, PairedDevice Device)?>(() => _pairing.AddLocalDevice(name, address, LegacyLlmImport.Module), null);
        return added is { } a ? (a.Token, ToDevice(a.Device)) : null;
    }

    // Token prüfen und die Rolle des Geräts in diesem Modul liefern (null = kein gültiges Cookie).
    public string? RoleOf(string? token, string? address = null) =>
        Guard<string?>(() => _pairing.RoleOf(token, LegacyLlmImport.Module, address), null);

    public bool Validate(string? token, string? address = null) => RoleOf(token, address) != null;

    // Rolle eines Geräts ändern. Unbekannte Rollen und unbekannte Geräte werden abgewiesen (Rückgabe false);
    // steht die Rolle schon so drin, gilt das als Erfolg – die Aktion bleibt damit wiederholbar.
    public bool SetRole(string id, string? role) =>
        Guard(() => _pairing.SetRole(id, LegacyLlmImport.Module, role), false);

    // Gerät entfernen (in allen Modulen, weil die Kopplung für alle gilt)
    public bool RemoveDevice(string id) => Guard(() => _pairing.RemoveDevice(id), false);

    // Gerät mit diesem Cookie abmelden (nur die eigene Sitzung)
    public bool RemoveToken(string? token) => Guard(() => _pairing.RemoveToken(token), false);

    // ── Kopplung andersherum: das neue Gerät zeigt den Code, ein angemeldetes gibt frei ──

    public IReadOnlyList<PairRequest> PendingRequests => _pairing.PendingRequests.Select(ToRequest).ToList();

    // Neue Anfrage (ohne Anmeldung erreichbar). Liefert die Anfrage und das Secret zum Nachfragen, oder null,
    // wenn schon zu viele offen sind oder die Ablage gerade gesperrt ist.
    public (PairRequest Request, string Secret)? RequestPairing(string? name, string? kind, string? address)
    {
        var made = Guard<(SharedRequest Request, string Secret)?>(() => _pairing.RequestPairing(name, address, LegacyLlmImport.Module), null);
        return made is { } m ? (ToRequest(m.Request), m.Secret) : null;
    }

    // Freigabe mit den Ziffern, die das neue Gerät zeigt. Es bekommt die gewünschte Rolle (Vorgabe Admin);
    // null = keine offene Anfrage mit diesem Code. Das Token holt sich das neue Gerät über Poll.
    public PairRequest? Approve(string? code, string? role = null)
    {
        var req = Guard<SharedRequest?>(() => _pairing.Approve(code, role), null);
        return req is { } r ? ToRequest(r) : null;
    }

    public bool Deny(string? id) => Guard(() => _pairing.Deny(id), false);

    // Das neue Gerät fragt nach: Status und – genau einmal – das Cookie. Ohne passendes Secret gibt es nichts.
    public (string Status, string? Token) Poll(string? id, string? secret) =>
        Guard<(string Status, string? Token)>(() => _pairing.Poll(id, secret), (PairStatus.Unknown, null));

    // Ein Stand, der sich ändert, wenn sich die Ablage ändert (auch von einem anderen Prozess): Code, Schalter,
    // Geräte mit Rolle und Namen, offene Anfragen. Für Maintain, damit Änderungen von außen gemeldet werden.
    private string Signature()
    {
        var state = _pairing.Store.Read();
        var sb = new StringBuilder();
        sb.Append(state.Code).Append('|').Append(state.RemoteEnabled).Append('|');
        foreach (var d in state.Devices.Where(d => d.Kind != DeviceKinds.Host))
            sb.Append(d.Id).Append(':').Append(d.RoleIn(LegacyLlmImport.Module)).Append(':').Append(d.Name).Append(';');
        foreach (var r in state.Requests) sb.Append(r.Id).Append(':').Append(r.Status).Append(';');
        return sb.ToString();
    }

    private static AccessDevice ToDevice(PairedDevice d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Created = SharedPairing.ToLocal(d.Created),
        LastSeen = d.LastSeen is { } seen ? SharedPairing.ToLocal(seen) : null,
        Address = d.Address,
        Role = d.RoleIn(LegacyLlmImport.Module),
    };

    private static PairRequest ToRequest(SharedRequest r) => new()
    {
        Id = r.Id,
        Code = r.Code,
        Name = r.Name,
        Kind = PairKinds.Browser,
        Address = r.Address,
        Created = SharedPairing.ToLocal(r.Created),
        Expires = SharedPairing.ToLocal(r.Expires),
        Status = r.Status,
        DeviceId = r.DeviceId,
    };

    // Eine Sperre der Ablage (zehn Sekunden, ein anderer Prozess schreibt gerade) oder ein Schreibfehler lässt nur diesen
    // Aufruf scheitern, nicht den Server.
    private static T Guard<T>(Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("pairing store busy or unavailable: " + ex.GetType().Name);
            return fallback;
        }
    }

    private static void Try(Action call) => Guard(() => { call(); return true; }, false);
}

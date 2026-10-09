using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerLlm.Core;

// Die Rolle eines angemeldeten Geräts: Admin darf alles wie das Fenster, Viewer sieht nur zu.
// Vor den Rollen war jedes Gerät Admin, deshalb ist Admin auch der Wert für access.dat ohne Rollenfeld.
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

// Ein Gerät, das sich mit dem Zugangscode angemeldet hat (Browser im Heimnetz, Handy, Fenster).
// Gespeichert wird nur der Hash des Cookies, nie das Cookie selbst: ein kopiertes access.dat erlaubt kein Mithören.
public sealed class AccessDevice
{
    private string _role = AccessRole.Admin;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? Address { get; set; }        // letzte IP, nur zur Anzeige
    public string Role { get => _role; set => _role = AccessRole.Normalize(value); }
    // Nur in access.dat, nie im Zustand für die Oberflächen (StateJson schreibt Name, Zeit und IP, keinen Hash)
    public string Hash { get; set; } = "";
}

public sealed class AccessState
{
    public bool RemoteEnabled { get; set; }
    public bool TailscaleEnabled { get; set; }
    public string Code { get; set; } = "";
    public DateTime CodeCreated { get; set; }     // der Code gilt CodeLifetime lang und nur für eine Anmeldung
    public List<AccessDevice> Devices { get; set; } = new();
}

// Kopplung andersherum (wie beim Fernseher): das neue Gerät zeigt sechs Ziffern, ein schon angemeldetes Gerät
// (Fenster, TUI, Web) tippt sie ein und gibt es damit frei. Das neue Gerät fragt mit Id und Secret nach, bis das
// Cookie bereitliegt. Anfragen leben nur im Speicher des Servers und verfallen nach CodeLifetime.
public sealed class PairRequest
{
    public string Id { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = PairKinds.Browser;
    public string? Address { get; init; }
    public DateTime Created { get; init; }
    public DateTime Expires { get; init; }
    public string Status { get; internal set; } = PairStatus.Pending;
    public string? DeviceId { get; internal set; }
    internal string SecretHash { get; init; } = "";
    internal string? Token { get; set; }
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

// Zugang zum Server: der Schalter "Home/VPN", der Zugangscode für Browser und der Liste der angemeldeten Geräte.
// Die Datei access.dat im Datenordner enthält den Code und die Geräte; sie wird an den Windows-Benutzer gebunden (DPAPI CurrentUser),
// damit ein kopierter Datenordner nicht reicht. Wo das nicht geht (Linux), wird sie im Klartext abgelegt – dort schützt ohnehin
// nur das Heimnetz. Fenster, TUI und Web zeigen dieselben Werte, weil es nur eine Datei gibt.
//
// Zwei Wege zur Anmeldung, beide mit sechs Ziffern und QR-Code:
//  1. Der PC zeigt den Code, das neue Gerät tippt oder scannt ihn (Pair).
//  2. Das neue Gerät zeigt einen Code, ein angemeldetes Gerät gibt ihn frei (RequestPairing → Approve → Poll).
public sealed class AccessControl
{
    public const string FileName = "access.dat";
    // Sechs Ziffern wie beim Fernseher: am Telefon schnell getippt. Kurz gültig und nach einer Anmeldung verbraucht,
    // nach MaxWrongTries Fehlversuchen gibt es einen neuen – so bleibt Raten aussichtslos (1 : 1 000 000 je Versuch).
    public const int CodeLength = 6;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public const int MaxWrongTries = 5;
    public const int MaxPendingRequests = 8;
    private const int TokenBytes = 32;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IPlatform _platform;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly AccessState _state = new();
    private readonly List<PairRequest> _requests = new();
    private int _wrongTries;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    public string FilePath { get; }
    public event Action? Changed;

    public AccessControl(AppPaths paths, IPlatform platform, Func<DateTime>? now = null)
    {
        FilePath = Path.Combine(paths.Root, FileName);
        _platform = platform;
        _now = now ?? (() => DateTime.Now);
        Load();
    }

    public bool RemoteEnabled { get { lock (_lock) return _state.RemoteEnabled; } }
    // Schalter Tailscale: lässt von außen nur Geräte im Tailnet zu, nicht das ganze Netz (dafür ist Home/VPN da)
    public bool TailscaleEnabled { get { lock (_lock) return _state.TailscaleEnabled; } }

    public string Code
    {
        get { lock (_lock) return _state.Code; }
    }

    // Bis wann der angezeigte Code gilt (Fenster, TUI und Web zeigen die Restzeit)
    public DateTime CodeExpires { get { lock (_lock) return _state.CodeCreated + CodeLifetime; } }

    private bool CodeExpired => _now() >= _state.CodeCreated + CodeLifetime;

    public IReadOnlyList<AccessDevice> Devices
    {
        get { lock (_lock) return _state.Devices.ToList(); }
    }

    // Beim ersten Start gibt es einen Code, damit der Browser sich anmelden kann (noch ohne Netzwerkzugriff von außen).
    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var raw = File.ReadAllText(FilePath).Trim();
                var json = raw.StartsWith("{", StringComparison.Ordinal) ? raw : Encoding.UTF8.GetString(Unprotect(raw));
                var loaded = JsonSerializer.Deserialize<AccessState>(json, Json);
                if (loaded != null)
                {
                    _state.RemoteEnabled = loaded.RemoteEnabled;
                    _state.TailscaleEnabled = loaded.TailscaleEnabled;
                    _state.Code = loaded.Code?.Trim() ?? "";
                    _state.CodeCreated = loaded.CodeCreated;
                    _state.Devices = loaded.Devices ?? new List<AccessDevice>();
                    _state.Devices.RemoveAll(d => string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Hash));
                    // Codes aus der Zeit vor den sechs Ziffern (acht Zeichen, ohne Ablauf) werden ersetzt
                    if (!IsDigits(_state.Code))
                    {
                        _state.Code = NewCode();
                        _state.CodeCreated = _now();
                        Write();
                    }
                    return;
                }
            }
        }
        catch { /* beschädigt: neuer Code, Geräte werden neu angemeldet */ }
        _state.Code = NewCode();
        _state.CodeCreated = _now();
        Write();
    }

    private static bool IsDigits(string s) => s.Length == CodeLength && s.All(char.IsAsciiDigit);

    // Eingaben wie "482 913" oder "482-913" gelten wie "482913"
    public static string CleanCode(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    // "482 913" zum Ablesen
    public static string Pretty(string? code) => code is { Length: CodeLength } ? code[..3] + " " + code[3..] : code ?? "";

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    // Im Takt des Servers (nur dort – Fenster und TUI lesen bloß): abgelaufenen Code ersetzen, alte Anfragen aufräumen.
    // Liefert true, wenn sich etwas geändert hat.
    public bool Maintain()
    {
        bool rotate, pruned;
        lock (_lock)
        {
            rotate = CodeExpired;
            pruned = PruneRequests();
        }
        if (rotate) RotateCode();
        else if (pruned) Changed?.Invoke();
        return rotate || pruned;
    }

    public void SetTailscale(bool on)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state.TailscaleEnabled != on;
            _state.TailscaleEnabled = on;
            if (changed) _dirty = true;
        }
        if (changed) { Write(); Changed?.Invoke(); }
    }

    // Darf dieser Absender von außen auf die Oberfläche? Home/VPN: alle Geräte im Netz. Tailscale: nur das Tailnet.
    public bool AllowsRemote(System.Net.IPAddress address) =>
        RemoteEnabled || (TailscaleEnabled && NetAddr.IsTailscale(address.ToString()));

    // Schalter "Home/VPN": aus = nur dieser PC (der Server nimmt dann nur Zugriffe von 127.0.0.1 an), an = ganzes Netz
    public void SetRemote(bool on)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state.RemoteEnabled != on;
            _state.RemoteEnabled = on;
            if (changed) _dirty = true;
        }
        if (changed) { Write(); Changed?.Invoke(); }
    }

    // Neuer Code (abgelaufen, verbraucht, zu oft falsch, oder jemand hat den QR-Code gesehen). Angemeldete Geräte
    // bleiben angemeldet – wer sie nicht mehr haben will, entfernt sie in der Geräteliste.
    public string RotateCode()
    {
        string code;
        lock (_lock)
        {
            code = _state.Code = NewCode();
            _state.CodeCreated = _now();
            _wrongTries = 0;
            _dirty = true;
        }
        Write();
        Changed?.Invoke();
        return code;
    }

    // Anmeldung mit dem Code: liefert das Cookie (Token) für das Gerät, oder null bei falschem oder abgelaufenem Code.
    // Der Code ist danach verbraucht (der nächste steht sofort in Fenster, TUI und Web). 
    public (string Token, AccessDevice Device)? Pair(string? code, string? name, string? address, string? kind = null)
    {
        var clean = CleanCode(code);
        if (clean.Length == 0) return null;
        var (token, device) = NewDevice(name, address, AccessRole.Admin);
        bool ok, burn;
        lock (_lock)
        {
            ok = !CodeExpired && SameCode(clean, _state.Code);
            if (ok) _state.Devices.Add(device);
            else _wrongTries++;
            burn = ok || _wrongTries >= MaxWrongTries;
            _dirty = true;
        }
        if (burn) RotateCode();                 // verbraucht oder zu oft falsch: neuer Code (schreibt auch die Datei)
        if (!ok) return null;
        Changed?.Invoke();
        return (token, device);
    }

    // Ein Gerät ohne Code anlegen – nur für Aufrufer, die den Schlüssel des Datenordners haben (die Fenster-Hülle
    // auf diesem PC, PLAN: Web-Hülle). Liefert das Token einmal; gespeichert wird nur sein Hash.
    public (string Token, AccessDevice Device) AddLocalDevice(string? name, string? address)
    {
        var (token, device) = NewDevice(name, address, AccessRole.Admin);
        lock (_lock) { _state.Devices.Add(device); _dirty = true; }
        Write();
        Changed?.Invoke();
        return (token, device);
    }

    private (string Token, AccessDevice Device) NewDevice(string? name, string? address, string role)
    {
        var device = new AccessDevice
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = CleanName(name),
            Created = _now(),
            LastSeen = _now(),
            Address = address,
            Role = role,
        };
        var token = NewToken();
        device.Hash = Hash(token);
        return (token, device);
    }

    // ── Kopplung andersherum: das neue Gerät zeigt den Code, ein angemeldetes gibt frei ──

    public IReadOnlyList<PairRequest> PendingRequests
    {
        get { lock (_lock) { PruneRequests(); return _requests.Where(r => r.Status == PairStatus.Pending && _now() < r.Expires).ToList(); } }
    }

    // Neue Anfrage (ohne Anmeldung erreichbar). Liefert die Anfrage und das Secret zum Nachfragen, oder null,
    // wenn schon zu viele offen sind (sonst ließe sich die Liste von außen fluten).
    public (PairRequest Request, string Secret)? RequestPairing(string? name, string? kind, string? address)
    {
        var secret = NewToken();
        PairRequest req;
        lock (_lock)
        {
            PruneRequests();
            if (_requests.Count(r => r.Status == PairStatus.Pending && _now() < r.Expires) >= MaxPendingRequests) return null;
            string code;
            do code = NewCode(); while (code == _state.Code || _requests.Any(r => r.Code == code));
            req = new PairRequest
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Code = code,
                Name = CleanName(name),
                Kind = PairKinds.Normalize(kind),
                Address = address,
                Created = _now(),
                Expires = _now() + CodeLifetime,
                SecretHash = Hash(secret),
            };
            _requests.Add(req);
        }
        Changed?.Invoke();
        return (req, secret);
    }

    // Freigabe mit den Ziffern, die das neue Gerät zeigt. Es bekommt die gewünschte Rolle (Vorgabe Admin);
    // null = keine offene Anfrage mit diesem Code.
    public PairRequest? Approve(string? code, string? role = null)
    {
        var clean = CleanCode(code);
        if (clean.Length != CodeLength) return null;
        PairRequest? req;
        lock (_lock)
        {
            PruneRequests();
            req = _requests.FirstOrDefault(r => r.Status == PairStatus.Pending && _now() < r.Expires && SameCode(r.Code, clean));
            if (req == null) return null;
            var want = role == AccessRole.Viewer ? AccessRole.Viewer : AccessRole.Admin;
            var (token, device) = NewDevice(req.Name, req.Address, want);
            _state.Devices.Add(device);
            req.Token = token;
            req.DeviceId = device.Id;
            req.Status = PairStatus.Approved;
            _dirty = true;
        }
        Write();
        Changed?.Invoke();
        return req;
    }

    public bool Deny(string? id)
    {
        bool found;
        lock (_lock)
        {
            var req = _requests.FirstOrDefault(r => r.Id == id && r.Status == PairStatus.Pending);
            found = req != null;
            if (req != null) req.Status = PairStatus.Denied;
        }
        if (found) Changed?.Invoke();
        return found;
    }

    // Das neue Gerät fragt nach: Status und – genau einmal – das Cookie. Ohne passendes Secret gibt es nichts.
    public (string Status, string? Token) Poll(string? id, string? secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return (PairStatus.Unknown, null);
        var hash = Hash(secret);
        lock (_lock)
        {
            var req = _requests.FirstOrDefault(r => r.Id == id);
            if (req == null || !SameCode(req.SecretHash, hash)) return (PairStatus.Unknown, null);
            if (req.Status == PairStatus.Pending && _now() >= req.Expires) req.Status = PairStatus.Expired;
            if (req.Status != PairStatus.Approved) return (req.Status, null);
            var token = req.Token;
            _requests.Remove(req);              // abgeholt: das Cookie liegt nirgends mehr im Speicher
            return (PairStatus.Approved, token);
        }
    }

    // Abgelaufene und erledigte Anfragen entfernen; eine Minute Nachlauf, damit das neue Gerät noch "abgelaufen" erfährt
    private bool PruneRequests()
    {
        var now = _now();
        return _requests.RemoveAll(r => now >= r.Expires + TimeSpan.FromMinutes(1)) > 0;
    }

    // Cookie prüfen und die Rolle des Geräts liefern (null = kein gültiges Cookie).
    // Der Vergleich läuft über die Zeit (FixedTimeEquals), damit sich nicht per Timing erraten lässt, ob ein Hash passt.
    public string? RoleOf(string? token, string? address = null)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = Hash(token);
        string? role = null;
        bool touch = false;
        lock (_lock)
        {
            foreach (var d in _state.Devices)
            {
                if (!SameCode(d.Hash, hash)) continue;
                role = d.Role;
                var seen = d.LastSeen ?? DateTime.MinValue;
                if ((_now() - seen).TotalMinutes >= 1)
                {
                    d.LastSeen = _now();
                    if (!string.IsNullOrEmpty(address)) d.Address = address;
                    _dirty = true;
                    touch = true;
                }
                break;
            }
        }
        if (role != null) { if (touch) WriteThrottled(); Changed?.Invoke(); }
        return role;
    }

    public bool Validate(string? token, string? address = null) => RoleOf(token, address) != null;

    // Rolle eines Geräts ändern. Unbekannte Rollen und unbekannte Geräte werden abgewiesen (Rückgabe false);
    // steht die Rolle schon so drin, gilt das als Erfolg – die Aktion bleibt damit wiederholbar.
    public bool SetRole(string id, string? role)
    {
        if (string.IsNullOrEmpty(id) || !AccessRole.IsValid(role)) return false;
        bool found = false, changed = false;
        lock (_lock)
        {
            var d = _state.Devices.FirstOrDefault(x => x.Id == id);
            if (d != null)
            {
                found = true;
                if (d.Role != role) { d.Role = role!; _dirty = true; changed = true; }
            }
        }
        if (changed) { Write(); Changed?.Invoke(); }
        return found;
    }

    public bool RemoveDevice(string id)
    {
        bool removed;
        lock (_lock) { removed = _state.Devices.RemoveAll(d => d.Id == id) > 0; if (removed) _dirty = true; }
        if (removed) { Write(); Changed?.Invoke(); }
        return removed;
    }

    // Gerät mit diesem Cookie abmelden (nur die eigene Sitzung)
    public bool RemoveToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = Hash(token);
        bool removed;
        lock (_lock) { removed = _state.Devices.RemoveAll(d => SameCode(d.Hash, hash)) > 0; if (removed) _dirty = true; }
        if (removed) { Write(); Changed?.Invoke(); }
        return removed;
    }

    public int DeviceCount { get { lock (_lock) return _state.Devices.Count; } }

    // Echte Änderungen (Code, Geräte, Schalter) sofort schreiben; das gemerkte „zuletzt gesehen“ höchstens einmal je Minute
    public void Write() => WriteFile(force: true);

    public void WriteThrottled() => WriteFile(force: false);

    private void WriteFile(bool force)
    {
        string json;
        lock (_lock)
        {
            if (!force)
            {
                if (!_dirty) return;
                if ((DateTime.Now - _lastSave).TotalSeconds < 60) return;
            }
            _dirty = false;
            json = JsonSerializer.Serialize(_state, Json);
        }
        _lastSave = DateTime.Now;
        try
        {
            var protectedBytes = _platform.ProtectForCurrentUser(Encoding.UTF8.GetBytes(json));
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Ohne Benutzerbindung bleibt die Datei im Klartext (Linux) – dann steht sie als JSON in der Datei.
            AtomicFile.WriteAllText(FilePath, protectedBytes == null ? json : Convert.ToBase64String(protectedBytes));
        }
        catch { /* dann gilt der Zustand nur für diese Sitzung */ }
    }

    private byte[] Unprotect(string base64)
    {
        var blob = Convert.FromBase64String(base64);
        return _platform.UnprotectForCurrentUser(blob) ?? blob;
    }

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("StykkerLLM.device.v1|" + token)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool SameCode(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a.Trim());
        var y = Encoding.UTF8.GetBytes(b.Trim());
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    // "WaterFox on Android" o. ä. – ohne Browserkennung nur der Host des Aufrufers
    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Browser";
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, ' ');
        return n.Length > 40 ? n[..40] : n;
    }
}

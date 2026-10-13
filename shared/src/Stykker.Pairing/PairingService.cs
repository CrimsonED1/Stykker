namespace Stykker.Pairing;

// Die Kopplung, wie sie ein Modul benutzt: Zugangscode, Geräte, Rollen je Modul, Hosts. Die Regeln sind die von
// StykkerLLM (AccessControl, HostPairing, HostRegistry); neu ist, dass alle Module dieselbe Ablage teilen und ein
// Gerät je Modul eine Rolle hat.
//
// Zwei Wege zur Anmeldung, beide mit sechs Ziffern:
//  1. Der PC zeigt den Code, das neue Gerät tippt oder scannt ihn (Pair).
//  2. Das neue Gerät zeigt einen Code, ein angemeldetes Gerät gibt ihn frei (RequestPairing → Approve → Poll).
public sealed class PairingService
{
    private static readonly TimeSpan LastSeenStep = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RequestGrace = TimeSpan.FromMinutes(1);

    private readonly PairingStore _store;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    // Token offener Anfragen, die dieser Prozess angenommen hat (Anfrage-Id -> Token). Nur im Speicher.
    private readonly Dictionary<string, string> _requestTokens = new();
    // Der Code für Hosts gilt nur in dem Prozess, der ihn zeigt: ein Host koppelt mit genau einem Modul.
    private string _hostCode = "";
    private DateTime _hostCodeCreated;
    private int _hostWrongTries;

    public event Action? Changed;

    public PairingService(PairingStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.UtcNow);
        _store.Changed += () => Changed?.Invoke();
        RotateHostCodeLocked();
    }

    public PairingStore Store => _store;

    // ── Zugangscode ──

    // Der angezeigte Code. Gibt es noch keinen (erster Start), entsteht er hier.
    public string Code
    {
        get
        {
            var code = _store.Read().Code;
            return PairingCodes.IsCode(code) ? code : EnsureCode();
        }
    }

    // Bis wann der angezeigte Code gilt (die Oberflächen zeigen die Restzeit)
    public DateTime CodeExpires => _store.Read().CodeCreated + PairingCodes.CodeLifetime;

    private string EnsureCode() => _store.Update(state =>
    {
        if (PairingCodes.IsCode(state.Code)) return (false, state.Code);
        NewCode(state);
        return (true, state.Code);
    });

    private void NewCode(PairingState state)
    {
        state.Code = PairingCodes.NewCode();
        state.CodeCreated = _now();
        state.CodeWrongTries = 0;
    }

    private bool CodeExpired(PairingState state) => _now() >= state.CodeCreated + PairingCodes.CodeLifetime;

    // Neuer Code (abgelaufen, verbraucht, zu oft falsch, oder jemand hat ihn gesehen). Angemeldete Geräte bleiben
    // angemeldet – wer sie nicht mehr haben will, entfernt sie in der Geräteliste.
    public string RotateCode() => _store.Update(state =>
    {
        NewCode(state);
        return (true, state.Code);
    });

    // Im Takt des Programms, das den Code zeigt: abgelaufenen Code ersetzen, alte Anfragen aufräumen.
    // Liefert true, wenn sich etwas geändert hat.
    public bool Maintain()
    {
        var seen = _store.Read();
        var now = _now();
        if (PairingCodes.IsCode(seen.Code) && !CodeExpired(seen) && !seen.Requests.Any(r => now >= r.Expires + RequestGrace))
            return false;   // der häufige Fall: nichts zu tun, also auch keine Sperre

        var changed = _store.Update(state =>
        {
            bool rotate = !PairingCodes.IsCode(state.Code) || CodeExpired(state);
            if (rotate) NewCode(state);
            bool pruned = PruneRequests(state);
            return (rotate || pruned, rotate || pruned);
        });
        if (changed) ForgetTokens(_store.Read());
        return changed;
    }

    // ── Netzschalter (zieht mit stykker.json in die Konfiguration um) ──

    public bool RemoteEnabled => _store.Read().RemoteEnabled;

    public void SetRemote(bool on) => _store.Update(state =>
    {
        if (state.RemoteEnabled == on) return false;
        state.RemoteEnabled = on;
        return true;
    });

    // ── Geräte ──

    // Die gekoppelten Geräte ohne Hosts.
    public IReadOnlyList<PairedDevice> Devices =>
        _store.Read().Devices.Where(d => d.Kind != DeviceKinds.Host).ToList();

    public int DeviceCount => Devices.Count;

    // Anmeldung mit dem Code: liefert das Token für das Gerät, oder null bei falschem oder abgelaufenem Code.
    // Das Gerät wird Admin in dem Modul, an dem es sich anmeldet; in allen anderen sieht es zu. Der Code ist danach
    // verbraucht, nach MaxWrongTries Fehlversuchen ebenfalls.
    public (string Token, PairedDevice Device)? Pair(string? code, string? name, string? address, string module)
    {
        var clean = PairingCodes.CleanCode(code);
        if (clean.Length == 0) return null;
        var (token, device) = NewDevice(name, address, DeviceKinds.Browser);
        device.Roles[module] = PairingRoles.Admin;

        var ok = _store.Update(state =>
        {
            bool match = PairingCodes.IsCode(state.Code) && !CodeExpired(state) && PairingCodes.Same(clean, state.Code);
            if (match)
            {
                state.Devices.Add(device);
                NewCode(state);
            }
            else if (++state.CodeWrongTries >= PairingCodes.MaxWrongTries) NewCode(state);
            return (true, match);
        });
        return ok ? (token, device) : null;
    }

    // Ein Gerät ohne Code anlegen – nur für Aufrufer, die sich anders ausgewiesen haben (die Fenster-Hülle auf diesem
    // PC mit dem Schlüssel des Datenordners). Liefert das Token einmal; gespeichert wird nur sein Hash.
    public (string Token, PairedDevice Device) AddLocalDevice(string? name, string? address, string module)
    {
        var (token, device) = NewDevice(name, address, DeviceKinds.Browser);
        device.Roles[module] = PairingRoles.Admin;
        _store.Update(state => { state.Devices.Add(device); return true; });
        return (token, device);
    }

    // Token prüfen und die Rolle des Geräts in diesem Modul liefern (null = kein gültiges Token).
    // Ein Gerät ohne Eintrag für das Modul ist dort Viewer: ein neues Modul sieht die vorhandene Kopplung.
    public string? RoleOf(string? token, string module, string? address = null)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = PairingCodes.Hash(token);
        var device = FindByHash(_store.Read(), hash, host: false);
        if (device == null) return null;

        var now = _now();
        if (now - (device.LastSeen ?? DateTime.MinValue) >= LastSeenStep)
        {
            // "Zuletzt gesehen" höchstens einmal je Minute schreiben. Scheitert das (Ablage gesperrt), gilt das Token trotzdem.
            try
            {
                _store.Update(state =>
                {
                    var stored = FindByHash(state, hash, host: false);
                    if (stored == null || now - (stored.LastSeen ?? DateTime.MinValue) < LastSeenStep) return false;
                    stored.LastSeen = now;
                    if (!string.IsNullOrEmpty(address)) stored.Address = address;
                    return true;
                });
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { }
        }
        return device.RoleIn(module);
    }

    public bool Validate(string? token, string module, string? address = null) => RoleOf(token, module, address) != null;

    // Rolle eines Geräts in einem Modul ändern. Unbekannte Rollen und unbekannte Geräte werden abgewiesen (false);
    // steht die Rolle schon so drin, gilt das als Erfolg – die Aktion bleibt damit wiederholbar.
    public bool SetRole(string id, string module, string? role)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(module) || !PairingRoles.IsValid(role)) return false;
        return _store.Update(state =>
        {
            var device = state.Devices.FirstOrDefault(d => d.Id == id && d.Kind != DeviceKinds.Host);
            if (device == null) return (false, false);
            if (device.Roles.TryGetValue(module, out var current) && current == role) return (false, true);
            device.Roles[module] = role!;
            return (true, true);
        });
    }

    public bool RemoveDevice(string id) => _store.Update(state =>
    {
        bool removed = state.Devices.RemoveAll(d => d.Id == id && d.Kind != DeviceKinds.Host) > 0;
        return (removed, removed);
    });

    // Gerät mit diesem Token abmelden (nur die eigene Sitzung)
    public bool RemoveToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = PairingCodes.Hash(token);
        return _store.Update(state =>
        {
            bool removed = state.Devices.RemoveAll(d => d.Kind != DeviceKinds.Host && PairingCodes.Same(d.Hash, hash)) > 0;
            return (removed, removed);
        });
    }

    // ── Kopplung andersherum: das neue Gerät zeigt den Code, ein angemeldetes gibt frei ──

    public IReadOnlyList<PairRequest> PendingRequests
    {
        get
        {
            var now = _now();
            return _store.Read().Requests.Where(r => r.Status == PairStatus.Pending && now < r.Expires).ToList();
        }
    }

    // Neue Anfrage (ohne Anmeldung erreichbar). Liefert die Anfrage und das Secret zum Nachfragen, oder null,
    // wenn schon zu viele offen sind (sonst ließe sich die Liste von außen fluten).
    public (PairRequest Request, string Secret)? RequestPairing(string? name, string? address, string module)
    {
        var secret = PairingCodes.NewToken();
        var token = PairingCodes.NewToken();
        var now = _now();
        var request = _store.Update(state =>
        {
            PruneRequests(state);
            if (state.Requests.Count(r => r.Status == PairStatus.Pending && now < r.Expires) >= PairingCodes.MaxPendingRequests)
                return (false, (PairRequest?)null);
            string code;
            do code = PairingCodes.NewCode(); while (code == state.Code || state.Requests.Any(r => r.Code == code));
            var req = new PairRequest
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Code = code,
                Name = CleanName(name),
                Module = module,
                Address = address,
                Created = now,
                Expires = now + PairingCodes.CodeLifetime,
                SecretHash = PairingCodes.Hash(secret),
                TokenHash = PairingCodes.Hash(token),
            };
            state.Requests.Add(req);
            return (true, (PairRequest?)req);
        });
        if (request == null) return null;
        lock (_lock) _requestTokens[request.Id] = token;
        return (request, secret);
    }

    // Freigabe mit den Ziffern, die das neue Gerät zeigt – auch aus einem anderen Prozess als dem, der die Anfrage
    // angenommen hat. Das Gerät bekommt die gewünschte Rolle (Vorgabe Admin) in dem Modul, an dem es angefragt hat.
    // null = keine offene Anfrage mit diesem Code.
    public PairRequest? Approve(string? code, string? role = null)
    {
        var clean = PairingCodes.CleanCode(code);
        if (clean.Length != PairingCodes.CodeLength) return null;
        var now = _now();
        return _store.Update(state =>
        {
            var req = state.Requests.FirstOrDefault(r => r.Status == PairStatus.Pending && now < r.Expires && PairingCodes.Same(r.Code, clean));
            if (req == null) return (false, (PairRequest?)null);
            var device = new PairedDevice
            {
                Id = NewDeviceId(),
                Name = req.Name,
                Kind = DeviceKinds.Browser,
                Created = now,
                LastSeen = now,
                Address = req.Address,
                Hash = req.TokenHash,
            };
            device.Roles[req.Module] = role == PairingRoles.Viewer ? PairingRoles.Viewer : PairingRoles.Admin;
            state.Devices.Add(device);
            req.DeviceId = device.Id;
            req.Status = PairStatus.Approved;
            return (true, (PairRequest?)req);
        });
    }

    public bool Deny(string? id) => _store.Update(state =>
    {
        var req = state.Requests.FirstOrDefault(r => r.Id == id && r.Status == PairStatus.Pending);
        if (req == null) return (false, false);
        req.Status = PairStatus.Denied;
        return (true, true);
    });

    // Das neue Gerät fragt nach: Status und – genau einmal – das Token. Ohne passendes Secret gibt es nichts.
    // Das Token hält nur der Prozess, der die Anfrage angenommen hat; wurde er neu gestartet, ist die Anfrage verfallen.
    public (string Status, string? Token) Poll(string? id, string? secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return (PairStatus.Unknown, null);
        var hash = PairingCodes.Hash(secret);
        var req = _store.Read().Requests.FirstOrDefault(r => r.Id == id);
        if (req == null || !PairingCodes.Same(req.SecretHash, hash)) return (PairStatus.Unknown, null);
        if (req.Status == PairStatus.Pending) return (_now() >= req.Expires ? PairStatus.Expired : PairStatus.Pending, null);
        if (req.Status != PairStatus.Approved) return (req.Status, null);

        string? token;
        lock (_lock) { _requestTokens.Remove(id, out token); }
        // Abgeholt: die Anfrage verschwindet aus der Ablage, das Token liegt nirgends mehr
        _store.Update(state => state.Requests.RemoveAll(r => r.Id == id) > 0);
        return token == null ? (PairStatus.Expired, null) : (PairStatus.Approved, token);
    }

    // Abgelaufene und erledigte Anfragen entfernen; eine Minute Nachlauf, damit das neue Gerät noch "abgelaufen" erfährt
    private bool PruneRequests(PairingState state)
    {
        var now = _now();
        return state.Requests.RemoveAll(r => now >= r.Expires + RequestGrace) > 0;
    }

    private void ForgetTokens(PairingState state)
    {
        var open = state.Requests.Select(r => r.Id).ToHashSet();
        lock (_lock)
            foreach (var id in _requestTokens.Keys.Where(id => !open.Contains(id)).ToList()) _requestTokens.Remove(id);
    }

    // ── Hosts: Geräte ohne Rollen, die sich mit ihrem Token melden ──

    public IReadOnlyList<PairedDevice> Hosts => _store.Read().Devices.Where(d => d.Kind == DeviceKinds.Host).ToList();

    // Der aktuelle Code für Hosts (getrennt vom Code fürs Handy; ein abgelaufener wird dabei ersetzt)
    public string HostCode
    {
        get
        {
            lock (_lock)
            {
                if (_now() - _hostCodeCreated >= PairingCodes.CodeLifetime) RotateHostCodeLocked();
                return _hostCode;
            }
        }
    }

    public DateTime HostCodeExpires { get { lock (_lock) return _hostCodeCreated + PairingCodes.CodeLifetime; } }

    public void RotateHostCode() { lock (_lock) RotateHostCodeLocked(); }

    private void RotateHostCodeLocked()
    {
        _hostCode = PairingCodes.NewCode();
        _hostCodeCreated = _now();
        _hostWrongTries = 0;
    }

    // Code prüfen: passt er, entsteht der Host (Name vom Host) und der Code ist verbraucht
    public (PairedDevice Host, string Token)? TryPairHost(string? code, string? name)
    {
        lock (_lock)
        {
            if (_now() - _hostCodeCreated >= PairingCodes.CodeLifetime) RotateHostCodeLocked();
            var given = PairingCodes.CleanCode(code);
            if (given.Length != PairingCodes.CodeLength || !PairingCodes.Same(given, _hostCode))
            {
                if (++_hostWrongTries >= PairingCodes.MaxWrongTries) RotateHostCodeLocked();
                return null;
            }
            RotateHostCodeLocked();
        }
        return AddHost(name);
    }

    // Neuer Host: Eintrag anlegen, das Token einmal zurückgeben (der Host speichert es, die Ablage nur den Hash)
    public (PairedDevice Host, string Token) AddHost(string? name)
    {
        var token = PairingCodes.NewToken();
        var trimmed = (name ?? "").Trim();
        var host = new PairedDevice
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = trimmed.Length == 0 ? "host" : trimmed[..Math.Min(64, trimmed.Length)],
            Kind = DeviceKinds.Host,
            Created = _now(),
            Hash = PairingCodes.Hash(token),
        };
        _store.Update(state => { state.Devices.Add(host); return true; });
        return (host, token);
    }

    public PairedDevice? FindHost(string? token) =>
        string.IsNullOrEmpty(token) ? null : FindByHash(_store.Read(), PairingCodes.Hash(token), host: true);

    public bool RemoveHost(string id) => _store.Update(state =>
    {
        bool removed = state.Devices.RemoveAll(d => d.Id == id && d.Kind == DeviceKinds.Host) > 0;
        return (removed, removed);
    });

    // ── Hilfen ──

    private static PairedDevice? FindByHash(PairingState state, string hash, bool host)
    {
        // Alle Einträge vergleichen, auch nach einem Treffer: die Dauer verrät nicht, welcher gepasst hat
        PairedDevice? found = null;
        foreach (var device in state.Devices)
            if ((device.Kind == DeviceKinds.Host) == host && PairingCodes.Same(device.Hash, hash)) found ??= device;
        return found;
    }

    private (string Token, PairedDevice Device) NewDevice(string? name, string? address, string kind)
    {
        var token = PairingCodes.NewToken();
        var device = new PairedDevice
        {
            Id = NewDeviceId(),
            Name = CleanName(name),
            Kind = kind,
            Created = _now(),
            LastSeen = _now(),
            Address = address,
            Hash = PairingCodes.Hash(token),
        };
        return (token, device);
    }

    private static string NewDeviceId() => Guid.NewGuid().ToString("N")[..8];

    // "WaterFox on Android" o. ä. – ohne Browserkennung nur "Browser"
    internal static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Browser";
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, ' ');
        return n.Length > 40 ? n[..40] : n;
    }
}

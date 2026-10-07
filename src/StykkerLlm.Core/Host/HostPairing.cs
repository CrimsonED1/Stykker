namespace StykkerLlm.Core;

// Kopplung eines Model-Hosts (docs/plan-hosts-gateway.md, P3): der Server zeigt einen eigenen Code (getrennt vom Code
// fürs Handy). Er gilt zehn Minuten und einmal; nach fünf falschen Versuchen gibt es einen neuen. Der Host schickt den
// Code an POST /hosts/pair und bekommt dafür sein Token.
public sealed class HostPairing
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxWrongTries = 5;

    private readonly HostRegistry _registry;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private string _code = "";
    private DateTime _created;
    private int _wrong;

    public HostPairing(HostRegistry registry, Func<DateTime>? now = null)
    {
        _registry = registry;
        _now = now ?? (() => DateTime.Now);
        Rotate();
    }

    // Der aktuelle Code (ein abgelaufener wird dabei ersetzt)
    public string Code
    {
        get
        {
            lock (_gate)
            {
                if (_now() - _created >= Lifetime) RotateLocked();
                return _code;
            }
        }
    }

    public DateTime Expires { get { lock (_gate) return _created + Lifetime; } }

    public void Rotate() { lock (_gate) RotateLocked(); }

    private void RotateLocked()
    {
        _code = AccessControl.NewCode();
        _created = _now();
        _wrong = 0;
    }

    // Code prüfen: passt er, entsteht der Host (Name vom Host) und der Code ist verbraucht
    public (HostEntry Entry, string Token)? TryPair(string? code, string? name)
    {
        lock (_gate)
        {
            if (_now() - _created >= Lifetime) RotateLocked();
            var given = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
            if (given.Length != AccessControl.CodeLength || given != _code)
            {
                if (++_wrong >= MaxWrongTries) RotateLocked();
                return null;
            }
            RotateLocked();
        }
        return _registry.Add(name ?? "host", _now());
    }
}

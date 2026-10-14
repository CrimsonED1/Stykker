using System.Text.Json;

namespace Stykker.Shared.Config;

// Die Module aus stykker.json, wie sie gerade in der Datei stehen. Die Datei wird neu gelesen, wenn sich ihre Änderungszeit
// oder Länge ändern: so wirken ein neues Ziel und ein neues Token ohne Neustart der Zentrale (ein neuer Port braucht einen
// Neustart, weil der Port beim Start geöffnet wird). Ein Stand, der nicht gelesen werden kann, bleibt der letzte gültige;
// fehlt die Datei, gilt die Vorgabe (wie bei HubModules.Load).
public sealed class HubConfigFile
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyCollection<string>> _ownAddresses;
    private IReadOnlyList<HubModule>? _current;
    private DateTime _stamp;
    private long _length;

    // ownAddresses liefert die IPv4-Adressen dieses PCs (zur Laufzeit gelesen, ServiceTargetRule.ReadThisPcAddresses).
    public HubConfigFile(string file, Func<IReadOnlyCollection<string>> ownAddresses)
    {
        FilePath = Path.GetFullPath(file);
        _ownAddresses = ownAddresses;
    }

    public string FilePath { get; }

    // Die Module im Augenblick. Liest nur neu, wenn die Datei sich geändert hat.
    public IReadOnlyList<HubModule> Current()
    {
        lock (_gate)
        {
            var info = new FileInfo(FilePath);
            bool exists = info.Exists;
            var stamp = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            var length = exists ? info.Length : -1;
            if (_current == null || stamp != _stamp || length != _length)
            {
                var fresh = Read();
                if (fresh != null) _current = fresh;
                else _current ??= HubModules.Defaults;
                _stamp = stamp;
                _length = length;
            }
            return _current;
        }
    }

    // null, wenn die Datei da ist, aber nicht gelesen werden kann (dann bleibt der letzte gültige Stand)
    private IReadOnlyList<HubModule>? Read()
    {
        if (!File.Exists(FilePath)) return HubModules.Defaults;
        try
        {
            var parsed = HubModules.Parse(File.ReadAllText(FilePath), _ownAddresses());
            return parsed.Count > 0 ? parsed : HubModules.Defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace StykkerFirewall.Backend.WindowsFirewall;

// Das Security-Log nennt Programme mit Gerätepfad („\device\harddiskvolume3\program files\…“). Regeln der
// Windows-Firewall brauchen den Laufwerkspfad („C:\program files\…“). QueryDosDevice liefert die Zuordnung.
public sealed class DevicePaths
{
    private readonly object _gate = new();
    private readonly bool _fixed;
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    // fixedMap (Tests): feste Zuordnung, die nie durch die echten Laufwerke ersetzt wird.
    public DevicePaths(IReadOnlyDictionary<string, string>? fixedMap = null)
    {
        _fixed = fixedMap is not null;
        if (fixedMap is not null) _map = new Dictionary<string, string>(fixedMap, StringComparer.OrdinalIgnoreCase);
        else Refresh();
    }

    public string Resolve(string path)
    {
        if (TryResolve(path, out var result)) return result;
        if (_fixed || !path.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)) return path;
        Refresh();   // Laufwerk neu eingehängt (USB-Stick, VHD)?
        return TryResolve(path, out result) ? result : path;
    }

    private bool TryResolve(string path, out string result)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) { result = path[4..]; return true; }
        lock (_gate)
        {
            foreach (var (device, drive) in _map)
            {
                if (path.Length > device.Length && path[device.Length] == '\\' &&
                    path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                {
                    result = drive + path[device.Length..];
                    return true;
                }
            }
        }
        result = path;
        return false;
    }

    private void Refresh()
    {
        if (!OperatingSystem.IsWindows()) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(1024);
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');   // "C:"
            buffer.Clear();
            if (QueryDosDevice(letter, buffer, buffer.Capacity) == 0) continue;
            var device = buffer.ToString();
            if (device.Length > 0) map[device] = letter;
        }
        lock (_gate) _map = map;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}

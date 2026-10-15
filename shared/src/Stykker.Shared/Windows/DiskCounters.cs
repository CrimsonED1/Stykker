using System.Runtime.InteropServices;
using System.Text;
using Stykker.Shared.Disk;

namespace Stykker.Shared.Windows;

// Laufwerke und Platten-E/A in Windows. Volumes (Name, Größe, frei) über DriveInfo; Takt-Werte über die Leistungsindikatoren
// (PhysicalDisk, LogicalDisk und Process, wie im Task-Manager); Art und Modell je physischem Laufwerk über
// IOCTL_STORAGE_QUERY_PROPERTY; die Zuordnung Volume → Laufwerk über IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Alle
// Abfragen sind lesend und brauchen keine Administratorrechte (Zugriffsrecht 0). Jeder Teil ist verzichtbar: fehlt er,
// bleibt sein Wert null – nie eine erfundene Zahl. Die Umrechnung steht in DiskMath (testbar ohne Windows).
public sealed class DiskCounters : IDisposable
{
    // Rate-Zähler liefern nur zwischen zwei Abtastungen einen Wert. Liegt die letzte mehr als so lange zurück, ist die
    // neue Abtastung nur ein Grundwert: die Raten sind dann null (der Zeitraum davor wäre ein Mittel über eine Pause).
    private const double WindowSeconds = 10;

    private static readonly string[] PhysicalPaths =
    {
        @"\PhysicalDisk(*)\Disk Read Bytes/sec", @"\PhysicalDisk(*)\Disk Write Bytes/sec",
        @"\PhysicalDisk(*)\% Disk Time", @"\PhysicalDisk(*)\Avg. Disk Queue Length",
    };
    private static readonly string[] LogicalPaths =
    {
        @"\LogicalDisk(*)\Disk Read Bytes/sec", @"\LogicalDisk(*)\Disk Write Bytes/sec",
        @"\LogicalDisk(*)\% Disk Time", @"\LogicalDisk(*)\Avg. Disk Queue Length",
    };
    private static readonly string[] ProcessPaths =
    {
        @"\Process(*)\IO Read Bytes/sec", @"\Process(*)\IO Write Bytes/sec", @"\Process(*)\ID Process",
    };

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlVolumeGetDiskExtents = 0x00560000;
    private const uint StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    private readonly object _gate = new();
    private readonly IntPtr _physQuery, _logQuery, _procQuery;
    private readonly IntPtr[] _physCounters, _logCounters, _procCounters;
    private readonly bool _physOk, _logOk, _procOk;
    private readonly Dictionary<int, (DiskKind Kind, string Model)> _identity = new();
    // Volume → Laufwerksnummer; -1 heißt: nicht zuzuordnen (einmal gefragt, nicht wieder)
    private readonly Dictionary<string, int> _diskOfLetter = new(StringComparer.Ordinal);
    private DateTime _lastDisk = DateTime.MinValue, _lastProc = DateTime.MinValue;

    public DiskCounters()
    {
        _physOk = Open(PhysicalPaths, out _physQuery, out _physCounters);
        _logOk = Open(LogicalPaths, out _logQuery, out _logCounters);
        _procOk = Open(ProcessPaths, out _procQuery, out _procCounters);
    }

    private static bool Open(string[] paths, out IntPtr query, out IntPtr[] counters)
    {
        bool ok = Pdh.Open(paths, out query, out counters);
        if (!ok) Pdh.Close(query);   // ein Zähler fehlt: die Abfrage ganz verwerfen (keine Teilwerte)
        return ok;
    }

    // Laufwerke und Volumes mit ihren Takt-Werten. null, wenn gar nichts zu lesen ist.
    public DiskSample? ReadDisks()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            bool rates = Window(ref _lastDisk, now);
            var physical = Collect(_physQuery, _physCounters, _physOk);
            var logical = Collect(_logQuery, _logCounters, _logOk);
            var volumes = ReadVolumes();
            if (physical == null && logical == null && volumes.Count == 0) return null;

            var numbers = new HashSet<int>();
            foreach (var name in physical?.Keys ?? Enumerable.Empty<string>())
                if (DiskMath.PhysicalNumber(name) is int n) numbers.Add(n);
            foreach (var v in volumes)
                if (v.DiskNumber is int n) numbers.Add(n);
            foreach (var n in numbers) IdentityOf(n);

            return DiskMath.BuildSample(now, physical ?? new Dictionary<string, double[]>(), logical ?? new Dictionary<string, double[]>(),
                volumes, _identity, rates);
        }
    }

    // Die Prozesse mit dem meisten Platten-E/A (Lesen und Schreiben je Sekunde). null, wenn der Zähler fehlt; im Grundwert leer.
    public IReadOnlyList<ProcessIoRow>? ReadProcesses(int count)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            bool rates = Window(ref _lastProc, now);
            var raw = Collect(_procQuery, _procCounters, _procOk);
            if (raw == null) return null;
            if (!rates) return Array.Empty<ProcessIoRow>();

            var pid = new Dictionary<string, int>(StringComparer.Ordinal);
            var read = new Dictionary<string, double>(StringComparer.Ordinal);
            var write = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, v) in raw)
            {
                pid[name] = (int)Math.Clamp(v[2], 0, int.MaxValue);   // ID Process: die PID des Prozesses
                read[name] = v[0];
                write[name] = v[1];
            }
            return DiskMath.ProcessRows(pid, read, write, count);
        }
    }

    // Ein neues Zeitfenster beginnen: nach einer Pause (Viewer weg) fängt die Messung wieder beim Grundwert an.
    public void Reset()
    {
        lock (_gate) { _lastDisk = DateTime.MinValue; _lastProc = DateTime.MinValue; }
    }

    // Prüft, ob die letzte Abtastung kurz genug her ist für Raten; schreibt den Zeitpunkt fort. false = Grundwert.
    private static bool Window(ref DateTime last, DateTime now)
    {
        bool rates = last != DateTime.MinValue && (now - last).TotalSeconds <= WindowSeconds;
        last = now;
        return rates;
    }

    // Eine Abtastung: je Instanz die Werte der Zähler in der Reihenfolge der Pfade. null, wenn die Abfrage fehlt.
    private static Dictionary<string, double[]>? Collect(IntPtr query, IntPtr[] counters, bool ok)
    {
        if (!ok || !Pdh.Collect(query)) return null;
        var map = new Dictionary<string, double[]>(StringComparer.Ordinal);
        for (int i = 0; i < counters.Length; i++)
            foreach (var (name, value) in Pdh.Values(counters[i], Pdh.FmtDouble))
            {
                if (!map.TryGetValue(name, out var row)) map[name] = row = new double[counters.Length];
                row[i] = value;
            }
        return map;
    }

    // Die Volumes mit Laufwerksbuchstaben (feste und Wechselmedien). Netzlaufwerke zählen nicht: sie haben kein Laufwerk hier.
    private List<DiskMath.VolumeFact> ReadVolumes()
    {
        var list = new List<DiskMath.VolumeFact>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return list; }
        catch (UnauthorizedAccessException) { return list; }
        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                var letter = char.ToUpperInvariant(d.Name[0]).ToString();
                list.Add(new DiskMath.VolumeFact(letter, SafeLabel(d), d.DriveFormat, d.TotalSize, d.TotalFreeSpace, DiskNumberOf(letter)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    // Das physische Laufwerk hinter einem Volume (einmal gefragt, dann aus dem Speicher)
    private int? DiskNumberOf(string letter)
    {
        if (!_diskOfLetter.TryGetValue(letter, out int known))
        {
            known = VolumeDiskNumber(letter) ?? -1;
            _diskOfLetter[letter] = known;
        }
        return known >= 0 ? known : null;
    }

    // Art und Modell eines physischen Laufwerks, einmal gelesen (die Art ändert sich nicht im Betrieb)
    private void IdentityOf(int number)
    {
        if (_identity.ContainsKey(number)) return;
        _identity[number] = ReadIdentity(number);
    }

    private static (DiskKind Kind, string Model) ReadIdentity(int number)
    {
        var handle = OpenDevice(@"\\.\PhysicalDrive" + number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (handle == IntPtr.Zero) return (DiskKind.Unknown, "");
        try
        {
            var desc = Query(handle, StorageDeviceProperty, 1024);
            if (desc == null || desc.Length < 36) return (DiskKind.Unknown, "");
            // STORAGE_DEVICE_DESCRIPTOR: BusType bei 28, ProductIdOffset bei 16 (Zeichenkette, null-terminiert)
            int bus = BitConverter.ToInt32(desc, 28);
            int productAt = BitConverter.ToInt32(desc, 16);
            string model = productAt > 0 && productAt < desc.Length ? AsciiString(desc, productAt) : "";
            var seek = Query(handle, StorageDeviceSeekPenaltyProperty, 16);
            // DEVICE_SEEK_PENALTY_DESCRIPTOR: IncursSeekPenalty (Boolean) bei 8
            bool? penalty = seek is { Length: >= 9 } ? seek[8] != 0 : null;
            return (DiskMath.Classify(bus, penalty), model);
        }
        finally { CloseHandle(handle); }
    }

    // Die Laufwerksnummer des ersten Abschnitts eines Volumes; null, wenn Windows keine nennt
    private static int? VolumeDiskNumber(string letter)
    {
        var handle = OpenDevice(@"\\.\" + letter + ":");
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new byte[512];
            if (!DeviceIoControl(handle, IoctlVolumeGetDiskExtents, null, 0, buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero)) return null;
            // VOLUME_DISK_EXTENTS: Anzahl (4 Byte), dann die Abschnitte; jeder beginnt mit der Laufwerksnummer
            if (returned < 12 || BitConverter.ToUInt32(buffer, 0) == 0) return null;
            return (int)BitConverter.ToUInt32(buffer, 8);
        }
        finally { CloseHandle(handle); }
    }

    // Eine Abfrage (STORAGE_PROPERTY_QUERY, Standardabfrage) ausführen; null, wenn Windows sie verweigert
    private static byte[]? Query(IntPtr handle, uint propertyId, int size)
    {
        var input = new byte[12];   // PropertyId, QueryType (0 = Standard) und ein Zusatzbyte
        BitConverter.GetBytes(propertyId).CopyTo(input, 0);
        var output = new byte[size];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, input, (uint)input.Length, output, (uint)output.Length, out uint returned, IntPtr.Zero))
            return null;
        return returned == 0 ? null : output[..(int)Math.Min(returned, (uint)size)];
    }

    private static string AsciiString(byte[] data, int at)
    {
        int end = at;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, at, end - at).Trim();
    }

    private static IntPtr OpenDevice(string path)
    {
        var handle = CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return handle == new IntPtr(-1) ? IntPtr.Zero : handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize,
        out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        lock (_gate)
        {
            Pdh.Close(_physQuery);
            Pdh.Close(_logQuery);
            Pdh.Close(_procQuery);
        }
    }
}

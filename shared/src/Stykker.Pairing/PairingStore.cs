using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stykker.Pairing;

// Die Ablage der Kopplung: eine Datei (pairing.dat) für alle Module. Mehrere Prozesse lesen und schreiben sie
// zugleich – Module, der Knoten, die Fenster. Darum gilt:
//  - Schreiben läuft unter einem benannten Mutex und liest die Datei vorher frisch: kein Prozess überschreibt,
//    was ein anderer gerade eingetragen hat.
//  - Geschrieben wird atomar (Temp-Datei, dann ersetzen): ein Leser sieht nie eine halbe Datei.
//  - Lesen prüft Änderungszeit und Länge und liest nur neu, wenn ein anderer Prozess geschrieben hat.
public sealed class PairingStore
{
    public const string FileName = "pairing.dat";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly IDataProtector _protector;
    private readonly string _mutexName;
    private readonly object _cacheLock = new();
    private PairingState _cache = new();
    private (DateTime Written, long Length) _cacheStamp = (DateTime.MinValue, -1);

    public string FilePath { get; }

    // Meldet eigene Änderungen. Änderungen anderer Prozesse zeigt das nächste Read().
    public event Action? Changed;

    public PairingStore(string file, IDataProtector? protector = null)
    {
        FilePath = Path.GetFullPath(file);
        _protector = protector ?? DataProtectors.ForCurrentUser();
        // Der Name hängt am Pfad: zwei Ablagen (Tests, eigener Datenordner) sperren sich nicht gegenseitig.
        // "Local\": je Windows-Sitzung. Ein Dienst in Sitzung 0 braucht später "Global\" und eine Ablage in ProgramData.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())))[..24];
        _mutexName = (OperatingSystem.IsWindows() ? @"Local\" : "") + "Stykker.Pairing." + key;
    }

    // %APPDATA%\Stykker\pairing.dat (unter Linux ~/.config/Stykker/pairing.dat)
    public static string DefaultFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stykker", FileName);

    // Der aktuelle Stand als eigene Kopie: der Aufrufer darf sie ändern, ohne die Ablage zu berühren.
    public PairingState Read()
    {
        lock (_cacheLock)
        {
            var stamp = Stamp();
            if (stamp != _cacheStamp)
            {
                _cache = Load() ?? new PairingState();
                _cacheStamp = stamp;
            }
            return Clone(_cache);
        }
    }

    // Ändern unter der Sperre: liest frisch, wendet change an, schreibt nur, wenn change true liefert.
    public T Update<T>(Func<PairingState, (bool Save, T Result)> change)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool held;
        try { held = mutex.WaitOne(LockTimeout); }
        catch (AbandonedMutexException) { held = true; }   // der Vorbesitzer ist abgestürzt: die Datei ist dank atomarem Schreiben heil
        if (!held) throw new TimeoutException($"Die Kopplungsablage ist gesperrt: {FilePath}");

        bool saved;
        T result;
        try
        {
            var state = Load();
            if (state == null)
            {
                // Vorhanden, aber unlesbar (beschädigt, oder von einem anderen Benutzer geschützt): nicht stillschweigend
                // überschreiben. Die Datei bleibt daneben liegen, die Geräte melden sich neu an.
                KeepUnreadable();
                state = new PairingState();
            }
            (saved, result) = change(state);
            if (saved)
            {
                Write(state);
                lock (_cacheLock) { _cache = state; _cacheStamp = Stamp(); }
            }
        }
        finally { mutex.ReleaseMutex(); }

        if (saved) Changed?.Invoke();
        return result;
    }

    public void Update(Func<PairingState, bool> change) => Update(state => (change(state), true));

    private (DateTime, long) Stamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    // null = Datei vorhanden, aber nicht lesbar. Fehlt sie, ist das ein leerer Stand.
    private PairingState? Load()
    {
        string raw;
        try
        {
            if (!File.Exists(FilePath)) return new PairingState();
            raw = ReadShared(FilePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        try
        {
            string json;
            if (raw.StartsWith('{')) json = raw;
            else
            {
                var plain = _protector.Unprotect(Convert.FromBase64String(raw));
                if (plain == null) return null;
                json = Encoding.UTF8.GetString(plain);
            }
            var state = JsonSerializer.Deserialize<PairingState>(json, Json);
            if (state == null) return null;
            state.Devices ??= new();
            state.Requests ??= new();
            state.Imported ??= new();
            state.Devices.RemoveAll(d => string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Hash));
            foreach (var device in state.Devices) device.Roles ??= new();
            return state;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    private void Write(PairingState state)
    {
        var json = JsonSerializer.Serialize(state, Json);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Ohne Benutzerbindung bleibt die Datei im Klartext (Linux) – dann steht sie als JSON in der Datei.
        WriteAtomic(FilePath, protectedBytes == null ? json : Convert.ToBase64String(protectedBytes));
    }

    private void KeepUnreadable()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* dann eben ohne Kopie */ }
    }

    internal static string ReadShared(string path)
    {
        // Ein Schreiber ersetzt die Datei gerade: kurz warten statt zu scheitern
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5 && File.Exists(path)) { Thread.Sleep(15 * attempt); }
        }
    }

    // Erst in eine Temp-Datei mit eindeutigem Namen, auf den Datenträger schreiben (Flush(true)), dann ersetzen.
    internal static void WriteAtomic(string path, string text)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(true);
            }
            // Das Ersetzen scheitert kurz, wenn ein Leser oder ein Virenscanner die Zieldatei gerade hält
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 8) { Thread.Sleep(15 * attempt); }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static PairingState Clone(PairingState state) =>
        JsonSerializer.Deserialize<PairingState>(JsonSerializer.Serialize(state, Json), Json)!;
}

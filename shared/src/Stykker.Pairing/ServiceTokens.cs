using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stykker.Pairing;

// Die Tokens, mit denen die Zentrale eigene Dienste anspricht (docs/plan-stykkernode.md, Abschnitt 4.2): je Dienst
// (id) ein Token, das nur die Zentrale kennt. Die Ablage services.dat liegt neben pairing.dat und ist geschützt wie
// diese (DPAPI des Benutzers, Zusatzwert Stykker.services.v1). Wie PairingStore: ein benannter Mutex je Datei, frisch
// lesen vor dem Schreiben, atomar ersetzen. Eine unlesbare Datei wird beiseitegelegt.
public sealed class ServiceTokens
{
    public const string FileName = "services.dat";
    // Zusatzwert der Ablage: andere Programme desselben Benutzers entschlüsseln sie nicht.
    public const string Entropy = "Stykker.services.v1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly IDataProtector _protector;
    private readonly string _mutexName;

    public string FilePath { get; }

    public ServiceTokens(string file, IDataProtector? protector = null)
    {
        FilePath = Path.GetFullPath(file);
        _protector = protector ?? DataProtectors.ForCurrentUser(Entropy);
        // Der Name hängt am Pfad, wie bei PairingStore: zwei Ablagen sperren sich nicht gegeneinander.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())))[..24];
        _mutexName = (OperatingSystem.IsWindows() ? @"Local\" : "") + "Stykker.Services." + key;
    }

    // services.dat neben der Kopplungsablage (mit --pairing-file also neben dieser Datei)
    public static string NextTo(string pairingFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pairingFile)) ?? "", FileName);

    // Das Token des Dienstes. Gibt es noch keins, wird es erzeugt und gespeichert; danach liefert jeder Aufruf dasselbe.
    public string GetOrCreate(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("Dienst-Id: nur Buchstaben, Ziffern und '-'.", nameof(id));

        using var mutex = new Mutex(false, _mutexName);
        bool held;
        try { held = mutex.WaitOne(LockTimeout); }
        catch (AbandonedMutexException) { held = true; }   // der Vorbesitzer ist abgestürzt: die Datei ist dank atomarem Schreiben heil
        if (!held) throw new TimeoutException($"Die Tokenablage ist gesperrt: {FilePath}");

        try
        {
            var state = Load();
            if (state == null)
            {
                // Vorhanden, aber unlesbar: nicht stillschweigend überschreiben, sondern daneben aufheben.
                KeepUnreadable();
                state = new ServiceState();
            }
            if (state.Tokens.TryGetValue(id, out var existing)) return existing;

            var token = PairingCodes.NewToken();
            state.Tokens[id] = token;
            Write(state);
            return token;
        }
        finally { mutex.ReleaseMutex(); }
    }

    // null = Datei vorhanden, aber nicht lesbar (beschädigt, oder mit einem anderen Schutz geschrieben).
    private ServiceState? Load()
    {
        string raw;
        try
        {
            if (!File.Exists(FilePath)) return new ServiceState();
            raw = PairingStore.ReadShared(FilePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (raw.Length == 0) return new ServiceState();

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
            var state = JsonSerializer.Deserialize<ServiceState>(json, Json);
            if (state == null) return null;
            state.Tokens ??= new();
            // Einträge ohne gültige Id oder ohne Token fallen weg
            foreach (var entry in state.Tokens.Where(e => !ValidId(e.Key) || string.IsNullOrEmpty(e.Value)).Select(e => e.Key).ToList())
                state.Tokens.Remove(entry);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    private void Write(ServiceState state)
    {
        var json = JsonSerializer.Serialize(state, Json);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (ReferenceEquals(_protector, DataProtectors.None))
        {
            // Ohne Schutz (Linux) liegt die Datei als JSON im Benutzerordner: so ist es bei der Kopplungsablage auch.
            PairingStore.WriteAtomic(FilePath, json);
            return;
        }
        // Mit Schutz nie als Klartext: scheitert der Schutz, wird nichts geschrieben.
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json))
            ?? throw new InvalidOperationException("Die Tokens lassen sich hier nicht schützen; sie werden nicht gespeichert.");
        PairingStore.WriteAtomic(FilePath, Convert.ToBase64String(protectedBytes));
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

    // Wie eine Modul-Id der Zentrale: Buchstaben, Ziffern und Bindestrich
    private static bool ValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Der Inhalt von services.dat: Dienst-Id -> Token
    private sealed class ServiceState
    {
        public Dictionary<string, string> Tokens { get; set; } = new();
    }
}

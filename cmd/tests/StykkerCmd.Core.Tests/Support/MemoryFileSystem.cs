using System.Text;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Model;

namespace StykkerCmd.Core.Tests.Support;

// Speicher-Dateisystem für Tests. Pfade sind Unix-artig ("/vol1/ordner/datei.txt"); der erste Pfadteil ist der Datenträger.
// Links verhalten sich wie echte Links: Listen, Lesen und Identität folgen ihnen, Entfernen und Umbenennen betreffen den Link.
public sealed class MemoryFileSystem : IFileSystem
{
    private static readonly DateTimeOffset DefaultTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Node
    {
        public bool IsDirectory { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTimeOffset Modified { get; set; } = DefaultTime;
        public string? LinkTarget { get; init; }
        public long Identity { get; init; }
    }

    // Schreibt den Inhalt beim Schließen zurück. Ein abgebrochener Schreibvorgang hinterlässt so einen Teilstand.
    private sealed class WriteBackStream(Action<byte[]> onClose) : MemoryStream
    {
        private bool _closed;

        protected override void Dispose(bool disposing)
        {
            Close_once();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Close_once();
            return base.DisposeAsync();
        }

        private void Close_once()
        {
            if (_closed)
                return;
            _closed = true;
            onClose(ToArray());
        }
    }

    private readonly Dictionary<string, Node> _nodes;
    private readonly HashSet<string> _unreadable;
    private long _nextIdentity;

    public MemoryFileSystem(bool caseSensitive = true)
    {
        CaseSensitive = caseSensitive;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _nodes = new Dictionary<string, Node>(comparer);
        _unreadable = new HashSet<string>(comparer);
        _nodes["/"] = NewDirectory();
    }

    public bool CaseSensitive { get; }

    public char Separator => '/';

    // ---- Testhilfen ------------------------------------------------------------------------

    public void AddDirectory(string path)
    {
        CreateParents(path);
        var key = Normalize(path);
        if (!_nodes.ContainsKey(key))
            _nodes[key] = NewDirectory();
    }

    public void AddFile(string path, string text) => AddFile(path, Encoding.UTF8.GetBytes(text));

    public void AddFile(string path, byte[] content)
    {
        CreateParents(path);
        _nodes[Normalize(path)] = new Node { Content = content, Identity = NextIdentity() };
    }

    public void AddLink(string path, string target)
    {
        CreateParents(path);
        _nodes[Normalize(path)] = new Node { LinkTarget = Normalize(target), Identity = NextIdentity() };
    }

    public void MarkUnreadable(string path) => _unreadable.Add(Normalize(path));

    public bool Contains(string path) => _nodes.ContainsKey(Normalize(path));

    public string ReadText(string path) => Encoding.UTF8.GetString(ResolveFile(path).Content);

    public byte[] ReadBytes(string path) => ResolveFile(path).Content;

    public void RemoveTree(string path)
    {
        var key = Normalize(path);
        foreach (var k in _nodes.Keys.Where(k => k == key || k.StartsWith(key + "/", Comparison())).ToList())
            _nodes.Remove(k);
    }

    // ---- IFileSystem -----------------------------------------------------------------------

    public string Normalize(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    public IReadOnlyList<FileEntry> List(string directory)
    {
        var key = Normalize(directory);
        var dir = Resolve(key) ?? throw new DirectoryNotFoundException(directory);
        if (!dir.IsDirectory)
            throw new IOException($"Kein Ordner: {directory}");
        if (_unreadable.Contains(key))
            throw new UnauthorizedAccessException(directory);

        var entries = new List<FileEntry>();
        foreach (var childKey in ChildKeys(key))
        {
            var child = _nodes[childKey];
            var target = Resolve(childKey);
            var name = ((IFileSystem)this).NameOf(childKey);
            bool isDirectory = target?.IsDirectory ?? false;
            long size = target is { IsDirectory: false } ? target.Content.Length : 0;
            var modified = (target ?? child).Modified;
            entries.Add(new FileEntry(name, ((IFileSystem)this).Combine(key, name), isDirectory, child.LinkTarget is not null, size, modified));
        }
        return entries;
    }

    public bool FileExists(string path) => Resolve(path) is { IsDirectory: false };

    public bool DirectoryExists(string path) => Resolve(path) is { IsDirectory: true };

    public bool IsLink(string path) => Find(path)?.LinkTarget is not null;

    public long FileLength(string path) => ResolveFile(path).Content.Length;

    public DateTimeOffset LastWriteTime(string path) => (Resolve(path) ?? throw new FileNotFoundException(path)).Modified;

    public void SetLastWriteTime(string path, DateTimeOffset time)
        => (Resolve(path) ?? throw new FileNotFoundException(path)).Modified = time;

    public void CopyAttributes(string source, string target)
    {
        // Keine Plattform-Attribute im Speicher.
    }

    public void CreateDirectory(string path)
    {
        var key = Normalize(path);
        if (Find(path) is not null)
            throw new IOException($"Der Ordner existiert bereits: {path}");

        RequireParentDirectory(key);
        _nodes[key] = NewDirectory();
    }

    public Stream OpenRead(string path)
    {
        if (_unreadable.Contains(Normalize(path)))
            throw new UnauthorizedAccessException(path);
        return new MemoryStream(ResolveFile(path).Content, writable: false);
    }

    public Stream CreateNewFile(string path)
    {
        var key = Normalize(path);
        if (Find(path) is not null)
            throw new IOException($"Die Datei existiert bereits: {path}");

        RequireParentDirectory(key);
        var node = new Node { Identity = NextIdentity() };
        _nodes[key] = node;
        return new WriteBackStream(bytes => node.Content = bytes);
    }

    public void DeleteFile(string path)
    {
        var key = Normalize(path);
        if (!_nodes.TryGetValue(key, out var node))
            throw new FileNotFoundException(path);
        if (node.IsDirectory)
            throw new IOException($"Ist ein Ordner: {path}");
        _nodes.Remove(key);
    }

    public void DeleteEmptyDirectory(string path)
    {
        var key = Normalize(path);
        if (!_nodes.TryGetValue(key, out var node))
            throw new DirectoryNotFoundException(path);

        if (node.LinkTarget is not null)
        {
            _nodes.Remove(key); // Nur der Link verschwindet, das Ziel bleibt.
            return;
        }

        if (!node.IsDirectory)
            throw new IOException($"Kein Ordner: {path}");
        if (ChildKeys(key).Any())
            throw new IOException($"Der Ordner ist nicht leer: {path}");
        _nodes.Remove(key);
    }

    public void Rename(string source, string target)
    {
        var from = Normalize(source);
        var to = Normalize(target);
        if (!_nodes.ContainsKey(from))
            throw new FileNotFoundException(source);
        if (_nodes.ContainsKey(to))
            throw new IOException($"Das Ziel existiert bereits: {target}");

        RequireParentDirectory(to);

        var moving = _nodes.Keys.Where(k => k == from || k.StartsWith(from + "/", Comparison())).ToList();
        foreach (var key in moving)
        {
            var node = _nodes[key];
            _nodes.Remove(key);
            _nodes[to + key[from.Length..]] = node;
        }
    }

    // Jeder Ordner direkt unter der Wurzel ist ein Datenträger.
    public IReadOnlyList<VolumeInfo> Volumes()
        => _nodes.Where(pair => pair.Key != "/" && pair.Value.IsDirectory && ParentOf(pair.Key) == "/")
            .Select(pair => pair.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new VolumeInfo(key, "Test"))
            .ToList();

    public string VolumeOf(string path)
    {
        var segments = Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + segments[0];
    }

    public string IdentityOf(string directory)
        => (Resolve(directory) ?? throw new DirectoryNotFoundException(directory)).Identity.ToString();

    // ---- intern ----------------------------------------------------------------------------

    private StringComparison Comparison() => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private Node NewDirectory() => new() { IsDirectory = true, Identity = NextIdentity() };

    private long NextIdentity() => ++_nextIdentity;

    private Node? Find(string path) => _nodes.TryGetValue(Normalize(path), out var node) ? node : null;

    // Folgt Links, höchstens 40 Sprünge.
    private Node? Resolve(string path)
    {
        var node = Find(path);
        for (int hops = 0; node?.LinkTarget is { } target; hops++)
        {
            if (hops > 40)
                throw new IOException("Zu viele verschachtelte Verknüpfungen.");
            node = Find(target);
        }
        return node;
    }

    private Node ResolveFile(string path)
    {
        var node = Resolve(path);
        if (node is null || node.IsDirectory)
            throw new FileNotFoundException(path);
        return node;
    }

    private string ParentOf(string key)
    {
        int i = key.LastIndexOf('/');
        return i <= 0 ? "/" : key[..i];
    }

    private IEnumerable<string> ChildKeys(string dirKey)
        => _nodes.Keys.Where(k => k != dirKey && ParentOf(k).Equals(dirKey, Comparison())).ToList();

    private void RequireParentDirectory(string key)
    {
        var parent = ParentOf(key);
        var node = Resolve(parent) ?? throw new DirectoryNotFoundException(parent);
        if (!node.IsDirectory)
            throw new DirectoryNotFoundException(parent);
    }

    private void CreateParents(string path)
    {
        var key = Normalize(path);
        var parent = ParentOf(key);
        if (parent == key)
            return;
        if (!_nodes.ContainsKey(parent))
        {
            CreateParents(parent);
            _nodes[parent] = NewDirectory();
        }
    }
}

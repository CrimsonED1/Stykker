using StykkerMeshShare.Core;

namespace StykkerMeshShare.Server;

// Nimmt lokale Änderungen auf: der Watcher meldet sie (gesammelt, 1,5 s Ruhe), und ein voller Scan läuft trotzdem jede
// Minute, weil ein Watcher Ereignisse verlieren kann. War eine Datei noch in Arbeit, kommt nach 3 s ein weiterer Scan.
public sealed class FolderWatcher(MeshNode node) : IHostedService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _periodic;
    private int _running;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _debounce = new Timer(_ => ScanNow(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new Timer(_ => ScanNow(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        try
        {
            _watcher = new FileSystemWatcher(node.Folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, _) => Soon();
            _watcher.Created += (_, _) => Soon();
            _watcher.Deleted += (_, _) => Soon();
            _watcher.Renamed += (_, _) => Soon();
            _watcher.Error += (_, _) => Soon();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"[watch] {ex.Message} – changes are picked up once a minute.");
        }
        return Task.CompletedTask;
    }

    public void Soon(int milliseconds = 1500) => _debounce?.Change(milliseconds, Timeout.Infinite);

    private void ScanNow()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) { Soon(); return; }
        try
        {
            node.Scan();
            if (node.PendingRescan) Soon(3000);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[scan] {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher != null) _watcher.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _periodic?.Dispose();
    }
}

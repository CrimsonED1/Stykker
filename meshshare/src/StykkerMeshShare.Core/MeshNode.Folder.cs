namespace StykkerMeshShare.Core;

// Den geteilten Ordner wechseln und Dateien aus der Oberfläche hineinlegen. Siehe MeshNode.cs.
public sealed partial class MeshNode
{
    // ── Ordner wechseln ──

    // Der neue Ordner muss leer sein oder noch nicht bestehen.
    //  move = true:  alle Dateien ziehen in den neuen Ordner um (Zeiten bleiben). Der Index gilt weiter; die anderen PCs
    //                merken nichts davon.
    //  move = false: der neue Ordner fängt leer an und holt alles von den anderen PCs. Der alte Ordner bleibt, wie er
    //                ist; an die anderen PCs geht keine Löschung. Dateien, die nur hier lagen, bleiben im alten Ordner.
    // Rückgabe: null bei Erfolg, sonst der Grund (englisch, für die Oberfläche).
    public string? ChangeFolder(string newFolder, bool move)
    {
        // Erst den laufenden Download anhalten und die offenen Teildateien schließen: sie liegen im Ordner
        // (.stykker\partial) und müssen mit umziehen bzw. weg.
        Interlocked.Increment(ref _holdTransfers);
        try
        {
            CancellationTokenSource? running;
            lock (_queueLock) running = _currentCancel;
            Cancel(running);
            for (int i = 0; i < 100 && _currentPath != null; i++) Thread.Sleep(50);
            CloseParked();
            return ChangeFolderCore(newFolder, move);
        }
        finally { Interlocked.Decrement(ref _holdTransfers); }
    }

    // Prüft einen Ordner, bevor er gewählt wird (für die Oberfläche beim Tippen): null, wenn er taugt.
    public string? CheckFolder(string newFolder)
    {
        var problem = FolderRules.Check(newFolder, _folder, _options.DataDir);
        if (problem != null) return problem;
        try
        {
            var target = Path.GetFullPath(newFolder);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return "The new folder is not empty. Choose an empty folder, or one that does not exist yet.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ex.Message; }
        return null;
    }

    private string? ChangeFolderCore(string newFolder, bool move)
    {
        string target;
        string old;
        _gate.Wait();
        try
        {
            var problem = FolderRules.Check(newFolder, _folder, _options.DataDir);
            if (problem != null) return problem;
            target = Path.GetFullPath(newFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return "The new folder is not empty. Choose an empty folder, or one that does not exist yet.";

            Directory.CreateDirectory(target);
            if (move)
            {
                var error = MoveAll(_folder, target);
                if (error != null) return error;
            }
            else
            {
                // Ohne die Einträge der vorhandenen Dateien holt der Abgleich sie neu; Grabsteine bleiben, damit eine
                // alte Löschung nicht zurückkommt. Alle Gegenstellen werden von vorn gelesen.
                foreach (var entry in _entries.Values.Where(e => !e.Deleted).ToList()) _entries.Remove(entry.Path);
                _cursors.Clear();
            }
            old = _folder;
            _folder = target;
            _folderGen++;
            OpenPartialStore();
            HeldDeletes = 0;
            _allowMassDelete = false;
            Record(ActivityKind.Folder, "", null, move ? $"Folder moved from {old}" : "Folder changed: fetching the files from the other PCs");
            Save();
        }
        finally { _gate.Release(); }
        // Beim Neuanfang bleiben die alten Dateien liegen; die Teildateien im alten Ordner braucht niemand mehr.
        if (!move)
        {
            try { Directory.Delete(WorkDir(old), recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        FolderChanged?.Invoke(target);
        Scan();
        return null;
    }

    // Zieht alle Dateien um. Geht unterwegs etwas schief, werden die schon verschobenen zurückgelegt.
    private static string? MoveAll(string from, string to)
    {
        if (!Directory.Exists(from)) return null;
        var moved = new List<(string From, string To)>();
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var source in Directory.EnumerateFiles(from, "*", enumeration).ToList())
            {
                var destination = Path.Combine(to, Path.GetRelativePath(from, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var time = File.GetLastWriteTimeUtc(source);
                File.Move(source, destination);
                File.SetLastWriteTimeUtc(destination, time);
                moved.Add((source, destination));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (source, destination) in Enumerable.Reverse(moved))
            {
                try { File.Move(destination, source); }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException) { }
            }
            return $"Could not move the files: {ex.Message} Nothing was changed.";
        }
        // Leere Unterordner bleiben sonst im alten Ordner stehen; der alte Ordner selbst auch, wenn er leer ist.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", enumeration).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // ── Hochladen aus der Oberfläche ──

    // Legt eine Datei in den Ordner (über eine Temp-Datei). Eine vorhandene Datei gleichen Namens bekommt " (2)" usw.
    public async Task<string?> AddFileAsync(string relativePath, Stream content, CancellationToken ct)
    {
        var relative = PathRules.Normalize(relativePath);
        if (relative == null || PathRules.IsIgnored(relative)) return null;
        Directory.CreateDirectory(TempDir);
        var temp = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var name = FreeName(relative);
                if (name == null) return null;
                var full = Full(name);
                if (full == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Move(temp, full);
                return name;
            }
            finally { _gate.Release(); }
        }
        finally { TryDelete(temp); }
    }

    private string? FreeName(string relative)
    {
        var full = Full(relative);
        if (full == null) return null;
        if (!File.Exists(full) && !Directory.Exists(full)) return relative;
        var slash = relative.LastIndexOf('/');
        var dir = slash >= 0 ? relative[..(slash + 1)] : "";
        var name = relative[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var ext = dot > 0 ? name[dot..] : "";
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{dir}{stem} ({i}){ext}";
            var candidateFull = Full(candidate);
            if (candidateFull != null && !File.Exists(candidateFull)) return candidate;
        }
        return null;
    }
}

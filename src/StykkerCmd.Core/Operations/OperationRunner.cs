using System.Diagnostics;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Model;

namespace StykkerCmd.Core.Operations;

// Führt Dateioperationen gegen IFileSystem aus. Fehler einzelner Elemente werden gesammelt; der Lauf geht weiter.
// Abbruch stoppt den Lauf: Teilstände bleiben als markierte Dateien liegen, Quellen bleiben vollständig.
public sealed class OperationRunner
{
    // Puffergröße beim Kopieren.
    public const int BufferSize = 1024 * 1024;

    // Markierung für Teilstände. Ein unvollständiges Ziel trägt diesen Anhang und nie den Endnamen.
    public const string PartialSuffix = ".stykker-teil";

    private readonly IFileSystem _fs;
    private readonly ITrash? _trash;

    public OperationRunner(IFileSystem fileSystem, ITrash? trash)
    {
        _fs = fileSystem;
        _trash = trash;
    }

    public Task<OperationResult> RunAsync(
        OperationRequest request,
        ConflictHandler? onConflict,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
        => new Session(_fs, _trash, onConflict, progress, ct).RunRequestAsync(request);

    // Führt einzelne Schritte aus, etwa Wiederholungen mit erhöhten Rechten. Konflikte werden übersprungen.
    public Task<OperationResult> RunLeavesAsync(
        IReadOnlyList<LeafOperation> leaves,
        IProgress<OperationProgress>? progress,
        CancellationToken ct)
        => new Session(_fs, _trash, null, progress, ct).RunLeavesAsync(leaves);

    // Ein Lauf hat eigenen Zustand; der Runner selbst bleibt zustandslos.
    private sealed class Session
    {
        private readonly record struct Resolution(string Path, bool Overwrite);

        private readonly IFileSystem _fs;
        private readonly ITrash? _trash;
        private readonly ConflictHandler? _onConflict;
        private readonly IProgress<OperationProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly List<OperationIssue> _issues = [];
        private readonly List<string> _partials = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = -1000;
        private ConflictAnswer? _sticky;
        private int _filesTotal;
        private int _filesDone;
        private int _skipped;
        private long _bytesTotal;
        private long _bytesDone;

        public Session(
            IFileSystem fs,
            ITrash? trash,
            ConflictHandler? onConflict,
            IProgress<OperationProgress>? progress,
            CancellationToken ct)
        {
            _fs = fs;
            _trash = trash;
            _onConflict = onConflict;
            _progress = progress;
            _ct = ct;
        }

        public async Task<OperationResult> RunRequestAsync(OperationRequest request)
        {
            if (request.Sources.Count == 0)
                throw new ArgumentException("Keine Elemente ausgewählt.", nameof(request));

            bool cancelled = false;
            try
            {
                switch (request.Kind)
                {
                    case OperationKind.Copy:
                    case OperationKind.Move:
                        ValidateTransfer(request);
                        await ScanAsync(request.Sources, followLinks: true);
                        foreach (var source in request.Sources)
                            await TransferTopLevelAsync(request.Kind == OperationKind.Move, source, request.TargetDirectory!);
                        break;

                    case OperationKind.DeletePermanent:
                        await ScanAsync(request.Sources, followLinks: false);
                        foreach (var source in request.Sources)
                            await DeleteTreeAsync(source);
                        break;

                    case OperationKind.DeleteToTrash:
                        _filesTotal = request.Sources.Count;
                        foreach (var source in request.Sources)
                            TrashOne(source);
                        break;

                    case OperationKind.CreateDirectory:
                        _filesTotal = 1;
                        CreateDirectoryLeaf(request.Sources[0]);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            return Finish(cancelled);
        }

        public async Task<OperationResult> RunLeavesAsync(IReadOnlyList<LeafOperation> leaves)
        {
            bool cancelled = false;
            try
            {
                _filesTotal = leaves.Count;
                foreach (var leaf in leaves)
                {
                    _ct.ThrowIfCancellationRequested();
                    await RunLeafAsync(leaf);
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            return Finish(cancelled);
        }

        private async Task RunLeafAsync(LeafOperation leaf)
        {
            switch (leaf.Kind)
            {
                case LeafKind.CopyFile:
                    if (!Exists(leaf.Target!))
                        await CopyFileLeafAsync(leaf.Source, leaf.Target!, overwrite: false);
                    break;

                case LeafKind.MoveFile:
                    if (!Exists(leaf.Target!))
                        RenameReplacing(leaf.Source, leaf.Target!, overwrite: false, isDirectory: _fs.DirectoryExists(leaf.Source));
                    break;

                case LeafKind.CreateDirectory:
                    if (!Exists(leaf.Target!))
                        CreateDirectoryLeaf(leaf.Target!);
                    break;

                case LeafKind.DeleteFile:
                    DeleteFileLeaf(leaf.Source);
                    break;

                case LeafKind.DeleteEmptyDirectory:
                    DeleteEmptyDirectoryLeaf(leaf.Source);
                    break;

                case LeafKind.Trash:
                    TrashOne(leaf.Source);
                    break;
            }
        }

        private void ValidateTransfer(OperationRequest request)
        {
            if (string.IsNullOrEmpty(request.TargetDirectory))
                throw new ArgumentException("Kein Zielordner angegeben.", nameof(request));

            if (!_fs.DirectoryExists(request.TargetDirectory))
                throw new DirectoryNotFoundException($"Der Zielordner existiert nicht: {request.TargetDirectory}");

            foreach (var source in request.Sources)
            {
                if (_fs.DirectoryExists(source) && _fs.IsSameOrInside(request.TargetDirectory, source))
                    throw new ArgumentException($"Der Zielordner liegt im Quellordner: {source}", nameof(request));
            }
        }

        // ---- Zählen für den Fortschritt -------------------------------------------------------

        private async Task ScanAsync(IReadOnlyList<string> sources, bool followLinks)
        {
            foreach (var source in sources)
                await ScanPathAsync(source, followLinks, new HashSet<string>());
        }

        private async Task ScanPathAsync(string path, bool followLinks, HashSet<string> ancestors)
        {
            _ct.ThrowIfCancellationRequested();

            if (!followLinks && _fs.IsLink(path))
            {
                _filesTotal++;
                return;
            }

            if (_fs.DirectoryExists(path))
            {
                var identity = followLinks ? IdentityOrNull(path) : null;
                if (identity is not null && !ancestors.Add(identity))
                    return; // Schleife: dieser Ordner liegt schon im aktuellen Pfad.

                try
                {
                    foreach (var entry in ListOrEmpty(path))
                        await ScanPathAsync(entry.FullPath, followLinks, ancestors);
                }
                finally
                {
                    if (identity is not null)
                        ancestors.Remove(identity);
                }
                return;
            }

            if (_fs.FileExists(path))
            {
                _filesTotal++;
                _bytesTotal += LengthOrZero(path);
            }
        }

        // ---- Kopieren und Verschieben ---------------------------------------------------------

        private async Task TransferTopLevelAsync(bool move, string source, string targetDirectory)
        {
            var target = _fs.Combine(targetDirectory, _fs.NameOf(source));
            var comparison = _fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            if (_fs.Normalize(source).Equals(_fs.Normalize(target), comparison))
            {
                AddIssueMessage(move ? LeafKind.MoveFile : LeafKind.CopyFile, source, target,
                    IssueReason.SameLocation, "Quelle und Ziel sind identisch.");
                return;
            }

            await TransferAsync(move, source, target, new HashSet<string>());
        }

        private async Task TransferAsync(bool move, string source, string target, HashSet<string> ancestors)
        {
            _ct.ThrowIfCancellationRequested();

            bool isDirectory = _fs.DirectoryExists(source);
            if (!isDirectory && !_fs.FileExists(source))
            {
                AddIssueMessage(move ? LeafKind.MoveFile : LeafKind.CopyFile, source, target, IssueReason.NotFound, "Nicht gefunden.");
                return;
            }

            bool isLink = _fs.IsLink(source);
            bool sameVolume = SameVolume(source, target);

            // Ein Link über Datenträgergrenzen wäre nur eine Kopie des Ziels. Das verweigern wir lieber.
            if (move && isLink && !sameVolume)
            {
                AddIssueMessage(LeafKind.MoveFile, source, target, IssueReason.Other,
                    "Verknüpfungen lassen sich nicht über Datenträgergrenzen verschieben.");
                return;
            }

            var resolution = await ResolveTargetAsync(source, target, isDirectory);
            if (resolution is null)
                return;

            var (path, overwrite) = resolution.Value;

            if (move && sameVolume)
            {
                if (isDirectory && !isLink && _fs.DirectoryExists(path))
                    await CopyOrMergeDirectoryAsync(move, source, path, ancestors);   // Ordner existiert schon: Inhalte einzeln verschieben
                else
                    RenameReplacing(source, path, overwrite, isDirectory);            // Atomar umbenennen
                return;
            }

            if (isDirectory)
            {
                await CopyOrMergeDirectoryAsync(move, source, path, ancestors);
                return;
            }

            // Kopie, auch für Verschieben über Datenträgergrenzen: Die Quelle wird erst nach geprüftem Ziel entfernt.
            bool copied = await CopyFileLeafAsync(source, path, overwrite);
            if (copied && move)
                DeleteFileLeaf(source);
        }

        private async Task CopyOrMergeDirectoryAsync(bool move, string source, string target, HashSet<string> ancestors)
        {
            var identity = IdentityOrNull(source);
            if (identity is not null && ancestors.Contains(identity))
            {
                AddIssueMessage(LeafKind.CreateDirectory, source, target, IssueReason.Loop,
                    "Schleife erkannt, übersprungen.");
                return;
            }

            // Inhalte vor dem Anlegen des Ziels lesen. So kann eine Kopie nicht in sich selbst hineinwachsen.
            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = _fs.List(source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.CreateDirectory, source, target, ex);
                return;
            }

            if (!_fs.DirectoryExists(target))
            {
                try
                {
                    _fs.CreateDirectory(target);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddIssue(LeafKind.CreateDirectory, source, target, ex);
                    return;
                }
            }

            if (identity is not null)
                ancestors.Add(identity);
            try
            {
                foreach (var entry in entries)
                    await TransferAsync(move, entry.FullPath, _fs.Combine(target, entry.Name), ancestors);
            }
            finally
            {
                if (identity is not null)
                    ancestors.Remove(identity);
            }

            // Beim Verschieben verschwindet der Quellordner nur, wenn er leer ist. Übersprungene Dateien bleiben dann stehen.
            if (move)
                RemoveSourceDirectoryIfEmpty(source);
        }

        private async Task<bool> CopyFileLeafAsync(string source, string target, bool overwrite)
        {
            var part = target + PartialSuffix;
            long length;
            try
            {
                length = _fs.FileLength(source);
                if (_fs.FileExists(part))
                    _fs.DeleteFile(part);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.CopyFile, source, target, ex);
                return false;
            }

            try
            {
                await using (var input = _fs.OpenRead(source))
                await using (var output = _fs.CreateNewFile(part))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await input.ReadAsync(buffer, _ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), _ct);
                        _bytesDone += read;
                        Report("Kopieren", source);
                    }

                    await output.FlushAsync(_ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Markierter Teilstand bleibt liegen; die Quelle ist unberührt.
                if (_fs.FileExists(part))
                    _partials.Add(part);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(part);
                AddIssue(LeafKind.CopyFile, source, target, ex);
                return false;
            }

            // Prüfung vor dem Umbenennen: falsche Größe heißt, das Ziel ist unbrauchbar.
            try
            {
                if (_fs.FileLength(part) != length)
                {
                    TryDelete(part);
                    AddIssueMessage(LeafKind.CopyFile, source, target, IssueReason.Verification,
                        "Die Größe stimmt nach dem Kopieren nicht.");
                    return false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(part);
                AddIssue(LeafKind.CopyFile, source, target, ex);
                return false;
            }

            try
            {
                _fs.CopyAttributes(source, part);
                _fs.SetLastWriteTime(part, _fs.LastWriteTime(source));
                if (overwrite && _fs.FileExists(target))
                    _fs.DeleteFile(target);
                _fs.Rename(part, target);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Das fertige Teilstück bleibt bewusst liegen und ist markiert, falls der Endname nicht mehr erreichbar ist.
                if (_fs.FileExists(part))
                    _partials.Add(part);
                AddIssue(LeafKind.CopyFile, source, target, ex);
                return false;
            }

            _filesDone++;
            Report("Kopieren", target, force: true);
            return true;
        }

        // Benennt um und ersetzt bei Bedarf. Die alte Datei wandert zuerst beiseite, damit bei einem Fehler nichts verloren geht.
        private void RenameReplacing(string source, string target, bool overwrite, bool isDirectory)
        {
            long bytes = 0;
            int files = 0;
            if (isDirectory)
                (files, bytes) = CountSubtree(source);
            else
                bytes = LengthOrZero(source);

            try
            {
                if (overwrite)
                {
                    var part = target + PartialSuffix;
                    _fs.Rename(source, part);
                    try
                    {
                        _fs.DeleteFile(target);
                    }
                    catch
                    {
                        _fs.Rename(part, source); // Rückbau: Quelle wieder an ihren Platz
                        throw;
                    }
                    _fs.Rename(part, target);
                }
                else
                {
                    _fs.Rename(source, target);
                }

                _filesDone += isDirectory ? files : 1;
                _bytesDone += bytes;
                Report("Verschieben", target, force: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.MoveFile, source, target, ex);
            }
        }

        private void RemoveSourceDirectoryIfEmpty(string source)
        {
            try
            {
                if (_fs.List(source).Count == 0)
                    _fs.DeleteEmptyDirectory(source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.DeleteEmptyDirectory, source, null, ex);
            }
        }

        // ---- Konflikte ------------------------------------------------------------------------

        private async Task<Resolution?> ResolveTargetAsync(string source, string target, bool sourceIsDirectory)
        {
            if (!Exists(target))
                return new Resolution(target, false);

            bool targetIsDirectory = _fs.DirectoryExists(target);
            if (sourceIsDirectory && targetIsDirectory)
                return new Resolution(target, false); // Ordner werden zusammengeführt

            bool canOverwrite = !sourceIsDirectory && !targetIsDirectory;
            ConflictAnswer answer;
            if (_sticky is not null)
            {
                answer = _sticky;
            }
            else
            {
                answer = await AskAsync(source, target, sourceIsDirectory, targetIsDirectory, canOverwrite);
            }

            // Ordner über Dateien oder umgekehrt werden nie überschrieben, auch nicht auf "für alle".
            var choice = answer.Choice == ConflictChoice.Overwrite && !canOverwrite ? ConflictChoice.Skip : answer.Choice;

            switch (choice)
            {
                case ConflictChoice.Overwrite:
                    return new Resolution(target, true);

                case ConflictChoice.Rename:
                    return new Resolution(NameGenerator.FreeName(_fs, target, Exists), false);

                case ConflictChoice.Cancel:
                    throw new OperationCanceledException();

                default:
                    _skipped++;
                    return null;
            }
        }

        private async Task<ConflictAnswer> AskAsync(string source, string target, bool sourceIsDirectory, bool targetIsDirectory, bool canOverwrite)
        {
            if (_onConflict is null)
                return new ConflictAnswer(ConflictChoice.Skip);

            var request = new ConflictRequest(
                source,
                target,
                sourceIsDirectory,
                targetIsDirectory,
                canOverwrite,
                LengthOrZero(source),
                LengthOrZero(target),
                TimeOrNow(source),
                TimeOrNow(target));

            var answer = await _onConflict(request);
            if (answer.ApplyToAll && answer.Choice != ConflictChoice.Cancel)
                _sticky = answer;
            return answer;
        }

        // ---- Löschen --------------------------------------------------------------------------

        private async Task DeleteTreeAsync(string path)
        {
            _ct.ThrowIfCancellationRequested();

            // Links werden entfernt, aber nie verfolgt: so kann ein Löschen nicht über Ziele hinauslaufen.
            if (_fs.IsLink(path))
            {
                DeleteLinkLeaf(path);
                return;
            }

            if (_fs.DirectoryExists(path))
            {
                IReadOnlyList<FileEntry> entries;
                try
                {
                    entries = _fs.List(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddIssue(LeafKind.DeleteEmptyDirectory, path, null, ex);
                    return;
                }

                foreach (var entry in entries)
                    await DeleteTreeAsync(entry.FullPath);

                DeleteEmptyDirectoryLeaf(path);
                return;
            }

            if (_fs.FileExists(path))
                DeleteFileLeaf(path);
            else
                AddIssueMessage(LeafKind.DeleteFile, path, null, IssueReason.NotFound, "Nicht gefunden.");
        }

        private void TrashOne(string path)
        {
            _ct.ThrowIfCancellationRequested();

            if (_trash is null || !_trash.IsAvailable(path))
            {
                AddIssueMessage(LeafKind.Trash, path, null, IssueReason.NoTrash,
                    "Auf diesem Datenträger gibt es keinen Papierkorb.");
                return;
            }

            try
            {
                _trash.MoveToTrash(path);
                _filesDone++;
                Report("Papierkorb", path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.Trash, path, null, ex);
            }
        }

        private void DeleteLinkLeaf(string path)
        {
            if (_fs.DirectoryExists(path))
                DeleteEmptyDirectoryLeaf(path);
            else
                DeleteFileLeaf(path);
        }

        private void DeleteFileLeaf(string path)
        {
            try
            {
                _fs.DeleteFile(path);
                _filesDone++;
                Report("Löschen", path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.DeleteFile, path, null, ex);
            }
        }

        private void DeleteEmptyDirectoryLeaf(string path)
        {
            try
            {
                _fs.DeleteEmptyDirectory(path);
                Report("Löschen", path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.DeleteEmptyDirectory, path, null, ex);
            }
        }

        private void CreateDirectoryLeaf(string path)
        {
            if (Exists(path))
            {
                AddIssueMessage(LeafKind.CreateDirectory, path, path, IssueReason.Exists, "Der Name ist bereits vergeben.");
                return;
            }

            try
            {
                _fs.CreateDirectory(path);
                _filesDone++;
                Report("Ordner anlegen", path, force: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddIssue(LeafKind.CreateDirectory, path, path, ex);
            }
        }

        // ---- Hilfen ---------------------------------------------------------------------------

        private bool Exists(string path) => _fs.FileExists(path) || _fs.DirectoryExists(path);

        private bool SameVolume(string source, string target)
            => string.Equals(
                _fs.VolumeOf(source),
                _fs.VolumeOf(target),
                _fs.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        private IReadOnlyList<FileEntry> ListOrEmpty(string path)
        {
            try
            {
                return _fs.List(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return []; // Fehler meldet der eigentliche Lauf beim Verarbeiten des Ordners.
            }
        }

        private (int Files, long Bytes) CountSubtree(string path)
        {
            int files = 0;
            long bytes = 0;
            foreach (var entry in ListOrEmpty(path))
            {
                if (entry.IsDirectory && !entry.IsLink)
                {
                    var (subFiles, subBytes) = CountSubtree(entry.FullPath);
                    files += subFiles;
                    bytes += subBytes;
                }
                else
                {
                    files++;
                    bytes += entry.Size;
                }
            }
            return (files, bytes);
        }

        private string? IdentityOrNull(string path)
        {
            try
            {
                return _fs.IdentityOf(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        private long LengthOrZero(string path)
        {
            try
            {
                return _fs.FileLength(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return 0;
            }
        }

        private DateTimeOffset TimeOrNow(string path)
        {
            try
            {
                return _fs.LastWriteTime(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DateTimeOffset.MinValue;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (_fs.FileExists(path))
                    _fs.DeleteFile(path);
            }
            catch (Exception)
            {
                // Aufräumen ist Kür; der eigentliche Fehler wurde bereits gemeldet.
            }
        }

        private void Report(string phase, string? current, bool force = false)
        {
            if (_progress is null)
                return;

            long now = _clock.ElapsedMilliseconds;
            if (!force && now - _lastReportMs < 50)
                return;

            _lastReportMs = now;
            _progress.Report(new OperationProgress(phase, current, _filesDone, _filesTotal, _bytesDone, _bytesTotal));
        }

        private OperationResult Finish(bool cancelled)
        {
            Report("Fertig", null, force: true);
            return new OperationResult(cancelled, _filesDone, _bytesDone, _skipped, _issues.ToList(), _partials.ToList());
        }

        private void AddIssue(LeafKind kind, string source, string? target, Exception ex)
        {
            var reason = Classify(ex);
            _issues.Add(new OperationIssue(new LeafOperation(kind, source, target), reason, Describe(reason, ex)));
        }

        private void AddIssueMessage(LeafKind kind, string source, string? target, IssueReason reason, string message)
            => _issues.Add(new OperationIssue(new LeafOperation(kind, source, target), reason, message));

        private static IssueReason Classify(Exception ex) => ex switch
        {
            UnauthorizedAccessException => IssueReason.AccessDenied,
            FileNotFoundException or DirectoryNotFoundException => IssueReason.NotFound,
            IOException io when IsLockViolation(io) => IssueReason.Locked,
            _ => IssueReason.Other,
        };

        private static bool IsLockViolation(IOException ex)
            => ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
               || ex.Message.Contains("Text file busy", StringComparison.OrdinalIgnoreCase);

        private static string Describe(IssueReason reason, Exception ex) => reason switch
        {
            IssueReason.AccessDenied => "Zugriff verweigert.",
            IssueReason.NotFound => "Nicht gefunden.",
            IssueReason.Locked => "Die Datei wird gerade von einem Programm verwendet.",
            _ => ex.Message,
        };
    }
}

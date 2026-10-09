using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Model;

namespace StykkerCmd.Platform.Linux;

// Dateisystem für Linux. Identität eines Ordners ist sein kanonischer Pfad (realpath), weil .NET keine Inode-Nummer öffentlich anbietet.
[SupportedOSPlatform("linux")]
public sealed class LinuxFileSystem : IFileSystem
{
    private const int BufferSize = 1024 * 1024;

    public bool CaseSensitive => true;

    public char Separator => '/';

    public string Normalize(string path) => Path.GetFullPath(path).TrimEnd('/');

    public IReadOnlyList<FileEntry> List(string directory)
    {
        var info = new DirectoryInfo(directory);
        var entries = new List<FileEntry>();
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            bool isLink = item.LinkTarget is not null;
            bool isDirectory = Directory.Exists(item.FullName); // folgt Links
            entries.Add(new FileEntry(
                item.Name,
                item.FullName,
                isDirectory,
                isLink,
                isDirectory ? 0 : SizeOf(item),
                new DateTimeOffset(item.LastWriteTime)));
        }
        return entries;
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    public long FileLength(string path) => new FileInfo(path).Length;

    public DateTimeOffset LastWriteTime(string path) => new(File.GetLastWriteTime(path));

    public void SetLastWriteTime(string path, DateTimeOffset time) => File.SetLastWriteTime(path, time.LocalDateTime);

    public void CopyAttributes(string source, string target)
        => File.SetUnixFileMode(target, File.GetUnixFileMode(source));

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Stream CreateNewFile(string path)
        => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void DeleteFile(string path) => File.Delete(path); // bei Links entfernt unlink den Link

    public void DeleteEmptyDirectory(string path)
    {
        // Ein Ordner-Link wird per unlink entfernt; rmdir würde auf einem Link scheitern.
        if (IsLink(path))
            File.Delete(path);
        else
            Directory.Delete(path, recursive: false);
    }

    public void Rename(string source, string target)
    {
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else
            File.Move(source, target);
    }

    public string VolumeOf(string path) => MountTable.VolumeOf(LinuxNative.CanonicalPath(path) ?? Path.GetFullPath(path));

    public string IdentityOf(string directory)
        => LinuxNative.CanonicalPath(directory) ?? throw new DirectoryNotFoundException(directory);

    private static long SizeOf(FileSystemInfo item)
    {
        if (item is not FileInfo file)
            return 0;
        try
        {
            return file.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

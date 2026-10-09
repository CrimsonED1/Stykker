using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using StykkerCmd.Core.Abstractions;
using StykkerCmd.Core.Model;

namespace StykkerCmd.Platform.Windows;

// Dateisystem für Windows. Alle Zugriffe laufen über den erweiterten Pfad "\\?\", damit auch lange Pfade gehen.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsFileSystem : IFileSystem
{
    private const int BufferSize = 1024 * 1024;

    public bool CaseSensitive => false;

    public char Separator => '\\';

    public string Normalize(string path) => WindowsPath.Absolute(path).TrimEnd('\\', '/');

    public IReadOnlyList<FileEntry> List(string directory)
    {
        var info = new DirectoryInfo(WindowsPath.ToExtended(WindowsPath.Absolute(directory)));
        var entries = new List<FileEntry>();
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            bool isDirectory = item.Attributes.HasFlag(FileAttributes.Directory);
            bool isLink = item.Attributes.HasFlag(FileAttributes.ReparsePoint);
            entries.Add(new FileEntry(
                item.Name,
                WindowsPath.FromExtended(item.FullName),
                isDirectory,
                isLink,
                isDirectory ? 0 : SizeOf(item),
                new DateTimeOffset(item.LastWriteTime)));
        }
        return entries;
    }

    public bool FileExists(string path) => File.Exists(Extended(path));

    public bool DirectoryExists(string path) => Directory.Exists(Extended(path));

    public bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(Extended(path)).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public long FileLength(string path) => new FileInfo(Extended(path)).Length;

    public DateTimeOffset LastWriteTime(string path) => new(File.GetLastWriteTime(Extended(path)));

    public void SetLastWriteTime(string path, DateTimeOffset time) => File.SetLastWriteTime(Extended(path), time.LocalDateTime);

    public void CopyAttributes(string source, string target)
    {
        // Windows kennt keine Rechte-Bits wie Linux; Zeitstempel setzt die Engine separat.
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Extended(path));

    public Stream OpenRead(string path)
        => new FileStream(
            Extended(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Stream CreateNewFile(string path)
        => new FileStream(
            Extended(path),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void DeleteFile(string path)
    {
        var target = Extended(path);
        var attributes = File.GetAttributes(target);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        File.Delete(target); // bei Links entfernt das den Link, nicht das Ziel
    }

    public void DeleteEmptyDirectory(string path) => Directory.Delete(Extended(path), recursive: false);

    public void Rename(string source, string target)
    {
        var from = Extended(source);
        var to = Extended(target);
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    public string VolumeOf(string path) => Path.GetPathRoot(WindowsPath.Absolute(path)) ?? string.Empty;

    public string IdentityOf(string directory)
    {
        // Ohne FILE_FLAG_OPEN_REPARSE_POINT folgt das Öffnen dem Link: Die Kennung gehört dann zum Ziel.
        using var handle = NativeMethods.CreateFileW(
            Extended(directory),
            NativeMethods.FILE_READ_ATTRIBUTES,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    public IReadOnlyList<VolumeInfo> Volumes()
        => DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new VolumeInfo(drive.RootDirectory.FullName, VolumeName(drive)))
            .ToList();

    private static string VolumeName(DriveInfo drive)
    {
        if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
            return drive.VolumeLabel;

        return drive.DriveType switch
        {
            DriveType.Removable => "Wechseldatenträger",
            DriveType.Network => "Netzlaufwerk",
            DriveType.CDRom => "CD/DVD",
            _ => "Lokaler Datenträger",
        };
    }

    private static string Extended(string path) => WindowsPath.ToExtended(WindowsPath.Absolute(path));

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
            return 0; // kaputter Link
        }
    }
}

using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Linux;

// Sucht Prozesse über /proc: offene Dateideskriptoren und das laufende Programm (für ETXTBSY beim Schreiben).
// Fremde Prozesse sind ohne Rechte nicht lesbar; dann nennt die Liste nur die eigenen.
[SupportedOSPlatform("linux")]
public sealed class LinuxLockInspector : ILockInspector
{
    public IReadOnlyList<LockHolder> FindHolders(string path)
    {
        var target = LinuxNative.CanonicalPath(path) ?? Path.GetFullPath(path);
        var holders = new List<LockHolder>();

        IEnumerable<string> processes;
        try
        {
            processes = Directory.EnumerateDirectories("/proc");
        }
        catch (IOException)
        {
            return holders;
        }

        foreach (var processDir in processes)
        {
            if (!int.TryParse(Path.GetFileName(processDir), out var pid))
                continue;
            if (UsesFile(processDir, target))
                holders.Add(new LockHolder(pid, ReadName(processDir, pid)));
        }
        return holders;
    }

    private static bool UsesFile(string processDir, string target)
    {
        try
        {
            foreach (var descriptor in Directory.EnumerateFileSystemEntries(Path.Combine(processDir, "fd")))
            {
                if (new FileInfo(descriptor).LinkTarget == target)
                    return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // fremder Prozess: nicht lesbar
        }

        try
        {
            return new FileInfo(Path.Combine(processDir, "exe")).LinkTarget == target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadName(string processDir, int pid)
    {
        try
        {
            var name = File.ReadAllText(Path.Combine(processDir, "comm")).Trim();
            return string.IsNullOrEmpty(name) ? $"Prozess {pid}" : name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Prozess {pid}";
        }
    }
}

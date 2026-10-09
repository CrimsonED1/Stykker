using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Findet Prozesse, die eine Datei offen halten, über den Restart Manager.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsLockInspector : ILockInspector
{
    public IReadOnlyList<LockHolder> FindHolders(string path)
    {
        var file = WindowsPath.Absolute(path);

        // Der Sitzungsschlüssel ist höchstens 32 Zeichen lang.
        if (NativeMethods.RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0)
            return [];

        try
        {
            if (NativeMethods.RmRegisterResources(session, 1, [file], 0, null, 0, null) != 0)
                return [];

            uint count = 0;
            int result = NativeMethods.RmGetList(session, out uint needed, ref count, null, out _);
            if (result != 0 && result != NativeMethods.ERROR_MORE_DATA)
                return [];
            if (needed == 0)
                return [];

            var infos = new NativeMethods.RM_PROCESS_INFO[needed];
            count = needed;
            result = NativeMethods.RmGetList(session, out _, ref count, infos, out _);
            if (result != 0)
                return [];

            return infos
                .Take((int)count)
                .Select(info => new LockHolder(
                    info.Process.ProcessId,
                    string.IsNullOrWhiteSpace(info.AppName) ? $"Prozess {info.Process.ProcessId}" : info.AppName))
                .ToList();
        }
        finally
        {
            NativeMethods.RmEndSession(session);
        }
    }
}

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace StykkerCmd.Platform.Windows;

// Aufrufe in kernel32, shell32 und den Restart Manager. Nur hier stehen P/Invoke-Signaturen.
internal static class NativeMethods
{
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_SHARE_READ = 0x0001;
    public const uint FILE_SHARE_WRITE = 0x0002;
    public const uint FILE_SHARE_DELETE = 0x0004;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // nötig, um Ordner zu öffnen

    public const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out ShellInterop.IShellFolder folder, out IntPtr childPidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    // Fensterunterklasse: Nachrichten des Fensters laufen erst durch diese Funktion, bevor sie an das Fenster gehen.
    public delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, UIntPtr subclassId, UIntPtr refData);

    [DllImport("comctl32.dll")]
    public static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc proc, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    public static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint session, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    public static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(
        uint session,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RM_UNIQUE_PROCESS[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmGetList(
        uint session,
        out uint neededCount,
        ref uint count,
        [In, Out] RM_PROCESS_INFO[]? affected,
        out uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    public struct RM_UNIQUE_PROCESS
    {
        public int ProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}

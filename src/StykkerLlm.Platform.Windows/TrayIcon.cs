using System.Runtime.InteropServices;

namespace StykkerLlm.Platform.Windows;

// Tray-Symbol für den Server, ohne WinForms: der Server läuft ohne Fenster und soll unter Linux gar nicht erst
// Win32 aufrufen. Dazu ein Message-only-Fenster auf eigenem Thread mit eigener Nachrichtenschleife und
// Shell_NotifyIcon. Nichts hier ist fatal – ohne Symbol läuft der Server weiter, nur erreicht man ihn dann nicht
// über das Symbol.
public sealed class TrayIcon : IDisposable
{
    // Ein Eintrag im Rechtsklick-Menü. Separator = true ergibt einen Trennstrich ohne Aktion.
    public sealed record Item(int Id, string Text, bool Separator = false);

    private const int WmClose = 0x0010, WmDestroy = 0x0002, WmNull = 0x0281, WmCommand = 0x0111;
    private const int WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205;
    private const int WmApp = 0x8000 + 1;            // eigene Nachricht: die Maus auf dem Symbol
    private const int NimAdd = 0, NimDelete = 2;
    private const int NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002, TpmReturnCmd = 0x0100, TpmNonotify = 0x0080;

    private static readonly IntPtr HwndMessage = new(-3);        // HWND_MESSAGE: Fenster ohne Rahmen, nur für Nachrichten
    private static readonly IntPtr IdiApplication = new(32512);
    private static readonly IntPtr Instance = GetModuleHandle(null);

    private readonly object _gate = new();
    private readonly Action<int> _onMenu;
    private readonly Action _onOpen;
    private readonly IReadOnlyList<Item> _items;
    private Thread? _thread;
    private IntPtr _window, _icon;
    private bool _added;
    private string? _grund;

    public bool Visible { get { lock (_gate) return _added; } }

    // Ob die Shell hier überhaupt ein Symbol annehmen kann: Windows mit Bildschirm und ein user32 mit dem
    // Einstiegspunkt. In manchen Umgebungen (gefilterte user32) fehlt der – dann läuft der Server ohne Symbol.
    public static bool Possible => OperatingSystem.IsWindows() && Environment.UserInteractive && HasNotifyIcon();

    private static bool HasNotifyIcon()
    {
        if (!NativeLibrary.TryLoad("user32.dll", out var lib)) return false;
        try { return NativeLibrary.TryGetExport(lib, "Shell_NotifyIconW", out _); }
        finally { NativeLibrary.Free(lib); }
    }

    // Das Fenster, hinter dem das Symbol hängt. Ein Test schickt darüber eine Menünachricht an diesen Pfad.
    public IntPtr Handle { get { lock (_gate) return _window; } }

    public TrayIcon(IReadOnlyList<Item> items, Action<int> onMenu, Action onOpen)
    {
        _items = items;
        _onMenu = onMenu;
        _onOpen = onOpen;
    }

    // Symbol mit Menü anzeigen. Antwortet false mit einem Grund, wenn es nicht klappt – dann läuft der Server ohne Symbol.
    public bool TryShow(string tooltip, out string? error)
    {
        error = null;
        if (!Possible)
        {
            error = !OperatingSystem.IsWindows() ? "no tray outside Windows"
                : !Environment.UserInteractive ? "no desktop for this session"
                : "user32.dll here has no Shell_NotifyIconW";
            return false;
        }

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(tooltip, ready))
        {
            IsBackground = true,   // der Server, nicht das Symbol, hält die Anwendung am Leben
            Name = "Stykker tray",
        };
        _thread.Start();
        if (ready.Wait(TimeSpan.FromSeconds(5)))
        {
            lock (_gate) error = _grund;
            return Visible;
        }
        error = "the tray icon did not come up within 5 s";
        return false;
    }

    private void Run(string tooltip, ManualResetEventSlim ready)
    {
        string cls = "StykkerTray" + Environment.ProcessId.ToString("x8");
        bool registered = false;
        try
        {
            var wndProc = new WndProc(WindowProc);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc), hInstance = Instance,
                lpszClassName = cls, hIcon = LoadIcon(IntPtr.Zero, IdiApplication),   // Message-only-Fenster: ohne Bild, nur der Rahmen
            };
            registered = RegisterClassEx(ref wc) != 0;
            var window = CreateWindowEx(0, cls, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, Instance, IntPtr.Zero);
            lock (_gate) { _window = window; _icon = AppIcon(); }
            if (window == IntPtr.Zero) { Fail(ready, $"the tray window was not created (error {Marshal.GetLastWin32Error()})"); return; }

            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window, uID = 1,
                uFlags = NifMessage | NifIcon | NifTip, uCallbackMessage = WmApp, hIcon = _icon, szTip = ShortTip(tooltip),
            };
            bool added = Shell_NotifyIcon(NimAdd, ref data);
            lock (_gate) _added = added;
            ready.Set();
            if (!added) { Cleanup(registered, cls); return; }

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            // Der Tray-Thread darf den ganzen Server nicht mitnehmen: der Grund landet im Serverlog.
            Fail(ready, ex.Message);
        }
        Cleanup(registered, cls);
    }

    // ready ist das Signal an TryShow; danach darf es nicht noch einmal gesetzt werden (es würde werfen).
    private void Fail(ManualResetEventSlim ready, string grund)
    {
        lock (_gate) _grund = grund;
        try { ready.Set(); } catch (ObjectDisposedException) { }
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmApp:
                // lParam trägt die Mausnachricht: links öffnet die Weboberfläche, rechts zeigt das Menü
                int mouse = unchecked((int)lParam.ToInt64());
                if (mouse is WmLButtonDblClk or WmLButtonUp) Guard(_onOpen);
                else if (mouse == WmRButtonUp) ShowMenu();
                return IntPtr.Zero;
            case WmCommand:
                Guard(() => _onMenu(unchecked((int)(wParam.ToInt64() & 0xFFFF))));
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void Guard(Action a)
    {
        try { a(); } catch { /* ein Klick darf den Tray-Thread nicht beenden */ }
    }

    // Rechtsklick: Menü an der Zeigerposition. Danach WM_NULL an sich selbst, sonst behält das Menü den Vordergrund
    // und der nächste Klick geht ins Leere (das ist der Grund für SetForegroundWindow davor).
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        foreach (var it in _items)
        {
            if (it.Separator) AppendMenu(menu, MfSeparator, 0, null);
            else AppendMenu(menu, 0, new IntPtr(it.Id), it.Text);
        }
        GetCursorPos(out var pt);
        var hwnd = Handle;
        SetForegroundWindow(hwnd);
        var cmd = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNonotify, pt.X, pt.Y, hwnd, IntPtr.Zero);
        PostMessage(hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd != IntPtr.Zero) Guard(() => _onMenu(cmd.ToInt32()));
    }

    // Das Symbol aus der eigenen Datei (ApplicationIcon), sonst das Standardsymbol des Systems
    private static IntPtr AppIcon()
    {
        var exe = Environment.ProcessPath;
        // LoadImage wäre für Systemsymbole der kürzere Weg, stürzt aber in manchen Umgebungen mit LR_DEFAULTSIZE
        // hart ab (0xC0000005, nicht abfangbar) – deshalb nur ExtractIconEx und LoadIcon.
        if (!string.IsNullOrEmpty(exe) && ExtractIconEx(exe, 0, out var big, out _, 1) != 0 && big != IntPtr.Zero)
            return big;
        return LoadIcon(IntPtr.Zero, IdiApplication);
    }

    // Der Kurzerklaerungstext der Notification area fasst 63 Zeichen; längerer Text wird von Windows kommentlos
    // abgeschnitten.
    private static string ShortTip(string tip) => tip.Length <= 63 ? tip : tip[..63];

    private void Cleanup(bool registered, string cls)
    {
        lock (_gate)
        {
            if (_added)
            {
                var d = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _window, uID = 1 };
                Shell_NotifyIcon(NimDelete, ref d);
                _added = false;
            }
            if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
            if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        }
        if (registered) UnregisterClass(cls, Instance);
    }

    public void Dispose()
    {
        var hwnd = Handle;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE → WM_DESTROY → PostQuitMessage
        var t = _thread;
        if (t is { IsAlive: true }) t.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public int time; public POINT pt; }

    // Alle Einstiegspunkte ausgeschrieben: der CharSet-Automatismus hängt am Namen, und ein fehlender Einstiegspunkt
    // (siehe TryShow) soll als klarer Fehler kommen und nicht als Absturz.
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true)] private static extern bool UnregisterClass(string cls, IntPtr inst);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)] private static extern IntPtr CreateWindowEx(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage", SetLastError = true)] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", SetLastError = true)] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "PostQuitMessage", SetLastError = true)] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)] private static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true)] private static extern int ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, int count);
    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)] private static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx", SetLastError = true)] private static extern IntPtr TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "DestroyMenu", SetLastError = true)] private static extern bool DestroyMenu(IntPtr menu);
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StykkerCmd.Platform.Windows;

// Das native Kontextmenü der Shell, wie es der Explorer zeigt: mit den Erweiterungen des Systems (Senden an,
// Mit ... öffnen, Archivprogramme). Die Shell füllt Untermenüs erst beim Öffnen. Dafür gehen die Menünachrichten
// des Fensters über eine Fensterunterklasse an das Kontextmenü.
[SupportedOSPlatform("windows")]
internal static class ShellContextMenu
{
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;
    private const uint CmfNormal = 0x0;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint CmicMaskUnicode = 0x00004000;
    private const int SwShowNormal = 1;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint WmMenuChar = 0x0120;
    private const int SOk = 0;
    private const int SFalse = 1;

    private static readonly UIntPtr SubclassId = new(1);
    private static readonly NativeMethods.SubclassProc SubclassCallback = HandleMenuMessage;

    // Das Menü, dessen Nachrichten gerade laufen. Die Felder sind nur während TrackPopupMenuEx gesetzt.
    private static ShellInterop.IContextMenu2? _messages2;
    private static ShellInterop.IContextMenu3? _messages3;

    public static void Show(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0)
            return;

        // Die Oberfläche läuft im STA-Thread und hat COM meist schon. Jeder erfolgreiche Aufruf braucht ein Gegenstück.
        int init = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_APARTMENTTHREADED);
        var pidls = new List<IntPtr>();
        var menu = IntPtr.Zero;
        ShellInterop.IShellFolder? folder = null;
        ShellInterop.IContextMenu? contextMenu = null;
        try
        {
            var parent = Path.GetDirectoryName(paths[0]);
            foreach (var path in paths)
            {
                if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Das Kontextmenü zeigt nur Einträge aus einem Ordner.");

                Check(NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
                pidls.Add(pidl);
            }

            // Der Ordner ist der Elternteil des ersten Eintrags; die übrigen Einträge liegen darin (oben geprüft).
            var folderId = ShellInterop.IidShellFolder;
            Check(NativeMethods.SHBindToParent(pidls[0], ref folderId, out folder, out _));

            var children = pidls.Select(pidl => NativeMethods.ILFindLastID(pidl)).ToArray();
            var menuId = ShellInterop.IidContextMenu;
            Check(folder.GetUIObjectOf(owner, (uint)children.Length, children, ref menuId, IntPtr.Zero, out contextMenu));

            menu = NativeMethods.CreatePopupMenu();
            Check(contextMenu.QueryContextMenu(menu, 0, CmdFirst, CmdLast, CmfNormal));

            _messages2 = contextMenu as ShellInterop.IContextMenu2;
            _messages3 = contextMenu as ShellInterop.IContextMenu3;
            NativeMethods.GetCursorPos(out var cursor);

            NativeMethods.SetWindowSubclass(owner, SubclassCallback, SubclassId, UIntPtr.Zero);
            uint command;
            try
            {
                command = NativeMethods.TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y, owner, IntPtr.Zero);
            }
            finally
            {
                NativeMethods.RemoveWindowSubclass(owner, SubclassCallback, SubclassId);
                _messages2 = null;
                _messages3 = null;
            }

            // 0 heißt: nichts gewählt.
            if (command != 0)
            {
                var info = new ShellInterop.CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<ShellInterop.CMINVOKECOMMANDINFOEX>(),
                    fMask = CmicMaskUnicode,
                    hwnd = owner,
                    lpVerb = new IntPtr((int)(command - CmdFirst)),
                    lpVerbW = new IntPtr((int)(command - CmdFirst)),
                    nShow = SwShowNormal,
                };
                contextMenu.InvokeCommand(ref info);
            }
        }
        finally
        {
            if (menu != IntPtr.Zero)
                NativeMethods.DestroyMenu(menu);
            foreach (var pidl in pidls)
                NativeMethods.ILFree(pidl);
            if (contextMenu is not null)
                Marshal.ReleaseComObject(contextMenu);
            if (folder is not null)
                Marshal.ReleaseComObject(folder);
            if (init is SOk or SFalse)
                NativeMethods.CoUninitialize();
        }
    }

    // Gibt die Menünachrichten an das Kontextmenü weiter; alles andere läuft über die normale Fensterverarbeitung.
    private static IntPtr HandleMenuMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr refData)
    {
        if (message is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
        {
            if (_messages3 is not null)
            {
                _messages3.HandleMenuMsg2(message, wParam, lParam, out var result);
                return result;
            }

            if (_messages2 is not null && message != WmMenuChar)
            {
                _messages2.HandleMenuMsg(message, wParam, lParam);
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    private static void Check(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }
}

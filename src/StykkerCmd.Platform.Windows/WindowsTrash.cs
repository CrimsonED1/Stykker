using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StykkerCmd.Core.Abstractions;

namespace StykkerCmd.Platform.Windows;

// Papierkorb über IFileOperation. Das Verfahren kennt lange Pfade. Die Shell verlangt einen STA-Thread, daher läuft jeder Aufruf dort.
[SupportedOSPlatform("windows")]
public sealed class WindowsTrash : ITrash
{
    // Netzwerk- und USB-Laufwerke haben meist keinen Papierkorb. Die Oberfläche bietet dort endgültiges Löschen an.
    public bool IsAvailable(string path)
    {
        var root = Path.GetPathRoot(WindowsPath.Absolute(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void MoveToTrash(string path) => StaThread.Run(() => Recycle(path));

    private static void Recycle(string path)
    {
        var operation = (ShellInterop.IFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ShellInterop.FileOperationClsid, throwOnError: true)!)!;
        try
        {
            ThrowIfFailed(operation.SetOperationFlags(
                ShellInterop.FOF_ALLOWUNDO | ShellInterop.FOF_NOCONFIRMATION | ShellInterop.FOF_SILENT | ShellInterop.FOF_NOERRORUI));

            var iid = ShellInterop.ShellItemIid;
            // Die Shell-Namensauflösung nimmt den Präfix "\\?\" nicht an (E_INVALIDARG), daher der normale absolute Pfad.
            ThrowIfFailed(NativeMethods.SHCreateItemFromParsingName(
                WindowsPath.Absolute(path), IntPtr.Zero, ref iid, out var item));
            try
            {
                ThrowIfFailed(operation.DeleteItem(item, IntPtr.Zero));
                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
                if (aborted)
                    throw new IOException($"Der Papierkorb-Vorgang wurde abgebrochen: {path}");
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private static void ThrowIfFailed(int hresult) => Marshal.ThrowExceptionForHR(hresult);
}

// Führt eine Aktion auf einem STA-Thread aus und wirft Fehler im Aufrufer weiter.
[SupportedOSPlatform("windows")]
internal static class StaThread
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

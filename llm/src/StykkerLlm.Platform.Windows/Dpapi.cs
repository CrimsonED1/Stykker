using System.Runtime.InteropServices;
using System.Text;

namespace StykkerLlm.Platform.Windows;

// Windows-Datenschutz (DPAPI) für den aktuellen Benutzer: schützt den Schlüssel der Startbestätigung (confirm.key). Mit einem festen
// Zusatzwert (Entropie), damit andere Programme desselben Benutzers den Blob nicht aus Versehen entschlüsseln.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;   // CRYPTPROTECT_UI_FORBIDDEN: nie ein Fenster zeigen
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StykkerLLM.confirm.v1");

    public static byte[]? Protect(byte[] data) => Run(data, protect: true);

    public static byte[]? Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[]? Run(byte[] data, bool protect)
    {
        IntPtr inPtr = IntPtr.Zero, entPtr = IntPtr.Zero;
        try
        {
            inPtr = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, inPtr, data.Length);
            entPtr = Marshal.AllocHGlobal(Entropy.Length);
            Marshal.Copy(Entropy, 0, entPtr, Entropy.Length);
            var input = new DataBlob { Size = data.Length, Data = inPtr };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entPtr };
            DataBlob output;
            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        catch { return null; }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entPtr != IntPtr.Zero) Marshal.FreeHGlobal(entPtr);
        }
    }
}

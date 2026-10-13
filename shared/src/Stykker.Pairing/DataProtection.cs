using System.Runtime.InteropServices;
using System.Text;

namespace Stykker.Pairing;

// Bindet Daten an den angemeldeten Benutzer. null als Ergebnis heißt: hier geht das nicht, die Datei bleibt Klartext.
public interface IDataProtector
{
    byte[]? Protect(byte[] data);
    byte[]? Unprotect(byte[] data);
}

public static class DataProtectors
{
    // Zusatzwert der neuen Ablage, und der, mit dem StykkerLLM seine access.dat und hosts.dat geschützt hat.
    public const string PairingEntropy = "Stykker.pairing.v1";
    public const string LegacyLlmEntropy = "StykkerLLM.confirm.v1";

    public static readonly IDataProtector None = new NoProtector();

    // DPAPI des Windows-Benutzers; auf anderen Systemen Klartext – dort schützt nur der Benutzerordner.
    public static IDataProtector ForCurrentUser(string entropy = PairingEntropy) =>
        OperatingSystem.IsWindows() ? new WindowsDpapi(entropy) : None;

    private sealed class NoProtector : IDataProtector
    {
        public byte[]? Protect(byte[] data) => null;
        public byte[]? Unprotect(byte[] data) => null;
    }
}

// Windows-Datenschutz (DPAPI) für den aktuellen Benutzer, mit einem festen Zusatzwert (Entropie), damit andere
// Programme desselben Benutzers den Blob nicht aus Versehen entschlüsseln. Derselbe Aufruf wie in StykkerLLM.
public sealed class WindowsDpapi : IDataProtector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;   // CRYPTPROTECT_UI_FORBIDDEN: nie ein Fenster zeigen
    private readonly byte[] _entropy;

    public WindowsDpapi(string entropy) => _entropy = Encoding.UTF8.GetBytes(entropy);

    public byte[]? Protect(byte[] data) => Run(data, protect: true);

    public byte[]? Unprotect(byte[] data) => Run(data, protect: false);

    private byte[]? Run(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr inPtr = IntPtr.Zero, entPtr = IntPtr.Zero;
        try
        {
            inPtr = Marshal.AllocHGlobal(Math.Max(1, data.Length));
            Marshal.Copy(data, 0, inPtr, data.Length);
            entPtr = Marshal.AllocHGlobal(Math.Max(1, _entropy.Length));
            Marshal.Copy(_entropy, 0, entPtr, _entropy.Length);
            var input = new DataBlob { Size = data.Length, Data = inPtr };
            var entropy = new DataBlob { Size = _entropy.Length, Data = entPtr };
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
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException) { return null; }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entPtr != IntPtr.Zero) Marshal.FreeHGlobal(entPtr);
        }
    }
}

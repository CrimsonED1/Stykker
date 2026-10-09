using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StykkerCmd.Platform.Linux;

// libc-Aufrufe. realpath löst alle Links einer Pfadkette auf und liefert den kanonischen Pfad.
[SupportedOSPlatform("linux")]
internal static class LinuxNative
{
    [DllImport("libc", SetLastError = true)]
    public static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc")]
    public static extern void free(IntPtr pointer);

    public static string? CanonicalPath(string path)
    {
        var pointer = realpath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            free(pointer);
        }
    }
}

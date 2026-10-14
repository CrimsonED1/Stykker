using System.ComponentModel;
using System.Diagnostics;
using StykkerStarter.Core.Versions;

namespace StykkerStarter.Platform.Windows;

// Die Version einer .exe: zuerst FileVersion, sonst ProductVersion (wie im Auftrag). Keine Version oder eine Datei,
// die kein Programm ist, ergibt null.
public sealed class FileVersionReader : IFileVersionReader
{
    public string? Read(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var version = string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion : info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or BadImageFormatException)
        {
            return null;
        }
    }
}

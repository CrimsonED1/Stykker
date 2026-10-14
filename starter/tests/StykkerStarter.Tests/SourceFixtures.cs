using System.Diagnostics;
using StykkerStarter.Core.Config;
using StykkerStarter.Core.Source;
using StykkerStarter.Platform.Windows;

namespace StykkerStarter.Tests;

// Eine Quelle mit einer echten .exe-artigen Datei, deren Dateiversion bekannt ist. Die Testassembly selbst ist eine
// DLL mit Dateiversion; sie wird als „App.exe“ in den Quellordner kopiert (FileVersionInfo liest das PE ohne Endung).
internal static class SourceFixtures
{
    public const string ExeName = "App.exe";

    public static string TestAssemblyPath => typeof(SourceFixtures).Assembly.Location;

    public static string ExpectedFileVersion => FileVersionInfo.GetVersionInfo(TestAssemblyPath).FileVersion!;

    // Legt <folder>\App.exe und die übrigen Dateien an
    public static string CreateSource(TempFolder temp, string name = "source", params (string Path, string Content)[] extra)
    {
        var folder = temp.Sub(name);
        Directory.CreateDirectory(folder);
        File.Copy(TestAssemblyPath, System.IO.Path.Combine(folder, ExeName), overwrite: true);
        foreach (var (path, content) in extra) TempFolder.Write(System.IO.Path.Combine(folder, path), content);
        return System.IO.Path.Combine(folder, ExeName);
    }

    public static ProgramEntry Program(string exePath, string id = "app") => new()
    {
        Id = id,
        Name = "App",
        SourceExe = exePath,
    };

    public static SourceTree Read(string exePath) => SourceTree.Read(exePath);

    public static string VersionOf(string exePath) => new FileVersionReader().Read(exePath)!;
}

#if WINSERVICE
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StykkerLlm.Core;

namespace StykkerLlm.Host;

// StykkerHost als Windows-Dienst (docs/plan-hosts-gateway.md, P8):
//   StykkerHost install-service              Dienst anlegen und starten (Administrator)
//   StykkerHost uninstall-service            Dienst stoppen und entfernen (Administrator)
//   StykkerHost pair-service <server> <code> den Dienst koppeln (Administrator; der Dienst koppelt selbst)
//   StykkerHost unpair-service               die Kopplung des Dienstes lösen
//   StykkerHost --service                    so startet ihn Windows (ohne Tray)
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ServiceSetup
{
    public static bool IsCommand(string arg) => arg is "install-service" or "uninstall-service" or "pair-service" or "unpair-service";

    public static int RunCommand(string[] args)
    {
        AttachConsole(-1);   // WinExe: Ausgabe ins Terminal, aus dem der Befehl kam
        Console.WriteLine();
        if (!IsAdmin())
        {
            Console.WriteLine(Strings.HostServiceNeedsAdmin);
            return 5;
        }
        var service = HostServiceFiles.ServicePaths();
        switch (args[0])
        {
            case "install-service":
            {
                CreateDataFolder(service.Root);
                HostServiceFiles.SeedSettings(HostStatus.DefaultPaths(), service);
                var exe = Environment.ProcessPath ?? "";
                foreach (var step in HostServiceFiles.InstallCommands(exe))
                    if (!Sc(step)) return 1;
                Console.WriteLine(Strings.HostServiceInstalled);
                Console.WriteLine(Strings.HostServiceTrayHint);
                return 0;
            }
            case "uninstall-service":
                foreach (var step in HostServiceFiles.UninstallCommands())
                    Sc(step, quiet: step[0] == "stop");   // stoppen darf scheitern (lief nicht)
                Console.WriteLine(Strings.HostServiceRemoved + service.Root);
                return 0;
            case "pair-service" when args.Length >= 3:
                return Ask(service, new HostServiceFiles.Request(args[1], args[2]));
            case "unpair-service":
                return Ask(service, new HostServiceFiles.Request(Unpair: true));
            default:
                Console.WriteLine(Strings.HostServiceUsage);
                return 2;
        }
    }

    // Unter Windows als Dienst laufen (außerhalb des Dienststeuerung läuft dasselbe als Konsolenprozess – zum Testen)
    public static void RunService(AppPaths paths)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = HostServiceFiles.ServiceName);
        builder.Services.AddSingleton(paths);
        builder.Services.AddHostedService<HostWorker>();
        builder.Build().Run();
    }

    private sealed class HostWorker(AppPaths paths) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
        {
            using var app = new HostApp(paths, service: true);
            app.Run(stoppingToken);
        }, CancellationToken.None);
    }

    private static int Ask(AppPaths service, HostServiceFiles.Request req)
    {
        HostServiceFiles.WriteRequest(service, req);
        var r = HostServiceFiles.WaitForResultAsync(service, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Console.WriteLine(r?.Message ?? Strings.HostServiceNoAnswer);
        return r?.Ok == true ? 0 : 1;
    }

    // Datenordner: Dienst (SYSTEM) und Administratoren dürfen schreiben, Benutzer nur lesen – so kann kein normales Konto
    // dem Dienst eine Kopplung unterschieben
    private static void CreateDataFolder(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(dir);
        if (info.Exists) info.SetAccessControl(sec);
        else info.Create(sec);
    }

    private static bool Sc(string[] args, bool quiet = false)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        if (p.ExitCode == 0 || quiet) return true;
        Console.WriteLine(Strings.HostServiceStepFailed(args[0], p.ExitCode, output));
        return false;
    }

    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
#endif

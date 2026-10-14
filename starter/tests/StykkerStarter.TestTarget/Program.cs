// Ziel für Tests und den Probelauf. Schreibt eine Zeile mit Version und PID, dann läuft es, bis es beendet wird
// (Strg+C, CloseMainWindow ist bei einer Konsole nicht vorgesehen; beendet wird es über den Prozess selbst).
// Argumente: optional "--exit <Code> <Sekunden>": beendet sich nach der Zeit mit dem Code (für Startfehler-Tests).
using System.Diagnostics;
using System.Reflection;

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").FileVersion;
Console.WriteLine($"StykkerStarter.TestTarget {version} pid {Environment.ProcessId}");

if (args.Length == 3 && args[0] == "--exit")
{
    await Task.Delay(TimeSpan.FromSeconds(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
    return int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
}

await Task.Delay(Timeout.Infinite);
return 0;

using StykkerFirewall.Backend.Simulator;
using StykkerFirewall.Backend.WindowsFirewall;
using StykkerFirewall.Core;
using StykkerFirewall.Core.Backend;
using StykkerFirewall.Core.Rules;
using StykkerFirewall.Service;

// Notausgang: Windows-Standard wiederherstellen (ausgehend erlaubt) und alle Stykker-Regeln entfernen.
// Läuft ohne Dienst, braucht Admin-Rechte: StykkerFirewall.Service.exe -reset
if (args.Any(a => a is "-reset" or "--reset"))
{
    Environment.Exit(await EmergencyReset.RunAsync());
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddEnvironmentVariables("STYKKERFW_");
builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["-backend"] = "Backend",
    ["-data-dir"] = "DataDir",
    ["-pipe"] = "Pipe",
});
builder.Services.AddWindowsService(o => o.ServiceName = "StykkerFirewall");

var options = builder.Configuration.Get<ServiceOptions>() ?? new ServiceOptions();
builder.Services.AddSingleton(options);

// Hier werden Backends eingetragen. Ein weiteres Backend (z. B. eigenes WFP) braucht nur eine Zeile mehr.
var catalog = new BackendCatalog()
    .Add(WindowsFirewallBackend.BackendId, () => new WindowsFirewallBackend())
    .Add(SimulatedBackend.BackendId, () => new SimulatedBackend());

builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BackendCatalog>().Create(options.Backend));
builder.Services.AddSingleton(new RuleStore(Path.Combine(options.ResolveDataDir(), "rules.json")));
builder.Services.AddSingleton(new PromptGate(TimeSpan.FromSeconds(options.QuietSecondsAfterBlockOnce)));
builder.Services.AddSingleton<FirewallCoordinator>();
builder.Services.AddHostedService<FirewallWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();

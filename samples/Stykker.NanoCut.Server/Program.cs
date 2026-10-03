using Stykker.NanoCut.Demo;
using Stykker.NanoCut.Demo.Services;
using Stykker.NanoCut.Server.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<Viewer>();

// One queue for the whole server. Every job already uses all cores (SolidBoolean.MaxParallelism stays at its
// default), so running more jobs at once than this only makes each of them slower.
int jobs = builder.Configuration.GetValue("Compute:MaxConcurrentJobs", 2);
var runner = new QueuedComputeRunner(jobs);
builder.Services.AddSingleton<IComputeRunner>(runner);
builder.Services.AddSingleton(runner);

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly);

// The queue at a glance: how many jobs ran, how many wait, how long they waited in total.
app.MapGet("/api/compute", (QueuedComputeRunner q) => new
{
    q.MaxConcurrentJobs,
    q.Jobs,
    q.Waiting,
    queuedMs = q.TimeQueued.TotalMilliseconds,
    processors = Environment.ProcessorCount,
});

app.Run();

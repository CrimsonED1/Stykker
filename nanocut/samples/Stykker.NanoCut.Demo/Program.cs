using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Stykker.NanoCut.Demo;
using Stykker.NanoCut.Demo.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped<Viewer>();
builder.Services.AddSingleton<IComputeRunner, InlineComputeRunner>();
await builder.Build().RunAsync();

using System.Text.Json;

namespace StykkerStarter.Core.Web;

// Das JSON für die Seite (app.js liest p.name, p.copyToLocal, last.steps …). Die Seite erwartet camelCase; ohne
// diese Vorgabe schriebe .NET „Name“, und die Seite zeigte leere Namen und Quellen.
public static class PageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

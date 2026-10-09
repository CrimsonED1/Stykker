using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;
using StykkerLlm.Core;

namespace StykkerLlm.Server;

// Rolle der aktuellen Browser-Sitzung (Blazor Server). Beim Vorab-Rendern ist der HttpContext noch da, dann steht
// die Rolle sofort fest; in der Schleife danach nicht mehr, deshalb fragt der Browser sie einmal über /api/whoami –
// dort ist das Gerätecookie da, und der Server entscheidet, nicht der Browser. Solange die Rolle unbekannt ist,
// gilt die Sitzung als Viewer: lieber zu wenig als zu viel.
public sealed class ViewerSession
{
    private readonly IJSRuntime _js;

    public string Role { get; private set; } = AccessRole.Viewer;
    public bool Known { get; private set; }
    public bool CanWrite => AccessRole.CanWrite(Role);
    public bool IsViewer => !CanWrite;

    public ViewerSession(IHttpContextAccessor http, IJSRuntime js)
    {
        _js = js;
        if (http.HttpContext?.Items.TryGetValue(AccessGate.RoleItem, out var role) == true && role is string r)
        {
            Role = AccessRole.Normalize(r);
            Known = true;
        }
    }

    // Einmal je Sitzung. Fehlt der Browser-Helfer oder die Antwort, bleibt es beim Viewer.
    public async Task LoadAsync()
    {
        if (Known) return;
        try
        {
            var json = await _js.InvokeAsync<string>("stykkerRole").ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Role = AccessRole.Normalize(doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() : null);
        }
        catch { /* kein Helfer oder keine Antwort: Viewer */ }
        Known = true;
    }
}

// Der Weg der Web-Seiten zu den Aktionen. Sie rufen ActionApi direkt auf (kein HTTP-Schritt dazwischen), deshalb
// hängt dieser Wrapper die Rolle der Sitzung an – dieselbe Prüfung wie bei /api/action.
public sealed class WebActions(ActionContext actions, WebPrompt prompt, ViewerSession viewer)
{
    public bool CanWrite => viewer.CanWrite;

    public Task<ActionResult> RunAsync(ActionRequest req) =>
        ActionApi.ExecuteAsync(req, actions, prompt, role: viewer.Role);
}
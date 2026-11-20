namespace StykkerMeshShare.Server.Components;

// Eine Kennung je Build, angehängt an CSS und Skripte (?v=…). Sonst nimmt WebView2 oder Safari nach einem Update noch
// die alte Fassung aus dem Zwischenspeicher, und die neue Seite läuft mit alten Skripten.
public static class AssetVersion
{
    public static readonly string Value = typeof(AssetVersion).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..10];

    public static string Url(string path) => $"{path}?v={Value}";
}

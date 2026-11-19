using System.Text.Json;

namespace StykkerMeshShare.Core;

// Schreibt eine Datei so, dass nach einem Absturz entweder die alte oder die neue Fassung da ist, nie eine halbe:
// erst in eine Nachbardatei, dann umbenennen.
public static class AtomicFile
{
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null) =>
        WriteText(path, JsonSerializer.Serialize(value, options));

    public static void WriteText(string path, string text)
    {
        var temp = path + ".new";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}

namespace StykkerStarter.Core;

// Protokoll des Starters: eine Zeile je Ereignis in <Ordner>\starter.log. Ein Windows-Programm ohne Konsole hat
// sonst keine Ausgabe. Schreibfehler werden verschluckt – das Protokoll darf den Starter nie anhalten.
public sealed class StarterLog
{
    public const string FileName = "starter.log";

    private readonly string _file;
    private readonly object _gate = new();

    public StarterLog(string file)
    {
        _file = file;
    }

    public static StarterLog In(DataLayout layout) => new(Path.Combine(layout.Root, FileName));

    public void Write(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.AppendAllText(_file, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

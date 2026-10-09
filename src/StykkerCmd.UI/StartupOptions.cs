namespace StykkerCmd.UI;

// Startordner aus der Befehlszeile: --left <ordner> und --right <ordner>. Fehlt ein Pfad, startet das Panel im Benutzerordner.
public static class StartupOptions
{
    public static string? LeftDirectory { get; private set; }

    public static string? RightDirectory { get; private set; }

    public static void Parse(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--left")
                LeftDirectory = args[i + 1];
            else if (args[i] == "--right")
                RightDirectory = args[i + 1];
        }
    }
}

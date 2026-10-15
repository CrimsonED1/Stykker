namespace StykkerStarter.Core;

// Die Ablage des Starters: <root>\starter.json, <root>\<id>\<version>\ je Programm und Version.
// Der Ordner <root> kommt von außen (%LOCALAPPDATA%\Stykker\Starter oder --data-dir), hier wird nichts festgelegt,
// das Windows wüsste.
public sealed class DataLayout
{
    public const string ConfigFileName = "starter.json";
    public const string PartialSuffix = ".partial";

    public DataLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("the data folder is empty", nameof(root));
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, ConfigFileName);

    public string ProgramDir(string programId)
    {
        RequireSafeName(programId, nameof(programId));
        return Path.Combine(Root, programId);
    }

    // Ordnername = Version. Zeichen, die Windows im Namen nicht mag, werden durch "_" ersetzt.
    public string VersionDir(string programId, string version) =>
        Path.Combine(ProgramDir(programId), SafeFolderName(version));

    public static string SafeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("the version is empty", nameof(name));
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }

    // Der Ordner eines Programms, oder null, wenn die Id kein einfacher Ordnername ist (ein kaputter Eintrag darf nie
    // einen fremden Ordner treffen)
    public string? TryProgramDir(string programId) => IsPlainName(programId) ? Path.Combine(Root, programId) : null;

    public static bool IsPlainName(string id) =>
        !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") && Path.GetFileName(id) == id
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void RequireSafeName(string id, string param)
    {
        if (!IsPlainName(id)) throw new ArgumentException("the program id is not a plain folder name", param);
    }
}

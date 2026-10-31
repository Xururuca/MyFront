namespace MyFront.Core;

/// <summary>Locations of the per-user files (%APPDATA%\MyFront).</summary>
public static class AppPaths
{
    public const string AppName = "MyFront";

    private static string? _dataDir;

    /// <summary>Roaming data folder. The self-test points this at a temp folder.</summary>
    public static string DataDir
    {
        get => _dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        internal set => _dataDir = value;
    }

    /// <summary>Machine-local folder for extracted native DLLs.</summary>
    public static string LocalDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static string File(string name)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, name);
    }

    public static string Dir(string name)
    {
        var dir = Path.Combine(DataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes a file atomically (temp file + rename) so a crash never leaves half a JSON file.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, content);
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}

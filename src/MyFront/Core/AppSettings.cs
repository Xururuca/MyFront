using System.Text.Json;

namespace MyFront.Core;

/// <summary>User preferences in %APPDATA%\MyFront\settings.json.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "light";

    /// <summary>Folder for one-click exports; null = Documents\MyFront.</summary>
    public string? ExportFolder { get; set; }
    public bool OpenAfterExport { get; set; } = true;
    public string CsvDelimiter { get; set; } = ";";
    public string CsvDecimal { get; set; } = ",";
    public string CsvEncoding { get; set; } = "utf-8-bom";
    public bool CsvHeader { get; set; } = true;
    /// <summary>SQL editors wrap long lines (View → Word wrap).</summary>
    public bool WordWrap { get; set; } = true;

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static string FilePath => AppPaths.File("settings.json");

    private static AppSettings Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { AppPaths.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(this, CoreJson.Default.AppSettings)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* preferences are best effort */ }
    }

    public string ResolvedExportFolder =>
        string.IsNullOrWhiteSpace(ExportFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppPaths.AppName)
            : ExportFolder;
}

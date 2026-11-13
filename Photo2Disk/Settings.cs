using System.Text.Json;

namespace Photo2Disk;

/// <summary>使用者設定，存在 %APPDATA%\Photo2Disk\settings.json。</summary>
internal sealed class Settings
{
    public string? BackupFolder { get; set; }

    /// <summary>複製新照片時的檔名規則。</summary>
    public NamingOptions Naming { get; set; } = new();

    /// <summary>啟動時是否連到 GitHub 檢查新版本。</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>使用者按「略過這一版」的版本，這一版不再提示。</summary>
    public string? SkippedVersion { get; set; }

    private static string Folder =>
        Environment.GetEnvironmentVariable("PHOTO2DISK_SETTINGS_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Photo2Disk");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                loaded.Naming ??= new NamingOptions();
                loaded.Naming.Normalize();
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

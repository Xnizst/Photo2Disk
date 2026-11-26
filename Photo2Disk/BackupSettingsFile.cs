using System.Text.Json;

namespace Photo2Disk;

/// <summary>
/// 「更多設定」的參數存在備份資料夾根目錄的隱藏檔（YAN 2026-11-26）：複製新照片時自動寫入這次用的設定，
/// 下次選這個備份資料夾時讀取，詢問使用者要不要套用。換電腦或程式重裝後，同一顆硬碟沿用同一套規則。
/// </summary>
public sealed class BackupSettingsFile
{
    public const string FileName = ".photo2disk_settings.json";

    public DateTime SavedAt { get; set; }

    public string? AppVersion { get; set; }

    public NamingOptions Naming { get; set; } = new();

    public static string PathOf(string backupRoot) => Path.Combine(backupRoot, FileName);

    /// <summary>讀取備份資料夾裡的參數檔；沒有或讀不懂時回傳 null。</summary>
    public static BackupSettingsFile? Load(string backupRoot)
    {
        try
        {
            var path = PathOf(backupRoot);
            if (!File.Exists(path))
            {
                return null;
            }
            var file = JsonSerializer.Deserialize<BackupSettingsFile>(File.ReadAllText(path));
            if (file is null)
            {
                return null;
            }
            file.Naming ??= new NamingOptions();
            file.Naming.Normalize();
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>寫入參數檔（隱藏檔）；失敗（ex. 唯讀）時回傳 false，不影響複製。</summary>
    public static bool Save(string backupRoot, NamingOptions naming, DateTime now)
    {
        var path = PathOf(backupRoot);
        var tmp = path + ".tmp";
        try
        {
            var file = new BackupSettingsFile
            {
                SavedAt = now,
                AppVersion = typeof(BackupSettingsFile).Assembly.GetName().Version?.ToString(3),
                Naming = naming.Clone(),
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            File.Move(tmp, path, overwrite: true);
            File.SetAttributes(path, FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    /// <summary>兩組設定是否完全相同（比較存檔內容）。</summary>
    public static bool SameAs(NamingOptions a, NamingOptions b)
    {
        static string Json(NamingOptions o)
        {
            var c = o.Clone();
            c.Normalize();
            return JsonSerializer.Serialize(c);
        }
        return Json(a) == Json(b);
    }
}

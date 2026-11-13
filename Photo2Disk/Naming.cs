using System.Globalization;
using System.Text;

namespace Photo2Disk;

/// <summary>檔名可以由哪些文字組成。</summary>
public enum NamePartKind
{
    Date,
    OriginalName,
    CustomText,
    Sequence,
    CameraModel,
    MediaType,
    FolderName,
}

public sealed class NamePart
{
    public NamePartKind Kind { get; set; }
    public bool Enabled { get; set; }
}

/// <summary>複製新照片時的檔名規則，存在使用者設定裡。</summary>
public sealed class NamingOptions
{
    public bool Enabled { get; set; }
    public List<NamePart> Parts { get; set; } = DefaultParts();
    public string DateFormat { get; set; } = "yyyy-MM";
    public string Separator { get; set; } = "-";
    public string CustomText { get; set; } = "";
    public int SequenceDigits { get; set; } = 3;

    public static readonly (string Format, string Label)[] DateFormats =
    {
        ("yyyy-MM", "年月（2024-03）"),
        ("yyyy-MM-dd", "年月日（2024-03-15）"),
        ("yyyyMMdd_HHmmss", "年月日_時分秒（20240315_143022）"),
        ("yyyy年MM月", "中文年月（2024年03月）"),
        ("yyyy年MM月dd日", "中文年月日（2024年03月15日）"),
        ("yyyy", "年（2024）"),
    };

    public static readonly (string Value, string Label)[] Separators =
    {
        ("-", "減號　-"),
        ("_", "底線　_"),
        (" ", "空格"),
    };

    public static readonly int[] SequenceDigitChoices = { 2, 3, 4 };

    public static string PartLabel(NamePartKind kind) => kind switch
    {
        NamePartKind.Date => "拍攝日期",
        NamePartKind.OriginalName => "原檔名",
        NamePartKind.CustomText => "自訂文字",
        NamePartKind.Sequence => "流水號",
        NamePartKind.CameraModel => "相機／手機型號",
        NamePartKind.MediaType => "照片／影片",
        NamePartKind.FolderName => "所在資料夾名稱",
        _ => kind.ToString(),
    };

    public static List<NamePart> DefaultParts() => new()
    {
        new() { Kind = NamePartKind.Date, Enabled = true },
        new() { Kind = NamePartKind.OriginalName, Enabled = true },
        new() { Kind = NamePartKind.CustomText },
        new() { Kind = NamePartKind.Sequence },
        new() { Kind = NamePartKind.CameraModel },
        new() { Kind = NamePartKind.MediaType },
        new() { Kind = NamePartKind.FolderName },
    };

    public IEnumerable<NamePartKind> ActiveParts => Parts.Where(p => p.Enabled).Select(p => p.Kind);

    public bool Uses(NamePartKind kind) => Enabled && ActiveParts.Contains(kind);

    /// <summary>規則有效且真的會改名。</summary>
    public bool IsActive => Enabled && ActiveParts.Any();

    public NamingOptions Clone() => new()
    {
        Enabled = Enabled,
        Parts = Parts.Select(p => new NamePart { Kind = p.Kind, Enabled = p.Enabled }).ToList(),
        DateFormat = DateFormat,
        Separator = Separator,
        CustomText = CustomText,
        SequenceDigits = SequenceDigits,
    };

    /// <summary>設定檔讀進來後修正：每種組成剛好一個、格式在允許清單內。</summary>
    public void Normalize()
    {
        var seen = new HashSet<NamePartKind>();
        Parts = (Parts ?? new List<NamePart>()).Where(p => p is not null && Enum.IsDefined(p.Kind) && seen.Add(p.Kind)).ToList();
        foreach (var kind in Enum.GetValues<NamePartKind>())
        {
            if (seen.Add(kind))
            {
                Parts.Add(new NamePart { Kind = kind });
            }
        }
        if (!DateFormats.Any(f => f.Format == DateFormat))
        {
            DateFormat = DateFormats[0].Format;
        }
        if (!Separators.Any(s => s.Value == Separator))
        {
            Separator = Separators[0].Value;
        }
        if (!SequenceDigitChoices.Contains(SequenceDigits))
        {
            SequenceDigits = 3;
        }
        CustomText ??= "";
    }

    /// <summary>給介面顯示的簡短說明，例如「拍攝日期-原檔名」。</summary>
    public string Describe()
    {
        if (!IsActive)
        {
            return "保留原檔名";
        }
        return string.Join(Separator, ActiveParts.Select(k => k switch
        {
            NamePartKind.CustomText when CustomText.Trim().Length > 0 => CustomText.Trim(),
            NamePartKind.Date => DateLabel,
            _ => PartLabel(k),
        }));
    }

    /// <summary>依日期格式稱呼：拍攝年月、拍攝年份、拍攝日期時間、拍攝日期。</summary>
    private string DateLabel => DateFormat switch
    {
        "yyyy-MM" or "yyyy年MM月" => "拍攝年月",
        "yyyy" => "拍攝年份",
        "yyyyMMdd_HHmmss" => "拍攝日期時間",
        _ => "拍攝日期",
    };

    // ------------------------------------------------------------ 預設組合

    public sealed record Preset(string Name, NamePartKind[] Parts, string DateFormat, string Separator);

    public static readonly Preset[] Presets =
    {
        new("拍攝年月-原檔名", new[] { NamePartKind.Date, NamePartKind.OriginalName }, "yyyy-MM", "-"),
        new("拍攝日期-原檔名", new[] { NamePartKind.Date, NamePartKind.OriginalName }, "yyyy-MM-dd", "-"),
        new("拍攝日期_時間", new[] { NamePartKind.Date }, "yyyyMMdd_HHmmss", "_"),
        new("拍攝日期-流水號", new[] { NamePartKind.Date, NamePartKind.Sequence }, "yyyy-MM-dd", "-"),
        new("自訂文字-拍攝日期-流水號", new[] { NamePartKind.CustomText, NamePartKind.Date, NamePartKind.Sequence }, "yyyy-MM-dd", "-"),
        new("拍攝年月-相機型號-原檔名", new[] { NamePartKind.Date, NamePartKind.CameraModel, NamePartKind.OriginalName }, "yyyy-MM", "-"),
    };

    public void Apply(Preset preset)
    {
        Enabled = true;
        var ordered = preset.Parts.Select(k => new NamePart { Kind = k, Enabled = true }).ToList();
        ordered.AddRange(Enum.GetValues<NamePartKind>().Where(k => !preset.Parts.Contains(k)).Select(k => new NamePart { Kind = k }));
        Parts = ordered;
        DateFormat = preset.DateFormat;
        Separator = preset.Separator;
    }

    /// <summary>目前設定和哪個預設組合相同；都不同時回傳 null。</summary>
    public Preset? MatchingPreset() => Presets.FirstOrDefault(p =>
        p.Parts.SequenceEqual(ActiveParts) && p.DateFormat == DateFormat && p.Separator == Separator);
}

/// <summary>產生一個檔名需要的資料。</summary>
public sealed record NameInput(string OriginalName, MediaInfo Info, string FolderName);

public static class FileNamer
{
    private const int MaxPartLength = 60;

    /// <summary>
    /// 依規則替一批檔案決定新檔名（已依拍攝時間排序）。isTaken 判斷檔名是否已被占用（含硬碟上已有的檔案）。
    /// 有流水號時，同一組前後文字各自從 1 開始編號，跳過已被占用的號碼；沒有流水號時撞名加「 (2)」。
    /// </summary>
    public static List<string> AssignNames(NamingOptions options, IReadOnlyList<NameInput> inputs, Func<string, bool> isTaken)
    {
        var result = new List<string>(inputs.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Free(string name) => !used.Contains(name) && !isTaken(name);

        foreach (var input in inputs)
        {
            var ext = Path.GetExtension(input.OriginalName);
            string name;
            if (!options.IsActive)
            {
                name = Unique(Sanitize(Path.GetFileNameWithoutExtension(input.OriginalName), "未命名"), ext, Free);
            }
            else if (options.Uses(NamePartKind.Sequence))
            {
                var key = BuildStem(options, input, SequenceSlot);
                var n = counters.GetValueOrDefault(key, 1);
                while (true)
                {
                    name = key.Replace(SequenceSlot, n.ToString("D" + options.SequenceDigits, CultureInfo.InvariantCulture)) + ext;
                    n++;
                    if (Free(name))
                    {
                        break;
                    }
                }
                counters[key] = n;
            }
            else
            {
                name = Unique(BuildStem(options, input, null), ext, Free);
            }
            used.Add(name);
            result.Add(name);
        }
        return result;
    }

    private const string SequenceSlot = "\u0001";

    private static string Unique(string stem, string ext, Func<string, bool> free)
    {
        var name = stem + ext;
        for (var n = 2; !free(name); n++)
        {
            name = $"{stem} ({n}){ext}";
        }
        return name;
    }

    private static string BuildStem(NamingOptions o, NameInput input, string? sequenceSlot)
    {
        var parts = new List<string>();
        foreach (var kind in o.ActiveParts)
        {
            var text = kind switch
            {
                NamePartKind.Date => input.Info.Taken?.ToString(o.DateFormat, CultureInfo.InvariantCulture) ?? "",
                NamePartKind.OriginalName => Path.GetFileNameWithoutExtension(input.OriginalName),
                NamePartKind.CustomText => o.CustomText,
                NamePartKind.Sequence => sequenceSlot ?? "",
                NamePartKind.CameraModel => input.Info.CameraModel ?? "",
                NamePartKind.MediaType => MediaMetadata.IsVideo(input.OriginalName) ? "影片" : "照片",
                NamePartKind.FolderName => input.FolderName,
                _ => "",
            };
            if (kind != NamePartKind.Sequence)
            {
                text = Sanitize(text, "");
                if (text.Length > MaxPartLength)
                {
                    text = text[..MaxPartLength].TrimEnd(' ', '.');
                }
            }
            if (text.Length > 0)
            {
                parts.Add(text);
            }
        }
        var stem = string.Join(o.Separator, parts);
        if (stem.Trim().Length == 0)
        {
            // 什麼都沒有（例如只勾相機型號但照片沒記錄）時保留原檔名
            stem = Sanitize(Path.GetFileNameWithoutExtension(input.OriginalName), "未命名");
        }
        return stem;
    }

    /// <summary>移除檔名不能用的字元與前後的空白、句點。</summary>
    public static string Sanitize(string text, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        var s = sb.ToString().Trim().Trim('.').Trim();
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>來源檔案所在的資料夾名稱：zip 裡的子資料夾，或 zip 本身／一般檔案的上層資料夾。</summary>
    public static string FolderNameOf(SourceItem item)
    {
        if (item.EntryName is not null)
        {
            var dir = Path.GetDirectoryName(item.EntryName.Replace('/', '\\'));
            return string.IsNullOrEmpty(dir)
                ? Path.GetFileNameWithoutExtension(item.SourcePath)
                : Path.GetFileName(dir);
        }
        return Path.GetFileName(Path.GetDirectoryName(item.SourcePath)) ?? "";
    }

    /// <summary>介面預覽用的範例照片。</summary>
    public static readonly NameInput[] Samples =
    {
        new("IMG_1234.HEIC", new MediaInfo(new DateTime(2024, 3, 15, 14, 30, 22), DateSource.Exif, "iPhone 15 Pro"), "2024 日本旅遊"),
        new("IMG_1240.HEIC", new MediaInfo(new DateTime(2024, 3, 15, 18, 5, 10), DateSource.Exif, "iPhone 15 Pro"), "2024 日本旅遊"),
        new("VID_0007.mp4", new MediaInfo(new DateTime(2024, 3, 16, 9, 12, 45), DateSource.Video, null), "2024 日本旅遊"),
    };
}

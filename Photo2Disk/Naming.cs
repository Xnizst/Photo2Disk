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

/// <summary>新照片放在「_待整理」底下的哪一種子資料夾。</summary>
public enum FolderMode
{
    /// <summary>複製當天的日期（_待整理\2026-11-20）。</summary>
    CopyDate,

    /// <summary>拍攝年份（_待整理\2024）。</summary>
    Year,

    /// <summary>拍攝年份＋年份-月份（_待整理\2024\2024-03）。</summary>
    YearMonth,

    /// <summary>拍攝年份＋月份（_待整理\2024\03）。2026-11-26 新增，數值接在後面以相容舊設定檔。</summary>
    YearThenMonth,
}

/// <summary>檔名中拍攝日期的精度。</summary>
public enum DatePrecision
{
    Year,
    YearMonth,
    Day,
    DayTime,
}

/// <summary>複製新照片時的檔名與資料夾規則，存在使用者設定裡。</summary>
public sealed class NamingOptions
{
    public const string UnknownDateFolder = "日期不明";

    /// <summary>預設依拍攝年份分資料夾（YAN 2026-11-20 決定預設開啟）。</summary>
    public FolderMode Folders { get; set; } = FolderMode.Year;

    public bool ByShotDate => Folders != FolderMode.CopyDate;

    public bool Enabled { get; set; }
    public List<NamePart> Parts { get; set; } = DefaultParts();
    public string DateFormat { get; set; } = "yyyy-MM";
    public string Separator { get; set; } = "-";
    public string CustomText { get; set; } = "";
    public int SequenceDigits { get; set; } = 3;

    /// <summary>
    /// 複製 DNG 時另存一份 JPG（原檔照常複製）。在「更多設定」視窗的 DNG 轉檔頁設定，跟著檔名規則一起存；預設關閉。
    /// </summary>
    public bool ConvertDng { get; set; }

    // ------------------------------------------------------------ 日期格式
    // 日期格式由「精度」與「日期內分隔」組合而成（YAN 2026-11-26：原本 6 種固定格式缺乏彈性，
    // 同學需要 20230710 這種完全不隔開的寫法）。設定檔仍只存 DateFormat 字串，舊設定檔的 6 種格式都在組合範圍內。

    /// <summary>日期內分隔用的特殊值：中文年月日。</summary>
    public const string ChineseDateStyle = "年月日";

    public static readonly (DatePrecision Value, string Label)[] DatePrecisions =
    {
        (DatePrecision.Year, "年"),
        (DatePrecision.YearMonth, "年月"),
        (DatePrecision.Day, "年月日"),
        (DatePrecision.DayTime, "年月日＋時分秒"),
    };

    public static readonly (string Value, string Name)[] DateStyles =
    {
        ("-", "減號"),
        ("_", "底線"),
        (".", "句點"),
        ("", "不隔開"),
        (ChineseDateStyle, "中文"),
    };

    /// <summary>由精度與日期內分隔組出 .NET 日期格式字串。時分秒一律接在「_」後面不隔開（中文則用時分秒）。</summary>
    public static string BuildDateFormat(DatePrecision precision, string style)
    {
        if (precision == DatePrecision.Year)
        {
            return "yyyy";
        }
        if (style == ChineseDateStyle)
        {
            return precision switch
            {
                DatePrecision.YearMonth => "yyyy年MM月",
                DatePrecision.Day => "yyyy年MM月dd日",
                _ => "yyyy年MM月dd日HH時mm分ss秒",
            };
        }
        var date = precision == DatePrecision.YearMonth
            ? $"yyyy{style}MM"
            : $"yyyy{style}MM{style}dd";
        return precision == DatePrecision.DayTime ? date + "_HHmmss" : date;
    }

    /// <summary>把日期格式字串拆回精度與日期內分隔；不是組合範圍內的格式時回傳 false。</summary>
    public static bool TryParseDateFormat(string? format, out DatePrecision precision, out string style)
    {
        foreach (var (p, _) in DatePrecisions)
        {
            foreach (var (s, _) in DateStyles)
            {
                if (BuildDateFormat(p, s) == format)
                {
                    precision = p;
                    style = p == DatePrecision.Year ? "-" : s;
                    return true;
                }
            }
        }
        precision = DatePrecision.YearMonth;
        style = "-";
        return false;
    }

    /// <summary>日期內分隔的選項文字，例如「不隔開（ex. 20240315）」。</summary>
    public static string DateStyleLabel(string style, string name)
    {
        var sample = new DateTime(2024, 3, 15).ToString(BuildDateFormat(DatePrecision.Day, style), CultureInfo.InvariantCulture);
        return $"{name}（ex. {sample}）";
    }

    // ------------------------------------------------------------ 分隔符號

    /// <summary>項目之間的分隔符號；不在清單內的值視為使用者自訂。</summary>
    public static readonly (string Value, string Label)[] Separators =
    {
        ("-", "減號　-"),
        ("_", "底線　_"),
        (" ", "空格"),
        ("@", "小老鼠　@"),
        ("", "不隔開"),
    };

    public const int MaxSeparatorLength = 5;

    /// <summary>自訂分隔符號：拿掉檔名不能用的字元，最多 5 個字。</summary>
    public static string CleanSeparator(string? text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((text ?? "").Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray());
        return s.Length > MaxSeparatorLength ? s[..MaxSeparatorLength] : s;
    }

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
        Folders = Folders,
        Enabled = Enabled,
        Parts = Parts.Select(p => new NamePart { Kind = p.Kind, Enabled = p.Enabled }).ToList(),
        DateFormat = DateFormat,
        Separator = Separator,
        CustomText = CustomText,
        SequenceDigits = SequenceDigits,
        ConvertDng = ConvertDng,
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
        if (!TryParseDateFormat(DateFormat, out _, out _))
        {
            DateFormat = "yyyy-MM";
        }
        Separator = CleanSeparator(Separator ?? "-");
        if (!SequenceDigitChoices.Contains(SequenceDigits))
        {
            SequenceDigits = 3;
        }
        if (!Enum.IsDefined(Folders))
        {
            Folders = FolderMode.Year;
        }
        CustomText ??= "";
    }

    // ------------------------------------------------------------ 資料夾

    /// <summary>
    /// 新照片在「_待整理」底下的子資料夾（相對路徑）。依拍攝日期分時，只採用照片記錄的拍攝時間、
    /// 影片時間與原檔名日期；只剩檔案修改時間（可能是下載當天）的放「日期不明」，避免分到錯誤年份。
    /// </summary>
    public string SubFolderFor(MediaInfo info, DateTime copyTime)
    {
        if (Folders == FolderMode.CopyDate)
        {
            return copyTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (info.Taken is not { } t || !IsReliableDate(info.Source))
        {
            return UnknownDateFolder;
        }
        var year = t.ToString("yyyy", CultureInfo.InvariantCulture);
        return Folders switch
        {
            FolderMode.YearMonth => Path.Combine(year, t.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
            FolderMode.YearThenMonth => Path.Combine(year, t.ToString("MM", CultureInfo.InvariantCulture)),
            _ => year,
        };
    }

    /// <summary>「分層」下拉選單的選項，順序即選單順序。</summary>
    public static readonly (FolderMode Value, string Label)[] FolderLayers =
    {
        (FolderMode.Year, "年份（ex. 2024）"),
        (FolderMode.YearMonth, "年份\\年份-月份（ex. 2024\\2024-03）"),
        (FolderMode.YearThenMonth, "年份\\月份（ex. 2024\\03）"),
    };

    public static bool IsReliableDate(DateSource source) =>
        source is DateSource.Exif or DateSource.Video or DateSource.FileName;

    /// <summary>給介面顯示的資料夾說明，例如「依拍攝年份（_待整理\2024）」。</summary>
    public string DescribeFolders() => Folders switch
    {
        FolderMode.Year => $"依拍攝年份（ex. {DedupEngine.StagingFolderName}\\2024）",
        FolderMode.YearMonth => $"依拍攝年份\\年份-月份（ex. {DedupEngine.StagingFolderName}\\2024\\2024-03）",
        FolderMode.YearThenMonth => $"依拍攝年份\\月份（ex. {DedupEngine.StagingFolderName}\\2024\\03）",
        _ => $"依複製日期（ex. {DedupEngine.StagingFolderName}\\{DateTime.Now:yyyy-MM-dd}）",
    };

    /// <summary>三行說明：資料夾、檔名、DNG 轉檔（套用備份資料夾設定的詢問視窗、按鈕提示用）。</summary>
    public string DescribeAll() =>
        "資料夾：" + DescribeFolders() + Environment.NewLine
        + "檔名：" + Describe() + Environment.NewLine
        + "DNG 轉檔：" + (ConvertDng ? "DNG 另存 JPG" : "不轉檔");

    /// <summary>給介面顯示的簡短說明，例如「拍攝日期-原檔名」。</summary>
    public string Describe()
    {
        if (!IsActive)
        {
            return "保留原檔名";
        }
        // 不隔開時用「＋」串接說明文字，否則「拍攝日期原檔名」看不出是兩個項目
        return string.Join(Separator.Length == 0 ? "＋" : Separator, ActiveParts.Select(k => k switch
        {
            NamePartKind.CustomText when CustomText.Trim().Length > 0 => CustomText.Trim(),
            NamePartKind.Date => DateLabel,
            _ => PartLabel(k),
        }));
    }

    /// <summary>依日期格式稱呼：拍攝年月、拍攝年份、拍攝日期時間、拍攝日期。</summary>
    private string DateLabel
    {
        get
        {
            TryParseDateFormat(DateFormat, out var precision, out _);
            return precision switch
            {
                DatePrecision.Year => "拍攝年份",
                DatePrecision.YearMonth => "拍攝年月",
                DatePrecision.DayTime => "拍攝日期時間",
                _ => "拍攝日期",
            };
        }
    }

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
        // 同學回報的命名方式（2026-11-26），ex. 20230710@DJI_0755.DNG
        new("拍攝日期@原檔名", new[] { NamePartKind.Date, NamePartKind.OriginalName }, "yyyyMMdd", "@"),
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

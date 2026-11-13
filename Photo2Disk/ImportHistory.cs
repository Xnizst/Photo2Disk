using System.Globalization;
using System.Text;

namespace Photo2Disk;

/// <summary>一筆匯入紀錄：哪個內容（雜湊）在什麼時候被複製到哪裡。</summary>
public sealed record ImportRecord(string Hash, long Size, DateTime ImportedAt, string RelativePath);

/// <summary>
/// 匯入紀錄，存在備份資料夾根目錄的隱藏檔，只附加不修改。
/// 用途：照片以前匯入過、現在硬碟裡找不到時，比對結果標示「曾遺失」。
/// </summary>
public sealed class ImportHistory
{
    public const string FileName = ".photo2disk_history.tsv";
    private const string Header = "#photo2disk-history v1";

    private readonly string root;
    private readonly Dictionary<string, ImportRecord> latestByHash = new(StringComparer.Ordinal);
    private readonly HashSet<long> sizes = new();

    private ImportHistory(string root)
    {
        this.root = root;
    }

    public string HistoryPath => Path.Combine(root, FileName);

    public static ImportHistory Load(string root)
    {
        var history = new ImportHistory(root);
        try
        {
            if (!File.Exists(history.HistoryPath))
            {
                return history;
            }
            foreach (var line in File.ReadLines(history.HistoryPath, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                var parts = line.Split('\t');
                if (parts.Length != 4
                    || parts[0].Length != 64
                    || !long.TryParse(parts[1], out var size)
                    || !DateTime.TryParseExact(parts[2], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    continue;
                }
                history.Remember(new ImportRecord(parts[0], size, at, parts[3]));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            history.latestByHash.Clear();
            history.sizes.Clear();
        }
        return history;
    }

    public bool ContainsSize(long size) => sizes.Contains(size);

    public ImportRecord? Find(string hash) => latestByHash.TryGetValue(hash, out var r) ? r : null;

    /// <summary>附加到紀錄檔；失敗時回傳 false，不影響已複製的照片。</summary>
    public bool Append(IReadOnlyCollection<ImportRecord> records)
    {
        if (records.Count == 0)
        {
            return true;
        }
        try
        {
            var isNew = !File.Exists(HistoryPath);
            if (!isNew)
            {
                File.SetAttributes(HistoryPath, FileAttributes.Normal);
            }
            using (var writer = new StreamWriter(HistoryPath, append: true, new UTF8Encoding(false)))
            {
                if (isNew)
                {
                    writer.WriteLine(Header);
                }
                foreach (var r in records)
                {
                    writer.WriteLine(string.Join('\t',
                        r.Hash, r.Size.ToString(CultureInfo.InvariantCulture),
                        r.ImportedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.RelativePath));
                    Remember(r);
                }
            }
            File.SetAttributes(HistoryPath, FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Remember(ImportRecord r)
    {
        latestByHash[r.Hash] = r;
        sizes.Add(r.Size);
    }
}

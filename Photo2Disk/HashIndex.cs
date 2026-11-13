using System.Text;

namespace Photo2Disk;

/// <summary>
/// 備份資料夾的雜湊快取：相對路徑 → (大小, 修改時間, SHA-256)。
/// 存在備份資料夾根目錄的隱藏檔，下次比對時大小與修改時間沒變的檔案不用重算。
/// </summary>
public sealed class HashIndex
{
    public const string FileName = ".photo2disk_index.tsv";
    private const string Header = "#photo2disk-index v1";

    public readonly record struct Entry(long Size, long MtimeTicks, string Hash);

    private readonly string root;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    private HashIndex(string root)
    {
        this.root = root;
    }

    public string IndexPath => Path.Combine(root, FileName);

    public int Count => entries.Count;

    public static HashIndex Load(string root)
    {
        var index = new HashIndex(root);
        try
        {
            if (!File.Exists(index.IndexPath))
            {
                return index;
            }
            foreach (var line in File.ReadLines(index.IndexPath, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                var parts = line.Split('\t');
                if (parts.Length != 4
                    || !long.TryParse(parts[1], out var size)
                    || !long.TryParse(parts[2], out var ticks)
                    || parts[3].Length != 64)
                {
                    continue;
                }
                index.entries[parts[0]] = new Entry(size, ticks, parts[3]);
            }
        }
        catch (IOException)
        {
            index.entries.Clear();
        }
        catch (UnauthorizedAccessException)
        {
            index.entries.Clear();
        }
        return index;
    }

    public string? TryGet(string relativePath, long size, long mtimeTicks)
    {
        return entries.TryGetValue(relativePath, out var e) && e.Size == size && e.MtimeTicks == mtimeTicks
            ? e.Hash
            : null;
    }

    public void Set(string relativePath, long size, long mtimeTicks, string hash)
    {
        var entry = new Entry(size, mtimeTicks, hash);
        if (!entries.TryGetValue(relativePath, out var old) || old != entry)
        {
            entries[relativePath] = entry;
            dirty = true;
        }
    }

    /// <summary>移除已經不存在的檔案。</summary>
    public void Prune(IEnumerable<string> existingRelativePaths)
    {
        var keep = new HashSet<string>(existingRelativePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in entries.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            entries.Remove(key);
            dirty = true;
        }
    }

    /// <summary>寫回硬碟；失敗（例如唯讀）時回傳 false，不影響比對結果。</summary>
    public bool Save()
    {
        if (!dirty)
        {
            return true;
        }
        var tmp = IndexPath + ".tmp";
        try
        {
            using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(Header);
                foreach (var (path, e) in entries)
                {
                    writer.Write(path);
                    writer.Write('\t');
                    writer.Write(e.Size);
                    writer.Write('\t');
                    writer.Write(e.MtimeTicks);
                    writer.Write('\t');
                    writer.WriteLine(e.Hash);
                }
            }
            if (File.Exists(IndexPath))
            {
                File.SetAttributes(IndexPath, FileAttributes.Normal);
            }
            File.Move(tmp, IndexPath, overwrite: true);
            File.SetAttributes(IndexPath, FileAttributes.Hidden);
            dirty = false;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

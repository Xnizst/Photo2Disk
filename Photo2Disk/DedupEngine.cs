using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Photo2Disk;

public enum ItemStatus
{
    New,
    AlreadyBackedUp,
    DuplicateInBatch,
}

/// <summary>來源資料中的一個檔案（可能在 zip 裡，也可能是一般檔案）。</summary>
public sealed class SourceItem
{
    public required string SourcePath { get; init; }
    public int EntryIndex { get; init; } = -1;
    public string? EntryName { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteTime { get; init; }

    public string? Hash { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.New;
    public string? MatchedWith { get; set; }
    public string? CopiedTo { get; set; }

    /// <summary>複製時讀到的拍攝資訊（檔名規則用到日期、型號或流水號時才讀）。</summary>
    public MediaInfo? Media { get; set; }

    /// <summary>改名時的補充說明，例如日期取自檔案修改時間。</summary>
    public string? NameNote { get; set; }

    /// <summary>以前匯入過、但比對時硬碟裡找不到（曾遺失）。</summary>
    public ImportRecord? PreviousImport { get; set; }

    public bool IsMissingBefore => Status == ItemStatus.New && PreviousImport is not null;

    public bool IsZipEntry => EntryName is not null;

    public string FileName => Path.GetFileName(EntryName ?? SourcePath);

    public string Location => EntryName is null
        ? SourcePath
        : Path.GetFileName(SourcePath) + "\\" + EntryName.Replace('/', '\\');
}

/// <summary>Total 為 0 代表無法預估進度。</summary>
public readonly record struct ProgressInfo(string Stage, long Done, long Total, string Detail);

public sealed class AnalysisResult
{
    public required string BackupRoot { get; init; }
    public required List<SourceItem> Items { get; init; }
    public required int BackupFileCount { get; init; }
    public required bool IndexSaved { get; init; }
    public required List<string> UnreadableFiles { get; init; }

    public int Count(ItemStatus status) => Items.Count(i => i.Status == status);

    public long Bytes(ItemStatus status) => Items.Where(i => i.Status == status).Sum(i => i.Size);
}

public sealed class CopySummary
{
    public int Copied { get; set; }
    public long Bytes { get; set; }
    public string OutputFolder { get; set; } = "";
}

/// <summary>要直接顯示給使用者看的錯誤。</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message)
    {
    }
}

public static class DedupEngine
{
    public const string StagingFolderName = "_待整理";
    private const string PartialSuffix = ".p2d-partial";
    private const int BufferSize = 1 << 20;
    private const long FreeSpaceMargin = 200L * 1024 * 1024;

    private readonly record struct BackupFile(string Path, string Rel, long Size, long Ticks);

    // ---------------------------------------------------------------- 比對

    public static AnalysisResult Analyze(
        string backupRoot,
        IReadOnlyList<string> sources,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        backupRoot = Path.GetFullPath(backupRoot);
        if (!Directory.Exists(backupRoot))
        {
            throw new UserFacingException($"找不到備份資料夾：{backupRoot}{Environment.NewLine}請確認外接硬碟已經接上。");
        }
        if (sources.Count == 0)
        {
            throw new UserFacingException("請先加入需匯入的來源資料。");
        }
        foreach (var s in sources)
        {
            if (IsSameOrInside(Path.GetFullPath(s), backupRoot))
            {
                throw new UserFacingException(
                    $"「{s}」位在備份資料夾裡面，無法拿來比對。{Environment.NewLine}請把來源資料放在備份資料夾以外的地方（例如「下載」資料夾）。");
            }
        }

        var items = ListSources(sources, progress, ct);
        var backupFiles = ScanBackup(backupRoot, progress, ct);

        // 大小不同就不可能是同一個檔案，只有大小相同的才需要計算雜湊
        var sourceSizeCounts = new Dictionary<long, int>();
        foreach (var it in items)
        {
            sourceSizeCounts[it.Size] = sourceSizeCounts.GetValueOrDefault(it.Size) + 1;
        }
        var candidates = backupFiles.Where(f => sourceSizeCounts.ContainsKey(f.Size)).ToList();
        var candidateSizes = candidates.Select(c => c.Size).ToHashSet();
        var history = ImportHistory.Load(backupRoot);
        var itemsToHash = items
            .Where(i => candidateSizes.Contains(i.Size) || sourceSizeCounts[i.Size] > 1 || history.ContainsSize(i.Size))
            .ToList();

        var index = HashIndex.Load(backupRoot);
        var cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var c in candidates)
        {
            var h = index.TryGet(c.Rel, c.Size, c.Ticks);
            if (h is not null)
            {
                cached[c.Path] = h;
            }
            else
            {
                totalBytes += c.Size;
            }
        }
        totalBytes += itemsToHash.Sum(i => i.Size);
        var meter = new ByteMeter("比對檔案內容", totalBytes, progress);

        var unreadable = new List<string>();
        var backupByHash = new Dictionary<string, string>(StringComparer.Ordinal);

        // 備份端：外接硬碟可能是傳統硬碟，依目錄順序單執行緒讀取，避免磁頭來回移動
        void HashBackupSide(CancellationToken token)
        {
            foreach (var c in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (!cached.TryGetValue(c.Path, out var hash))
                {
                    meter.Detail = c.Path;
                    try
                    {
                        hash = HashFile(c.Path, meter, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unreadable.Add(c.Path);
                        continue;
                    }
                    index.Set(c.Rel, c.Size, c.Ticks, hash);
                }
                backupByHash.TryAdd(hash, c.Path);
            }
        }

        // 來源端與備份端在不同磁碟時兩邊同時讀；同一顆磁碟時依序讀
        var sameDisk = sources.Any(s => SameDrive(s, backupRoot));
        var sourceParallelism = sameDisk ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var workUnits = SplitWork(itemsToHash, sourceParallelism);
        if (sameDisk)
        {
            HashBackupSide(ct);
            HashSourceSide(workUnits, 1, meter, ct);
        }
        else
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sourceTask = Task.Run(() => HashSourceSide(workUnits, sourceParallelism, meter, linked.Token));
            try
            {
                HashBackupSide(linked.Token);
            }
            catch
            {
                linked.Cancel();
                try
                {
                    sourceTask.Wait();
                }
                catch (AggregateException)
                {
                }
                throw;
            }
            sourceTask.GetAwaiter().GetResult();
        }
        meter.Flush();

        // 來源資料重複的照片先判定（不論硬碟有沒有），第一份才依硬碟狀況分成新照片或硬碟已有，
        // 這樣「新照片＋硬碟已有＋來源資料重複」會等於來源資料的檔案數
        var firstInBatch = new Dictionary<string, SourceItem>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            if (it.Hash is null)
            {
                it.Status = ItemStatus.New;
            }
            else if (firstInBatch.TryGetValue(it.Hash, out var first))
            {
                it.Status = ItemStatus.DuplicateInBatch;
                it.MatchedWith = first.Location;
            }
            else if (backupByHash.TryGetValue(it.Hash, out var existing))
            {
                it.Status = ItemStatus.AlreadyBackedUp;
                it.MatchedWith = existing;
                firstInBatch[it.Hash] = it;
            }
            else
            {
                it.Status = ItemStatus.New;
                it.PreviousImport = history.Find(it.Hash);
                firstInBatch[it.Hash] = it;
            }
        }

        index.Prune(backupFiles.Select(f => f.Rel));
        var saved = index.Save();

        return new AnalysisResult
        {
            BackupRoot = backupRoot,
            Items = items,
            BackupFileCount = backupFiles.Count,
            IndexSaved = saved,
            UnreadableFiles = unreadable,
        };
    }

    // ---------------------------------------------------------------- 複製

    public static string GetStagingFolder(string backupRoot, DateTime now)
    {
        return Path.Combine(backupRoot, StagingFolderName, now.ToString("yyyy-MM-dd"));
    }

    /// <summary>
    /// 複製新照片。每張先寫成暫存檔（邊寫邊核對雜湊），全部完成、取消或出錯時，
    /// 已完成的暫存檔依檔名規則（依拍攝時間排序，流水號才會照時間編）改成正式檔名。
    /// </summary>
    public static CopySummary CopyNew(AnalysisResult analysis, IProgress<ProgressInfo>? progress, CancellationToken ct,
        NamingOptions? naming = null)
    {
        naming ??= new NamingOptions();
        var toCopy = analysis.Items.Where(i => i.Status == ItemStatus.New && i.CopiedTo is null).ToList();
        var outDir = GetStagingFolder(analysis.BackupRoot, DateTime.Now);
        var summary = new CopySummary { OutputFolder = outDir };
        if (toCopy.Count == 0)
        {
            return summary;
        }
        if (!Directory.Exists(analysis.BackupRoot))
        {
            throw new UserFacingException($"找不到備份資料夾：{analysis.BackupRoot}{Environment.NewLine}請確認外接硬碟已經接上。");
        }

        var need = toCopy.Sum(i => i.Size);
        var free = new DriveInfo(Path.GetPathRoot(analysis.BackupRoot)!).AvailableFreeSpace;
        if (free < need + FreeSpaceMargin)
        {
            throw new UserFacingException(
                $"外接硬碟空間不足：需要 {FormatSize(need)}，目前只剩 {FormatSize(free)}。");
        }

        Directory.CreateDirectory(outDir);
        DeleteStalePartials(outDir);
        var index = HashIndex.Load(analysis.BackupRoot);
        var meter = new ByteMeter("複製新照片", need, progress);
        var history = ImportHistory.Load(analysis.BackupRoot);
        var imported = new List<ImportRecord>();
        var importTime = DateTime.Now;
        var needMedia = naming.Uses(NamePartKind.Date) || naming.Uses(NamePartKind.CameraModel)
                        || naming.Uses(NamePartKind.Sequence);
        var completed = new List<CopiedFile>();
        var copyFailed = false;

        try
        {
            foreach (var group in toCopy.GroupBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                using var zip = group.First().IsZipEntry ? OpenZip(group.Key) : null;
                foreach (var it in group)
                {
                    ct.ThrowIfCancellationRequested();
                    var partial = Path.Combine(outDir, Guid.NewGuid().ToString("N") + PartialSuffix);
                    meter.Detail = it.FileName;
                    try
                    {
                        string hash;
                        using (var src = zip is null ? OpenFileRead(it.SourcePath) : OpenZipEntry(zip, it))
                        using (var dst = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
                        {
                            hash = HashStream(src, dst, meter, ct, it);
                        }
                        if (it.Hash is not null && it.Hash != hash)
                        {
                            throw new UserFacingException(
                                $"「{it.FileName}」複製時讀到的內容與比對時不同，來源檔案可能在比對後被修改。請重新比對。");
                        }
                        if (it.LastWriteTime.Year > 1980)
                        {
                            File.SetLastWriteTime(partial, it.LastWriteTime);
                        }
                        if (needMedia)
                        {
                            // 剛寫好的檔案還在系統快取裡，只讀開頭的資訊區塊，zip 也不用再解壓一次
                            it.Media = MediaMetadata.Read(partial, it.FileName, it.LastWriteTime);
                        }
                        completed.Add(new CopiedFile(it, partial, hash, completed.Count));
                    }
                    catch
                    {
                        TryDeletePartial(partial);
                        throw;
                    }
                }
            }
        }
        catch
        {
            copyFailed = true;
            throw;
        }
        finally
        {
            try
            {
                var moveError = FinishCopies(analysis.BackupRoot, outDir, naming, needMedia, completed, index,
                    imported, importTime, summary);
                if (moveError is not null && !copyFailed)
                {
                    throw moveError;
                }
            }
            finally
            {
                meter.Flush();
                index.Save();
                history.Append(imported);
            }
        }
        return summary;
    }

    private sealed record CopiedFile(SourceItem Item, string Partial, string Hash, int Order);

    /// <summary>把完成的暫存檔改成正式檔名；改名失敗的刪除暫存檔（下次比對仍是新照片），回傳第一個錯誤。</summary>
    private static Exception? FinishCopies(string backupRoot, string outDir, NamingOptions naming, bool sortByDate,
        List<CopiedFile> completed, HashIndex index, List<ImportRecord> imported, DateTime importTime, CopySummary summary)
    {
        if (completed.Count == 0)
        {
            return null;
        }
        var ordered = sortByDate
            ? completed.OrderBy(c => c.Item.Media?.Taken ?? DateTime.MaxValue).ThenBy(c => c.Order).ToList()
            : completed;
        var inputs = ordered
            .Select(c => new NameInput(c.Item.FileName, c.Item.Media ?? default, FileNamer.FolderNameOf(c.Item)))
            .ToList();
        var names = FileNamer.AssignNames(naming, inputs, name =>
        {
            var full = Path.Combine(outDir, name);
            return File.Exists(full) || Directory.Exists(full) || File.Exists(full + PartialSuffix);
        });

        Exception? firstError = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var (it, partial, hash, _) = ordered[i];
            var target = Path.Combine(outDir, names[i]);
            try
            {
                File.Move(partial, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeletePartial(partial);
                firstError ??= ex;
                continue;
            }
            var fi = new FileInfo(target);
            var rel = Path.GetRelativePath(backupRoot, target);
            index.Set(rel, fi.Length, fi.LastWriteTimeUtc.Ticks, hash);
            imported.Add(new ImportRecord(hash, fi.Length, importTime, rel));
            it.Hash = hash;
            it.CopiedTo = target;
            it.NameNote = naming.Uses(NamePartKind.Date) ? DateNote(it.Media?.Source ?? DateSource.None) : null;
            summary.Copied++;
            summary.Bytes += it.Size;
        }
        return firstError;
    }

    private static string? DateNote(DateSource source) => source switch
    {
        DateSource.FileName => "日期取自原檔名",
        DateSource.FileTime => "找不到拍攝時間，日期取自檔案修改時間",
        DateSource.None => "找不到拍攝時間，檔名沒有加日期",
        _ => null,
    };

    /// <summary>上次程式異常結束留下的暫存檔。</summary>
    private static void DeleteStalePartials(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*" + PartialSuffix))
        {
            TryDeletePartial(f);
        }
    }

    // ---------------------------------------------------------------- 平行計算

    /// <summary>把要計算雜湊的來源檔案切成工作單位：同一個 zip 依項目順序切成幾段，每段各自開啟 zip。</summary>
    private static List<List<SourceItem>> SplitWork(List<SourceItem> items, int parallelism)
    {
        var units = new List<List<SourceItem>>();
        foreach (var group in items.GroupBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(i => i.EntryIndex).ToList();
            var chunks = ordered.Count >= 16 ? parallelism : 1;
            var size = (ordered.Count + chunks - 1) / chunks;
            for (var start = 0; start < ordered.Count; start += size)
            {
                units.Add(ordered.GetRange(start, Math.Min(size, ordered.Count - start)));
            }
        }
        return units;
    }

    private static void HashSourceSide(List<List<SourceItem>> units, int parallelism, ByteMeter meter, CancellationToken ct)
    {
        void HashUnit(List<SourceItem> unit)
        {
            ct.ThrowIfCancellationRequested();
            if (unit[0].IsZipEntry)
            {
                using var zip = OpenZip(unit[0].SourcePath);
                foreach (var it in unit)
                {
                    meter.Detail = it.Location;
                    using var stream = OpenZipEntry(zip, it);
                    it.Hash = HashStream(stream, null, meter, ct, it);
                }
            }
            else
            {
                foreach (var it in unit)
                {
                    meter.Detail = it.Location;
                    it.Hash = HashFile(it.SourcePath, meter, ct);
                }
            }
        }

        if (parallelism <= 1 || units.Count <= 1)
        {
            foreach (var unit in units)
            {
                HashUnit(unit);
            }
            return;
        }
        try
        {
            Parallel.ForEach(units, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, HashUnit);
        }
        catch (AggregateException ae)
        {
            // 讓 UserFacingException、OperationCanceledException 等原樣傳出
            var inner = ae.Flatten().InnerExceptions;
            var first = inner.FirstOrDefault(e => e is UserFacingException)
                ?? inner.FirstOrDefault(e => e is not OperationCanceledException)
                ?? inner[0];
            ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    private static bool SameDrive(string a, string b)
    {
        var ra = Path.GetPathRoot(Path.GetFullPath(a));
        var rb = Path.GetPathRoot(Path.GetFullPath(b));
        return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 工具

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredName(string name)
    {
        return name.Equals(HashIndex.FileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(ImportHistory.FileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(HashIndex.FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static EnumerationOptions RecursiveOptions() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private static List<SourceItem> ListSources(IReadOnlyList<string> sources, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var items = new List<SourceItem>();
        foreach (var src in sources)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo("讀取來源資料清單", 0, 0, src));
            if (Directory.Exists(src))
            {
                foreach (var fi in new DirectoryInfo(src).EnumerateFiles("*", RecursiveOptions()))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsIgnoredName(fi.Name) || fi.Length == 0)
                    {
                        continue;
                    }
                    if (IsZip(fi.FullName))
                    {
                        AddZipEntries(fi.FullName, items);
                    }
                    else
                    {
                        items.Add(new SourceItem { SourcePath = fi.FullName, Size = fi.Length, LastWriteTime = fi.LastWriteTime });
                    }
                }
            }
            else if (File.Exists(src))
            {
                if (IsZip(src))
                {
                    AddZipEntries(src, items);
                }
                else
                {
                    var fi = new FileInfo(src);
                    if (fi.Length > 0)
                    {
                        items.Add(new SourceItem { SourcePath = fi.FullName, Size = fi.Length, LastWriteTime = fi.LastWriteTime });
                    }
                }
            }
            else
            {
                throw new UserFacingException($"找不到「{src}」，可能已經被移動或刪除。");
            }
        }
        return items;
    }

    private static void AddZipEntries(string zipPath, List<SourceItem> items)
    {
        using var zip = OpenZip(zipPath);
        for (var i = 0; i < zip.Entries.Count; i++)
        {
            var e = zip.Entries[i];
            var name = Path.GetFileName(e.FullName);
            if (e.Length == 0 || name.Length == 0 || IsIgnoredName(name)
                || e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
            {
                continue;
            }
            items.Add(new SourceItem
            {
                SourcePath = zipPath,
                EntryIndex = i,
                EntryName = e.FullName,
                Size = e.Length,
                LastWriteTime = e.LastWriteTime.DateTime,
            });
        }
    }

    private static List<BackupFile> ScanBackup(string root, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var list = new List<BackupFile>();
        progress?.Report(new ProgressInfo("掃描備份資料夾", 0, 0, ""));
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", RecursiveOptions()))
        {
            if (IsIgnoredName(fi.Name))
            {
                continue;
            }
            list.Add(new BackupFile(fi.FullName, Path.GetRelativePath(root, fi.FullName), fi.Length, fi.LastWriteTimeUtc.Ticks));
            if (list.Count % 500 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo("掃描備份資料夾", 0, 0, $"已找到 {list.Count:N0} 個檔案"));
            }
        }
        return list;
    }

    private static ZipArchive OpenZip(string path)
    {
        try
        {
            return ZipFile.Open(path, ZipArchiveMode.Read, Encoding.UTF8);
        }
        catch (InvalidDataException)
        {
            throw new UserFacingException($"無法讀取壓縮檔「{Path.GetFileName(path)}」，檔案可能損壞或還沒下載完成。");
        }
    }

    private static Stream OpenZipEntry(ZipArchive zip, SourceItem item)
    {
        try
        {
            return zip.Entries[item.EntryIndex].Open();
        }
        catch (InvalidDataException)
        {
            throw new UserFacingException($"壓縮檔裡的「{item.Location}」無法讀取，壓縮檔可能損壞，請重新下載。");
        }
    }

    private static FileStream OpenFileRead(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
    }

    private static string HashFile(string path, ByteMeter meter, CancellationToken ct)
    {
        using var fs = OpenFileRead(path);
        return HashStream(fs, null, meter, ct, null);
    }

    /// <summary>計算 SHA-256，copyTo 不為 null 時同時寫出（複製與驗證一次完成）。</summary>
    private static string HashStream(Stream src, Stream? copyTo, ByteMeter meter, CancellationToken ct, SourceItem? item)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int n;
            while (true)
            {
                try
                {
                    n = src.Read(buffer, 0, BufferSize);
                }
                catch (InvalidDataException) when (item is not null)
                {
                    throw new UserFacingException($"壓縮檔裡的「{item.Location}」無法讀取，壓縮檔可能損壞，請重新下載。");
                }
                if (n <= 0)
                {
                    break;
                }
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, n);
                copyTo?.Write(buffer, 0, n);
                meter.Add(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>以位元組計算進度，每 100 ms 回報一次。</summary>
    private sealed class ByteMeter
    {
        private readonly string stage;
        private readonly long total;
        private readonly IProgress<ProgressInfo>? progress;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly object gate = new();
        private long done;
        private long lastReportMs = -1000;

        public ByteMeter(string stage, long total, IProgress<ProgressInfo>? progress)
        {
            this.stage = stage;
            this.total = total;
            this.progress = progress;
            Flush();
        }

        public string Detail { get; set; } = "";

        public void Add(long n)
        {
            Interlocked.Add(ref done, n);
            var ms = watch.ElapsedMilliseconds;
            if (ms - Interlocked.Read(ref lastReportMs) < 100)
            {
                return;
            }
            lock (gate)
            {
                if (ms - lastReportMs >= 100)
                {
                    lastReportMs = ms;
                    Flush();
                }
            }
        }

        public void Flush()
        {
            progress?.Report(new ProgressInfo(stage, Interlocked.Read(ref done), Math.Max(total, 1), Detail));
        }
    }
}

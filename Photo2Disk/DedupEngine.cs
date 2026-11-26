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

    /// <summary>使用者在清單中取消勾選（只對新照片有意義），複製時略過。</summary>
    public bool Excluded { get; set; }

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

/// <summary>預計的新位置（完整路徑）與補充說明（ex. 日期取自原檔名、放在「日期不明」）。</summary>
public sealed record PlannedName(string Path, string? Note);

/// <summary>Total 為 0 代表無法預估進度。IsCount 為 true 時 Done／Total 是張數，否則是位元組。</summary>
public readonly record struct ProgressInfo(string Stage, long Done, long Total, string Detail, bool IsCount = false);

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

    /// <summary>依複製日期時是當天的資料夾；依拍攝日期時是「_待整理」本身。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>每個子資料夾（相對於「_待整理」）複製了幾張。</summary>
    public SortedDictionary<string, int> FolderCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>放到硬碟裡已有的影片旁邊（可能在「_待整理」以外）的字幕檔數，不算在 FolderCounts。</summary>
    public int NextToBackedUpVideo { get; set; }

    /// <summary>DNG 轉出 JPG 的張數、失敗張數、因為已有相機直出 JPG 而略過的張數。</summary>
    public int Converted { get; set; }
    public int ConvertFailed { get; set; }
    public int ConvertSkipped { get; set; }

    /// <summary>轉檔途中被取消（照片已經複製完成，只是剩下的 DNG 沒轉）。</summary>
    public bool ConvertCancelled { get; set; }
}

/// <summary>轉檔的來源資訊，寫進新檔的 XMP。</summary>
public sealed record RawSourceInfo(string Name, string Sha256, long Size);

public enum RawConvertMethod
{
    /// <summary>直接沿用 DNG 內嵌的全尺寸預覽（相機的成品）。</summary>
    CameraPreview,

    /// <summary>RAW 解碼後依內嵌小預覽校正顏色。</summary>
    MatchedToPreview,

    /// <summary>沒有可用的預覽，只做 RAW 解碼。</summary>
    RawOnly,
}

/// <summary>DNG 轉 JPG。主程式用 DngConverter（LibRaw），測試可換成假的實作。失敗時拋出例外。</summary>
public interface IRawConverter
{
    RawConvertMethod Convert(string rawPath, string outPath, RawSourceInfo source);
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
                    $"「{s}」位在備份資料夾裡面，無法拿來比對。{Environment.NewLine}請把來源資料放在備份資料夾以外的地方（ex. 「下載」資料夾）。");
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

        // 結果清單的「修改後檔名」要依拍攝時間預先算好（YAN 2026-11-26）
        ReadNewMedia(items, progress, ct);

        return new AnalysisResult
        {
            BackupRoot = backupRoot,
            Items = items,
            BackupFileCount = backupFiles.Count,
            IndexSaved = saved,
            UnreadableFiles = unreadable,
        };
    }

    /// <summary>zip 裡的檔案先解出這麼多位元組讀拍攝資訊，讀不到（ex. 影片資訊放在檔尾）才整個解出來。</summary>
    private const int MediaPrefixBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 替新照片讀拍攝時間與型號，供結果清單預先顯示新檔名。一般檔案只讀開頭的資訊區塊；
    /// zip 裡的檔案不能跳著讀，先把開頭解到暫存檔，必要時才整個解出來。讀不到時用原檔名日期或修改時間。
    /// </summary>
    private static void ReadNewMedia(List<SourceItem> items, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var todo = items.Where(i => i.Status == ItemStatus.New).ToList();
        if (todo.Count == 0)
        {
            return;
        }
        var tempDir = Path.Combine(Path.GetTempPath(), "Photo2Disk_meta", Guid.NewGuid().ToString("N"));
        var done = 0;
        try
        {
            foreach (var group in todo.GroupBy(i => i.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                ZipArchive? zip = null;
                try
                {
                    if (group.First().IsZipEntry)
                    {
                        Directory.CreateDirectory(tempDir);
                        zip = OpenZip(group.Key);
                    }
                    foreach (var it in group.OrderBy(i => i.EntryIndex))
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new ProgressInfo("讀取拍攝時間", done++, todo.Count, it.Location, true));
                        it.Media = zip is null
                            ? MediaMetadata.Read(it.SourcePath, it.FileName, it.LastWriteTime)
                            : ReadZipEntryMedia(zip, it, tempDir);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                               or UserFacingException)
                {
                    // 讀不到的照片只用原檔名日期或修改時間；真正的錯誤留到複製時再報
                    foreach (var it in group.Where(i => i.Media is null))
                    {
                        it.Media = MediaMetadata.Read("", it.FileName, it.LastWriteTime);
                    }
                }
                finally
                {
                    zip?.Dispose();
                }
            }
            progress?.Report(new ProgressInfo("讀取拍攝時間", todo.Count, todo.Count, "", true));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static MediaInfo ReadZipEntryMedia(ZipArchive zip, SourceItem it, string tempDir)
    {
        var temp = Path.Combine(tempDir, "entry" + Path.GetExtension(it.FileName));
        void Extract(long limit)
        {
            using var src = OpenZipEntry(zip, it);
            using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while (total < limit && (n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - total))) > 0)
            {
                dst.Write(buffer, 0, n);
                total += n;
            }
        }

        Extract(MediaPrefixBytes);
        var info = MediaMetadata.Read(temp, it.FileName, it.LastWriteTime);
        if (info.Source is DateSource.Exif or DateSource.Video || it.Size <= MediaPrefixBytes)
        {
            return info;
        }
        Extract(long.MaxValue);
        return MediaMetadata.Read(temp, it.FileName, it.LastWriteTime);
    }

    // ---------------------------------------------------------------- 複製

    public static string GetStagingRoot(string backupRoot) => Path.Combine(backupRoot, StagingFolderName);

    /// <summary>新照片要放的資料夾：依複製日期時是當天的資料夾，依拍攝日期時是「_待整理」（底下再分年份）。</summary>
    public static string GetOutputFolder(string backupRoot, DateTime now, NamingOptions naming)
    {
        return naming.ByShotDate
            ? GetStagingRoot(backupRoot)
            : Path.Combine(GetStagingRoot(backupRoot), naming.SubFolderFor(default, now));
    }

    /// <summary>
    /// 複製新照片。每張先寫成「_待整理」裡的暫存檔（邊寫邊核對雜湊），全部完成、取消或出錯時，
    /// 已完成的暫存檔依規則決定子資料夾與檔名（依拍攝時間排序，流水號才會照時間編）再搬過去。
    /// </summary>
    public static CopySummary CopyNew(AnalysisResult analysis, IProgress<ProgressInfo>? progress, CancellationToken ct,
        NamingOptions? naming = null, IRawConverter? converter = null)
    {
        naming ??= new NamingOptions();
        var toCopy = analysis.Items.Where(i => i.Status == ItemStatus.New && i.CopiedTo is null && !i.Excluded).ToList();
        var importTime = DateTime.Now;
        var stagingRoot = GetStagingRoot(analysis.BackupRoot);
        var summary = new CopySummary { OutputFolder = GetOutputFolder(analysis.BackupRoot, importTime, naming) };
        if (toCopy.Count == 0)
        {
            return summary;
        }
        if (!Directory.Exists(analysis.BackupRoot))
        {
            throw new UserFacingException($"找不到備份資料夾：{analysis.BackupRoot}{Environment.NewLine}請確認外接硬碟已經接上。");
        }

        var convert = naming.ConvertDng && converter is not null;
        var need = toCopy.Sum(i => i.Size);
        if (convert)
        {
            need += toCopy.Count(i => IsDng(i.FileName)) * EstimatedJpegSize; // 轉出的 JPG
        }
        var free = new DriveInfo(Path.GetPathRoot(analysis.BackupRoot)!).AvailableFreeSpace;
        if (free < need + FreeSpaceMargin)
        {
            throw new UserFacingException(
                $"外接硬碟空間不足：需要 {FormatSize(need)}，目前只剩 {FormatSize(free)}。");
        }

        // 這次用的「更多設定」存進備份資料夾，下次選這個資料夾時可以套用（寫不進去不影響複製）
        BackupSettingsFile.Save(analysis.BackupRoot, naming, importTime);
        Directory.CreateDirectory(stagingRoot);
        DeleteStalePartials(stagingRoot);
        var index = HashIndex.Load(analysis.BackupRoot);
        var meter = new ByteMeter("複製新照片", need, progress);
        var history = ImportHistory.Load(analysis.BackupRoot);
        var imported = new List<ImportRecord>();
        var needMedia = NeedsMedia(naming);
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
                    var partial = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N") + PartialSuffix);
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
                var moveError = FinishCopies(analysis.BackupRoot, stagingRoot, naming, needMedia, completed, analysis.Items,
                    index, imported, importTime, summary);
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
        if (convert)
        {
            ConvertRaws(analysis.Items, toCopy, stagingRoot, converter!, progress, ct, summary);
        }
        return summary;
    }

    private const long EstimatedJpegSize = 12L * 1024 * 1024;

    public static bool IsDng(string fileName) => Path.GetExtension(fileName).Equals(".dng", StringComparison.OrdinalIgnoreCase);

    private static bool IsJpeg(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".jpg" or ".jpeg";

    /// <summary>
    /// 這次複製的 DNG 各另存一份 JPG，放在 DNG 旁邊、同檔名。來源資料同資料夾已有同名 JPG（相機同時拍 JPG＋RAW）、
    /// 或目的地已有同名 JPG 時略過。單張失敗只記在結果清單；取消時停止轉剩下的（照片已經複製完成，不拋例外）。
    /// </summary>
    private static void ConvertRaws(IReadOnlyList<SourceItem> allItems, List<SourceItem> copiedNow, string stagingRoot,
        IRawConverter converter, IProgress<ProgressInfo>? progress, CancellationToken ct, CopySummary summary)
    {
        var raws = copiedNow.Where(i => i.CopiedTo is not null && i.Hash is not null && IsDng(i.FileName)).ToList();
        if (raws.Count == 0)
        {
            return;
        }
        var cameraJpegs = allItems.Where(i => IsJpeg(i.FileName))
            .Select(PairKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var n = 0; n < raws.Count; n++)
        {
            var it = raws[n];
            if (ct.IsCancellationRequested)
            {
                summary.ConvertCancelled = true;
                return;
            }
            progress?.Report(new ProgressInfo("轉檔 DNG", n, raws.Count, it.FileName, true));
            var dir = Path.GetDirectoryName(it.CopiedTo)!;
            var stem = Path.GetFileNameWithoutExtension(it.CopiedTo);
            if (cameraJpegs.Contains(PairKey(it))
                || File.Exists(Path.Combine(dir, stem + ".jpg")) || File.Exists(Path.Combine(dir, stem + ".jpeg")))
            {
                summary.ConvertSkipped++;
                AddNote(it, "已有同名 JPG（相機直出），不另外轉檔");
                continue;
            }
            var target = Path.Combine(dir, stem + ".jpg");
            var partial = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N") + PartialSuffix);
            try
            {
                var method = converter.Convert(it.CopiedTo!, partial, new RawSourceInfo(it.FileName, it.Hash!, it.Size));
                File.Move(partial, target);
                summary.Converted++;
                AddNote(it, $"已轉出 {Path.GetFileName(target)}（{MethodText(method)}）");
            }
#pragma warning disable CA1031 // 轉檔函式庫可能拋出各種例外，單張失敗不能中斷其他照片
            catch (Exception ex)
#pragma warning restore CA1031
            {
                TryDeletePartial(partial);
                summary.ConvertFailed++;
                AddNote(it, "轉檔失敗：" + ex.Message.Trim());
            }
        }
        progress?.Report(new ProgressInfo("轉檔 DNG", raws.Count, raws.Count, "", true));
    }

    private static string MethodText(RawConvertMethod method) => method switch
    {
        RawConvertMethod.CameraPreview => "相機原圖",
        RawConvertMethod.MatchedToPreview => "依相機顏色重建",
        _ => "RAW 沖洗，顏色可能和相機不同",
    };

    private static void AddNote(SourceItem it, string note) =>
        it.NameNote = string.IsNullOrEmpty(it.NameNote) ? note : it.NameNote + "；" + note;

    private sealed record CopiedFile(SourceItem Item, string Partial, string Hash, int Order);

    /// <summary>規則決定的目的地。SubFolder 是相對於「_待整理」的子資料夾；放在硬碟裡已有的影片旁邊時為 null。</summary>
    private sealed record PlanEntry(SourceItem Item, string? SubFolder, string Dir, string Name, string? Note, SourceItem? Video);

    /// <summary>檔名或資料夾規則用到拍攝資訊時，要依拍攝時間排序（流水號才會照時間編）。</summary>
    private static bool NeedsMedia(NamingOptions naming) =>
        naming.ByShotDate || naming.Uses(NamePartKind.Date) || naming.Uses(NamePartKind.CameraModel)
        || naming.Uses(NamePartKind.Sequence);

    /// <summary>
    /// 預計的新位置（完整路徑）：勾選、尚未複製的新照片依目前規則會放在哪裡、叫什麼檔名。
    /// 結果清單的「修改後檔名」用；和實際複製走同一個 <see cref="PlanTargets"/>，只有複製當下硬碟又多了同名檔案時才會不同。
    /// </summary>
    public static Dictionary<SourceItem, PlannedName> PlanNames(AnalysisResult analysis, NamingOptions naming, DateTime now)
    {
        var batch = analysis.Items.Where(i => i.Status == ItemStatus.New && i.CopiedTo is null && !i.Excluded).ToList();
        var plan = PlanTargets(analysis.BackupRoot, GetStagingRoot(analysis.BackupRoot), naming, NeedsMedia(naming),
            batch, analysis.Items, now);
        return plan.ToDictionary(e => e.Item, e => new PlannedName(Path.Combine(e.Dir, e.Name), e.Note));
    }

    /// <summary>預覽用：zip 裡的檔案解到指定資料夾並回傳路徑；一般檔案直接回傳原路徑。</summary>
    public static string ExtractEntry(SourceItem it, string dir)
    {
        if (!it.IsZipEntry)
        {
            return it.SourcePath;
        }
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, FileNamer.Sanitize(it.FileName, "預覽" + Path.GetExtension(it.FileName)));
        using var zip = OpenZip(it.SourcePath);
        using var src = OpenZipEntry(zip, it);
        using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        src.CopyTo(dst);
        return target;
    }

    /// <summary>
    /// 依規則替一批檔案決定子資料夾與檔名，不動任何檔案。檔名（含流水號）在每個子資料夾內各自決定，
    /// 避開硬碟上已有的檔名與這批已決定的檔名。字幕檔跟同批的影片走（排在影片後面，Video 指向那支影片），
    /// 影片已在備份資料夾裡時放到那支影片旁邊。
    /// </summary>
    private static List<PlanEntry> PlanTargets(string backupRoot, string stagingRoot, NamingOptions naming, bool sortByDate,
        IReadOnlyList<SourceItem> batch, IReadOnlyList<SourceItem> allItems, DateTime importTime)
    {
        var entries = new List<PlanEntry>();
        if (batch.Count == 0)
        {
            return entries;
        }
        var ordered = sortByDate
            ? batch.Select((it, i) => (it, i)).OrderBy(x => x.it.Media?.Taken ?? DateTime.MaxValue).ThenBy(x => x.i)
                .Select(x => x.it).ToList()
            : batch.ToList();

        // 硬碟上已有的檔名（每個資料夾只列一次）加上這批已決定的檔名
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> NamesIn(string dir)
        {
            if (!names.TryGetValue(dir, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.EnumerateFileSystemEntries(dir))
                        {
                            set.Add(Path.GetFileName(f));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                names[dir] = set;
            }
            return set;
        }
        bool Taken(string dir, string name) => NamesIn(dir).Contains(name);
        void Reserve(string dir, string name) => NamesIn(dir).Add(name);

        // SRT 跟來源資料裡同資料夾、同檔名的影片配對（不佔流水號）：影片這次一起複製時跟著影片走；
        // 影片已經在備份資料夾裡（比對為硬碟已有）時，放到硬碟裡那支影片旁邊
        var inBatch = batch.ToHashSet();
        var byLocation = allItems.GroupBy(i => i.Location, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var videosByKey = allItems.Where(i => MediaMetadata.IsVideo(i.FileName))
            .GroupBy(i => PairKey(i), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var companions = new List<(SourceItem Srt, SourceItem Video)>();
        var besideBackup = new List<(SourceItem Srt, string VideoPath)>();
        foreach (var srt in batch.Where(i => MediaMetadata.IsSubtitle(i.FileName)))
        {
            if (!videosByKey.TryGetValue(PairKey(srt), out var candidates))
            {
                continue;
            }
            // 來源資料重複的影片，改看它重複的那一份
            var resolved = candidates
                .Select(v => v.Status == ItemStatus.DuplicateInBatch && v.MatchedWith is not null
                    ? byLocation.GetValueOrDefault(v.MatchedWith) ?? v
                    : v)
                .ToList();
            if (resolved.FirstOrDefault(inBatch.Contains) is { } video)
            {
                companions.Add((srt, video));
            }
            else if (resolved.FirstOrDefault(v => v.Status == ItemStatus.AlreadyBackedUp && File.Exists(v.MatchedWith)) is { } onDisk)
            {
                besideBackup.Add((srt, onDisk.MatchedWith!));
            }
        }
        var paired = companions.Select(p => p.Srt).Concat(besideBackup.Select(p => p.Srt)).ToHashSet();
        var videoTargets = new Dictionary<SourceItem, PlanEntry>();

        string NameBeside(string dir, string videoName, string companionName)
        {
            var stem = Path.GetFileNameWithoutExtension(videoName);
            var ext = Path.GetExtension(companionName);
            var name = stem + ext;
            for (var n = 2; Taken(dir, name); n++)
            {
                name = $"{stem} ({n}){ext}"; // 不覆蓋既有檔案；這時就不再同名
            }
            return name;
        }

        foreach (var group in ordered.Where(i => !paired.Contains(i))
                     .GroupBy(i => naming.SubFolderFor(i.Media ?? default, importTime), StringComparer.OrdinalIgnoreCase))
        {
            var subFolder = group.Key;
            var dir = Path.Combine(stagingRoot, subFolder);
            var files = group.ToList();
            var inputs = files.Select(i => new NameInput(i.FileName, i.Media ?? default, FileNamer.FolderNameOf(i))).ToList();
            var assigned = FileNamer.AssignNames(naming, inputs, name => Taken(dir, name));
            var unknownFolder = naming.ByShotDate && subFolder == NamingOptions.UnknownDateFolder;
            for (var i = 0; i < files.Count; i++)
            {
                Reserve(dir, assigned[i]);
                var e = new PlanEntry(files[i], subFolder, dir, assigned[i],
                    CopyNote(naming, files[i].Media?.Source ?? DateSource.None, unknownFolder), null);
                entries.Add(e);
                videoTargets[files[i]] = e;
            }
        }

        // 字幕檔：放進影片所在的資料夾，檔名＝影片的新檔名＋原副檔名
        foreach (var (srt, video) in companions)
        {
            var v = videoTargets[video];
            var name = NameBeside(v.Dir, v.Name, srt.FileName);
            Reserve(v.Dir, name);
            entries.Add(new PlanEntry(srt, v.SubFolder, v.Dir, name, $"與影片 {v.Name} 放在一起", video));
        }
        foreach (var (srt, videoPath) in besideBackup)
        {
            var dir = Path.GetDirectoryName(videoPath)!;
            var name = NameBeside(dir, Path.GetFileName(videoPath), srt.FileName);
            Reserve(dir, name);
            entries.Add(new PlanEntry(srt, null, dir, name,
                $"放在硬碟裡已有的影片 {Path.GetRelativePath(backupRoot, videoPath)} 旁邊", null));
        }
        return entries;
    }

    /// <summary>
    /// 把完成的暫存檔搬到 <see cref="PlanTargets"/> 決定的位置；搬移失敗的刪除暫存檔（下次比對仍是新照片），
    /// 回傳第一個錯誤。字幕跟著的影片搬移失敗時，字幕改照一般規則放。
    /// </summary>
    private static Exception? FinishCopies(string backupRoot, string stagingRoot, NamingOptions naming, bool sortByDate,
        List<CopiedFile> completed, IReadOnlyList<SourceItem> allItems, HashIndex index, List<ImportRecord> imported,
        DateTime importTime, CopySummary summary)
    {
        if (completed.Count == 0)
        {
            return null;
        }
        var byItem = completed.ToDictionary(c => c.Item);
        Exception? firstError = null;

        // subFolder 為 null 時放在「_待整理」以外（硬碟裡已有的影片旁邊），不算進各資料夾張數
        bool Place(CopiedFile c, string? subFolder, string dir, string name, string? note)
        {
            var (it, partial, hash, _) = c;
            var target = Path.Combine(dir, name);
            try
            {
                Directory.CreateDirectory(dir);
                File.Move(partial, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeletePartial(partial);
                firstError ??= ex;
                return false;
            }
            var fi = new FileInfo(target);
            var rel = Path.GetRelativePath(backupRoot, target);
            index.Set(rel, fi.Length, fi.LastWriteTimeUtc.Ticks, hash);
            imported.Add(new ImportRecord(hash, fi.Length, importTime, rel));
            it.Hash = hash;
            it.CopiedTo = target;
            it.NameNote = note;
            summary.Copied++;
            summary.Bytes += it.Size;
            if (subFolder is null)
            {
                summary.NextToBackedUpVideo++;
            }
            else
            {
                summary.FolderCounts[subFolder] = summary.FolderCounts.GetValueOrDefault(subFolder) + 1;
            }
            return true;
        }

        var plan = PlanTargets(backupRoot, stagingRoot, naming, sortByDate, completed.Select(c => c.Item).ToList(),
            allItems, importTime);
        var placed = new HashSet<SourceItem>();
        var orphans = new List<SourceItem>();
        foreach (var e in plan)
        {
            if (e.Video is not null && !placed.Contains(e.Video))
            {
                orphans.Add(e.Item);
                continue;
            }
            if (Place(byItem[e.Item], e.SubFolder, e.Dir, e.Name, e.Note))
            {
                placed.Add(e.Item);
            }
        }
        if (orphans.Count > 0)
        {
            // 影片沒有搬過去：重新規劃時影片已不在這批，字幕照一般規則放
            foreach (var e in PlanTargets(backupRoot, stagingRoot, naming, sortByDate, orphans, allItems, importTime))
            {
                Place(byItem[e.Item], e.SubFolder, e.Dir, e.Name, e.Note);
            }
        }
        return firstError;
    }

    /// <summary>配對字幕與影片用：同一個來源資料夾（zip 內以內部路徑為準，分割的 zip 也能配對）＋去掉副檔名的檔名。</summary>
    private static string PairKey(SourceItem it)
    {
        var folder = it.EntryName is not null
            ? "zip:" + (Path.GetDirectoryName(it.EntryName.Replace('/', '\\')) ?? "")
            : "dir:" + (Path.GetDirectoryName(it.SourcePath) ?? "");
        return folder + "|" + Path.GetFileNameWithoutExtension(it.FileName);
    }

    /// <summary>結果清單的補充說明：檔名日期的來源、為什麼放在「日期不明」。</summary>
    private static string? CopyNote(NamingOptions naming, DateSource source, bool unknownFolder)
    {
        var dateNote = naming.Uses(NamePartKind.Date) ? DateNote(source) : null;
        if (!unknownFolder)
        {
            return dateNote;
        }
        const string folderNote = "放在「" + NamingOptions.UnknownDateFolder + "」資料夾";
        return dateNote is null ? "找不到拍攝時間，" + folderNote : dateNote + "，" + folderNote;
    }

    private static string? DateNote(DateSource source) => source switch
    {
        DateSource.FileName => "日期取自原檔名",
        DateSource.FileTime => "找不到拍攝時間，檔名日期取自檔案修改時間",
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
            || name.Equals(BackupSettingsFile.FileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(BackupSettingsFile.FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
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

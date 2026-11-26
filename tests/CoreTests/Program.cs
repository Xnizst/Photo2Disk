using System.IO.Compression;
using Photo2Disk;

// 比對核心的回歸測試：在暫存資料夾建立模擬的備份資料夾與 Google 下載 zip，
// 檢查分類、複製、命名衝突、快取、取消等行為。
var failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "[通過] " : "[失敗] ") + what);
    if (!ok)
    {
        failures++;
    }
}

// 既有測試沿用「依複製日期」放資料夾（預設已改成依拍攝年份）
NamingOptions ByCopyDate() => new() { Folders = FolderMode.CopyDate };

var rng = new Random(20261107);
byte[] Bytes(int size)
{
    var b = new byte[size];
    rng.NextBytes(b);
    return b;
}

var work = Path.Combine(Path.GetTempPath(), "p2d_test_" + Guid.NewGuid().ToString("N")[..8]);
var backup = Path.Combine(work, "外接硬碟", "照片");
var downloads = Path.Combine(work, "下載");
Directory.CreateDirectory(Path.Combine(backup, "2024 旅遊"));
Directory.CreateDirectory(downloads);

try
{
    // 備份資料夾既有內容
    var a = Bytes(100_000);
    var b = Bytes(200_000);
    var c = Bytes(300_000);
    File.WriteAllBytes(Path.Combine(backup, "IMG_A.jpg"), a);
    File.WriteAllBytes(Path.Combine(backup, "2024 旅遊", "改過名字.jpg"), b);
    File.WriteAllBytes(Path.Combine(backup, "IMG_C.jpg"), c);

    var new1 = Bytes(150_000);
    var sameSizeAsC = Bytes(300_000);       // 大小與 C 相同、內容不同
    var collide1 = Bytes(50_000);
    var collide2 = Bytes(60_000);

    void MakeZip(string path, params (string Name, byte[]? Data)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var e = zip.CreateEntry(name);
            if (data is not null)
            {
                using var s = e.Open();
                s.Write(data);
            }
        }
    }

    var zip1 = Path.Combine(downloads, "Photos-001.zip");
    var zip2 = Path.Combine(downloads, "Photos-002.zip");
    MakeZip(zip1,
        ("IMG_A.jpg", a),                  // 硬碟已有（同名）
        ("IMG_B(1).jpg", b),               // 硬碟已有（不同名、在子資料夾）
        ("新照片1.jpg", new1),              // 新
        ("IMG_C.jpg", sameSizeAsC),        // 新（同名同大小但內容不同）
        ("新照片1 的副本.jpg", new1),        // 來源資料重複
        ("空檔.txt", Array.Empty<byte>()), // 略過
        ("子資料夾/", null),                // 資料夾項目，略過
        ("子資料夾/IMG_0001.jpg", collide1)); // 新
    MakeZip(zip2,
        ("新照片1.jpg", new1),              // 來源資料重複（跨 zip）
        ("IMG_0001.jpg", collide2));       // 新，檔名與上面撞到
    var looseFolder = Path.Combine(downloads, "已解壓");
    Directory.CreateDirectory(looseFolder);
    var loose = Bytes(70_000);
    File.WriteAllBytes(Path.Combine(looseFolder, "散檔.jpg"), loose);
    File.WriteAllBytes(Path.Combine(looseFolder, "硬碟已有.jpg"), a);

    var sources = new[] { zip1, zip2, looseFolder };

    // ---- 第一次比對
    var r1 = DedupEngine.Analyze(backup, sources, null, CancellationToken.None);
    string StatusOf(string location) => r1.Items.Single(i => i.Location.EndsWith(location, StringComparison.Ordinal)).Status.ToString();

    Check(r1.Items.Count == 10, $"項目數 10（實際 {r1.Items.Count}），空檔與資料夾項目已略過");
    Check(StatusOf("Photos-001.zip\\IMG_A.jpg") == "AlreadyBackedUp", "同名同內容 → 硬碟已有");
    Check(StatusOf("IMG_B(1).jpg") == "AlreadyBackedUp", "改名後放在子資料夾 → 仍判定硬碟已有");
    Check(StatusOf("Photos-001.zip\\IMG_C.jpg") == "New", "同名同大小但內容不同 → 新照片");
    Check(StatusOf("Photos-001.zip\\新照片1.jpg") == "New", "第一次出現 → 新照片");
    Check(StatusOf("新照片1 的副本.jpg") == "DuplicateInBatch", "同一個 zip 內重複 → 來源資料重複");
    Check(StatusOf("Photos-002.zip\\新照片1.jpg") == "DuplicateInBatch", "跨 zip 重複 → 來源資料重複");
    var dupOfExisting = r1.Items.Single(i => i.Location.EndsWith("硬碟已有.jpg", StringComparison.Ordinal));
    Check(dupOfExisting.Status == ItemStatus.DuplicateInBatch && dupOfExisting.MatchedWith == "Photos-001.zip\\IMG_A.jpg",
        $"硬碟已有的照片在來源資料裡也重複 → 來源資料重複，指向第一份（實際 {dupOfExisting.Status}、{dupOfExisting.MatchedWith}）");
    Check(StatusOf("散檔.jpg") == "New", "資料夾來源 → 新照片");
    Check(r1.Count(ItemStatus.New) == 5, $"新照片 5 張（實際 {r1.Count(ItemStatus.New)}）");
    Check(r1.Count(ItemStatus.AlreadyBackedUp) == 2 && r1.Count(ItemStatus.DuplicateInBatch) == 3,
        $"硬碟已有 2、來源資料重複 3（實際 {r1.Count(ItemStatus.AlreadyBackedUp)}、{r1.Count(ItemStatus.DuplicateInBatch)}）");
    Check(r1.Count(ItemStatus.New) + r1.Count(ItemStatus.AlreadyBackedUp) + r1.Count(ItemStatus.DuplicateInBatch) == r1.Items.Count,
        "新照片＋硬碟已有＋來源資料重複＝來源資料的檔案數");
    Check(r1.IndexSaved && File.Exists(Path.Combine(backup, HashIndex.FileName)), "快取檔已寫入");
    Check((File.GetAttributes(Path.Combine(backup, HashIndex.FileName)) & FileAttributes.Hidden) != 0, "快取檔為隱藏檔");

    // ---- 複製
    var copy = DedupEngine.CopyNew(r1, null, CancellationToken.None, ByCopyDate());
    var outDir = copy.OutputFolder;
    Check(copy.Copied == 5, $"複製 5 張（實際 {copy.Copied}）");
    Check(File.Exists(Path.Combine(outDir, "IMG_0001.jpg")) && File.Exists(Path.Combine(outDir, "IMG_0001 (2).jpg")), "檔名衝突自動加 (2)");
    Check(File.ReadAllBytes(Path.Combine(outDir, "IMG_C.jpg")).AsSpan().SequenceEqual(sameSizeAsC), "複製內容正確");
    Check(File.ReadAllBytes(Path.Combine(backup, "IMG_C.jpg")).AsSpan().SequenceEqual(c), "原本的 IMG_C.jpg 未被覆蓋");
    Check(!Directory.EnumerateFiles(outDir, "*.p2d-partial").Any(), "沒有殘留暫存檔");
    Check(Directory.EnumerateFiles(outDir).Count() == 5, "待整理資料夾剛好 5 個檔案");

    // ---- 第二次比對：剛複製的都要算硬碟已有
    var r2 = DedupEngine.Analyze(backup, sources, null, CancellationToken.None);
    Check(r2.Count(ItemStatus.New) == 0, $"第二次比對沒有新照片（實際 {r2.Count(ItemStatus.New)}）");

    // ---- 使用者自己整理（搬移、改名）後再比對
    var moved = Path.Combine(backup, "2025 家庭", "整理後.jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
    File.Move(Path.Combine(outDir, "散檔.jpg"), moved);
    var r3 = DedupEngine.Analyze(backup, sources, null, CancellationToken.None);
    var looseItem = r3.Items.Single(i => i.FileName == "散檔.jpg");
    Check(looseItem.Status == ItemStatus.AlreadyBackedUp && looseItem.MatchedWith == moved, "搬移改名後仍判定硬碟已有，且指向新位置");
    Check(r3.Items.All(i => !i.IsMissingBefore), "照片還在硬碟裡時不會標示曾遺失");

    // ---- 匯入過的照片被刪除 → 標示曾遺失，仍可補回
    var historyPath = Path.Combine(backup, ImportHistory.FileName);
    Check(File.Exists(historyPath) && (File.GetAttributes(historyPath) & FileAttributes.Hidden) != 0, "匯入紀錄檔已寫入且為隱藏檔");
    File.Delete(moved);
    var r6 = DedupEngine.Analyze(backup, sources, null, CancellationToken.None);
    var lost = r6.Items.Single(i => i.FileName == "散檔.jpg");
    Check(lost.Status == ItemStatus.New && lost.IsMissingBefore, "刪除後再比對 → 新照片並標示曾遺失");
    Check(lost.PreviousImport?.RelativePath == Path.GetRelativePath(backup, Path.Combine(outDir, "散檔.jpg")), $"曾遺失註明當初匯入位置（{lost.PreviousImport?.RelativePath}）");
    Check(r6.Items.Count(i => i.IsMissingBefore) == 1, "只有被刪除的那張標示曾遺失");
    var copy2 = DedupEngine.CopyNew(r6, null, CancellationToken.None, ByCopyDate());
    Check(copy2.Copied == 1, "曾遺失的照片可以補回");
    var r7 = DedupEngine.Analyze(backup, sources, null, CancellationToken.None);
    Check(r7.Count(ItemStatus.New) == 0 && r7.Items.All(i => !i.IsMissingBefore), "補回後再比對 → 全部硬碟已有");
    var historyLines = File.ReadAllLines(historyPath).Count(l => !l.StartsWith('#'));
    Check(historyLines == 6, $"匯入紀錄共 6 筆（5 + 補回 1，實際 {historyLines}）");

    // ---- 新來源放在備份資料夾內 → 拒絕
    try
    {
        DedupEngine.Analyze(backup, new[] { Path.Combine(backup, "2024 旅遊") }, null, CancellationToken.None);
        Check(false, "來源在備份資料夾內應拒絕");
    }
    catch (UserFacingException)
    {
        Check(true, "來源在備份資料夾內會被拒絕");
    }

    // ---- 損壞的 zip
    var bad = Path.Combine(downloads, "壞掉.zip");
    File.WriteAllBytes(bad, Bytes(1000));
    try
    {
        DedupEngine.Analyze(backup, new[] { bad }, null, CancellationToken.None);
        Check(false, "損壞 zip 應顯示錯誤");
    }
    catch (UserFacingException ex)
    {
        Check(ex.Message.Contains("壞掉.zip"), "損壞 zip 顯示可讀的錯誤訊息");
    }

    // ---- 取消複製：不可留下半個檔案
    var bigZip = Path.Combine(downloads, "大檔.zip");
    MakeZip(bigZip, ("影片.mp4", Bytes(64 * 1024 * 1024)));
    var r4 = DedupEngine.Analyze(backup, new[] { bigZip }, null, CancellationToken.None);
    using (var cts = new CancellationTokenSource())
    {
        var p = new SyncProgress(info =>
        {
            if (info.Stage == "複製新照片" && info.Done > 4 * 1024 * 1024)
            {
                cts.Cancel();
            }
        });
        try
        {
            DedupEngine.CopyNew(r4, p, cts.Token, ByCopyDate());
            Check(false, "應該被取消");
        }
        catch (OperationCanceledException)
        {
            var leftovers = Directory.EnumerateFiles(outDir, "影片*").ToList();
            Check(leftovers.Count == 0, $"取消後沒有殘留（找到 {leftovers.Count} 個）");
        }
    }

    Check(!Directory.EnumerateFiles(Path.Combine(backup, DedupEngine.StagingFolderName), "*.p2d-partial", SearchOption.AllDirectories).Any(),
        "取消後沒有殘留暫存檔");

    // ---- 不同磁碟：zip 在 C、備份在 D，走平行計算路徑（zip 切段、兩邊同時讀）
    if (Directory.Exists(@"D:\") && !string.Equals(Path.GetPathRoot(work), @"D:\", StringComparison.OrdinalIgnoreCase))
    {
        var dBackup = Path.Combine(@"D:\", "p2d_coretest_" + Guid.NewGuid().ToString("N")[..8], "照片");
        Directory.CreateDirectory(dBackup);
        try
        {
            var existingData = Enumerable.Range(0, 30).Select(i => Bytes(10_000 + i * 997)).ToList();
            for (var i = 0; i < existingData.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(dBackup, $"舊_{i}.jpg"), existingData[i]);
            }
            var bigZip2 = Path.Combine(downloads, "跨磁碟.zip");
            var entries = new List<(string, byte[]?)>();
            for (var i = 0; i < 30; i++)
            {
                entries.Add(($"有_{i}.jpg", existingData[i]));        // 30 張硬碟已有
                entries.Add(($"新_{i}.jpg", Bytes(10_000 + i * 997)));  // 30 張新，大小與舊的相同、內容不同
            }
            MakeZip(bigZip2, entries.ToArray());
            var rx = DedupEngine.Analyze(dBackup, new[] { bigZip2 }, null, CancellationToken.None);
            Check(rx.Count(ItemStatus.AlreadyBackedUp) == 30 && rx.Count(ItemStatus.New) == 30,
                $"跨磁碟平行比對：已有 30、新 30（實際 {rx.Count(ItemStatus.AlreadyBackedUp)}、{rx.Count(ItemStatus.New)}）");
            Check(rx.Items.Where(i => i.FileName.StartsWith("有_")).All(i => i.Status == ItemStatus.AlreadyBackedUp)
                  && rx.Items.Where(i => i.FileName.StartsWith("新_")).All(i => i.Status == ItemStatus.New),
                "跨磁碟平行比對：每一張都分類正確");
            using var cts2 = new CancellationTokenSource();
            cts2.Cancel();
            try
            {
                DedupEngine.Analyze(dBackup, new[] { bigZip2 }, null, cts2.Token);
                Check(false, "跨磁碟取消應拋出 OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                Check(true, "跨磁碟比對可以取消");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(dBackup)!, true);
            }
            catch (IOException)
            {
            }
        }
    }
    else
    {
        Console.WriteLine("[略過] 沒有 D 槽，跨磁碟測試未執行");
    }

    // ---- 快取：備份檔案內容改變（大小、時間變了）要重算
    File.WriteAllBytes(Path.Combine(backup, "IMG_A.jpg"), Bytes(100_000));
    var r5 = DedupEngine.Analyze(backup, new[] { zip1 }, null, CancellationToken.None);
    var aItem = r5.Items.Single(i => i.Location.EndsWith("Photos-001.zip\\IMG_A.jpg", StringComparison.Ordinal));
    Check(aItem.Status == ItemStatus.New, "備份檔被換掉後重新計算，不沿用舊快取");
}
finally
{
    try
    {
        Directory.Delete(work, true);
    }
    catch (IOException)
    {
    }
}

// ================================================================ 檔名規則與拍攝日期
var work2 = Path.Combine(Path.GetTempPath(), "p2d_naming_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    Directory.CreateDirectory(work2);

    // ---- 拍攝日期：檔名
    Check(MediaMetadata.ParseFileNameDate("PXL_20231105_190233001.jpg") == new DateTime(2023, 11, 5, 19, 2, 33), "檔名日期：PXL_20231105_190233001");
    Check(MediaMetadata.ParseFileNameDate("IMG-20240102-WA0001.jpg") == new DateTime(2024, 1, 2), "檔名日期：IMG-20240102-WA0001（只有日期）");
    Check(MediaMetadata.ParseFileNameDate("Screenshot_2024-03-15-14-30-22.png") == new DateTime(2024, 3, 15, 14, 30, 22), "檔名日期：Screenshot_2024-03-15-14-30-22");
    Check(MediaMetadata.ParseFileNameDate("IMG_1234.jpg") is null, "檔名日期：IMG_1234 沒有日期");
    Check(MediaMetadata.ParseFileNameDate("20240231.jpg") is null, "檔名日期：不存在的 2 月 31 日不採用");

    // ---- 拍攝日期：檔案內容
    var jpgLe = MakeJpeg(MakeTiff("iPhone 15 Pro", "2024:03:15 14:30:22", true), Bytes(20_000));
    var jpgBe = MakeJpeg(MakeTiff("NIKON Z 6", "2021:07:01 08:09:10", false), Bytes(30_000));
    var heic = MakeHeic(MakeTiff("iPhone 15 Pro", "2024:03:15 18:05:10", false), Bytes(40_000));
    var videoUtc = new DateTime(2023, 12, 24, 12, 0, 0, DateTimeKind.Utc);
    var mp4 = MakeMp4(videoUtc, Bytes(50_000));
    var noExif = MakeJpeg(null, Bytes(25_000));
    var broken = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x02, 0xFF, 0xE1, 0xFF, 0xFF, 1, 2, 3, 4, 5, 6 };

    MediaInfo Meta(byte[] data, string name, DateTime? fileTime = null)
    {
        var f = Path.Combine(work2, "meta_" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllBytes(f, data);
        return MediaMetadata.Read(f, name, fileTime ?? new DateTime(2020, 1, 1));
    }
    var m1 = Meta(jpgLe, "IMG_1234.jpg");
    Check(m1.Taken == new DateTime(2024, 3, 15, 14, 30, 22) && m1.Source == DateSource.Exif && m1.CameraModel == "iPhone 15 Pro",
        $"JPEG EXIF（Intel 位元組順序）：{m1}");
    var m2 = Meta(jpgBe, "DSC_0001.jpg");
    Check(m2.Taken == new DateTime(2021, 7, 1, 8, 9, 10) && m2.CameraModel == "NIKON Z 6", $"JPEG EXIF（Motorola 位元組順序）：{m2}");
    var m3 = Meta(heic, "IMG_1240.HEIC");
    Check(m3.Taken == new DateTime(2024, 3, 15, 18, 5, 10) && m3.Source == DateSource.Exif, $"HEIC EXIF：{m3}");
    var m4 = Meta(mp4, "VID_0007.mp4");
    var videoLocal = DateTime.SpecifyKind(videoUtc.ToLocalTime(), DateTimeKind.Unspecified);
    Check(m4.Taken == videoLocal && m4.Source == DateSource.Video, $"MP4 建立時間轉當地時間：{m4}");
    // iPhone MOV：Apple 的當地時間欄位優先於 mvhd（真實樣本中 mvhd 可能是轉存時間）
    var mov = MakeMov(new DateTime(2019, 11, 4, 2, 7, 30, DateTimeKind.Utc), "2018-04-06T17:26:41+0100", "iPhone X", Bytes(30_000));
    var mMov = Meta(mov, "IMG_0001.MOV");
    Check(mMov.Taken == new DateTime(2018, 4, 6, 17, 26, 41) && mMov.Source == DateSource.Video && mMov.CameraModel == "iPhone X",
        $"iPhone MOV 用拍攝地當地時間與機型：{mMov}");
    var m5 = Meta(noExif, "PXL_20231105_190233001.jpg");
    Check(m5.Taken == new DateTime(2023, 11, 5, 19, 2, 33) && m5.Source == DateSource.FileName, $"沒有 EXIF → 取檔名日期：{m5}");
    var m6 = Meta(noExif, "照片.jpg", new DateTime(2022, 1, 2, 10, 0, 0));
    Check(m6.Taken == new DateTime(2022, 1, 2, 10, 0, 0) && m6.Source == DateSource.FileTime, $"都沒有 → 檔案修改時間：{m6}");
    var m7 = Meta(broken, "壞掉.jpg", new DateTime(2022, 1, 2));
    Check(m7.Source == DateSource.FileTime, "損壞的 JPEG 不會出錯，改用檔案修改時間");

    // ---- 檔名組成與流水號
    var opt = new NamingOptions();
    opt.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝年月-原檔名"));
    var names = FileNamer.AssignNames(opt, FileNamer.Samples, _ => false);
    Check(names.SequenceEqual(new[] { "2024-03-IMG_1234.HEIC", "2024-03-IMG_1240.HEIC", "2024-03-VID_0007.mp4" }),
        "拍攝年月-原檔名：" + string.Join("、", names));
    opt.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝日期-流水號"));
    names = FileNamer.AssignNames(opt, FileNamer.Samples, _ => false);
    Check(names.SequenceEqual(new[] { "2024-03-15-001.HEIC", "2024-03-15-002.HEIC", "2024-03-16-001.mp4" }),
        "流水號每天從 001 開始：" + string.Join("、", names));
    names = FileNamer.AssignNames(opt, FileNamer.Samples, n => n == "2024-03-15-001.HEIC");
    Check(names[0] == "2024-03-15-002.HEIC" && names[1] == "2024-03-15-003.HEIC", "流水號跳過已存在的檔名：" + string.Join("、", names));
    opt.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝日期_時間"));
    var sameSecond = new[] { FileNamer.Samples[0], FileNamer.Samples[0] with { OriginalName = "IMG_1235.HEIC" } };
    names = FileNamer.AssignNames(opt, sameSecond, _ => false);
    Check(names.SequenceEqual(new[] { "20240315_143022.HEIC", "20240315_143022 (2).HEIC" }), "同一秒撞名加 (2)：" + string.Join("、", names));
    opt.Apply(NamingOptions.Presets.Single(x => x.Name == "自訂文字-拍攝日期-流水號"));
    opt.CustomText = "日本:旅遊?";
    opt.Separator = "_";
    names = FileNamer.AssignNames(opt, FileNamer.Samples.Take(1).ToList(), _ => false);
    Check(names[0] == "日本_旅遊__2024-03-15_001.HEIC", "自訂文字的不合法字元換成底線：" + names[0]);
    opt.Parts = NamingOptions.DefaultParts();
    foreach (var part in opt.Parts)
    {
        part.Enabled = part.Kind is NamePartKind.CameraModel or NamePartKind.MediaType or NamePartKind.FolderName;
    }
    opt.Separator = "-";
    names = FileNamer.AssignNames(opt, FileNamer.Samples, _ => false);
    Check(names[0] == "iPhone 15 Pro-照片-2024 日本旅遊.HEIC" && names[2] == "影片-2024 日本旅遊.mp4",
        "型號、照片／影片、資料夾名稱；影片沒有型號時略過：" + string.Join("、", names));
    var off = new NamingOptions();
    names = FileNamer.AssignNames(off, new[] { FileNamer.Samples[0], FileNamer.Samples[0] }, _ => false);
    Check(names.SequenceEqual(new[] { "IMG_1234.HEIC", "IMG_1234 (2).HEIC" }), "不改名時保留原檔名，撞名加 (2)");

    // ---- 實際複製：zip 內的照片依拍攝年月改名
    var backup2 = Path.Combine(work2, "硬碟", "照片");
    Directory.CreateDirectory(backup2);
    var takeout = Path.Combine(work2, "下載", "Takeout.zip");
    Directory.CreateDirectory(Path.GetDirectoryName(takeout)!);
    using (var zip = ZipFile.Open(takeout, ZipArchiveMode.Create))
    {
        void Add(string name, byte[] data, DateTime? time = null)
        {
            var e = zip.CreateEntry(name);
            if (time is { } t)
            {
                e.LastWriteTime = t;
            }
            using var st = e.Open();
            st.Write(data);
        }
        Add("Google 相簿/2024 日本旅遊/IMG_1234.jpg", jpgLe);
        Add("Google 相簿/2024 日本旅遊/IMG_1240.HEIC", heic);
        Add("VID_0007.mp4", mp4);
        Add("PXL_20231105_190233001.jpg", noExif);
        Add("random.png", Bytes(10_000), new DateTime(2022, 1, 2, 10, 0, 0));
    }
    var rn = DedupEngine.Analyze(backup2, new[] { takeout }, null, CancellationToken.None);
    var naming = new NamingOptions();
    naming.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝年月-原檔名"));
    naming.Folders = FolderMode.CopyDate;
    var cn = DedupEngine.CopyNew(rn, null, CancellationToken.None, naming);
    var copiedNames = Directory.EnumerateFiles(cn.OutputFolder).Select(Path.GetFileName).OrderBy(x => x).ToList();
    var expected = new[]
    {
        "2022-01-random.png", "2023-11-PXL_20231105_190233001.jpg", videoLocal.ToString("yyyy-MM") + "-VID_0007.mp4",
        "2024-03-IMG_1234.jpg", "2024-03-IMG_1240.HEIC",
    }.OrderBy(x => x).ToList();
    Check(copiedNames.SequenceEqual(expected), "複製時改名：" + string.Join("、", copiedNames));
    var pngItem = rn.Items.Single(i => i.FileName == "random.png");
    Check(pngItem.NameNote?.Contains("檔案修改時間") == true, "日期取自檔案修改時間時有註明：" + pngItem.NameNote);
    Check(rn.Items.Single(i => i.FileName == "IMG_1234.jpg").NameNote is null, "有拍攝時間的照片不加註明");
    Check(File.ReadAllBytes(Path.Combine(cn.OutputFolder, "2024-03-IMG_1240.HEIC")).AsSpan().SequenceEqual(heic), "改名後內容不變");
    var again = DedupEngine.Analyze(backup2, new[] { takeout }, null, CancellationToken.None);
    Check(again.Count(ItemStatus.AlreadyBackedUp) == 5, "改名後再比對：全部硬碟已有");
    var hist = File.ReadAllLines(Path.Combine(backup2, ImportHistory.FileName)).Where(l => !l.StartsWith('#')).ToList();
    Check(hist.Count == 5 && hist.Any(l => l.EndsWith("2024-03-IMG_1234.jpg", StringComparison.Ordinal)), "匯入紀錄寫的是新檔名");

    // ---- 依拍攝年份分資料夾
    Check(new NamingOptions().Folders == FolderMode.Year, "預設依拍攝年份分資料夾");
    var oldSettings = System.Text.Json.JsonSerializer.Deserialize<NamingOptions>("{\"Enabled\":true,\"DateFormat\":\"yyyy-MM\"}")!;
    Check(oldSettings.Folders == FolderMode.Year, "舊版設定檔沒有資料夾設定 → 依拍攝年份");
    var badSettings = System.Text.Json.JsonSerializer.Deserialize<NamingOptions>("{\"Folders\":9}")!;
    badSettings.Normalize();
    Check(badSettings.Folders == FolderMode.Year, "資料夾設定值不合法 → 依拍攝年份");

    // ---- 年份\月份分層、日期精度＋日期分隔、@ 與自訂分隔符號（2026-11-26）
    var shot = new MediaInfo(new DateTime(2023, 7, 10, 8, 5, 9), DateSource.Exif, "DJI");
    Check(new NamingOptions { Folders = FolderMode.YearMonth }.SubFolderFor(shot, DateTime.Now) == @"2023\2023-07", "年份\\年份-月份");
    Check(new NamingOptions { Folders = FolderMode.YearThenMonth }.SubFolderFor(shot, DateTime.Now) == @"2023\07", "年份\\月份");
    var monthOnly = System.Text.Json.JsonSerializer.Deserialize<NamingOptions>(
        System.Text.Json.JsonSerializer.Serialize(new NamingOptions { Folders = FolderMode.YearThenMonth }))!;
    monthOnly.Normalize();
    Check(monthOnly.Folders == FolderMode.YearThenMonth, "年份\\月份設定可存回");
    foreach (var oldFormat in new[] { "yyyy-MM", "yyyy-MM-dd", "yyyyMMdd_HHmmss", "yyyy年MM月", "yyyy年MM月dd日", "yyyy" })
    {
        var o = new NamingOptions { DateFormat = oldFormat };
        o.Normalize();
        Check(o.DateFormat == oldFormat, "舊版日期格式保留：" + oldFormat);
    }
    var expectedDates = new (DatePrecision P, string S, string Text)[]
    {
        (DatePrecision.Day, "", "20230710"),
        (DatePrecision.Day, ".", "2023.07.10"),
        (DatePrecision.Day, "_", "2023_07_10"),
        (DatePrecision.YearMonth, "", "202307"),
        (DatePrecision.DayTime, "-", "2023-07-10_080509"),
        (DatePrecision.DayTime, NamingOptions.ChineseDateStyle, "2023年07月10日08時05分09秒"),
        (DatePrecision.Year, ".", "2023"),
    };
    foreach (var (p, s, text) in expectedDates)
    {
        var fmt = NamingOptions.BuildDateFormat(p, s);
        var got = shot.Taken!.Value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
        Check(got == text, $"日期格式 {p}/{s} → {got}");
        Check(NamingOptions.TryParseDateFormat(fmt, out var p2, out var s2) && NamingOptions.BuildDateFormat(p2, s2) == fmt, "日期格式可拆回：" + fmt);
    }
    var badDate = new NamingOptions { DateFormat = "dd/MM/yyyy" };
    badDate.Normalize();
    Check(badDate.DateFormat == "yyyy-MM", "不在組合範圍內的日期格式 → 年月");
    Check(NamingOptions.DateStyleLabel("", "不隔開") == "不隔開（ex. 20240315）", "日期分隔選項文字：" + NamingOptions.DateStyleLabel("", "不隔開"));

    var at = new NamingOptions();
    at.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝日期@原檔名"));
    var atNames = FileNamer.AssignNames(at, new[] { new NameInput("DJI_0755.DNG", shot, "") }, _ => false);
    Check(atNames.Single() == "20230710@DJI_0755.DNG", "同學回報的命名方式：" + atNames.Single());
    Check(at.MatchingPreset()?.Name == "拍攝日期@原檔名" && at.Describe() == "拍攝日期@原檔名", "@ 組合的說明：" + at.Describe());

    var custom = new NamingOptions { Enabled = true, DateFormat = "yyyyMMdd", Separator = "#~" };
    custom.Normalize();
    Check(custom.Separator == "#~", "自訂分隔符號保留");
    Check(FileNamer.AssignNames(custom, new[] { new NameInput("DJI_0755.DNG", shot, "") }, _ => false).Single() == "20230710#~DJI_0755.DNG", "自訂分隔符號套用");
    Check(NamingOptions.CleanSeparator("a/b:c*d?ef") == "abcde", "自訂分隔符號拿掉不合法字元並限制 5 字：" + NamingOptions.CleanSeparator("a/b:c*d?ef"));
    var joined = new NamingOptions { Enabled = true, DateFormat = "yyyyMMdd", Separator = "" };
    joined.Normalize();
    Check(FileNamer.AssignNames(joined, new[] { new NameInput("DJI_0755.DNG", shot, "") }, _ => false).Single() == "20230710DJI_0755.DNG"
          && joined.Describe() == "拍攝日期＋原檔名", "不隔開：" + joined.Describe());

    string Staging(string root) => Path.Combine(root, DedupEngine.StagingFolderName);
    List<string> RelFiles(string root) => Directory.EnumerateFiles(Staging(root), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(Staging(root), f)).OrderBy(x => x, StringComparer.Ordinal).ToList();

    var backupY = Path.Combine(work2, "硬碟Y", "照片");
    Directory.CreateDirectory(backupY);
    var ry = DedupEngine.Analyze(backupY, new[] { takeout }, null, CancellationToken.None);
    var cy = DedupEngine.CopyNew(ry, null, CancellationToken.None, new NamingOptions { Folders = FolderMode.Year });
    var vy = videoLocal.ToString("yyyy");
    var expectedY = new[]
    {
        @"2024\IMG_1234.jpg", @"2024\IMG_1240.HEIC", vy + @"\VID_0007.mp4", @"2023\PXL_20231105_190233001.jpg",
        NamingOptions.UnknownDateFolder + @"\random.png",
    }.OrderBy(x => x, StringComparer.Ordinal).ToList();
    var gotY = RelFiles(backupY);
    Check(gotY.SequenceEqual(expectedY), "依拍攝年份：" + string.Join("、", gotY));
    Check(cy.OutputFolder == Staging(backupY), "依拍攝年份時輸出資料夾是「_待整理」本身");
    Check(cy.FolderCounts.GetValueOrDefault("2024") == 2 && cy.FolderCounts.GetValueOrDefault(NamingOptions.UnknownDateFolder) == 1
          && cy.FolderCounts.Values.Sum() == 5, "各資料夾張數：" + string.Join("、", cy.FolderCounts.Select(kv => $"{kv.Key}={kv.Value}")));
    var pngY = ry.Items.Single(i => i.FileName == "random.png");
    Check(pngY.NameNote == "找不到拍攝時間，放在「日期不明」資料夾", "只有檔案修改時間 → 日期不明並註明：" + pngY.NameNote);
    Check(ry.Items.Single(i => i.FileName == "PXL_20231105_190233001.jpg").NameNote is null, "原檔名日期可信，分進年份、不加註明");
    Check(!Directory.EnumerateFiles(Staging(backupY), "*.p2d-partial", SearchOption.AllDirectories).Any(), "依拍攝年份：沒有殘留暫存檔");
    var againY = DedupEngine.Analyze(backupY, new[] { takeout }, null, CancellationToken.None);
    Check(againY.Count(ItemStatus.AlreadyBackedUp) == 5, "分年份後再比對：全部硬碟已有");
    var histY = File.ReadAllLines(Path.Combine(backupY, ImportHistory.FileName)).Where(l => !l.StartsWith('#')).ToList();
    Check(histY.Any(l => l.EndsWith(DedupEngine.StagingFolderName + @"\2024\IMG_1234.jpg", StringComparison.Ordinal)), "匯入紀錄寫的是年份資料夾裡的位置");

    // ---- 年份＋月份，搭配流水號（流水號在每個資料夾內各自編）
    var backupM = Path.Combine(work2, "硬碟M", "照片");
    Directory.CreateDirectory(backupM);
    var rm = DedupEngine.Analyze(backupM, new[] { takeout }, null, CancellationToken.None);
    var namingM = new NamingOptions();
    namingM.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝日期-流水號"));
    namingM.Folders = FolderMode.YearMonth;
    Check(rm.Items.Where(i => i.Status == ItemStatus.New).All(i => i.Media is not null), "比對時已讀好新照片的拍攝資訊");
    Check(rm.Items.Single(i => i.FileName == "IMG_1234.jpg").Media?.Source == DateSource.Exif, "zip 內照片的 EXIF 在比對時讀到");
    var planM = DedupEngine.PlanNames(rm, namingM, DateTime.Now);
    DedupEngine.CopyNew(rm, null, CancellationToken.None, namingM);
    var planMismatch = rm.Items.Where(i => i.CopiedTo is not null && planM.GetValueOrDefault(i)?.Path != i.CopiedTo)
        .Select(i => $"{i.FileName}: 預計 {planM.GetValueOrDefault(i)?.Path}，實際 {i.CopiedTo}").ToList();
    Check(planM.Count == 5 && planMismatch.Count == 0, "預計檔名與實際複製結果相同" + string.Join("；", planMismatch));

    // ---- 複製時把這次用的更多設定存進備份資料夾（隱藏檔），不算進備份檔案
    var settingsPath = BackupSettingsFile.PathOf(backupM);
    var savedM = BackupSettingsFile.Load(backupM);
    Check(savedM is not null && BackupSettingsFile.SameAs(savedM.Naming, namingM) && savedM.SavedAt > DateTime.Now.AddMinutes(-5),
        "複製後備份資料夾裡有這次的設定");
    Check((File.GetAttributes(settingsPath) & FileAttributes.Hidden) != 0, "設定檔為隱藏檔");
    Check(!BackupSettingsFile.SameAs(savedM!.Naming, new NamingOptions()), "設定不同時判斷為不同");
    var againM = DedupEngine.Analyze(backupM, new[] { takeout }, null, CancellationToken.None);
    Check(againM.BackupFileCount == 5 && againM.Count(ItemStatus.AlreadyBackedUp) == 5, $"設定檔不算進備份檔案（{againM.BackupFileCount}）");
    File.SetAttributes(settingsPath, FileAttributes.Normal);
    File.WriteAllText(settingsPath, "{壞掉的內容");
    Check(BackupSettingsFile.Load(backupM) is null, "設定檔損壞時當作沒有");
    Check(BackupSettingsFile.Load(Path.Combine(work2, "不存在")) is null, "沒有設定檔時回傳 null");
    var gotM = RelFiles(backupM);
    var vm = videoLocal.ToString("yyyy") + @"\" + videoLocal.ToString("yyyy-MM") + @"\" + videoLocal.ToString("yyyy-MM-dd") + "-001.mp4";
    var expectedM = new[]
    {
        @"2024\2024-03\2024-03-15-001.jpg", @"2024\2024-03\2024-03-15-002.HEIC", vm,
        @"2023\2023-11\2023-11-05-001.jpg", NamingOptions.UnknownDateFolder + @"\2022-01-02-001.png",
    }.OrderBy(x => x, StringComparer.Ordinal).ToList();
    Check(gotM.SequenceEqual(expectedM), "年份＋月份＋流水號：" + string.Join("、", gotM));
    var pngM = rm.Items.Single(i => i.FileName == "random.png");
    Check(pngM.NameNote == "找不到拍攝時間，檔名日期取自檔案修改時間，放在「日期不明」資料夾", "檔名與資料夾的註明合併：" + pngM.NameNote);

    // ---- 同一個年份資料夾已有同名檔案 → 加 (2)，不覆蓋
    var rm2Backup = Path.Combine(work2, "硬碟Y2", "照片");
    Directory.CreateDirectory(Path.Combine(rm2Backup, DedupEngine.StagingFolderName, "2024"));
    var squatter = Bytes(1234);
    File.WriteAllBytes(Path.Combine(rm2Backup, DedupEngine.StagingFolderName, "2024", "IMG_1234.jpg"), squatter);
    var ry2 = DedupEngine.Analyze(rm2Backup, new[] { takeout }, null, CancellationToken.None);
    DedupEngine.CopyNew(ry2, null, CancellationToken.None, new NamingOptions { Folders = FolderMode.Year });
    var y2 = Path.Combine(rm2Backup, DedupEngine.StagingFolderName, "2024");
    Check(File.ReadAllBytes(Path.Combine(y2, "IMG_1234.jpg")).AsSpan().SequenceEqual(squatter)
          && File.ReadAllBytes(Path.Combine(y2, "IMG_1234 (2).jpg")).AsSpan().SequenceEqual(jpgLe),
        "年份資料夾已有同名檔案：新照片加 (2)，原檔不變");

    // ---- 字幕檔（DJI 的 SRT）跟同名影片放在同一個資料夾、沿用影片的新檔名
    var drone = Path.Combine(work2, "空拍下載");
    Directory.CreateDirectory(drone);
    var droneUtc = new DateTime(2023, 10, 5, 12, 56, 20, DateTimeKind.Utc);
    var droneLocal = droneUtc.ToLocalTime();
    File.WriteAllBytes(Path.Combine(drone, "DJI_0001.MP4"), MakeMp4(droneUtc, Bytes(40_000)));
    string Srt(string date) =>
        $"1\r\n00:00:00,000 --> 00:00:00,033\r\n<font size=\"36\">SrtCnt : 1, DiffTime : 33ms\r\n{date},015,053\r\n[iso : 250] [shutter : 1/30.0]\r\n</font>\r\n";
    File.WriteAllText(Path.Combine(drone, "DJI_0001.SRT"), Srt("2023-10-05 20:56:20"));
    File.WriteAllText(Path.Combine(drone, "DJI_0002.SRT"), Srt("2022-06-01 08:00:00")); // 影片不在這批
    File.WriteAllText(Path.Combine(drone, "movie.srt"), "1\r\n00:00:01,000 --> 00:00:02,000\r\n你好\r\n"); // 一般字幕，沒有日期
    File.SetLastWriteTime(Path.Combine(drone, "movie.srt"), new DateTime(2021, 1, 1, 9, 0, 0));
    Check(MediaMetadata.Read(Path.Combine(drone, "DJI_0002.SRT"), "DJI_0002.SRT", DateTime.MinValue)
            is { Taken: var srtTaken, Source: DateSource.Video } && srtTaken == new DateTime(2022, 6, 1, 8, 0, 0),
        "SRT 讀出第一筆拍攝時間");

    var backupS = Path.Combine(work2, "硬碟S", "照片");
    Directory.CreateDirectory(backupS);
    var rs = DedupEngine.Analyze(backupS, new[] { drone }, null, CancellationToken.None);
    var namingS = new NamingOptions();
    namingS.Apply(NamingOptions.Presets.Single(x => x.Name == "拍攝日期-流水號"));
    namingS.Folders = FolderMode.Year;
    var planS = DedupEngine.PlanNames(rs, namingS, DateTime.Now);
    DedupEngine.CopyNew(rs, null, CancellationToken.None, namingS);
    Check(rs.Items.Where(i => i.CopiedTo is not null).All(i => planS.GetValueOrDefault(i)?.Path == i.CopiedTo),
        "字幕跟影片：預計位置與實際相同");
    var videoStem = droneLocal.ToString("yyyy") + @"\" + droneLocal.ToString("yyyy-MM-dd") + "-001";
    var expectedS = new[]
    {
        videoStem + ".MP4", videoStem + ".SRT", @"2022\2022-06-01-001.SRT",
        NamingOptions.UnknownDateFolder + @"\2021-01-01-001.srt",
    }.OrderBy(x => x, StringComparer.Ordinal).ToList();
    var gotS = RelFiles(backupS);
    Check(gotS.SequenceEqual(expectedS), "字幕跟影片同資料夾同檔名、不佔流水號：" + string.Join("、", gotS));
    Check(rs.Items.Single(i => i.FileName == "DJI_0001.SRT").NameNote == $"與影片 {Path.GetFileName(videoStem)}.MP4 放在一起",
        "字幕註明跟哪支影片放在一起");
    var againS = DedupEngine.Analyze(backupS, new[] { drone }, null, CancellationToken.None);
    Check(againS.Count(ItemStatus.AlreadyBackedUp) == 4, "字幕改名搬移後再比對：全部硬碟已有");

    // 保留原檔名、依年份：字幕仍跟影片同一個資料夾（不會被分到 SRT 自己的日期）
    var backupS2 = Path.Combine(work2, "硬碟S2", "照片");
    Directory.CreateDirectory(backupS2);
    File.WriteAllText(Path.Combine(drone, "DJI_0001.SRT"), Srt("2019-01-01 00:00:00")); // 故意和影片不同年
    var rs2 = DedupEngine.Analyze(backupS2, new[] { drone }, null, CancellationToken.None);
    DedupEngine.CopyNew(rs2, null, CancellationToken.None, new NamingOptions { Folders = FolderMode.Year });
    var gotS2 = RelFiles(backupS2);
    Check(gotS2.Contains(droneLocal.ToString("yyyy") + @"\DJI_0001.MP4") && gotS2.Contains(droneLocal.ToString("yyyy") + @"\DJI_0001.SRT"),
        "保留原檔名：字幕跟影片同資料夾：" + string.Join("、", gotS2));

    // 影片已經在備份資料夾裡（使用者改名整理過）→ 字幕放到那支影片旁邊，檔名跟著影片
    var drone3 = Path.Combine(work2, "空拍下載3");
    Directory.CreateDirectory(drone3);
    var video3 = MakeMp4(droneUtc.AddDays(1), Bytes(45_000));
    File.WriteAllBytes(Path.Combine(drone3, "DJI_0003.MP4"), video3);
    File.WriteAllText(Path.Combine(drone3, "DJI_0003.SRT"), Srt("2023-10-06 20:56:20"));
    var backupS3 = Path.Combine(work2, "硬碟S3", "照片");
    var userFolder = Path.Combine(backupS3, "2023 空拍");
    Directory.CreateDirectory(userFolder);
    File.WriteAllBytes(Path.Combine(userFolder, "飛行紀錄.MP4"), video3);
    var rs3 = DedupEngine.Analyze(backupS3, new[] { drone3 }, null, CancellationToken.None);
    Check(rs3.Items.Single(i => i.FileName == "DJI_0003.MP4").Status == ItemStatus.AlreadyBackedUp, "影片已在硬碟（改名整理過）→ 硬碟已有");
    var planS3 = DedupEngine.PlanNames(rs3, namingS, DateTime.Now);
    var cs3 = DedupEngine.CopyNew(rs3, null, CancellationToken.None, namingS);
    Check(rs3.Items.Where(i => i.CopiedTo is not null).All(i => planS3.GetValueOrDefault(i)?.Path == i.CopiedTo),
        "字幕放到硬碟已有的影片旁：預計位置與實際相同");
    Check(File.Exists(Path.Combine(userFolder, "飛行紀錄.SRT"))
          && !Directory.EnumerateFiles(Staging(backupS3), "*", SearchOption.AllDirectories).Any(),
        "字幕放到硬碟裡那支影片旁邊、檔名跟著影片，沒有放進「_待整理」");
    Check(cs3.Copied == 1 && cs3.NextToBackedUpVideo == 1 && cs3.FolderCounts.Count == 0, "完成摘要：1 個字幕放在已有的影片旁");
    Check(rs3.Items.Single(i => i.FileName == "DJI_0003.SRT").NameNote == @"放在硬碟裡已有的影片 2023 空拍\飛行紀錄.MP4 旁邊",
        "字幕註明放在哪支影片旁邊");
    var againS3 = DedupEngine.Analyze(backupS3, new[] { drone3 }, null, CancellationToken.None);
    Check(againS3.Count(ItemStatus.AlreadyBackedUp) == 2, "再比對：影片與字幕都是硬碟已有");

    // ---- DNG 轉檔（用假的轉檔器測流程；真實轉檔品質由 tests\ConvertProbe 驗證）
    var cam = Path.Combine(work2, "相機下載");
    Directory.CreateDirectory(cam);
    byte[] FakeDng() => new byte[] { (byte)'I', (byte)'I', 42, 0 }.Concat(Bytes(30_000)).ToArray();
    File.WriteAllBytes(Path.Combine(cam, "CAM_0001.DNG"), FakeDng());
    File.WriteAllBytes(Path.Combine(cam, "CAM_0002.DNG"), FakeDng());
    File.WriteAllBytes(Path.Combine(cam, "CAM_0002.JPG"), MakeJpeg(null, Bytes(20_000))); // 相機同時拍的 JPG
    File.WriteAllBytes(Path.Combine(cam, "CAM_0003.DNG"), FakeDng());
    var calls = new List<RawSourceInfo>();
    var fake = new FakeConverter((rawPath, outPath, info) =>
    {
        calls.Add(info);
        if (info.Name == "CAM_0003.DNG")
        {
            File.WriteAllBytes(outPath, new byte[] { 1 }); // 寫到一半失敗
            throw new InvalidDataException("模擬的解碼錯誤");
        }
        Check(File.Exists(rawPath), "轉檔時讀的是已複製到備份資料夾的 DNG");
        File.WriteAllBytes(outPath, MakeJpeg(null, Bytes(5_000)));
        return RawConvertMethod.MatchedToPreview;
    });
    var backupD = Path.Combine(work2, "硬碟D", "照片");
    Directory.CreateDirectory(backupD);
    var rd = DedupEngine.Analyze(backupD, new[] { cam }, null, CancellationToken.None);
    var cd = DedupEngine.CopyNew(rd, null, CancellationToken.None, new NamingOptions { ConvertDng = true }, fake);
    var unknown = Path.Combine(Staging(backupD), NamingOptions.UnknownDateFolder);
    Check(File.Exists(Path.Combine(unknown, "CAM_0001.jpg")) && File.Exists(Path.Combine(unknown, "CAM_0001.DNG")),
        "DNG 原檔照常複製，旁邊另存同名 JPG");
    Check(cd.Converted == 1 && cd.ConvertSkipped == 1 && cd.ConvertFailed == 1 && !cd.ConvertCancelled,
        $"轉檔摘要：轉出 {cd.Converted}、略過 {cd.ConvertSkipped}、失敗 {cd.ConvertFailed}");
    Check(calls.Count == 2 && calls.All(c => c.Sha256.Length == 64), "有相機直出 JPG 的 DNG 不呼叫轉檔器；傳入原檔雜湊");
    Check(rd.Items.Single(i => i.FileName == "CAM_0001.DNG").NameNote?.EndsWith("已轉出 CAM_0001.jpg（依相機顏色重建）") == true,
        "結果清單註明轉出的檔名與方式：" + rd.Items.Single(i => i.FileName == "CAM_0001.DNG").NameNote);
    Check(rd.Items.Single(i => i.FileName == "CAM_0002.DNG").NameNote?.Contains("相機直出") == true, "略過時註明原因");
    Check(rd.Items.Single(i => i.FileName == "CAM_0003.DNG").NameNote?.Contains("轉檔失敗：模擬的解碼錯誤") == true
          && !File.Exists(Path.Combine(unknown, "CAM_0003.jpg")), "轉檔失敗：註明原因、不留半成品 JPG");
    Check(!Directory.EnumerateFiles(Staging(backupD), "*.p2d-partial", SearchOption.AllDirectories).Any(), "轉檔後沒有殘留暫存檔");
    var againD = DedupEngine.Analyze(backupD, new[] { cam }, null, CancellationToken.None);
    Check(againD.Count(ItemStatus.AlreadyBackedUp) == 4 && againD.Count(ItemStatus.New) == 0, "轉檔後再比對：來源全部硬碟已有，轉出的 JPG 不影響");

    // 關閉轉檔：不呼叫轉檔器
    var backupD2 = Path.Combine(work2, "硬碟D2", "照片");
    Directory.CreateDirectory(backupD2);
    calls.Clear();
    var rd2 = DedupEngine.Analyze(backupD2, new[] { cam }, null, CancellationToken.None);
    var cd2 = DedupEngine.CopyNew(rd2, null, CancellationToken.None, new NamingOptions(), fake);
    Check(calls.Count == 0 && cd2.Converted == 0 && cd2.Copied == 4, "預設不轉檔");

    // 轉檔途中取消：已複製的照片保留，剩下的 DNG 不轉，不拋例外
    var backupD3 = Path.Combine(work2, "硬碟D3", "照片");
    Directory.CreateDirectory(backupD3);
    using var ctsD = new CancellationTokenSource();
    var cancelling = new FakeConverter((_, outPath, _) =>
    {
        File.WriteAllBytes(outPath, MakeJpeg(null, Bytes(5_000)));
        ctsD.Cancel();
        return RawConvertMethod.RawOnly;
    });
    var rd3 = DedupEngine.Analyze(backupD3, new[] { cam }, null, CancellationToken.None);
    var cd3 = DedupEngine.CopyNew(rd3, null, ctsD.Token, new NamingOptions { ConvertDng = true }, cancelling);
    Check(cd3.Copied == 4 && cd3.Converted == 1 && cd3.ConvertCancelled, "轉檔途中取消：照片全部保留、只轉出 1 張");
}
finally
{
    try
    {
        Directory.Delete(work2, true);
    }
    catch (IOException)
    {
    }
}

// ---- 產生測試檔案用
static void W16(List<byte> b, int v, bool le)
{
    b.AddRange(le ? new[] { (byte)v, (byte)(v >> 8) } : new[] { (byte)(v >> 8), (byte)v });
}

static void W32(List<byte> b, long v, bool le)
{
    var x = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    if (le)
    {
        Array.Reverse(x);
    }
    b.AddRange(x);
}

static byte[] MakeTiff(string model, string date, bool le)
{
    var modelBytes = System.Text.Encoding.ASCII.GetBytes(model + "\0");
    var dateBytes = System.Text.Encoding.ASCII.GetBytes(date + "\0");
    const int ifd0 = 8;
    const int ifd0Size = 2 + 12 * 2 + 4;
    const int exifIfd = ifd0 + ifd0Size;
    const int dataStart = exifIfd + 2 + 12 + 4;
    var b = new List<byte>();
    b.AddRange(le ? "II"u8.ToArray() : "MM"u8.ToArray());
    W16(b, 42, le);
    W32(b, ifd0, le);
    W16(b, 2, le);
    W16(b, 0x0110, le); W16(b, 2, le); W32(b, modelBytes.Length, le); W32(b, dataStart, le);
    W16(b, 0x8769, le); W16(b, 4, le); W32(b, 1, le); W32(b, exifIfd, le);
    W32(b, 0, le);
    W16(b, 1, le);
    W16(b, 0x9003, le); W16(b, 2, le); W32(b, dateBytes.Length, le); W32(b, dataStart + modelBytes.Length, le);
    W32(b, 0, le);
    b.AddRange(modelBytes);
    b.AddRange(dateBytes);
    return b.ToArray();
}

static byte[] MakeJpeg(byte[]? tiff, byte[] payload)
{
    var b = new List<byte> { 0xFF, 0xD8 };
    if (tiff is not null)
    {
        b.AddRange(new byte[] { 0xFF, 0xE1 });
        W16(b, 2 + 6 + tiff.Length, false);
        b.AddRange("Exif\0\0"u8.ToArray());
        b.AddRange(tiff);
    }
    b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x02 });
    b.AddRange(payload);
    b.AddRange(new byte[] { 0xFF, 0xD9 });
    return b.ToArray();
}

static void Box(List<byte> b, string type, byte[] body)
{
    W32(b, 8 + body.Length, false);
    b.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
    b.AddRange(body);
}

static byte[] MakeHeic(byte[] tiff, byte[] payload)
{
    var exifItem = new List<byte>();
    W32(exifItem, 6, false);
    exifItem.AddRange("Exif\0\0"u8.ToArray());
    exifItem.AddRange(tiff);

    var file = new List<byte>();
    Box(file, "ftyp", "heic\0\0\0\0mif1"u8.ToArray());
    var infe = new List<byte> { 2, 0, 0, 0 };
    W16(infe, 1, false);
    W16(infe, 0, false);
    infe.AddRange("Exif\0"u8.ToArray());
    var iinf = new List<byte> { 0, 0, 0, 0 };
    W16(iinf, 1, false);
    Box(iinf, "infe", infe.ToArray());
    var iloc = new List<byte> { 0, 0, 0, 0, 0x44, 0x00 };
    W16(iloc, 1, false);  // item_count
    W16(iloc, 1, false);  // item_ID
    W16(iloc, 0, false);  // data_reference_index
    W16(iloc, 1, false);  // extent_count
    var offsetPos = iloc.Count;
    W32(iloc, 0, false);  // extent_offset（稍後填）
    W32(iloc, exifItem.Count, false);
    var meta = new List<byte> { 0, 0, 0, 0 };
    Box(meta, "iinf", iinf.ToArray());
    var ilocStartInMeta = meta.Count;
    Box(meta, "iloc", iloc.ToArray());
    var metaStart = file.Count;
    Box(file, "meta", meta.ToArray());
    var exifOffset = file.Count + 8;
    var pos = metaStart + 8 + ilocStartInMeta + 8 + offsetPos;
    file[pos] = (byte)(exifOffset >> 24);
    file[pos + 1] = (byte)(exifOffset >> 16);
    file[pos + 2] = (byte)(exifOffset >> 8);
    file[pos + 3] = (byte)exifOffset;
    Box(file, "mdat", exifItem.Concat(payload).ToArray());
    return file.ToArray();
}

static byte[] MakeMov(DateTime mvhdUtc, string appleDate, string model, byte[] payload)
{
    var file = new List<byte>();
    Box(file, "ftyp", "qt  \0\0\0\0qt  "u8.ToArray());
    var mvhd = new List<byte> { 0, 0, 0, 0 };
    var seconds = (long)(mvhdUtc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    W32(mvhd, seconds, false);
    W32(mvhd, seconds, false);
    W32(mvhd, 600, false);
    W32(mvhd, 3000, false);
    mvhd.AddRange(new byte[80]);

    var keyNames = new[] { "com.apple.quicktime.creationdate", "com.apple.quicktime.model" };
    var values = new[] { appleDate, model };
    var keys = new List<byte> { 0, 0, 0, 0 };
    W32(keys, keyNames.Length, false);
    foreach (var k in keyNames)
    {
        var kb = System.Text.Encoding.UTF8.GetBytes(k);
        W32(keys, 8 + kb.Length, false);
        keys.AddRange("mdta"u8.ToArray());
        keys.AddRange(kb);
    }
    var ilst = new List<byte>();
    for (var i = 0; i < values.Length; i++)
    {
        var data = new List<byte>();
        W32(data, 1, false); // UTF-8
        W32(data, 0, false); // locale
        data.AddRange(System.Text.Encoding.UTF8.GetBytes(values[i]));
        var item = new List<byte>();
        Box(item, "data", data.ToArray());
        W32(ilst, 8 + item.Count, false);
        W32(ilst, i + 1, false); // 類型欄位是鍵的編號
        ilst.AddRange(item);
    }
    var meta = new List<byte>(); // QuickTime 的 meta 沒有 version/flags
    Box(meta, "hdlr", new byte[24]);
    Box(meta, "keys", keys.ToArray());
    Box(meta, "ilst", ilst.ToArray());
    var moov = new List<byte>();
    Box(moov, "mvhd", mvhd.ToArray());
    Box(moov, "meta", meta.ToArray());
    Box(file, "moov", moov.ToArray());
    Box(file, "mdat", payload);
    return file.ToArray();
}

static byte[] MakeMp4(DateTime utc, byte[] payload)
{
    var file = new List<byte>();
    Box(file, "ftyp", "isom\0\0\0\0isom"u8.ToArray());
    var mvhd = new List<byte> { 0, 0, 0, 0 };
    var seconds = (long)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    W32(mvhd, seconds, false);
    W32(mvhd, seconds, false);
    W32(mvhd, 1000, false);
    W32(mvhd, 5000, false);
    mvhd.AddRange(new byte[80]);
    var moov = new List<byte>();
    Box(moov, "mvhd", mvhd.ToArray());
    Box(file, "moov", moov.ToArray());
    Box(file, "mdat", payload);
    return file.ToArray();
}

// ---------------------------------------------------------------- 自動更新
{
    using var server = new Photo2Disk.TestSupport.FakeReleaseServer();
    Environment.SetEnvironmentVariable("PHOTO2DISK_UPDATE_API", server.ApiUrl);
    var current = new Version(1, 1, 1);
    var updDir = Path.Combine(Path.GetTempPath(), "p2d_upd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(updDir);
    try
    {
        var newExe = Bytes(3_000_000);
        server.SetRelease("v1.2.0", newExe, "新增自動更新");
        var info = await Updater.CheckAsync(current, CancellationToken.None);
        Check(info is not null && info.Version == new Version(1, 2, 0) && info.Notes == "新增自動更新", $"有新版時傳回資訊：{info}");

        server.SetRelease("v1.1.1", newExe);
        Check(await Updater.CheckAsync(current, CancellationToken.None) is null, "同版本不提示");
        server.SetRelease("1.1", newExe);
        Check(await Updater.CheckAsync(current, CancellationToken.None) is null, "舊版本（1.1）不提示");
        server.SetRelease("v1.3.0", newExe, withSha: false);
        Check(await Updater.CheckAsync(current, CancellationToken.None) is null, "沒附雜湊檔不提示");
        server.SetRelease("v1.3.0", newExe, prerelease: true);
        Check(await Updater.CheckAsync(current, CancellationToken.None) is null, "預先發行版不提示");
        Check(Updater.ParseSha("ABCDEF") is null && Updater.ParseSha(new string('a', 64) + "  x.exe") == new string('a', 64), "雜湊檔格式判斷");

        // 下載替換：成功
        var target = Path.Combine(updDir, "照片備份過濾工具.exe");
        var oldExe = Bytes(500_000);
        File.WriteAllBytes(target, oldExe);
        server.SetRelease("v1.2.0", newExe);
        info = await Updater.CheckAsync(current, CancellationToken.None);
        double lastProgress = 0;
        await Updater.DownloadAndReplaceAsync(info!, target, new SyncDoubleProgress(f => lastProgress = f), CancellationToken.None);
        Check(File.ReadAllBytes(target).AsSpan().SequenceEqual(newExe) && File.ReadAllBytes(target + ".old").AsSpan().SequenceEqual(oldExe)
              && !File.Exists(target + ".download") && lastProgress >= 0.999,
            "下載替換：新版放到原檔名、舊版改成 .old、暫存檔刪除、進度到 100%");

        // 雜湊不符：維持原狀
        File.WriteAllBytes(target, oldExe);
        File.Delete(target + ".old");
        server.SetRelease("v1.2.0", newExe, shaOverride: new string('0', 64));
        info = await Updater.CheckAsync(current, CancellationToken.None);
        var hashError = false;
        try
        {
            await Updater.DownloadAndReplaceAsync(info!, target, null, CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            hashError = true;
        }
        Check(hashError && File.ReadAllBytes(target).AsSpan().SequenceEqual(oldExe) && !File.Exists(target + ".old") && !File.Exists(target + ".download"),
            "雜湊不符：報錯、原檔不動、不留暫存檔");

        // 取消：維持原狀
        server.SetRelease("v1.2.0", newExe);
        info = await Updater.CheckAsync(current, CancellationToken.None);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var wasCancelled = false;
            try
            {
                await Updater.DownloadAndReplaceAsync(info!, target, null, cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
            Check(wasCancelled && File.ReadAllBytes(target).AsSpan().SequenceEqual(oldExe) && !File.Exists(target + ".download"),
                "取消下載：原檔不動、不留暫存檔");
        }

        // 沒網路（伺服器關閉）：不提示也不報錯
        server.Dispose();
        Check(await Updater.CheckAsync(current, CancellationToken.None) is null, "連不上伺服器時安靜略過");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PHOTO2DISK_UPDATE_API", null);
        Directory.Delete(updDir, true);
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部測試通過" : $"有 {failures} 項失敗");
return failures == 0 ? 0 : 1;

sealed class SyncProgress(Action<ProgressInfo> handler) : IProgress<ProgressInfo>
{
    public void Report(ProgressInfo value) => handler(value);
}

sealed class SyncDoubleProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}

sealed class FakeConverter(Func<string, string, RawSourceInfo, RawConvertMethod> convert) : IRawConverter
{
    public RawConvertMethod Convert(string rawPath, string outPath, RawSourceInfo source) => convert(rawPath, outPath, source);
}

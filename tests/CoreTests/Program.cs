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
    var copy = DedupEngine.CopyNew(r1, null, CancellationToken.None);
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
    var copy2 = DedupEngine.CopyNew(r6, null, CancellationToken.None);
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
            DedupEngine.CopyNew(r4, p, cts.Token);
            Check(false, "應該被取消");
        }
        catch (OperationCanceledException)
        {
            var leftovers = Directory.EnumerateFiles(outDir, "影片*").ToList();
            Check(leftovers.Count == 0, $"取消後沒有殘留（找到 {leftovers.Count} 個）");
        }
    }

    Check(!Directory.EnumerateFiles(outDir, "*.p2d-partial").Any(), "取消後沒有殘留暫存檔");

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

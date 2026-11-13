using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Photo2Disk;

// 大量照片效能測試。
// 用法：Benchmark.exe <測試根目錄> [檔案數=500000] [下載張數=2000] [下載資料夾]
// 備份資料夾用 NTFS 稀疏檔模擬：大小是真實照片大小（對數常態，中位數約 3 MB），
// 只有開頭 4 KB 寫入隨機內容，所以 50 萬個檔案只佔約 2 GB。
// 量得到：目錄掃描、大小碰撞、快取讀寫、整體流程的額外負擔。
// 量不到：真實的讀取速度（稀疏部分讀起來不用存取磁碟），需另外以硬碟讀速估算。

Console.OutputEncoding = System.Text.Encoding.UTF8;
var root = args.Length > 0 ? args[0] : throw new ArgumentException("請指定測試根目錄");
var fileCount = args.Length > 1 ? int.Parse(args[1]) : 500_000;
var downloadCount = args.Length > 2 ? int.Parse(args[2]) : 2000;
var backup = Path.Combine(root, "備份");
var download = args.Length > 3 ? args[3] : Path.Combine(root, "下載");
var rng = new Random(42);

long PhotoSize()
{
    // 對數常態：中位數 3 MB，多數落在 1～8 MB；約 3% 是影片（20～300 MB）
    if (rng.NextDouble() < 0.03)
    {
        return (long)(20_000_000 + rng.NextDouble() * 280_000_000);
    }
    var z = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
    return Math.Max(50_000, (long)(3_000_000 * Math.Exp(0.5 * z)));
}

var sw = Stopwatch.StartNew();
var marker = Path.Combine(backup, ".benchmark_done");
if (!File.Exists(marker))
{
    Console.WriteLine($"產生 {fileCount:N0} 個稀疏檔案…");
    var header = new byte[4096];
    for (var i = 0; i < fileCount; i++)
    {
        var year = 2005 + i % 22;
        var month = 1 + i / 22 % 12;
        var dir = Path.Combine(backup, year.ToString(), $"{year}-{month:00}");
        if (i < 22 * 12)
        {
            Directory.CreateDirectory(dir);
        }
        rng.NextBytes(header);
        Sparse.Create(Path.Combine(dir, $"IMG_{i:D7}.jpg"), header, PhotoSize());
        if (i % 50_000 == 0 && i > 0)
        {
            Console.WriteLine($"  {i:N0}（{sw.Elapsed.TotalSeconds:0} 秒）");
        }
    }
    File.WriteAllText(marker, "");
    Console.WriteLine($"產生完成：{sw.Elapsed.TotalSeconds:0} 秒");
}

// 下載 zip：一半是硬碟已有的照片（隨機挑），一半是新照片
Directory.CreateDirectory(download);
var zipPath = Path.Combine(download, $"Photos-{downloadCount}.zip");
if (!File.Exists(zipPath))
{
    Console.WriteLine("建立下載 zip…");
    var all = Directory.GetFiles(backup, "*.jpg", SearchOption.AllDirectories);
    using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
    var buf = new byte[1 << 20];
    for (var i = 0; i < downloadCount; i++)
    {
        var entry = zip.CreateEntry($"DL_{i:D5}.jpg", CompressionLevel.Fastest);
        using var dst = entry.Open();
        if (i % 2 == 0)
        {
            using var src = File.OpenRead(all[rng.Next(all.Length)]);
            src.CopyTo(dst);
        }
        else
        {
            var head = new byte[4096];
            rng.NextBytes(head);
            dst.Write(head);
            var remain = PhotoSize() - head.Length;
            Array.Clear(buf);
            while (remain > 0)
            {
                var n = (int)Math.Min(buf.Length, remain);
                dst.Write(buf, 0, n);
                remain -= n;
            }
        }
    }
}

// 第一次比對前把舊快取移開，確保「沒有快取」是真的沒有
var oldIndex = Path.Combine(backup, HashIndex.FileName);
if (File.Exists(oldIndex))
{
    File.SetAttributes(oldIndex, FileAttributes.Normal);
    File.Move(oldIndex, oldIndex + ".舊", overwrite: true);
}
Console.WriteLine($"下載 zip：{zipPath}（{new FileInfo(zipPath).Length / 1048576.0:0} MB）");

for (var run = 1; run <= 2; run++)
{
    var stages = new List<(string Stage, double At)>();
    var watch = Stopwatch.StartNew();
    var progress = new SyncProgress(p =>
    {
        if (stages.Count == 0 || stages[^1].Stage != p.Stage)
        {
            stages.Add((p.Stage, watch.Elapsed.TotalSeconds));
        }
    });
    var before = GC.GetTotalAllocatedBytes();
    var r = DedupEngine.Analyze(backup, new[] { zipPath }, progress, CancellationToken.None);
    var total = watch.Elapsed.TotalSeconds;
    Console.WriteLine();
    Console.WriteLine($"=== 第 {run} 次比對（{(run == 1 ? "沒有快取" : "有快取")}）：總共 {total:0.0} 秒 ===");
    for (var i = 0; i < stages.Count; i++)
    {
        var end = i + 1 < stages.Count ? stages[i + 1].At : total;
        Console.WriteLine($"  {stages[i].Stage,-12} {end - stages[i].At,6:0.0} 秒");
    }
    Console.WriteLine($"  備份檔案 {r.BackupFileCount:N0} 個；新照片 {r.Count(ItemStatus.New):N0}，硬碟已有 {r.Count(ItemStatus.AlreadyBackedUp):N0}");
    Console.WriteLine($"  記憶體配置 {(GC.GetTotalAllocatedBytes() - before) / 1048576.0:0} MB，工作集 {Environment.WorkingSet / 1048576.0:0} MB");
    var idx = Path.Combine(backup, HashIndex.FileName);
    Console.WriteLine($"  快取檔 {new FileInfo(idx).Length / 1024.0:0} KB");
}

// 大小碰撞：與下載檔案同大小、但內容不同的備份檔案數（這些會被多讀一次）
var sizes = Directory.EnumerateFiles(backup, "*.jpg", SearchOption.AllDirectories)
    .GroupBy(f => new FileInfo(f).Length).ToDictionary(g => g.Key, g => g.Count());
using (var z = ZipFile.OpenRead(zipPath))
{
    var collide = z.Entries.Sum(e => sizes.GetValueOrDefault(e.Length));
    Console.WriteLine();
    Console.WriteLine($"下載 {z.Entries.Count:N0} 張中，與備份檔案同大小的配對共 {collide:N0} 組（其中約 {downloadCount / 2:N0} 組是真的相同）");
}

// 記憶體模擬：不寫檔案，估算大量照片時「大小巧合」造成的額外讀取
Console.WriteLine();
Console.WriteLine("大小巧合模擬（下載 2,000 張全新照片，備份端與其同大小、內容不同的檔案數）：");
foreach (var n in new[] { 250_000, 500_000, 1_000_000 })
{
    var counts = new Dictionary<long, int>();
    for (var i = 0; i < n; i++)
    {
        var s = PhotoSize();
        counts[s] = counts.GetValueOrDefault(s) + 1;
    }
    long extraFiles = 0, extraBytes = 0, itemsHit = 0;
    for (var i = 0; i < 2000; i++)
    {
        var s = PhotoSize();
        var c = counts.GetValueOrDefault(s);
        if (c > 0)
        {
            itemsHit++;
            extraFiles += c;
            extraBytes += c * s;
        }
    }
    Console.WriteLine($"  備份 {n,9:N0} 個：{itemsHit,4} 張下載照片撞到大小，備份端多讀 {extraFiles,4} 個檔案（{extraBytes / 1048576.0,7:0} MB），下載端多讀 {itemsHit,4} 張");
}
return 0;

sealed class SyncProgress(Action<ProgressInfo> handler) : IProgress<ProgressInfo>
{
    public void Report(ProgressInfo value) => handler(value);
}

static class Sparse
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    public static void Create(string path, byte[] header, long size)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096);
        if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("無法設定稀疏檔（需要 NTFS）", Marshal.GetLastWin32Error());
        }
        fs.Write(header);
        fs.SetLength(size);
    }
}

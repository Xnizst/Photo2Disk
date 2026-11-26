using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Photo2Disk;

// 用主程式的 DngConverter 轉真實 DNG。每張印出轉檔方式、耗時、拍攝資訊與 XMP 是否寫入。
Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2)
{
    Console.WriteLine("用法：RawConvertCheck.exe <輸出資料夾> <DNG> ...");
    return 1;
}
if (args[0] == "--copy" && args.Length >= 3)
{
    // 端到端：比對 → 複製（依拍攝年份）→ DNG 另存 JPG，使用真實的轉檔器
    var analysis = DedupEngine.Analyze(args[1], args.Skip(2).ToArray(), null, CancellationToken.None);
    var summary = DedupEngine.CopyNew(analysis, null, CancellationToken.None, new NamingOptions { ConvertDng = true }, new DngConverter());
    foreach (var it in analysis.Items)
    {
        Console.WriteLine($"{it.Location}｜{it.Status}｜{(it.CopiedTo is null ? it.MatchedWith : Path.GetRelativePath(args[1], it.CopiedTo))}｜{it.NameNote}");
    }
    Console.WriteLine($"複製 {summary.Copied}，轉出 {summary.Converted}，略過 {summary.ConvertSkipped}，失敗 {summary.ConvertFailed}");
    var again = DedupEngine.Analyze(args[1], args.Skip(2).ToArray(), null, CancellationToken.None);
    var pass = summary.ConvertFailed == 0 && summary.Converted + summary.ConvertSkipped == analysis.Items.Count(i => DedupEngine.IsDng(i.FileName) && i.CopiedTo is not null)
               && again.Count(ItemStatus.New) == 0;
    Console.WriteLine(pass ? "端到端檢查：通過" : "端到端檢查：失敗");
    return pass ? 0 : 2;
}
Directory.CreateDirectory(args[0]);
var converter = new DngConverter();
var ok = true;
foreach (var dng in args.Skip(1))
{
    var name = Path.GetFileName(dng);
    var bytes = File.ReadAllBytes(dng);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var outPath = Path.Combine(args[0], Path.GetFileNameWithoutExtension(dng) + ".jpg");
    var sw = Stopwatch.StartNew();
    try
    {
        var method = converter.Convert(dng, outPath, new RawSourceInfo(name, hash, bytes.Length));
        var info = MediaMetadata.Read(outPath, Path.GetFileName(outPath), DateTime.MinValue);
        var orig = MediaMetadata.Read(dng, name, DateTime.MinValue);
        var xmpOk = Encoding.UTF8.GetString(File.ReadAllBytes(outPath)).Contains($"p2d:SourceSha256=\"{hash}\"");
        var same = info.Taken == orig.Taken && info.CameraModel == orig.CameraModel;
        ok &= same && xmpOk;
        Console.WriteLine($"{name}：{method}，{sw.ElapsedMilliseconds} ms，{new FileInfo(outPath).Length:N0} 位元組，" +
                          $"拍攝 {info.Taken:yyyy-MM-dd HH:mm:ss}／{info.CameraModel}（{(same ? "與原檔相同" : "不同！")}），XMP {(xmpOk ? "有" : "沒有！")}");
    }
    catch (Exception ex)
    {
        ok = false;
        Console.WriteLine($"{name}：失敗 {ex.GetType().Name} {ex.Message}");
    }
}
Console.WriteLine($"記憶體高峰：{Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
Console.WriteLine(ok ? "轉檔檢查：全部通過" : "轉檔檢查：有項目失敗");
return ok ? 0 : 2;

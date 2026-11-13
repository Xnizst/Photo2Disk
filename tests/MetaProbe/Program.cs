using Photo2Disk;

// 拍攝資訊檢查工具：印出程式從每個檔案讀到的拍攝時間、來源與型號。
// 用法：MetaProbe.exe <檔案或資料夾> ...
Console.OutputEncoding = System.Text.Encoding.UTF8;
var files = args.SelectMany<string, string>(a => Directory.Exists(a)
        ? Directory.EnumerateFiles(a).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        : new[] { a })
    .ToList();
if (files.Count == 0)
{
    Console.WriteLine("用法：MetaProbe.exe <檔案或資料夾> ...");
    return 1;
}
foreach (var f in files)
{
    var info = MediaMetadata.Read(f, Path.GetFileName(f), DateTime.MinValue);
    Console.WriteLine($"{Path.GetFileName(f)}\t{info.Taken:yyyy-MM-dd HH:mm:ss}\t{info.Source}\t{info.CameraModel}");
}
return 0;

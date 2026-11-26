using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Photo2Disk.ConvertProbe;

// 轉檔原型。用法：ConvertProbe.exe <輸出資料夾> <檔案> ...
// 每個檔案轉出 JPG（品質 92）與 PNG，寫入 EXIF、ICC 色彩描述檔、XMP（原檔 SHA-256），再讀回驗證。
internal static class Program
{
    private const string XmpNs = "https://github.com/Xnizst/Photo2Disk/ns/1.0/";
    private const int JpegQuality = 92;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length >= 3 && args[0] == "--variants")
        {
            return Variants.Run(args[1], args.Skip(2));
        }
        if (args.Length < 2)
        {
            Console.WriteLine("用法：ConvertProbe.exe <輸出資料夾> <檔案> ...");
            return 1;
        }
        Directory.CreateDirectory(args[0]);
        var ok = true;
        foreach (var file in args.Skip(1))
        {
            try
            {
                ok &= Convert(file, args[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[失敗] {Path.GetFileName(file)}：{ex.GetType().Name}：{ex.Message}");
                ok = false;
            }
        }
        Console.WriteLine($"記憶體高峰：{Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
        Console.WriteLine(ok ? "轉檔原型：全部成功" : "轉檔原型：有項目失敗");
        return ok ? 0 : 2;
    }

    private static bool Convert(string file, string outDir)
    {
        var name = Path.GetFileName(file);
        Console.WriteLine($"=== {name}");
        var sw = Stopwatch.StartNew();

        var srcBytes = File.ReadAllBytes(file);
        var srcHash = System.Convert.ToHexString(SHA256.HashData(srcBytes)).ToLowerInvariant();
        Console.WriteLine($"原檔：{srcBytes.Length:N0} 位元組，SHA-256 {srcHash}");

        // 程式現有的拍攝資訊讀取（比對、分資料夾用），順便確認 DNG 讀得到
        var orig = MediaMetadata.Read(file, name, DateTime.MinValue);
        Console.WriteLine($"原檔拍攝資訊：{orig.Taken:yyyy-MM-dd HH:mm:ss}（{orig.Source}），機型 {orig.CameraModel}");

        // 解碼：DNG 用 LibRaw（不含 HEVC）；HEIC 只用 WIC（系統解碼器，HEVC 授權由使用者的延伸模組處理）
        var isRaw = srcBytes.Length > 4 && (srcBytes[0] == 'I' && srcBytes[1] == 'I' || srcBytes[0] == 'M' && srcBytes[1] == 'M');
        BitmapSource pixels;
        byte[]? icc;
        byte[]? exif;
        byte[]? jpgReuse = null; // 直接沿用的 JPEG（DNG 的全尺寸內嵌預覽），不重新壓縮
        var jpgExif = (byte[]?)null;
        try
        {
            if (isRaw && FullPreview(file, srcBytes) is { } pv)
            {
                // 相機自己的沖洗結果：JPG 直接沿用並保留原方向標記；PNG 用轉正後的畫面
                var orientation = DngExif.ReadOrientation(srcBytes);
                jpgReuse = pv;
                jpgExif = DngExif.Build(srcBytes, orientation);
                pixels = Rotate(DecodeWic(pv).Item1, orientation);
                icc = null;
                exif = DngExif.Build(srcBytes);
                Console.WriteLine($"使用 DNG 內嵌的全尺寸預覽（方向 {orientation}）");
            }
            else
            {
                (pixels, icc) = isRaw ? DecodeLibRaw(file, DngExif.ReadBaselineExposure(srcBytes)) : DecodeWic(srcBytes);
                exif = isRaw ? DngExif.Build(srcBytes) : MediaMetadata.ReadExifBlock(file);
                if (isRaw && DngExif.ExtractPreview(srcBytes) is { } small)
                {
                    pixels = ColorMatch.ToPreview(pixels, DecodeWic(small.Jpeg).Item1);
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or FileFormatException)
        {
            Console.WriteLine($"[無法轉檔] 解碼失敗：{ex.Message.Trim()}（0x{ex.HResult:X8}）。" +
                              "HEIC 需要使用者電腦有可用的 HEVC 視訊延伸模組。");
            return false;
        }
        Console.WriteLine($"解碼完成：{pixels.PixelWidth}×{pixels.PixelHeight}，耗時 {sw.ElapsedMilliseconds} ms");

        Console.WriteLine($"ICC：{(icc is null ? "無" : $"{icc.Length} 位元組，{IccDescription(icc)}")}；" +
                          $"EXIF：{(exif is null ? "無" : $"{exif.Length} 位元組，方向 {ExifOrientation(exif)}")}");

        var xmp = BuildXmp(srcHash, srcBytes.Length, name);
        var stem = Path.GetFileNameWithoutExtension(file);

        // JPG
        sw.Restart();
        byte[] jpg;
        if (jpgReuse is not null)
        {
            jpg = RebuildJpeg(jpgReuse, jpgExif, xmp, null);
        }
        else
        {
            var jpgEnc = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            jpgEnc.Frames.Add(BitmapFrame.Create(pixels));
            jpg = RebuildJpeg(Encode(jpgEnc), exif, xmp, icc);
        }
        var jpgPath = Path.Combine(outDir, stem + ".jpg");
        File.WriteAllBytes(jpgPath, jpg);
        Console.WriteLine($"JPG：{jpg.Length:N0} 位元組（原檔的 {100.0 * jpg.Length / srcBytes.Length:F0}%），耗時 {sw.ElapsedMilliseconds} ms");

        // PNG
        sw.Restart();
        var pngEnc = new PngBitmapEncoder();
        pngEnc.Frames.Add(BitmapFrame.Create(pixels));
        var png = RebuildPng(Encode(pngEnc), exif, xmp, icc);
        var pngPath = Path.Combine(outDir, stem + ".png");
        File.WriteAllBytes(pngPath, png);
        Console.WriteLine($"PNG：{png.Length:N0} 位元組（原檔的 {100.0 * png.Length / srcBytes.Length:F0}%），耗時 {sw.ElapsedMilliseconds} ms");

        // 沿用預覽且方向不是 1 時，JPG 的畫素是未轉正的，看圖軟體依方向標記旋轉
        var rotated = jpgReuse is not null && DngExif.ReadOrientation(srcBytes) is 6 or 8;
        var (jw, jh) = rotated ? (pixels.PixelHeight, pixels.PixelWidth) : (pixels.PixelWidth, pixels.PixelHeight);
        return Verify(jpgPath, srcHash, jw, jh, exif is not null, icc is not null)
             & Verify(pngPath, srcHash, pixels.PixelWidth, pixels.PixelHeight, exif is not null, icc is not null);
    }

    /// <summary>DNG 有全尺寸內嵌預覽（寬高都達成像尺寸 9 成以上）時回傳其 JPEG 資料。</summary>
    private static byte[]? FullPreview(string path, byte[] src)
    {
        if (DngExif.ExtractPreview(src) is not { } pv)
        {
            return null;
        }
        using var raw = Sdcb.LibRaw.RawContext.OpenFile(path);
        int rw = raw.Width, rh = raw.Height; // 未轉正的成像尺寸，和預覽的儲存方向相同
        var full = pv.Width >= rw * 0.9 && pv.Height >= rh * 0.9;
        Console.WriteLine($"內嵌預覽 {pv.Width}×{pv.Height}，成像尺寸 {rw}×{rh} → {(full ? "全尺寸，直接使用" : "不足，改用 LibRaw")}");
        return full ? pv.Jpeg : null;
    }

    private static BitmapSource Rotate(BitmapSource src, int orientation)
    {
        var angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle == 0)
        {
            return src;
        }
        var t = new TransformedBitmap(src, new RotateTransform(angle));
        t.Freeze();
        return t;
    }

    private static (BitmapSource, byte[]?) DecodeWic(byte[] src)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(src),
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        Console.WriteLine($"WIC 解碼器：{decoder.CodecInfo?.FriendlyName}，{frame.Format}");
        BitmapSource pixels = frame.Format == PixelFormats.Bgr24 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        pixels.Freeze();
        return (pixels, ReadIcc(frame));
    }

    // LibRaw：相機白平衡、輸出 sRGB 8 位元、sRGB 伽瑪曲線；MakeDcrawMemoryImage 會依原檔方向轉正。
    // 自動亮度之後再乘上 2^BaselineExposure（2026-11-25 實驗：Pentax ΔE 9.0 → 5.1，其他樣本不受影響）
    private static (BitmapSource, byte[]?) DecodeLibRaw(string path, double baselineExposure)
    {
        using var raw = Sdcb.LibRaw.RawContext.OpenFile(path);
        raw.Unpack();
        raw.DcrawProcess(c =>
        {
            c.UseCameraWb = true;
            c.OutputBps = 8;
            c.Gamma[0] = 1 / 2.4;
            c.Gamma[1] = 12.92;
            c.Brightness = (float)Math.Pow(2, baselineExposure);
        });
        using var img = raw.MakeDcrawMemoryImage();
        Console.WriteLine($"LibRaw 解碼：{img.Width}×{img.Height}，{img.Channels} 色版，{img.Bits} 位元");
        if (img.Channels != 3 || img.Bits != 8)
        {
            throw new NotSupportedException($"未處理的輸出格式：{img.Channels} 色版 {img.Bits} 位元");
        }
        var rgb = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Rgb24, null,
            img.AsSpan<byte>().ToArray(), img.Width * 3);
        var bgr = new FormatConvertedBitmap(rgb, PixelFormats.Bgr24, null, 0);
        bgr.Freeze();
        return (bgr, null); // 輸出已是 sRGB，不另外嵌入色彩描述檔
    }

    // ------------------------------------------------------------ 驗證

    private static bool Verify(string path, string srcHash, int expectW, int expectH, bool hadExif, bool hadIcc)
    {
        var bytes = File.ReadAllBytes(path);
        var ext = Path.GetExtension(path).ToUpperInvariant().TrimStart('.');
        var problems = new List<string>();

        var xmp = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ReadPngXmp(bytes) : ReadJpegXmp(bytes);
        var m = xmp is null ? null : Regex.Match(xmp, "SourceSha256=\"([0-9a-f]{64})\"");
        if (m is null || !m.Success || m.Groups[1].Value != srcHash)
        {
            problems.Add("XMP 原檔雜湊讀不回來或不一致");
        }

        var info = MediaMetadata.Read(path, Path.GetFileName(path), DateTime.MinValue);
        if (hadExif && info.Source.ToString() != "Exif")
        {
            problems.Add($"EXIF 拍攝時間讀不到（來源 {info.Source}）");
        }

        var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var f = dec.Frames[0];
        if (f.PixelWidth != expectW || f.PixelHeight != expectH)
        {
            problems.Add($"尺寸不同 {f.PixelWidth}×{f.PixelHeight}");
        }
        if (hadIcc && (f.ColorContexts?.Count ?? 0) == 0)
        {
            problems.Add("Windows 讀不到色彩描述檔");
        }

        Console.WriteLine($"  驗證 {ext}：拍攝 {info.Taken:yyyy-MM-dd HH:mm:ss}，機型 {info.CameraModel}，" +
                          $"XMP 雜湊 {(problems.Any(p => p.StartsWith("XMP")) ? "失敗" : "一致")}，" +
                          $"色彩描述檔 {f.ColorContexts?.Count ?? 0} 個 → {(problems.Count == 0 ? "通過" : string.Join("；", problems))}");
        return problems.Count == 0;
    }

    // ------------------------------------------------------------ 中繼資料

    private static byte[] BuildXmp(string sha256, long size, string name)
    {
        var xml =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            $"<rdf:Description rdf:about=\"\" xmlns:p2d=\"{XmpNs}\"" +
            $" p2d:SourceSha256=\"{sha256}\" p2d:SourceSize=\"{size}\"" +
            $" p2d:SourceName=\"{SecurityElement.Escape(name)}\"" +
            $" p2d:SourceFormat=\"{Path.GetExtension(name).TrimStart('.').ToUpperInvariant()}\"/>" +
            "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xml);
    }

    private static byte[]? ReadIcc(BitmapFrame frame)
    {
        if (frame.ColorContexts is not { Count: > 0 } ctx)
        {
            return null;
        }
        using var s = ctx[0].OpenProfileStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ICC 的 desc 標籤（v2 'desc' 或 v4 'mluc'），只用來顯示
    private static string IccDescription(byte[] icc)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var p = 132 + i * 12;
            if (Encoding.ASCII.GetString(icc, p, 4) != "desc")
            {
                continue;
            }
            var off = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(p + 4));
            var type = Encoding.ASCII.GetString(icc, off, 4);
            if (type == "desc")
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(off + 8));
                return Encoding.ASCII.GetString(icc, off + 12, Math.Max(0, len - 1));
            }
            if (type == "mluc")
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(off + 20));
                var start = BinaryPrimitives.ReadInt32BigEndian(icc.AsSpan(off + 24));
                return Encoding.BigEndianUnicode.GetString(icc, off + start, len);
            }
        }
        return "（無名稱）";
    }

    private static int ExifOrientation(byte[] t)
    {
        var le = t[0] == 'I';
        uint U32(int p) => le ? BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(p)) : BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(p));
        ushort U16(int p) => le ? BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(p)) : BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p));
        var ifd = (int)U32(4);
        int n = U16(ifd);
        for (var i = 0; i < n; i++)
        {
            var e = ifd + 2 + i * 12;
            if (U16(e) == 0x0112)
            {
                return U16(e + 8);
            }
        }
        return 0;
    }

    // ------------------------------------------------------------ JPEG

    private static byte[] Encode(BitmapEncoder enc)
    {
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // 拿掉編碼器寫的 APPn，依序放入 EXIF（APP1）、XMP（APP1）、ICC（APP2）
    private static byte[] RebuildJpeg(byte[] enc, byte[]? exif, byte[] xmp, byte[]? icc)
    {
        var o = new MemoryStream();
        o.Write(enc, 0, 2); // SOI
        if (exif is not null)
        {
            WriteSegment(o, 0xE1, Concat("Exif\0\0"u8.ToArray(), exif));
        }
        WriteSegment(o, 0xE1, Concat(Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), xmp));
        if (icc is not null)
        {
            WriteSegment(o, 0xE2, Concat("ICC_PROFILE\0"u8.ToArray(), new byte[] { 1, 1 }, icc));
        }
        var p = 2;
        while (enc[p] == 0xFF && enc[p + 1] is >= 0xE0 and <= 0xEF)
        {
            p += 2 + BinaryPrimitives.ReadUInt16BigEndian(enc.AsSpan(p + 2));
        }
        o.Write(enc, p, enc.Length - p);
        return o.ToArray();
    }

    private static void WriteSegment(Stream o, byte marker, byte[] data)
    {
        if (data.Length + 2 > 0xFFFF)
        {
            throw new InvalidDataException($"區段太大（{data.Length} 位元組），需要分段");
        }
        o.WriteByte(0xFF);
        o.WriteByte(marker);
        o.WriteByte((byte)((data.Length + 2) >> 8));
        o.WriteByte((byte)(data.Length + 2));
        o.Write(data);
    }

    private static string? ReadJpegXmp(byte[] b)
    {
        var p = 2;
        var header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        while (p + 4 < b.Length && b[p] == 0xFF && b[p + 1] != 0xDA)
        {
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 2));
            if (b[p + 1] == 0xE1 && b.AsSpan(p + 4).StartsWith(header))
            {
                return Encoding.UTF8.GetString(b, p + 4 + header.Length, len - 2 - header.Length);
            }
            p += 2 + len;
        }
        return null;
    }

    // ------------------------------------------------------------ PNG

    // IHDR 後面插入 iCCP、eXIf、iTXt（XMP）；拿掉編碼器寫的 sRGB／gAMA／cHRM，避免和 ICC 衝突
    private static byte[] RebuildPng(byte[] enc, byte[]? exif, byte[] xmp, byte[]? icc)
    {
        var o = new MemoryStream();
        o.Write(enc, 0, 8);
        var p = 8;
        while (p < enc.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(enc.AsSpan(p));
            var type = Encoding.ASCII.GetString(enc, p + 4, 4);
            var total = 12 + len;
            var drop = icc is not null && type is "sRGB" or "gAMA" or "cHRM" or "iCCP";
            if (!drop)
            {
                o.Write(enc, p, total);
            }
            if (type == "IHDR")
            {
                if (icc is not null)
                {
                    using var z = new MemoryStream();
                    using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true))
                    {
                        zs.Write(icc);
                    }
                    WriteChunk(o, "iCCP", Concat("ICC Profile\0\0"u8.ToArray(), z.ToArray()));
                }
                if (exif is not null)
                {
                    WriteChunk(o, "eXIf", exif);
                }
                WriteChunk(o, "iTXt", Concat("XML:com.adobe.xmp\0\0\0\0\0"u8.ToArray(), xmp));
            }
            p += total;
        }
        return o.ToArray();
    }

    private static void WriteChunk(Stream o, string type, byte[] data)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        o.Write(head);
        o.Write(data);
        var crc = Crc32(Concat(head.AsSpan(4).ToArray(), data));
        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        o.Write(c);
    }

    private static string? ReadPngXmp(byte[] b)
    {
        var p = 8;
        var key = "XML:com.adobe.xmp\0"u8.ToArray();
        while (p + 12 <= b.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            var type = Encoding.ASCII.GetString(b, p + 4, 4);
            if (type == "iTXt" && b.AsSpan(p + 8).StartsWith(key))
            {
                var start = p + 8 + key.Length + 2; // 壓縮旗標、壓縮方法
                start = Array.IndexOf(b, (byte)0, start) + 1; // 語言標籤
                start = Array.IndexOf(b, (byte)0, start) + 1; // 翻譯關鍵字
                return Encoding.UTF8.GetString(b, start, p + 8 + len - start);
            }
            if (type == "IDAT")
            {
                return null;
            }
            p += 12 + len;
        }
        return null;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(x => x.Length)];
        var p = 0;
        foreach (var x in parts)
        {
            Buffer.BlockCopy(x, 0, r, p, x.Length);
            p += x.Length;
        }
        return r;
    }
}

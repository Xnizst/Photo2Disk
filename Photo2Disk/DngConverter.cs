using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security;
using System.Text;
using Sdcb.LibRaw;

namespace Photo2Disk;

/// <summary>
/// DNG 轉 JPG，目標是「看起來和拍攝裝置自己沖洗的結果一樣」（YAN 2026-11-25）。依 DNG 的內嵌預覽分三種：
/// 1. 有全尺寸預覽：直接沿用預覽（相機的成品），JPG 不重新壓縮，保留原方向標記。
/// 2. 只有小預覽（短邊 ≥ 240、長寬比差 ≤ 3%）：LibRaw 解出全尺寸畫面，再用小預覽的色彩分布校正（各色版直方圖對應）。
/// 3. 沒有可用的預覽：LibRaw 解碼，自動亮度後乘 2^BaselineExposure。
/// 新檔寫入 EXIF（拍攝時間、機型、GPS）與 XMP（原檔 SHA-256、大小、檔名），之後比對可辨識是哪個原檔轉出的。
/// 實驗與驗證見 tests\ConvertProbe、tools\compare_render.py、tools\color_match_experiment.py。
/// LibRaw 以 CDDL-1.0 授權使用（LibRaw 為 LGPL-2.1／CDDL-1.0 雙授權），不含 HEVC。
/// </summary>
public sealed class DngConverter : IRawConverter
{
    public const int JpegQuality = 92;
    private const double FullPreviewRatio = 0.9;
    private const int MinReferenceShortSide = 240;
    private const double MaxAspectDiff = 0.03;
    private const string XmpNs = "https://github.com/Xnizst/Photo2Disk/ns/1.0/";

    public RawConvertMethod Convert(string rawPath, string outPath, RawSourceInfo source)
    {
        var src = File.ReadAllBytes(rawPath);
        var xmp = BuildXmp(source);
        var preview = DngExif.ExtractPreview(src);

        using var raw = RawContext.OpenFile(rawPath);
        if (preview is { } pv && pv.Width >= raw.Width * FullPreviewRatio && pv.Height >= raw.Height * FullPreviewRatio)
        {
            // 預覽以原始方向儲存，保留原方向標記讓看圖軟體旋轉
            var exifKeep = DngExif.Build(src, DngExif.ReadOrientation(src));
            File.WriteAllBytes(outPath, RebuildJpeg(pv.Jpeg, exifKeep, xmp));
            return RawConvertMethod.CameraPreview;
        }

        raw.Unpack();
        var baseline = DngExif.ReadBaselineExposure(src);
        raw.DcrawProcess(c =>
        {
            c.UseCameraWb = true;
            c.OutputBps = 8;
            c.Gamma[0] = 1 / 2.4; // sRGB 伽瑪
            c.Gamma[1] = 12.92;
            c.Brightness = (float)Math.Pow(2, baseline);
        });
        using var img = raw.MakeDcrawMemoryImage(); // 已依原檔方向轉正
        if (img.Channels != 3 || img.Bits != 8)
        {
            throw new NotSupportedException($"未處理的 LibRaw 輸出格式：{img.Channels} 色版 {img.Bits} 位元");
        }
        img.SwapRGB(); // 轉成 GDI+ 的 BGR 順序
        var width = img.Width;
        var height = img.Height;
        var pixels = img.AsSpan<byte>().ToArray();

        var method = RawConvertMethod.RawOnly;
        if (preview is { } small && TryColorMatch(pixels, width, height, small.Jpeg))
        {
            method = RawConvertMethod.MatchedToPreview;
        }

        using var bmp = ToBitmap(pixels, width, height);
        using var ms = new MemoryStream();
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using (var ep = new EncoderParameters(1))
        {
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)JpegQuality);
            bmp.Save(ms, jpegCodec, ep);
        }
        File.WriteAllBytes(outPath, RebuildJpeg(ms.ToArray(), DngExif.Build(src), xmp));
        return method;
    }

    // ------------------------------------------------------------ 依內嵌預覽校正顏色

    /// <summary>
    /// 把全尺寸結果縮到預覽大小，比較兩者每個色版的累積分布，建出 256 階對照表後套到全尺寸畫面。
    /// 只比較分布、不需要畫面對齊；預覽太小或長寬比不符（有裁切）時不校正，回傳 false。
    /// </summary>
    private static bool TryColorMatch(byte[] bgr, int width, int height, byte[] previewJpeg)
    {
        using var previewImg = DecodeJpeg(previewJpeg);
        if (previewImg is null)
        {
            return false;
        }
        double Aspect(int w, int h) => (double)Math.Max(w, h) / Math.Min(w, h);
        if (Math.Min(previewImg.Width, previewImg.Height) < MinReferenceShortSide
            || Math.Abs(Aspect(width, height) / Aspect(previewImg.Width, previewImg.Height) - 1) > MaxAspectDiff)
        {
            return false;
        }
        var refPx = ReadBgr(previewImg);

        // 預覽以原始方向儲存、全尺寸已轉正；直方圖與方向無關，縮成同樣畫素數但維持全尺寸自己的方向
        var sameWay = width >= height == previewImg.Width >= previewImg.Height;
        var (tw, th) = sameWay ? (previewImg.Width, previewImg.Height) : (previewImg.Height, previewImg.Width);
        byte[] smallPx;
        using (var full = ToBitmap(bgr, width, height))
        using (var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, tw, th));
            }
            smallPx = ReadBgr(small);
        }

        var luts = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            luts[c] = BuildLut(Histogram(smallPx, c), Histogram(refPx, c));
        }
        for (var i = 0; i < bgr.Length; i += 3)
        {
            bgr[i] = luts[0][bgr[i]];
            bgr[i + 1] = luts[1][bgr[i + 1]];
            bgr[i + 2] = luts[2][bgr[i + 2]];
        }
        return true;
    }

    private static double[] Histogram(byte[] bgr, int channel)
    {
        var h = new double[256];
        for (var i = channel; i < bgr.Length; i += 3)
        {
            h[bgr[i]]++;
        }
        return h;
    }

    /// <summary>直方圖規格化：每個來源階調取累積比例的中點，找參考分布中同一比例的階調（線性內插，保持遞增）。</summary>
    internal static byte[] BuildLut(double[] srcHist, double[] refHist)
    {
        static double[] Cdf(double[] h)
        {
            var total = Math.Max(1, h.Sum());
            var cdf = new double[256];
            double acc = 0;
            for (var i = 0; i < 256; i++)
            {
                acc += h[i];
                cdf[i] = acc / total;
            }
            return cdf;
        }
        var sc = Cdf(srcHist);
        var rc = Cdf(refHist);
        var lut = new byte[256];
        var j = 0;
        var prev = 0.0;
        for (var v = 0; v < 256; v++)
        {
            var target = v == 0 ? sc[0] / 2 : (sc[v - 1] + sc[v]) / 2;
            while (j < 255 && rc[j] < target)
            {
                j++;
            }
            var lo = j == 0 ? 0 : rc[j - 1];
            var t = rc[j] > lo ? (target - lo) / (rc[j] - lo) : 1;
            var value = Math.Max(prev, j - 0.5 + Math.Clamp(t, 0, 1)); // 參考階調 j 的範圍是 [j-0.5, j+0.5]
            prev = value;
            lut[v] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
        return lut;
    }

    // ------------------------------------------------------------ GDI+ 影像

    private static Bitmap? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms, false, true);
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            }
            return bmp;
        }
        catch (ArgumentException)
        {
            return null; // 預覽資料損壞就不拿來當參考
        }
    }

    private static Bitmap ToBitmap(byte[] bgr, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bgr, y * width * 3, data.Scan0 + y * data.Stride, width * 3);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static byte[] ReadBgr(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var result = new byte[w * h * 3];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, result, y * w * 3, w * 3);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return result;
    }

    // ------------------------------------------------------------ JPEG 中繼資料

    internal static byte[] BuildXmp(RawSourceInfo source)
    {
        var xml =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            $"<rdf:Description rdf:about=\"\" xmlns:p2d=\"{XmpNs}\"" +
            $" p2d:SourceSha256=\"{source.Sha256}\" p2d:SourceSize=\"{source.Size}\"" +
            $" p2d:SourceName=\"{SecurityElement.Escape(source.Name)}\"" +
            $" p2d:SourceFormat=\"{Path.GetExtension(source.Name).TrimStart('.').ToUpperInvariant()}\"/>" +
            "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>拿掉 JPEG 原有的 APPn 區段，依序放入 EXIF（APP1）、XMP（APP1）。</summary>
    internal static byte[] RebuildJpeg(byte[] jpeg, byte[]? exif, byte[] xmp)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("不是 JPEG 資料");
        }
        var o = new MemoryStream(jpeg.Length + 70_000);
        o.Write(jpeg, 0, 2);
        if (exif is not null && exif.Length + 8 <= 0xFFFF)
        {
            WriteSegment(o, 0xE1, "Exif\0\0"u8, exif);
        }
        if (xmp.Length + 31 <= 0xFFFF)
        {
            WriteSegment(o, 0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), xmp);
        }
        var p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF && jpeg[p + 1] is >= 0xE0 and <= 0xEF)
        {
            p += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(p + 2));
        }
        o.Write(jpeg, p, jpeg.Length - p);
        return o.ToArray();
    }

    private static void WriteSegment(Stream o, byte marker, ReadOnlySpan<byte> header, byte[] data)
    {
        var len = header.Length + data.Length + 2;
        o.WriteByte(0xFF);
        o.WriteByte(marker);
        o.WriteByte((byte)(len >> 8));
        o.WriteByte((byte)len);
        o.Write(header);
        o.Write(data);
    }
}

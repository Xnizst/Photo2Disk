using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Photo2Disk.ConvertProbe;

/// <summary>
/// 以 DNG 的小尺寸內嵌預覽（相機自己的沖洗結果）當顏色參考，校正 LibRaw 的全尺寸結果。
/// 做法：各色版直方圖對應。把全尺寸結果縮到預覽大小，比較兩者每個色版的累積分布，
/// 建出 256 階對照表，再套到全尺寸畫面。只比較分布，不需要畫面精確對齊。
/// 2026-11-25 實驗：DJI ΔE 15.2 → 4.2；Leica 7.0 → 6.6（剩餘差距主要來自預覽取景略不同）。
/// </summary>
internal static class ColorMatch
{
    private const int MinShortSide = 240;      // 預覽太小時分布不可靠
    private const double MaxAspectDiff = 0.03; // 長寬比差太多代表預覽有裁切，不拿來當參考

    public static BitmapSource ToPreview(BitmapSource full, BitmapSource preview)
    {
        // 直方圖與方向無關，只要比較長短邊
        double Aspect(BitmapSource b) => (double)Math.Max(b.PixelWidth, b.PixelHeight) / Math.Min(b.PixelWidth, b.PixelHeight);
        var shortSide = Math.Min(preview.PixelWidth, preview.PixelHeight);
        var aspectDiff = Math.Abs(Aspect(full) / Aspect(preview) - 1);
        if (shortSide < MinShortSide || aspectDiff > MaxAspectDiff)
        {
            Console.WriteLine($"內嵌預覽 {preview.PixelWidth}×{preview.PixelHeight} 不適合當顏色參考（短邊 {shortSide}，長寬比差 {aspectDiff:P1}），維持 LibRaw 結果");
            return full;
        }

        var refPx = ToBgr(preview);
        // 預覽以原始方向儲存，全尺寸結果已轉正；縮成和預覽同樣大小，但維持全尺寸自己的方向
        var sameWay = full.PixelWidth >= full.PixelHeight == refPx.Width >= refPx.Height;
        var (tw, th) = sameWay ? (refPx.Width, refPx.Height) : (refPx.Height, refPx.Width);
        var small = new TransformedBitmap(full, new ScaleTransform((double)tw / full.PixelWidth, (double)th / full.PixelHeight));
        var srcPx = ToBgr(small);

        var luts = new byte[3][];
        for (var c = 0; c < 3; c++)
        {
            luts[c] = BuildLut(Histogram(srcPx.Data, c), Histogram(refPx.Data, c));
        }

        var fullPx = ToBgr(full);
        var d = fullPx.Data;
        for (var i = 0; i < d.Length; i += 3)
        {
            d[i] = luts[0][d[i]];
            d[i + 1] = luts[1][d[i + 1]];
            d[i + 2] = luts[2][d[i + 2]];
        }
        var result = BitmapSource.Create(fullPx.Width, fullPx.Height, 96, 96, PixelFormats.Bgr24, null, d, fullPx.Width * 3);
        result.Freeze();
        Console.WriteLine($"依內嵌預覽 {preview.PixelWidth}×{preview.PixelHeight} 校正顏色（直方圖對應）");
        return result;
    }

    private record Pixels(int Width, int Height, byte[] Data);

    private static Pixels ToBgr(BitmapSource b)
    {
        BitmapSource s = b.Format == PixelFormats.Bgr24 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgr24, null, 0);
        var data = new byte[s.PixelWidth * s.PixelHeight * 3];
        s.CopyPixels(data, s.PixelWidth * 3, 0);
        return new Pixels(s.PixelWidth, s.PixelHeight, data);
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
    private static byte[] BuildLut(double[] srcHist, double[] refHist)
    {
        double[] Cdf(double[] h)
        {
            var total = h.Sum();
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
            var value = Math.Max(prev, j - 1 + Math.Clamp(t, 0, 1) + 0.5); // 參考階調 j 的範圍是 [j-0.5, j+0.5]
            prev = value;
            lut[v] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
        return lut;
    }
}

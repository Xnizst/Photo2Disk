using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sdcb.LibRaw;

namespace Photo2Disk.ConvertProbe;

/// <summary>
/// 成像參數實驗：同一張 DNG 用幾組 LibRaw 參數各轉一張半尺寸 PNG，交給 tools\compare_render.py 和內嵌預覽算色差。
/// 用法：ConvertProbe.exe --variants &lt;輸出資料夾&gt; &lt;DNG&gt; ...
/// </summary>
internal static class Variants
{
    private record Variant(string Name, Action<OutputParams, double> Apply);

    private static readonly Variant[] All =
    {
        // 目前的做法：相機白平衡、自動亮度
        new("A_預設", (c, be) => { }),
        // 關閉自動亮度：亮度由相機的白點決定
        new("B_關自動亮度", (c, be) => c.NoAutoBright = true),
        // 關閉自動亮度＋套用 BaselineExposure
        new("C_關自動亮度加基準曝光", (c, be) =>
        {
            c.NoAutoBright = true;
            if (be != 0)
            {
                c.ExpCorrec = true;
                c.ExpShift = (float)Math.Pow(2, be);
                c.ExpPreser = 0.8f; // 加亮時保留亮部
            }
        }),
        // 自動亮度＋套用 BaselineExposure
        new("D_自動亮度加基準曝光", (c, be) =>
        {
            if (be != 0)
            {
                c.ExpCorrec = true;
                c.ExpShift = (float)Math.Pow(2, be);
                c.ExpPreser = 0.8f;
            }
        }),
        // 自動亮度之後再乘上 2^BaselineExposure（dcraw 的 -b 亮度倍率）
        new("E_自動亮度乘基準曝光", (c, be) => c.Brightness = (float)Math.Pow(2, be)),
        // 白平衡與色彩矩陣的替代做法（亮度同 E）
        new("F_不用相機白平衡", (c, be) => { c.Brightness = (float)Math.Pow(2, be); c.UseCameraWb = false; }),
        new("G_自動白平衡", (c, be) => { c.Brightness = (float)Math.Pow(2, be); c.UseCameraWb = false; c.UseAutoWb = true; }),
        new("H_不用DNG色彩矩陣", (c, be) => { c.Brightness = (float)Math.Pow(2, be); c.UseCameraMatrix = false; }),
    };

    public static int Run(string outDir, IEnumerable<string> files)
    {
        Directory.CreateDirectory(outDir);
        foreach (var file in files)
        {
            var be = DngExif.ReadBaselineExposure(File.ReadAllBytes(file));
            Console.WriteLine($"=== {Path.GetFileName(file)}（BaselineExposure {be:+0.00;-0.00;0} EV）");
            foreach (var v in All)
            {
                using var raw = RawContext.OpenFile(file);
                raw.Unpack();
                raw.DcrawProcess(c =>
                {
                    c.UseCameraWb = true;
                    c.OutputBps = 8;
                    c.HalfSize = true;
                    c.Gamma[0] = 1 / 2.4;
                    c.Gamma[1] = 12.92;
                    v.Apply(c, be);
                });
                using var img = raw.MakeDcrawMemoryImage();
                var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Rgb24, null,
                    img.AsSpan<byte>().ToArray(), img.Width * 3);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                var path = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(file)}__{v.Name}.png");
                using (var fs = File.Create(path))
                {
                    enc.Save(fs);
                }
                Console.WriteLine($"  {v.Name}：{img.Width}×{img.Height}");
            }
        }
        return 0;
    }
}

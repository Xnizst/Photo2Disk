using System.Buffers.Binary;

namespace Photo2Disk;

/// <summary>
/// 從 DNG（TIFF 結構）挑出可以搬到 JPG/PNG 的欄位，重新組成一份 EXIF。
/// DNG 的 IFD0 混有 RAW 專用欄位（色彩矩陣、影像資料位置等），整份搬過去會讓看圖軟體誤判，
/// 所以只留：IFD0 的基本資料、Exif IFD（拍攝時間、相機參數）、GPS IFD。
/// 輸出沿用原檔的位元組順序，欄位資料原樣複製，不必逐一轉換。
/// </summary>
internal static class DngExif
{
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagOrientation = 0x0112;

    // IFD0 只留這些（描述、廠牌、型號、軟體、修改時間、作者、版權）
    private static readonly HashSet<ushort> Ifd0Keep = new() { 0x010E, 0x010F, 0x0110, 0x0131, 0x0132, 0x013B, 0x8298 };

    // Exif IFD 不搬：MakerNote（內含相對位移，搬家後會壞）、Interop 指標
    private static readonly HashSet<ushort> ExifDrop = new() { 0x927C, 0xA005 };

    private record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    /// <summary>TIFF/EP 允許把 Exif 欄位直接放在 IFD0（例如曝光時間、ISO、拍攝時間），這些要移到 Exif IFD。</summary>
    private static bool IsExifTag(ushort tag) =>
        tag is 0x829A or 0x829D or 0x8822 or 0x8824 or 0x8827 or 0x8828 or 0x8830 or 0x8832
        || tag is >= 0x9000 and <= 0x92FF
        || tag is >= 0xA000 and <= 0xA4FF;

    /// <summary>
    /// 找 DNG 內最大的 JPEG 預覽（NewSubFileType = 1、JPEG 壓縮），回傳 JPEG 資料與尺寸；沒有時回傳 null。
    /// 預覽是相機自己的沖洗結果，以原始方向儲存（方向由 IFD0 的 Orientation 決定）。
    /// </summary>
    public static (byte[] Jpeg, int Width, int Height)? ExtractPreview(byte[] src)
    {
        if (src.Length < 8 || (src[0] != 'I' && src[0] != 'M'))
        {
            return null;
        }
        var r = new Reader(src, src[0] == 'I');
        var ifd0 = r.ReadIfd(r.U32(4));
        var ifds = new List<List<Entry>> { ifd0 };
        if (ifd0.FirstOrDefault(e => e.Tag == 0x014A) is { } subs)
        {
            ifds.AddRange(r.Numbers(subs).Select(o => r.ReadIfd((uint)o)));
        }
        (byte[], int, int)? best = null;
        foreach (var d in ifds)
        {
            uint Get(ushort tag) => d.FirstOrDefault(e => e.Tag == tag) is { } e ? (uint)r.Numbers(e)[0] : 0;
            if (Get(0x00FE) != 1 || Get(0x0103) is not (6 or 7))
            {
                continue;
            }
            var offsets = d.FirstOrDefault(e => e.Tag == 0x0111);
            var counts = d.FirstOrDefault(e => e.Tag == 0x0117);
            if (offsets is null || counts is null)
            {
                continue;
            }
            var w = (int)Get(0x0100);
            if (best is { } b && w <= b.Item2)
            {
                continue;
            }
            var ms = new MemoryStream();
            foreach (var (o, c) in r.Numbers(offsets).Zip(r.Numbers(counts)))
            {
                if (o + c > src.Length)
                {
                    ms = null;
                    break;
                }
                ms.Write(src, (int)o, (int)c);
            }
            var jpeg = ms?.ToArray();
            if (jpeg is { Length: > 2 } && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            {
                best = (jpeg, w, (int)Get(0x0101));
            }
        }
        return best;
    }

    /// <summary>讀 IFD0 的 Orientation，沒有時回傳 1。</summary>
    public static int ReadOrientation(byte[] src)
    {
        if (src.Length < 8 || (src[0] != 'I' && src[0] != 'M'))
        {
            return 1;
        }
        var r = new Reader(src, src[0] == 'I');
        return r.ReadIfd(r.U32(4)).FirstOrDefault(e => e.Tag == TagOrientation) is { } e ? (int)r.Numbers(e)[0] : 1;
    }

    /// <param name="orientation">寫進新檔的方向。畫面已轉正時給 1；直接沿用未轉正的內嵌預覽時給原檔方向。</param>
    public static byte[]? Build(byte[] src, int orientation = 1)
    {
        if (src.Length < 8)
        {
            return null;
        }
        var le = src[0] == 'I';
        if (!(le && src[1] == 'I' && src[2] == 42) && !(!le && src[0] == 'M' && src[1] == 'M' && src[3] == 42))
        {
            return null;
        }
        var r = new Reader(src, le);
        var ifd0 = r.ReadIfd(r.U32(4));

        var exif = new Dictionary<ushort, Entry>();
        var gps = new List<Entry>();
        if (ifd0.FirstOrDefault(e => e.Tag == TagExifIfd) is { } exifPtr)
        {
            foreach (var e in r.ReadIfd(r.Pointer(exifPtr)))
            {
                if (!ExifDrop.Contains(e.Tag) && e.Type != 13)
                {
                    exif[e.Tag] = e;
                }
            }
        }
        foreach (var e in ifd0.Where(e => IsExifTag(e.Tag)))
        {
            exif.TryAdd(e.Tag, e);
        }
        if (ifd0.FirstOrDefault(e => e.Tag == TagGpsIfd) is { } gpsPtr)
        {
            gps.AddRange(r.ReadIfd(r.Pointer(gpsPtr)).Where(e => e.Type != 13));
        }

        var w = new Writer(le);
        // 子 IFD 先寫，IFD0 最後寫，指標位置才算得出來
        var exifOffset = exif.Count > 0 ? w.WriteIfd(exif.Values.ToList()) : 0;
        var gpsOffset = gps.Count > 0 ? w.WriteIfd(gps) : 0;

        var main = ifd0.Where(e => Ifd0Keep.Contains(e.Tag)).ToList();
        main.Add(new Entry(TagOrientation, 3, 1, w.U16Bytes((ushort)orientation)));
        if (exifOffset > 0)
        {
            main.Add(new Entry(TagExifIfd, 4, 1, w.U32Bytes(exifOffset)));
        }
        if (gpsOffset > 0)
        {
            main.Add(new Entry(TagGpsIfd, 4, 1, w.U32Bytes(gpsOffset)));
        }
        var ifd0Offset = w.WriteIfd(main);
        return w.Finish(ifd0Offset);
    }

    /// <summary>讀 IFD0 的 BaselineExposure（0xC62A，廠商建議的曝光補償，單位 EV）；沒有時回傳 0。</summary>
    public static double ReadBaselineExposure(byte[] src)
    {
        if (src.Length < 8 || (src[0] != 'I' && src[0] != 'M'))
        {
            return 0;
        }
        var le = src[0] == 'I';
        var r = new Reader(src, le);
        var e = r.ReadIfd(r.U32(4)).FirstOrDefault(x => x.Tag == 0xC62A && x.Type == 10 && x.Value.Length == 8);
        if (e is null)
        {
            return 0;
        }
        var num = le ? BinaryPrimitives.ReadInt32LittleEndian(e.Value) : BinaryPrimitives.ReadInt32BigEndian(e.Value);
        var den = le ? BinaryPrimitives.ReadInt32LittleEndian(e.Value.AsSpan(4)) : BinaryPrimitives.ReadInt32BigEndian(e.Value.AsSpan(4));
        return den == 0 ? 0 : (double)num / den;
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    private sealed class Reader(byte[] b, bool le)
    {
        public ushort U16(long p) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)p)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)p));
        public uint U32(long p) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)p)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)p));
        public uint Pointer(Entry e) => le ? BinaryPrimitives.ReadUInt32LittleEndian(e.Value) : BinaryPrimitives.ReadUInt32BigEndian(e.Value);

        /// <summary>SHORT／LONG／IFD 型別欄位的數值。</summary>
        public long[] Numbers(Entry e) => e.Type switch
        {
            3 => Enumerable.Range(0, (int)e.Count).Select(i => (long)(le
                ? BinaryPrimitives.ReadUInt16LittleEndian(e.Value.AsSpan(i * 2))
                : BinaryPrimitives.ReadUInt16BigEndian(e.Value.AsSpan(i * 2)))).ToArray(),
            4 or 13 => Enumerable.Range(0, (int)e.Count).Select(i => (long)(le
                ? BinaryPrimitives.ReadUInt32LittleEndian(e.Value.AsSpan(i * 4))
                : BinaryPrimitives.ReadUInt32BigEndian(e.Value.AsSpan(i * 4)))).ToArray(),
            _ => new long[] { 0 },
        };

        public List<Entry> ReadIfd(uint offset)
        {
            var list = new List<Entry>();
            if (offset == 0 || offset + 2 > b.Length)
            {
                return list;
            }
            int n = U16(offset);
            for (var i = 0; i < n; i++)
            {
                var p = offset + 2 + i * 12L;
                if (p + 12 > b.Length)
                {
                    break;
                }
                var tag = U16(p);
                var type = U16(p + 2);
                var count = U32(p + 4);
                var size = (long)TypeSize(type) * count;
                if (size == 0 || size > 64 * 1024)
                {
                    continue; // 不認得的型別或過大的欄位（例如內嵌預覽）不搬
                }
                var at = size <= 4 ? p + 8 : U32(p + 8);
                if (at + size > b.Length)
                {
                    continue;
                }
                list.Add(new Entry(tag, type, count, b.AsSpan((int)at, (int)size).ToArray()));
            }
            return list;
        }
    }

    private sealed class Writer(bool le)
    {
        private readonly MemoryStream _out = new();

        public byte[] U16Bytes(ushort v)
        {
            var b = new byte[2];
            if (le) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v);
            return b;
        }

        public byte[] U32Bytes(uint v)
        {
            var b = new byte[4];
            if (le) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v);
            return b;
        }

        private long Pos => 8 + _out.Length;

        /// <summary>寫一個 IFD（含資料區），回傳它在 TIFF 中的位移。</summary>
        public uint WriteIfd(List<Entry> entries)
        {
            entries.Sort((a, c) => a.Tag.CompareTo(c.Tag));
            if (Pos % 2 == 1)
            {
                _out.WriteByte(0);
            }
            var start = (uint)Pos;
            var dataPos = start + 2 + entries.Count * 12 + 4;
            var data = new MemoryStream();
            _out.Write(U16Bytes((ushort)entries.Count));
            foreach (var e in entries)
            {
                _out.Write(U16Bytes(e.Tag));
                _out.Write(U16Bytes(e.Type));
                _out.Write(U32Bytes(e.Count));
                if (e.Value.Length <= 4)
                {
                    var v = new byte[4];
                    e.Value.CopyTo(v, 0);
                    _out.Write(v);
                }
                else
                {
                    if (data.Length % 2 == 1)
                    {
                        data.WriteByte(0);
                    }
                    _out.Write(U32Bytes((uint)(dataPos + data.Length)));
                    data.Write(e.Value);
                }
            }
            _out.Write(U32Bytes(0)); // 下一個 IFD
            data.Position = 0;
            data.CopyTo(_out);
            return start;
        }

        public byte[] Finish(uint ifd0Offset)
        {
            var head = new byte[8];
            head[0] = head[1] = (byte)(le ? 'I' : 'M');
            U16Bytes(42).CopyTo(head, 2);
            U32Bytes(ifd0Offset).CopyTo(head, 4);
            var r = new byte[8 + _out.Length];
            head.CopyTo(r, 0);
            _out.ToArray().CopyTo(r, 8);
            return r;
        }
    }
}

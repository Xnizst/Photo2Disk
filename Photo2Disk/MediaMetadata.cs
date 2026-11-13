using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Photo2Disk;

/// <summary>拍攝日期是從哪裡取得的。</summary>
public enum DateSource
{
    None,
    Exif,
    Video,
    FileName,
    FileTime,
}

public readonly record struct MediaInfo(DateTime? Taken, DateSource Source, string? CameraModel);

/// <summary>
/// 讀取照片、影片的拍攝時間與相機型號（不依賴第三方套件）。
/// 順序：照片 EXIF → 影片 mvhd 建立時間 → 檔名裡的日期 → 檔案修改時間。
/// </summary>
public static partial class MediaMetadata
{
    private const int MaxBlock = 4 * 1024 * 1024;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp", ".3g2", ".avi", ".mkv", ".wmv", ".mts", ".m2ts", ".webm", ".mpg", ".mpeg",
    };

    public static bool IsVideo(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>讀取本機檔案；originalName 用來從檔名找日期，fileTime 是最後的備案。</summary>
    public static MediaInfo Read(string path, string originalName, DateTime fileTime)
    {
        DateTime? taken = null;
        string? model = null;
        var source = DateSource.None;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            (taken, model, source) = ReadEmbedded(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            // 讀不到或格式不符就當作沒有內嵌資訊
        }
        if (taken is null && ParseFileNameDate(originalName) is { } fromName)
        {
            taken = fromName;
            source = DateSource.FileName;
        }
        if (taken is null && IsPlausible(fileTime))
        {
            taken = fileTime;
            source = DateSource.FileTime;
        }
        return new MediaInfo(taken, taken is null ? DateSource.None : source, model);
    }

    private static (DateTime?, string?, DateSource) ReadEmbedded(Stream s)
    {
        var head = new byte[16];
        var n = ReadFully(s, head, 0, head.Length);
        if (n < 12)
        {
            return (null, null, DateSource.None);
        }
        s.Position = 0;

        // JPEG
        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            return FromTiff(ReadJpegExif(s));
        }
        // TIFF（DNG 等 RAW 檔）
        if ((head[0] == 'I' && head[1] == 'I' && head[2] == 42 && head[3] == 0)
            || (head[0] == 'M' && head[1] == 'M' && head[2] == 0 && head[3] == 42))
        {
            return FromTiff(ReadBlock(s, 0, (int)Math.Min(s.Length, MaxBlock)));
        }
        // PNG
        if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
        {
            return FromTiff(ReadPngExif(s));
        }
        // ISO 媒體格式：HEIC／AVIF 照片、MP4／MOV 影片
        var type = Encoding.ASCII.GetString(head, 4, 4);
        if (type is "ftyp" or "moov" or "mdat" or "wide" or "free" or "skip" or "pnot")
        {
            return ReadIsoMedia(s);
        }
        return (null, null, DateSource.None);
    }

    private static (DateTime?, string?, DateSource) FromTiff(byte[]? tiff)
    {
        if (tiff is null)
        {
            return (null, null, DateSource.None);
        }
        var (date, model) = ParseTiff(tiff);
        return (date, model, date is null ? DateSource.None : DateSource.Exif);
    }

    // ------------------------------------------------------------ JPEG

    private static byte[]? ReadJpegExif(Stream s)
    {
        s.Position = 2;
        var hdr = new byte[4];
        while (s.Position < s.Length)
        {
            if (ReadFully(s, hdr, 0, 2) < 2 || hdr[0] != 0xFF)
            {
                return null;
            }
            var marker = hdr[1];
            while (marker == 0xFF)
            {
                var b = s.ReadByte();
                if (b < 0)
                {
                    return null;
                }
                marker = (byte)b;
            }
            if (marker is 0xD9 or 0xDA)
            {
                return null; // 影像資料開始，EXIF 一定在前面
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                continue; // 沒有長度欄位的標記
            }
            if (ReadFully(s, hdr, 0, 2) < 2)
            {
                return null;
            }
            var len = BinaryPrimitives.ReadUInt16BigEndian(hdr) - 2;
            if (len < 0)
            {
                return null;
            }
            if (marker == 0xE1 && len > 6)
            {
                var data = ReadBlock(s, s.Position, len);
                if (data.Length > 6 && data[0] == 'E' && data[1] == 'x' && data[2] == 'i' && data[3] == 'f' && data[4] == 0)
                {
                    return data.AsSpan(6).ToArray();
                }
                continue; // 可能是 XMP，繼續找
            }
            s.Position += len;
        }
        return null;
    }

    // ------------------------------------------------------------ PNG

    private static byte[]? ReadPngExif(Stream s)
    {
        s.Position = 8;
        var hdr = new byte[8];
        while (ReadFully(s, hdr, 0, 8) == 8)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.ASCII.GetString(hdr, 4, 4);
            if (type == "eXIf" && len <= MaxBlock)
            {
                return ReadBlock(s, s.Position, (int)len);
            }
            if (type is "IDAT" or "IEND")
            {
                return null;
            }
            s.Position += len + 4L; // 資料＋CRC
        }
        return null;
    }

    // ------------------------------------------------------------ TIFF／EXIF

    /// <summary>從 TIFF 結構讀出拍攝時間（DateTimeOriginal 優先）與相機型號。</summary>
    internal static (DateTime? Date, string? Model) ParseTiff(byte[] t)
    {
        if (t.Length < 8)
        {
            return (null, null);
        }
        bool le;
        if (t[0] == 'I' && t[1] == 'I')
        {
            le = true;
        }
        else if (t[0] == 'M' && t[1] == 'M')
        {
            le = false;
        }
        else
        {
            return (null, null);
        }
        ushort U16(long o) => o + 2 <= t.Length
            ? (le ? BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan((int)o)))
            : throw new InvalidDataException();
        uint U32(long o) => o + 4 <= t.Length
            ? (le ? BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan((int)o)))
            : throw new InvalidDataException();

        string? Ascii(long entry)
        {
            var count = U32(entry + 4);
            if (count == 0 || count > 1024)
            {
                return null;
            }
            long off = count <= 4 ? entry + 8 : U32(entry + 8);
            if (off < 0 || off + count > t.Length)
            {
                return null;
            }
            return Encoding.ASCII.GetString(t, (int)off, (int)count).Trim('\0', ' ');
        }

        var tags = new Dictionary<ushort, long>();
        void ReadIfd(long ifd)
        {
            if (ifd <= 0 || ifd + 2 > t.Length)
            {
                return;
            }
            var count = U16(ifd);
            for (var i = 0; i < count; i++)
            {
                var entry = ifd + 2 + 12L * i;
                if (entry + 12 > t.Length)
                {
                    return;
                }
                tags.TryAdd(U16(entry), entry);
            }
        }

        try
        {
            ReadIfd(U32(4));
            if (tags.TryGetValue(0x8769, out var exifPtr))
            {
                ReadIfd(U32(exifPtr + 8));
            }
            string? model = tags.TryGetValue(0x0110, out var m) ? Ascii(m) : null;
            DateTime? date = null;
            foreach (var tag in new ushort[] { 0x9003, 0x9004, 0x0132 })
            {
                if (tags.TryGetValue(tag, out var e) && ParseExifDate(Ascii(e)) is { } d)
                {
                    date = d;
                    break;
                }
            }
            return (date, string.IsNullOrWhiteSpace(model) ? null : model);
        }
        catch (InvalidDataException)
        {
            return (null, null);
        }
    }

    private static DateTime? ParseExifDate(string? s)
    {
        if (s is null || s.Length < 19)
        {
            return null;
        }
        return DateTime.TryParseExact(s[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && IsPlausible(d)
            ? d
            : null;
    }

    // ------------------------------------------------------------ HEIC／MP4／MOV

    private readonly record struct Box(string Type, long Start, long DataStart, long End);

    private static IEnumerable<Box> Boxes(Stream s, long start, long end)
    {
        var hdr = new byte[16];
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (ReadFully(s, hdr, 0, 8) < 8)
            {
                yield break;
            }
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var type = Encoding.ASCII.GetString(hdr, 4, 4);
            long dataStart = pos + 8;
            if (size == 1)
            {
                if (ReadFully(s, hdr, 8, 8) < 8)
                {
                    yield break;
                }
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                dataStart = pos + 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < dataStart - pos || pos + size > end)
            {
                yield break;
            }
            yield return new Box(type, pos, dataStart, pos + size);
            pos += size;
        }
    }

    private static (DateTime?, string?, DateSource) ReadIsoMedia(Stream s)
    {
        foreach (var box in Boxes(s, 0, s.Length).ToList())
        {
            if (box.Type == "meta" && box.End - box.DataStart <= MaxBlock)
            {
                var tiff = ReadHeifExif(s, box);
                if (tiff is not null)
                {
                    var r = FromTiff(tiff);
                    if (r.Item1 is not null)
                    {
                        return r;
                    }
                }
            }
            else if (box.Type == "moov")
            {
                // iPhone 影片的 com.apple.quicktime.creationdate 是拍攝地的當地時間（和照片 EXIF 同義），優先採用；
                // mvhd 是 UTC，且影片被編輯、轉存後可能變成轉存時間（實測樣本差到一年以上）
                DateTime? mvhd = null;
                (DateTime? Date, string? Model) apple = (null, null);
                foreach (var child in Boxes(s, box.DataStart, box.End).ToList())
                {
                    if (child.Type == "mvhd")
                    {
                        mvhd ??= ReadMvhd(s, child);
                    }
                    else if (child.Type == "meta" && child.End - child.DataStart <= MaxBlock && apple.Date is null)
                    {
                        apple = ReadAppleKeys(ReadBlock(s, child.DataStart, (int)(child.End - child.DataStart)));
                    }
                }
                var date = apple.Date ?? mvhd;
                if (date is not null)
                {
                    return (date, apple.Model, DateSource.Video);
                }
            }
        }
        return (null, null, DateSource.None);
    }

    private static DateTime? ReadMvhd(Stream s, Box box)
    {
        var data = ReadBlock(s, box.DataStart, (int)Math.Min(box.End - box.DataStart, 32));
        if (data.Length < 8)
        {
            return null;
        }
        ulong seconds = data[0] == 1
            ? (data.Length >= 12 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(4)) : 0)
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (seconds == 0 || seconds > 10_000_000_000)
        {
            return null;
        }
        // mvhd 記錄的是 UTC（從 1904-01-01 起算的秒數），轉成這台電腦的當地時間
        var local = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return IsPlausible(local) ? local : null;
    }

    /// <summary>
    /// QuickTime 的 moov/meta：keys 列出鍵名（第 1、2…個），ilst 底下的子 box 類型是鍵的編號，裡面的 data box 是值。
    /// QuickTime 的 meta 沒有 version／flags，MP4 的 meta 有，兩種都試。
    /// </summary>
    internal static (DateTime? Date, string? Model) ReadAppleKeys(byte[] meta)
    {
        foreach (var start in new[] { 0, 4 })
        {
            var children = ChildBoxes(meta, start, meta.Length);
            if (!children.Any(c => c.Type is "keys" or "ilst" or "hdlr"))
            {
                continue;
            }
            var keys = new Dictionary<uint, string>();
            var keysBox = children.FirstOrDefault(c => c.Type == "keys");
            if (keysBox.Type == "keys")
            {
                var p = keysBox.Body + 4;
                var count = ReadU32(meta, p);
                p += 4;
                for (uint i = 1; i <= count && p + 8 <= keysBox.End; i++)
                {
                    var size = (int)ReadU32(meta, p);
                    if (size < 8 || p + size > keysBox.End)
                    {
                        break;
                    }
                    keys[i] = Encoding.UTF8.GetString(meta, p + 8, size - 8);
                    p += size;
                }
            }
            DateTime? date = null;
            string? model = null;
            var ilst = children.FirstOrDefault(c => c.Type == "ilst");
            if (ilst.Type == "ilst")
            {
                foreach (var entry in ChildBoxes(meta, ilst.Body, ilst.End))
                {
                    if (!keys.TryGetValue(ReadU32(meta, entry.Start + 4), out var key))
                    {
                        continue;
                    }
                    var data = ChildBoxes(meta, entry.Body, entry.End).FirstOrDefault(c => c.Type == "data");
                    if (data.Type != "data" || data.End - data.Body < 8)
                    {
                        continue;
                    }
                    var value = Encoding.UTF8.GetString(meta, data.Body + 8, data.End - data.Body - 8).Trim('\0', ' ');
                    if (key == "com.apple.quicktime.creationdate")
                    {
                        date = ParseIsoLocal(value);
                    }
                    else if (key == "com.apple.quicktime.model" && value.Length > 0)
                    {
                        model = value;
                    }
                }
            }
            return (date, model);
        }
        return (null, null);
    }

    private readonly record struct MemBox(string Type, int Start, int Body, int End);

    private static List<MemBox> ChildBoxes(byte[] b, int start, int end)
    {
        var list = new List<MemBox>();
        var p = start;
        while (p + 8 <= end)
        {
            var size = ReadU32(b, p);
            if (size < 8 || p + size > end)
            {
                break;
            }
            list.Add(new MemBox(Encoding.ASCII.GetString(b, p + 4, 4), p, p + 8, p + (int)size));
            p += (int)size;
        }
        return list;
    }

    private static uint ReadU32(byte[] b, int p) =>
        p >= 0 && p + 4 <= b.Length ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)) : throw new InvalidDataException();

    /// <summary>「2019-07-24T11:25:40+0300」取前面的當地日期時間，不換算時區。</summary>
    private static DateTime? ParseIsoLocal(string s)
    {
        if (s.Length < 19)
        {
            return null;
        }
        return DateTime.TryParseExact(s[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && IsPlausible(d)
            ? d
            : null;
    }

    /// <summary>HEIC 的 EXIF 是一個 item：在 iinf 找類型為 Exif 的編號，再到 iloc 找它在檔案中的位置。</summary>
    private static byte[]? ReadHeifExif(Stream s, Box meta)
    {
        var m = ReadBlock(s, meta.DataStart, (int)(meta.End - meta.DataStart));
        var pos = 4; // FullBox 的 version／flags
        uint? exifId = null;
        (long Offset, long Length)? exifLoc = null;
        var locations = new Dictionary<uint, (long, long)>();

        uint U(int o, int size) => size switch
        {
            0 => 0,
            1 => m[o],
            2 => BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(o)),
            4 => BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(o)),
            8 => (uint)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(o)), uint.MaxValue),
            _ => throw new InvalidDataException(),
        };

        while (pos + 8 <= m.Length)
        {
            var size = (int)Math.Min(U(pos, 4), int.MaxValue);
            var type = Encoding.ASCII.GetString(m, pos + 4, 4);
            if (size < 8 || pos + size > m.Length)
            {
                break;
            }
            var body = pos + 8;
            var end = pos + size;
            if (type == "iinf")
            {
                var version = m[body];
                var p = body + 4 + (version == 0 ? 2 : 4);
                while (p + 8 <= end)
                {
                    var csize = (int)U(p, 4);
                    if (csize < 8 || p + csize > end)
                    {
                        break;
                    }
                    if (Encoding.ASCII.GetString(m, p + 4, 4) == "infe" && m[p + 8] >= 2)
                    {
                        var v = m[p + 8];
                        var q = p + 12;
                        var id = v == 2 ? U(q, 2) : U(q, 4);
                        q += (v == 2 ? 2 : 4) + 2;
                        if (q + 4 <= p + csize && Encoding.ASCII.GetString(m, q, 4) == "Exif")
                        {
                            exifId = id;
                        }
                    }
                    p += csize;
                }
            }
            else if (type == "iloc")
            {
                var version = m[body];
                var p = body + 4;
                var offsetSize = m[p] >> 4;
                var lengthSize = m[p] & 0xF;
                var baseOffsetSize = m[p + 1] >> 4;
                var indexSize = version is 1 or 2 ? m[p + 1] & 0xF : 0;
                p += 2;
                var count = version < 2 ? U(p, 2) : U(p, 4);
                p += version < 2 ? 2 : 4;
                for (var i = 0; i < count && p < end; i++)
                {
                    var id = version < 2 ? U(p, 2) : U(p, 4);
                    p += version < 2 ? 2 : 4;
                    var method = 0u;
                    if (version is 1 or 2)
                    {
                        method = U(p, 2) & 0xF;
                        p += 2;
                    }
                    p += 2; // data_reference_index
                    long baseOffset = U(p, baseOffsetSize);
                    p += baseOffsetSize;
                    var extents = U(p, 2);
                    p += 2;
                    for (var e = 0; e < extents; e++)
                    {
                        p += indexSize;
                        long off = U(p, offsetSize);
                        p += offsetSize;
                        long len = U(p, lengthSize);
                        p += lengthSize;
                        if (e == 0 && method == 0)
                        {
                            locations[id] = (baseOffset + off, len);
                        }
                    }
                }
            }
            pos = end;
        }

        if (exifId is { } eid && locations.TryGetValue(eid, out var loc))
        {
            exifLoc = loc;
        }
        if (exifLoc is not { } l || l.Length < 8 || l.Offset + l.Length > s.Length)
        {
            return null;
        }
        var item = ReadBlock(s, l.Offset, (int)Math.Min(l.Length, MaxBlock));
        // 開頭 4 位元組是 TIFF 標頭的位移（通常跳過 "Exif\0\0"）
        var skip = 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(item);
        return skip >= 4 && skip < item.Length ? item.AsSpan(skip).ToArray() : null;
    }

    // ------------------------------------------------------------ 檔名日期

    // 例：IMG_20240315_143022.jpg、PXL_20240315_143022123.jpg、IMG-20240315-WA0001.jpg、Screenshot_2024-03-15-14-30-22.png
    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?:[-_ T.]?(?<H>[01]\d|2[0-3])[-_.:]?(?<M>[0-5]\d)[-_.:]?(?<S>[0-5]\d))?")]
    private static partial Regex FileNameDateRegex();

    internal static DateTime? ParseFileNameDate(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (Match m in FileNameDateRegex().Matches(stem))
        {
            int G(string name) => m.Groups[name].Success ? int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
            var y = G("y");
            var mo = G("m");
            var d = G("d");
            if (d > DateTime.DaysInMonth(y, mo))
            {
                continue;
            }
            var date = new DateTime(y, mo, d, G("H"), G("M"), G("S"));
            if (IsPlausible(date))
            {
                return date;
            }
        }
        return null;
    }

    // ------------------------------------------------------------ 工具

    private static bool IsPlausible(DateTime d) => d.Year >= 1980 && d <= DateTime.Now.AddDays(2);

    private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = s.Read(buffer, offset + total, count - total);
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static byte[] ReadBlock(Stream s, long position, int length)
    {
        if (length <= 0 || length > MaxBlock || position < 0 || position >= s.Length)
        {
            return Array.Empty<byte>();
        }
        length = (int)Math.Min(length, s.Length - position);
        var buffer = new byte[length];
        s.Position = position;
        var n = ReadFully(s, buffer, 0, length);
        return n == length ? buffer : buffer.AsSpan(0, n).ToArray();
    }
}

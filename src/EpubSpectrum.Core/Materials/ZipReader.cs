using System.IO.Compression;
using System.Text;

namespace EpubSpectrum.Core.Materials;

/// <summary>
/// 直接解析 ZIP central directory：物理顺序、DOS 时间基准(1980-01-01)、UTF-8 标志、
/// 压缩方式、CRC32、压缩/未压缩大小都属于不可变材料，必须原样保留。
/// </summary>
public static class ZipReader
{
    private const uint LocalSig = 0x04034b50;
    private const uint CentralSig = 0x02014b50;
    private const uint EocdSig = 0x06054b50;

    public sealed class ZipFormatException(string message) : Exception(message);

    public static IReadOnlyList<ZipEntryData> Read(byte[] zip)
    {
        if (zip.Length < 22) throw new ZipFormatException("ZIP 字节数不足 22，缺少 end of central directory。");
        int eocd = -1;
        int maxScan = Math.Min(zip.Length, 65557);
        for (int i = zip.Length - 22; i >= zip.Length - maxScan; i--)
        {
            if (i < 0) break;
            if (ReadU32(zip, i) == EocdSig) { eocd = i; break; }
        }
        if (eocd < 0) throw new ZipFormatException("未找到 end of central directory 记录 (0x06054b50)。");

        ushort cdEntries = ReadU16(zip, eocd + 10);
        uint cdOffset = ReadU32(zip, eocd + 16);
        if (cdOffset == 0xFFFFFFFFu)
            throw new ZipFormatException("ZIP64 EOCD64 定位暂不支持，central directory 偏移为 ZIP64 哨兵值。");

        var entries = new List<ZipEntryData>();
        int p = (int)cdOffset;
        for (int i = 0; i < cdEntries; i++)
        {
            if (p + 46 > zip.Length || ReadU32(zip, p) != CentralSig)
                throw new ZipFormatException($"central directory 第 {i + 1} 条记录签名损坏（偏移 {p} byte）。");

            ushort flags = ReadU16(zip, p + 8);
            ushort method = ReadU16(zip, p + 10);
            ushort dosTime = ReadU16(zip, p + 12);
            ushort dosDate = ReadU16(zip, p + 14);
            uint crc = ReadU32(zip, p + 16);
            uint compSize = ReadU32(zip, p + 20);
            uint uncompSize = ReadU32(zip, p + 24);
            ushort nameLen = ReadU16(zip, p + 28);
            ushort extraLen = ReadU16(zip, p + 30);
            ushort commentLen = ReadU16(zip, p + 32);
            uint localOffset = ReadU32(zip, p + 42);

            string rawName = DecodeName(zip, p + 46, nameLen, flags);
            int extraStart = p + 46 + nameLen;

            // ZIP64 extra（0x0001）中的真实大小。
            long compL = compSize == 0xFFFFFFFFu ? -1 : compSize;
            long uncompL = uncompSize == 0xFFFFFFFFu ? -1 : uncompSize;
            ParseZip64Extra(zip, extraStart, extraLen, ref compL, ref uncompL);

            byte[] data = ExtractData(zip, (int)localOffset, method, compL, uncompL, rawName);
            if (uncompL >= 0 && data.Length != uncompL)
                throw new ZipFormatException($"条目 {rawName} 解压后 {data.Length} byte 与声明 {uncompL} byte 不一致。");

            entries.Add(new ZipEntryData
            {
                RawName = rawName,
                Bytes = data,
                OrderIndex = i,
                Utf8Flag = (flags & 0x0800) != 0,
                Crc32 = crc,
                CompressedSize = compL >= 0 ? compL : -1,
                DosDate = dosDate,
                DosTime = dosTime,
                CompressionMethod = method,
                ModifiedUtc = DosToDateTime(dosDate, dosTime),
            });

            p = extraStart + extraLen + commentLen;
        }
        return entries;
    }

    private static void ParseZip64Extra(byte[] zip, int start, int len, ref long comp, ref long uncomp)
    {
        int e = start + len;
        int q = start;
        while (q + 4 <= e)
        {
            ushort tag = ReadU16(zip, q);
            ushort size = ReadU16(zip, q + 2);
            int d = q + 4;
            if (tag == 0x0001)
            {
                int cursor = d;
                if (uncomp < 0 && cursor + 8 <= d + size) { uncomp = (long)ReadU64(zip, cursor); cursor += 8; }
                if (comp < 0 && cursor + 8 <= d + size) { comp = (long)ReadU64(zip, cursor); cursor += 8; }
            }
            q = d + size;
        }
    }

    private static byte[] ExtractData(byte[] zip, int localOffset, ushort method, long compSize, long uncompSize, string name)
    {
        if (localOffset + 30 > zip.Length || ReadU32(zip, localOffset) != LocalSig)
            throw new ZipFormatException($"条目 {name} 的 local file header 签名损坏（偏移 {localOffset} byte）。");
        ushort nLen = ReadU16(zip, localOffset + 26);
        ushort eLen = ReadU16(zip, localOffset + 28);
        int dataStart = localOffset + 30 + nLen + eLen;
        if (dataStart > zip.Length) throw new ZipFormatException($"条目 {name} 数据起始越界。");

        bool isDirectory = name.EndsWith('/');
        if (isDirectory) return Array.Empty<byte>();

        long take = compSize;
        if (take < 0)
        {
            // 无描述符时回退：借助 data descriptor 无法可靠定位，这里要求 ZIP64 已提供。
            throw new ZipFormatException($"条目 {name} 压缩大小未知且无 ZIP64 信息，无法确定数据区间。");
        }
        if (dataStart + take > zip.Length) throw new ZipFormatException($"条目 {name} 数据区间超出 ZIP 容器字节范围。");

        if (method == 0)
        {
            var stored = new byte[take];
            Buffer.BlockCopy(zip, dataStart, stored, 0, (int)take);
            return stored;
        }
        if (method == 8)
        {
            using var ms = new MemoryStream(zip, dataStart, (int)take);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
        throw new ZipFormatException($"条目 {name} 使用不支持的压缩方式 {method}（仅支持 0 stored / 8 deflate）。");
    }

    private static string DecodeName(byte[] zip, int offset, int len, ushort flags)
    {
        var enc = (flags & 0x0800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding("IBM437");
        return enc.GetString(zip, offset, len);
    }

    private static DateTimeOffset DosToDateTime(ushort date, ushort time)
    {
        int year = ((date >> 9) & 0x7F) + 1980;
        int month = (date >> 5) & 0x0F;
        int day = date & 0x1F;
        int hour = (time >> 11) & 0x1F;
        int minute = (time >> 5) & 0x3F;
        int second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day is < 1 or > 31) return DateTimeOffset.MinValue;
        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
    }

    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | (uint)b[o + 3] << 24);
    private static ulong ReadU64(byte[] b, int o)
        => (ulong)ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);
}

using System.IO.Compression;
using System.Text;

namespace EpubReview.Core;

/// <summary>One ZIP central-directory entry with its *decompressed* bytes preserved.</summary>
public sealed class ZipEntry
{
    public required string FullName { get; init; }
    public required byte[] Data { get; init; }
    public required ushort CompressionMethod { get; init; }
    public required uint Crc32 { get; init; }
    public required uint CompressedSize { get; init; }
    public required uint UncompressedSize { get; init; }
    public required ushort Flags { get; init; }
    public bool IsDirectory => FullName.EndsWith('/');
    public string Sha256 => StableId.Sha256Hex(Data);
}

/// <summary>Result of a low-level ZIP read. Corruption is a distinct outcome, not an exception.</summary>
public sealed class ZipReadResult
{
    public required bool Ok { get; init; }
    public List<ZipEntry> Entries { get; init; } = new();
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public bool CrcVerified { get; init; }
}

public sealed record Diagnostic(
    string Code,
    string Message,
    string? Location = null,
    string? EntryPath = null,
    long? Offset = null)
{
    public Diagnostic At(string? entry = null, long? offset = null) =>
        new(Code, Message, Location, entry ?? EntryPath, offset ?? Offset);
}

/// <summary>
/// A deliberately small STORE/DEFLATE reader so raw central-directory facts stay
/// observable, and a deterministic STORE writer used by the export path.
/// </summary>
public static class MinimalZip
{
    private const uint LFH = 0x04034b50;
    private const uint CDH = 0x02014b50;
    private const uint EOCD = 0x06054b50;

    public static ZipReadResult Read(byte[] bytes)
    {
        var result = new ZipReadResult { Ok = true };
        try
        {
            var eocd = FindEocd(bytes);
            if (eocd < 0)
            {
                result.Ok = false;
                result.Diagnostics.Add(new Diagnostic("zip.eocd-missing",
                    "未找到 ZIP 中央目录结束标记(EOCD)，容器格式损坏。", offset: bytes.LongLength));
                return result;
            }
            ushort totalEntries = BitConverter.ToUInt16(bytes, eocd + 10);
            uint cdOffset = BitConverter.ToUInt32(bytes, eocd + 16);
            uint cdSize = BitConverter.ToUInt32(bytes, eocd + 12);
            if (cdOffset >= bytes.LongLength)
            {
                result.Ok = false;
                result.Diagnostics.Add(new Diagnostic("zip.cd-outside",
                    "中央目录偏移越出文件范围，容器格式损坏。", offset: cdOffset));
                return result;
            }

            var pos = (long)cdOffset;
            var allCrc = true;
            var seenNames = new HashSet<string>();
            for (var i = 0; i < totalEntries; i++)
            {
                if (pos + 46 > bytes.LongLength || ReadU32(bytes, pos) != CDH)
                {
                    result.Ok = false;
                    result.Diagnostics.Add(new Diagnostic("zip.cd-truncated",
                        $"中央目录条目 {i} 被截断。", offset: pos));
                    return result;
                }
                var method = BitConverter.ToUInt16(bytes, pos + 10);
                var flags = BitConverter.ToUInt16(bytes, pos + 8);
                var crc = BitConverter.ToUInt32(bytes, pos + 16);
                var compSize = BitConverter.ToUInt32(bytes, pos + 20);
                var uncompSize = BitConverter.ToUInt32(bytes, pos + 24);
                ushort nameLen = BitConverter.ToUInt16(bytes, pos + 28);
                ushort extraLen = BitConverter.ToUInt16(bytes, pos + 30);
                ushort commentLen = BitConverter.ToUInt16(bytes, pos + 32);
                uint localOffset = BitConverter.ToUInt32(bytes, pos + 42);
                var name = DecodeName(bytes, pos + 46, nameLen, flags);
                var dataPos = pos + 46 + nameLen + extraLen;

                byte[]? data = null;
                var dataDiag = TryReadLocal(bytes, localOffset, method, flags, compSize, uncompSize, name, out data);
                if (dataDiag is not null)
                {
                    result.Diagnostics.Add(dataDiag);
                    allCrc = false;
                }
                else if (data is not null)
                {
                    var actual = Crc32Algorithm.Compute(data);
                    if (actual != crc)
                    {
                        allCrc = false;
                        result.Diagnostics.Add(new Diagnostic("zip.crc-mismatch",
                            $"条目 '{name}' 的 CRC32 与中央目录不符：声明 {crc:x8}，实际 {actual:x8}。",
                            EntryPath: name));
                    }
                    result.Entries.Add(new ZipEntry
                    {
                        FullName = name,
                        Data = data,
                        CompressionMethod = method,
                        Crc32 = crc,
                        CompressedSize = compSize,
                        UncompressedSize = uncompSize,
                        Flags = flags
                    });
                }
                if (!seenNames.Add(name))
                {
                    result.Diagnostics.Add(new Diagnostic("zip.duplicate-entry",
                        $"ZIP 中出现重复条目名 '{name}'，按最后出现者建立字节映射。", EntryPath: name));
                }
                pos = dataPos + commentLen;
            }
            result.CrcVerified = allCrc && result.Diagnostics.All(d => d.Code != "zip.crc-mismatch");
            if (pos > (long)cdOffset + cdSize + 64 && cdSize > 0)
            {
                // not fatal: extra bytes tolerated, but surface them.
                result.Diagnostics.Add(new Diagnostic("zip.cd-size-mismatch",
                    "中央目录实际长度与 EOCD 声明长度不完全一致，已按条目计数解析。", offset: pos));
            }
            return result;
        }
        catch (IndexOutOfRangeException)
        {
            result.Ok = false;
            result.Diagnostics.Add(new Diagnostic("zip.truncated", "ZIP 字节流被截断，容器格式损坏。"));
            return result;
        }
    }

    private static Diagnostic? TryReadLocal(byte[] bytes, uint localOffset, ushort method, ushort flags,
        uint compSize, uint uncompSize, string name, out byte[]? data)
    {
        data = null;
        if (localOffset + 30 > bytes.LongLength || ReadU32(bytes, localOffset) != LFH)
        {
            return new Diagnostic("zip.local-missing", $"条目 '{name}' 的本地文件头缺失或越界。", EntryPath: name, Offset: localOffset);
        }
        ushort lNameLen = BitConverter.ToUInt16(bytes, localOffset + 26);
        ushort lExtraLen = BitConverter.ToUInt16(bytes, localOffset + 28);
        var dataStart = (long)localOffset + 30 + lNameLen + lExtraLen;
        var declaredCrc = BitConverter.ToUInt32(bytes, localOffset + 14);
        if ((flags & 0x08) != 0)
        {
            // Data descriptor: central directory sizes are authoritative for standard writers we emit;
            // for imported files fall back to scanning is out of scope, so require CD sizes.
        }
        if (dataStart + compSize > bytes.LongLength)
        {
            return new Diagnostic("zip.data-outside", $"条目 '{name}' 的数据区越出文件范围。", EntryPath: name, Offset: dataStart);
        }
        if (method == 0)
        {
            if (compSize != uncompSize)
                return new Diagnostic("zip.stored-size", $"STORED 条目 '{name}' 压缩/未压缩尺寸不一致。", EntryPath: name);
            data = bytes[dataStart..(int)(dataStart + compSize)];
        }
        else if (method == 8)
        {
            try
            {
                using var ms = new MemoryStream(bytes, (int)dataStart, (int)compSize);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                ds.CopyTo(outMs);
                data = outMs.ToArray();
                if (data.LongLength != uncompSize)
                    return new Diagnostic("zip.size-mismatch",
                        $"DEFLATE 条目 '{name}' 解压长度 {data.Length} 与声明 {uncompSize} 不符。", EntryPath: name);
            }
            catch (Exception ex) when (ex is InvalidDataException or ZLibException-ish())
            {
                return new Diagnostic("zip.deflate-corrupt", $"条目 '{name}' DEFLATE 数据损坏：{ex.Message}", EntryPath: name);
            }
        }
        else
        {
            return new Diagnostic("zip.unsupported-method",
                $"条目 '{name}' 使用不支持的压缩方式 {method}（仅支持 0=STORE, 8=DEFLATE），字节保留但不解析内容。", EntryPath: name);
        }
        _ = declaredCrc;
        return null;
    }

    private static long FindEocd(byte[] bytes)
    {
        var maxBack = Math.Min(bytes.Length, 66000);
        for (var i = bytes.Length - 22; i >= bytes.Length - maxBack; i--)
        {
            if (i < 0) break;
            if (ReadU32(bytes, i) == EOCD)
                return i;
        }
        return -1;
    }

    private static uint ReadU32(byte[] b, long p) =>
        (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);

    private static string DecodeName(byte[] bytes, long start, ushort len, ushort flags)
    {
        var slice = bytes[start..(start + len)];
        return (flags & 0x0800) != 0
            ? Encoding.UTF8.GetString(slice)
            : Encoding.GetEncoding("ISO-8859-1").GetString(slice);
    }

    /// <summary>Deterministic STORE-only writer. Stable byte order => stable input digest.</summary>
    public static byte[] Write(IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        var central = new List<(uint Offset, uint Crc, uint Size, ushort NameLen, byte[] NameBytes)>();
        foreach (var (name, data) in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var crc = Crc32Algorithm.Compute(data);
            var offset = (uint)ms.Position;
            ms.Write(BitConverter.GetBytes(LFH));
            ms.Write(BitConverter.GetBytes((ushort)20));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes(crc));
            ms.Write(BitConverter.GetBytes((uint)data.Length));
            ms.Write(BitConverter.GetBytes((uint)data.Length));
            ms.Write(BitConverter.GetBytes((ushort)nameBytes.Length));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(nameBytes);
            ms.Write(data);
            central.Add((offset, crc, (uint)data.Length, (ushort)nameBytes.Length, nameBytes));
        }
        var cdStart = (uint)ms.Position;
        foreach (var e in central)
        {
            ms.Write(BitConverter.GetBytes(CDH));
            ms.Write(BitConverter.GetBytes((ushort)20));
            ms.Write(BitConverter.GetBytes((ushort)20));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes(e.Crc));
            ms.Write(BitConverter.GetBytes(e.Size));
            ms.Write(BitConverter.GetBytes(e.Size));
            ms.Write(BitConverter.GetBytes(e.NameLen));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((ushort)0));
            ms.Write(BitConverter.GetBytes((uint)0));
            ms.Write(BitConverter.GetBytes(e.Offset));
            ms.Write(e.NameBytes);
        }
        var cdSize = (uint)(ms.Position - cdStart);
        ms.Write(BitConverter.GetBytes(EOCD));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)central.Count));
        ms.Write(BitConverter.GetBytes((ushort)central.Count));
        ms.Write(BitConverter.GetBytes(cdSize));
        ms.Write(BitConverter.GetBytes(cdStart));
        ms.Write(BitConverter.GetBytes((ushort)0));
        return ms.ToArray();
    }
}

internal static class Crc32Algorithm
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(byte[] data)
    {
        uint c = 0xffffffff;
        foreach (var b in data)
            c = Table[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffff;
    }
}

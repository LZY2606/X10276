using System.IO.Compression;
using System.Text;
using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public sealed record ZipReadResult(
    IReadOnlyList<ZipEntryRecord> Entries,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class ZipReader
{
    private const uint LocalFileHeaderSig = 0x04034b50;
    private const uint CentralDirSig = 0x02014b50;
    private const uint EocdSig = 0x06054b50;

    public static ZipReadResult Read(byte[] zip, long maxUncompressed, long maxEntries)
    {
        var diagnostics = new List<Diagnostic>();
        long budget = maxUncompressed;
        var entries = new List<ZipEntryRecord>();

        var eocd = FindEocd(zip);
        if (eocd < 0)
        {
            return new ZipReadResult(entries, new[]
            {
                new Diagnostic("ZIP-EOCD-MISSING",
                    "End of central directory signature not found; input is not a ZIP container.",
                    DiagnosticSeverity.Error,
                    new SourceLocation("*.zip", ByteOffset: zip.Length))
            });
        }

        ushort totalEntries = ReadU16(zip, eocd + 10);
        uint cdOffset = ReadU32(zip, eocd + 16);
        if (cdOffset >= zip.Length)
        {
            diagnostics.Add(new Diagnostic("ZIP-CD-OFFSET",
                $"Central directory offset {cdOffset} is beyond container length {zip.Length}.",
                DiagnosticSeverity.Error,
                new SourceLocation("*.zip", ByteOffset: eocd + 16)));
            return new ZipReadResult(entries, diagnostics);
        }

        long pos = cdOffset;
        for (int i = 0; i < totalEntries; i++)
        {
            if (pos + 46 > zip.Length || ReadU32(zip, pos) != CentralDirSig)
            {
                diagnostics.Add(new Diagnostic("ZIP-CD-TRUNCATED",
                    $"Central directory entry {i} is truncated or has a bad signature.",
                    DiagnosticSeverity.Error,
                    new SourceLocation("*.zip", ByteOffset: pos)));
                return new ZipReadResult(entries, diagnostics);
            }

            if (entries.Count >= maxEntries)
            {
                diagnostics.Add(new Diagnostic("ZIP-BUDGET-ENTRIES",
                    $"Entry count budget exhausted: limit {maxEntries}.",
                    DiagnosticSeverity.Error,
                    new SourceLocation("*.zip", ByteOffset: pos),
                    new Dictionary<string, string> { ["limit"] = maxEntries.ToString() }));
                return new ZipReadResult(entries, diagnostics);
            }

            ushort method = ReadU16(zip, pos + 10);
            uint crc = ReadU32(zip, pos + 16);
            uint compSize = ReadU32(zip, pos + 20);
            uint uncompSize = ReadU32(zip, pos + 24);
            ushort nameLen = ReadU16(zip, pos + 28);
            ushort extraLen = ReadU16(zip, pos + 30);
            ushort commentLen = ReadU16(zip, pos + 32);
            ushort flags = ReadU16(zip, pos + 8);
            uint localOffset = ReadU32(zip, pos + 42);
            string nameRaw = Encoding.UTF8.GetString(zip, (int)(pos + 46), nameLen);

            var localDiags = new List<Diagnostic>();
            byte[] data = Array.Empty<byte>();
            long dataOffset = -1;
            bool crcOk = false;

            if (localOffset + 30 <= zip.Length && ReadU32(zip, localOffset) == LocalFileHeaderSig)
            {
                ushort lNameLen = ReadU16(zip, localOffset + 26);
                ushort lExtraLen = ReadU16(zip, localOffset + 28);
                dataOffset = localOffset + 30 + lNameLen + lExtraLen;
                if (dataOffset + compSize <= zip.Length)
                {
            var slice = new ReadOnlySpan<byte>(zip, (int)dataOffset, (int)compSize);
                    try
                    {
                        data = Inflate(slice, method, (int)uncompSize);
                        if (uncompSize == 0 && (flags & 0x08) == 0)
                        {
                        }
                        var actualCrc = Crc32.Compute(data);
                        crcOk = actualCrc == crc;
                        if (!crcOk)
                        {
                            localDiags.Add(new Diagnostic("ZIP-CRC-MISMATCH",
                                $"CRC32 mismatch for '{nameRaw}': header {crc:X8}, computed {actualCrc:X8}.",
                                DiagnosticSeverity.Warning,
                                new SourceLocation(nameRaw, ByteOffset: dataOffset)));
                        }

                        if ((long)uncompSize > budget)
                        {
                            diagnostics.Add(new Diagnostic("ZIP-BUDGET-BYTES",
                                $"Uncompressed byte budget exhausted at '{nameRaw}': limit {maxUncompressed} B.",
                                DiagnosticSeverity.Error,
                                new SourceLocation(nameRaw, ByteOffset: dataOffset),
                                new Dictionary<string, string>
                                {
                                    ["limitBytes"] = maxUncompressed.ToString(),
                                    ["observedBytes"] = uncompSize.ToString()
                                }));
                            return new ZipReadResult(entries, diagnostics);
                        }
                        budget -= uncompSize;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        localDiags.Add(new Diagnostic("ZIP-INFLATE-FAILED",
                            $"Could not inflate '{nameRaw}': {ex.Message}",
                            DiagnosticSeverity.Warning,
                            new SourceLocation(nameRaw, ByteOffset: dataOffset)));
                    }
                }
                else
                {
                    localDiags.Add(new Diagnostic("ZIP-DATA-OUT-OF-RANGE",
                        $"Data range for '{nameRaw}' exceeds container length.",
                        DiagnosticSeverity.Error,
                        new SourceLocation(nameRaw, ByteOffset: dataOffset)));
                }
            }
            else
            {
                localDiags.Add(new Diagnostic("ZIP-LOCAL-HEADER",
                    $"Local file header for '{nameRaw}' is missing or damaged.",
                    DiagnosticSeverity.Error,
                    new SourceLocation(nameRaw, ByteOffset: localOffset)));
            }

            string sha = data.Length == 0 ? Hashing.Sha256Hex(Array.Empty<byte>()) : Hashing.Sha256Hex(data);
            entries.Add(new ZipEntryRecord(
                i, nameRaw, nameRaw, method, compSize, uncompSize, crc,
                dataOffset, crcOk, data, sha));
            diagnostics.AddRange(localDiags);

            pos += 46 + nameLen + extraLen + commentLen;
        }

        return new ZipReadResult(entries, diagnostics);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, ushort method, int expectedSize)
    {
        if (method == 0)
        {
            return compressed.ToArray();
        }
        if (method != 8)
        {
            throw new InvalidDataException($"Unsupported compression method {method}.");
        }

        using var ms = new MemoryStream(expectedSize > 0 ? expectedSize : 4096);
        using (var inner = new MemoryStream(compressed.ToArray(), writable: false))
        using (var deflate = new DeflateStream(inner, CompressionMode.Decompress))
        {
            deflate.CopyTo(ms);
        }
        return ms.ToArray();
    }

    private static int FindEocd(byte[] zip)
    {
        int start = Math.Max(0, zip.Length - 65557);
        for (int i = zip.Length - 22; i >= start; i--)
        {
            if (ReadU32(zip, i) == EocdSig) return i;
        }
        return -1;
    }

    private static ushort ReadU16(byte[] b, long o) =>
        (ushort)(b[o] | (b[o + 1] << 8));

    private static uint ReadU32(byte[] b, long o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    public static uint Compute(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}

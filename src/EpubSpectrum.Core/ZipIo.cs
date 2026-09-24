using System.IO.Compression;
using System.Text;

namespace EpubSpectrum.Core;

/// <summary>ZIP 条目字节级读写：读取保留每条目的原始字节与 CRC32。</summary>
public static class ZipIo
{
    public static (List<ZipEntryRec> Entries, List<Diag> Diags) ReadEntries(byte[] zipBytes)
    {
        var entries = new List<ZipEntryRec>();
        var diags = new List<Diag>();
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var e in zip.Entries)
            {
                using var es = e.Open();
                using var bos = new MemoryStream();
                es.CopyTo(bos);
                var bytes = bos.ToArray();
                entries.Add(new ZipEntryRec(
                    e.FullName, e.CompressedLength, e.Length,
                    Crc32Of(bytes), Hashing.Sha256Hex(bytes), bytes));
            }
        }
        catch (InvalidDataException ex)
        {
            diags.Add(new Diag(DiagCode.ZipCorrupt, DiagLevel.Error,
                "ZIP 容器结构损坏：" + ex.Message, "zip:/"));
        }
        return (entries, diags);
    }

    /// <summary>确定性打包：不压缩、固定顺序、相同输入得到字节一致的容器。</summary>
    public static byte[] Pack(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var os = entry.Open();
                os.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    public static uint Crc32Of(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }
}

/// <summary>
/// 容器路径规范化：绝不允许越出容器根。
/// 反斜杠、盘符、前导斜杠、.. 越界全部显式报告，不以默认值伪装成功。
/// </summary>
public static class ContainerPath
{
    public sealed record Result(bool Ok, string? Normalized, bool Escapes, string? Reason)
    { }

    public static Result Resolve(string baseDir, string rawHref)
    {
        if (rawHref.Contains('\\'))
            return new Result(false, null, true, $"引用 '{rawHref}' 含反斜杠，容器路径只接受正斜杠");
        if (rawHref.Length >= 2 && rawHref[1] == ':')
            return new Result(false, null, true, $"引用 '{rawHref}' 含盘符，禁止越出容器");
        var combined = rawHref.StartsWith('/')
            ? rawHref[1..]
            : (baseDir.Length == 0 ? rawHref : $"{baseDir}/{rawHref}");
        var parts = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg is "." or "")
            {
                if (seg == "." ) { /* keep? skip */ }
                continue;
            }
            if (seg == "..")
            {
                if (parts.Count == 0)
                    return new Result(false, null, true, $"路径 '{rawHref}' 规范化后越出容器根");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return new Result(true, string.Join('/', parts), false, null);
    }

    public static string DirOf(string fullPath)
    {
        var i = fullPath.LastIndexOf('/');
        return i < 0 ? "" : fullPath[..i];
    }
}

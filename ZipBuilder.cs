using System.IO.Compression;

namespace EpubReview;

public static class ZipBuilder
{
    public static readonly DateTimeOffset Epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Build(IReadOnlyDictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(kv.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = Epoch;
                using var stream = entry.Open();
                stream.Write(kv.Value);
            }
        }
        return ms.ToArray();
    }

    public static List<(string Name, byte[] Bytes)> ReadEntries(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var list = new List<(string, byte[])>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            list.Add((entry.FullName.Replace('\\', '/'), ms.ToArray()));
        }
        return list.OrderBy(e => e.Item1, StringComparer.Ordinal).ToList();
    }
}

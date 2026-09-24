using System.IO.Compression;

namespace EpubReview.Domain;

public sealed class ContainerCorruptException : Exception
{
    public ContainerCorruptException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record EpubEntry(string Path, byte[] Bytes, string ContentHash);

public sealed record EpubContainer(IReadOnlyList<EpubEntry> Entries)
{
    public EpubEntry? Find(string normalizedPath) =>
        Entries.FirstOrDefault(e => string.Equals(e.Path, normalizedPath, StringComparison.Ordinal));

    public string Digest() =>
        Hashing.Sha256Hex(string.Join('\n', Entries.OrderBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Path + " " + e.ContentHash)));

    public static EpubContainer Read(Stream zipStream)
    {
        var entries = new List<EpubEntry>();
        try
        {
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var bytes = ms.ToArray();
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                entries.Add(new EpubEntry(path, bytes, Hashing.Sha256Hex(bytes)));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ContainerCorruptException("ZIP container is not readable: " + ex.Message, ex);
        }
        if (entries.Count == 0)
            throw new ContainerCorruptException("ZIP container holds no file entries.");
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new EpubContainer(entries);
    }

    public static EpubContainer Write(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = DateTimeOffset.UnixEpoch;
                using var es = entry.Open();
                es.Write(bytes);
            }
        }
        ms.Position = 0;
        return Read(ms);
    }
}

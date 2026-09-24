using System.IO.Compression;

namespace EpubReview.Storage;

public static class ZipPatcher
{
    public static byte[] ReplaceEntry(byte[] originalZip, string entryName,
        byte[] newBytes, string action)
    {
        using var srcMs = new MemoryStream(originalZip);
        using var src = new ZipArchive(srcMs, ZipArchiveMode.Read);
        using var dstMs = new MemoryStream();
        using (var dst = new ZipArchive(dstMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            bool replaced = false;
            foreach (var entry in src.Entries)
            {
                var content = action == "delete" && entry.FullName == entryName
                    ? Array.Empty<byte>()
                    : ReadEntry(entry);

                if (entry.FullName == entryName && action != "delete")
                {
                    content = newBytes;
                    replaced = true;
                }

                if (entry.FullName == entryName && action == "delete")
                {
                    continue;
                }

                var newEntry = dst.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using var os = newEntry.Open();
                os.Write(content, 0, content.Length);
            }

            if (!replaced && action != "delete")
            {
                var newEntry = dst.CreateEntry(entryName, CompressionLevel.NoCompression);
                using var os = newEntry.Open();
                os.Write(newBytes, 0, newBytes.Length);
            }
        }
        return dstMs.ToArray();
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

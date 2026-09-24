using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EpubReview.Web.Services;

public sealed class ExportService
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void WriteProject(string path, ProjectExport export)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                WriteText(zip, "project.epub-review.json", JsonSerializer.Serialize(export, JsonOptions));
                foreach (var material in export.Materials) WriteBytes(zip, $"materials/{material.MaterialId}.bin", material.Bytes);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ProjectExport ReadProject(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = zip.GetEntry("project.epub-review.json") ?? throw new InvalidDataException("Export package lacks project.epub-review.json.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var export = JsonSerializer.Deserialize<ProjectExport>(reader.ReadToEnd(), JsonOptions) ?? throw new InvalidDataException("Export project manifest is empty.");
        var missing = export.Materials.Where(m => zip.GetEntry($"materials/{m.MaterialId}.bin") is null).Select(m => m.MaterialId).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"Export package is missing materials: {string.Join(", ", missing)}");
        return export;
    }

    private static void WriteText(ZipArchive zip, string path, string text) => WriteBytes(zip, path, new UTF8Encoding(false).GetBytes(text));
    private static void WriteBytes(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var writer = new BinaryWriter(entry.Open());
        writer.Write(bytes);
    }
}

public sealed record ProjectExport(string Schema, ProjectCase Case, IReadOnlyList<ProjectEvent> Events, IReadOnlyList<ProjectMaterial> Materials);
public sealed record ProjectCase(string CaseId, string Title, string CreatedEventId, string CreatedAt);
public sealed record ProjectEvent(string EventId, int Sequence, string Type, string OccurredAt, string ParametersVersion, string? ParentDependencyVersion, string? InputSha256, string? DependencyVersion, string PayloadJson, string Status);
public sealed record ProjectMaterial(string MaterialId, string Role, string RawPath, string? NormalizedPath, string? MediaType, long ByteLength, string Sha256, string? ParentMaterialId, string CreatedEventId, byte[] Bytes);

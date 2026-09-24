namespace EpubReview.Web.Services;

public static class ExportServiceExtensions
{
    public static byte[] WriteToBytes(this ExportService service, ProjectExport export)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epubreview.zip");
        try
        {
            service.WriteProject(path, export);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }
}

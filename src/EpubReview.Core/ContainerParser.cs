using System.Xml.Linq;

namespace EpubReview.Core;

public static class ContainerParser
{
    public const string ContainerPath = "META-INF/container.xml";

    public static ContainerRecord Parse(byte[] bytes, bool exists)
    {
        var diags = new List<Diagnostic>();
        if (!exists)
        {
            diags.Add(Diagnostic.Create(
                "CONTAINER_MISSING", Severity.Error,
                "缺少 META-INF/container.xml，无法定位 package 文档。",
                new TextLocation(ContainerPath, null, null, null)));
            return new ContainerRecord(bytes, null, Array.Empty<ContainerRootFileRecord>(), diags, null);
        }

        var doc = Xml.Parse(bytes, ContainerPath, diags);
        if (doc is null)
            return new ContainerRecord(bytes, null, Array.Empty<ContainerRootFileRecord>(), diags, null);

        var root = doc.Root;
        var rootFilesEl = root?.Element(EpubNamespaces.Container + "rootfiles");
        var records = new List<ContainerRootFileRecord>();
        foreach (var rf in rootFilesEl?.Elements(EpubNamespaces.Container + "rootfile") ?? Enumerable.Empty<XElement>())
        {
            var fullPath = rf.Attribute("full-path")?.Value;
            var mediaType = rf.Attribute("media-type")?.Value;
            var xpath = "/c:container/c:rootfiles/c:rootfile";
            NormalizedPath? resolved = null;
            if (!string.IsNullOrEmpty(fullPath))
            {
                var baseDir = fullPath.Contains('/') ? fullPath[..fullPath.LastIndexOf('/')] : "";
                resolved = Paths.NormalizedPath.Resolve(baseDir, fullPath[(fullPath.LastIndexOf('/') + 1)..]);
            }

            var escapes = resolved?.Escapes ?? false;
            if (string.IsNullOrEmpty(fullPath))
            {
                diags.Add(Diagnostic.Create("CONTAINER_ROOTFILE_NO_PATH", Severity.Error,
                    "rootfile 缺少 full-path 属性。", Xml.Loc(rf, ContainerPath, xpath)));
            }
            if (escapes)
            {
                diags.Add(Diagnostic.Create("CONTAINER_PATH_TRAVERSAL", Severity.Error,
                    $"rootfile 路径 \"{fullPath}\" 规范化后越出容器根。",
                    Xml.Loc(rf, ContainerPath, xpath)));
            }
            records.Add(new ContainerRootFileRecord(
                fullPath, resolved?.Normalized, escapes, mediaType, Xml.Loc(rf, ContainerPath, xpath)));
        }

        if (records.Count == 0)
        {
            diags.Add(Diagnostic.Create("CONTAINER_NO_ROOTFILE", Severity.Error,
                "container.xml 未声明任何 package 文档（rootfile）。",
                Xml.Loc(rootFilesEl, ContainerPath, "/c:container/c:rootfiles")));
        }

        var version = root?.Attribute("version")?.Value;
        return new ContainerRecord(bytes, version, records, diags, Xml.Loc(root, ContainerPath, "/c:container"));
    }
}

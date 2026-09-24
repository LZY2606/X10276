using System.Text;

namespace EpubReview.Core;

public sealed record ResolvedPath(string? NormalizedPath, bool EscapesContainer, string? FailureReason)
{
    public string? Fragment { get; init; }
}

public sealed class ContainerPathResolver
{
    public ResolvedPath Resolve(string baseEntryPath, string rawReference)
    {
        var href = rawReference;
        string? fragment = null;
        var hash = href.IndexOf('#');
        if (hash >= 0)
        {
            fragment = href[(hash + 1)..];
            href = href[..hash];
        }

        if (href.Length == 0)
        {
            return new ResolvedPath(NormalizeEntry(baseEntryPath), false, null) { Fragment = fragment };
        }

        var combined = Uri.TryCreate(new Uri($"file:///{baseEntryPath.Replace("#", "%23", StringComparison.Ordinal)}", UriKind.Absolute), href, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
            : null;
        if (combined is null || combined.Contains('\\', StringComparison.Ordinal))
        {
            return new ResolvedPath(null, true, "PATH.UNPARSEABLE") { Fragment = fragment };
        }

        var normalized = NormalizeLogical(combined, out var escapes);
        return new ResolvedPath(escapes ? null : NormalizeEntry(normalized), escapes, escapes ? "PATH.ESCAPE" : null) { Fragment = fragment };
    }

    public static string NormalizeEntry(string entryPath)
    {
        var parts = entryPath.Replace('\\', '/').Split('/');
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return string.Join('/', stack);
    }

    private static string NormalizeLogical(string entryPath, out bool escapes)
    {
        escapes = false;
        var stack = new List<string>();
        foreach (var part in entryPath.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (stack.Count == 0) escapes = true;
                else stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        var builder = new StringBuilder();
        for (var i = 0; i < stack.Count; i++)
        {
            if (i > 0) builder.Append('/');
            builder.Append(stack[i]);
        }
        return builder.ToString();
    }
}

namespace SourceMapChain.Core;

public static class SourcePaths
{
    public static string Resolve(string? sourceRoot, string source)
    {
        var normalizedSource = NormalizeSlashes(source);
        if (string.IsNullOrEmpty(sourceRoot) || HasScheme(normalizedSource) || normalizedSource.StartsWith('/'))
        {
            return Collapse(normalizedSource.TrimStart('/'));
        }

        var root = NormalizeSlashes(sourceRoot);
        var combined = root.EndsWith('/') ? root + normalizedSource : root + "/" + normalizedSource;
        return Collapse(combined);
    }

    public static string NormalizeSlashes(string value) => value.Replace('\\', '/');

    private static bool HasScheme(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == ':')
            {
                return i > 0;
            }

            if (path[i] == '/')
            {
                return false;
            }
        }

        return false;
    }

    private static string Collapse(string path)
    {
        var parts = path.Split('/');
        var stack = new List<string>();
        var leadingSlash = path.StartsWith('/');

        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == ".." && stack.Count > 0 && stack[^1] != "..")
            {
                stack.RemoveAt(stack.Count - 1);
            }
            else if (part == ".." && !leadingSlash)
            {
                stack.Add(part);
            }
            else if (part != "..")
            {
                stack.Add(part);
            }
        }

        var collapsed = string.Join('/', stack);
        if (leadingSlash)
        {
            return "/" + collapsed;
        }

        return collapsed.Length == 0 ? "." : collapsed;
    }
}

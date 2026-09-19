namespace SourceMapChains.Core;

/// <summary>
/// Fixed text behaviors: BOM is stripped (and recorded), lines split on '\n',
/// a trailing newline does not create a phantom line, a final line without a
/// newline is still a line, and '\r' is kept as content.
/// </summary>
public static class TextLines
{
    public static string StripBom(string content, out bool hadBom)
    {
        hadBom = content.Length > 0 && content[0] == '﻿';
        return hadBom ? content[1..] : content;
    }

    public static IReadOnlyList<string> Split(string content)
    {
        if (content.Length == 0) return Array.Empty<string>();
        var parts = content.Split('\n');
        if (content.EndsWith('\n')) parts = parts[..^1];
        return parts;
    }

    public static int LineCount(string content) => Split(content).Count;

    /// <summary>Length of a line in the given column semantics.</summary>
    public static int ColumnLength(string line, ColumnSemantics semantics) =>
        semantics == ColumnSemantics.Utf16 ? line.Length : CountScalars(line);

    private static int CountScalars(string line)
    {
        var count = 0;
        foreach (var _ in line.EnumerateRunes()) count++;
        return count;
    }
}

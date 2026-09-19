using System.Text;

namespace SourceMapChain.Core;

public static class TextLines
{
    public static string NormalizeInput(string? value)
    {
        var text = value ?? string.Empty;
        if (text.StartsWith("\uFEFF", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    public static IReadOnlyList<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    public static bool EndsWithNewline(string text) => text.Length > 0 && text[^1] == '\n';

    public static (int Line, int Column) PositionAtUtf16(string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var line = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        var starts = LineStarts(text);
        return (line, offset - starts[line]);
    }

    public static int OffsetAtUtf16(string text, int line, int column)
    {
        var starts = LineStarts(text);
        if (line < 0 || line >= starts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
        var offset = starts[line] + column;
        if (offset > end)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return offset;
    }

    public static int ScalarColumnFromUtf16Column(string lineText, int utf16Column)
    {
        var column = 0;
        for (var i = 0; i < utf16Column && i < lineText.Length; i++)
        {
            if (!char.IsHighSurrogate(lineText[i]) || i + 1 >= lineText.Length || !char.IsLowSurrogate(lineText[i + 1]))
            {
                column++;
            }
        }

        return column;
    }

    public static int Utf16ColumnFromScalarColumn(string lineText, int scalarColumn)
    {
        var scalar = 0;
        var i = 0;
        while (i < lineText.Length && scalar < scalarColumn)
        {
            if (char.IsHighSurrogate(lineText[i]) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
            {
                i += 2;
            }
            else
            {
                i++;
            }

            scalar++;
        }

        if (scalar != scalarColumn)
        {
            throw new ArgumentOutOfRangeException(nameof(scalarColumn));
        }

        return i;
    }

    public static string LineSlice(string text, int line)
    {
        var starts = LineStarts(text);
        var start = starts[line];
        var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
        return text[start..end];
    }

    public static string Sha256Fingerprint(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

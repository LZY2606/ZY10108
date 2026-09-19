using System.Text.Json;

namespace SourceMapChains.Core;

/// <summary>Column counting semantics for a file or map.</summary>
public enum ColumnSemantics
{
    Utf16 = 0,
    UnicodeScalar = 1,
}

/// <summary>One decoded mapping segment. Positions are 0-based.</summary>
public sealed record Segment(int GenLine, int GenCol, int SrcIndex, int SrcLine, int SrcCol, int NameIndex);

/// <summary>A parsed source map document.</summary>
public sealed class SourceMapDocument
{
    public int Version { get; set; }
    public string? File { get; set; }
    public string? SourceRoot { get; set; }
    public List<string> Sources { get; set; } = new();
    public List<string?>? SourcesContent { get; set; }
    public List<string> Names { get; set; } = new();
    public string Mappings { get; set; } = "";
    public ColumnSemantics Semantics { get; set; } = ColumnSemantics.Utf16;

    private List<List<Segment>>? _lines;

    /// <summary>Decoded segments grouped by generated line (empty list = line with no segments).</summary>
    public IReadOnlyList<IReadOnlyList<Segment>> Lines => _lines ??= DecodeMappings();

    public static SourceMapDocument Parse(string json, ColumnSemantics semantics = ColumnSemantics.Utf16)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var map = new SourceMapDocument();
        if (root.TryGetProperty("version", out var v)) map.Version = v.GetInt32();
        if (root.TryGetProperty("file", out var f)) map.File = f.GetString();
        if (root.TryGetProperty("sourceRoot", out var sr)) map.SourceRoot = sr.GetString();
        if (root.TryGetProperty("sources", out var s))
            map.Sources = s.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (root.TryGetProperty("sourcesContent", out var sc))
            map.SourcesContent = sc.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList();
        if (root.TryGetProperty("names", out var n))
            map.Names = n.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (root.TryGetProperty("mappings", out var m)) map.Mappings = m.GetString() ?? "";
        if (root.TryGetProperty("x_columnSemantics", out var cs) &&
            string.Equals(cs.GetString(), "unicodeScalar", StringComparison.OrdinalIgnoreCase))
            map.Semantics = ColumnSemantics.UnicodeScalar;
        if (map.Version != 3)
            throw new FormatException($"Unsupported source map version {map.Version}.");
        map.Semantics = semantics == ColumnSemantics.UnicodeScalar ? semantics : map.Semantics;
        return map;
    }

    private List<List<Segment>> DecodeMappings()
    {
        var lines = new List<List<Segment>>();
        var srcIndex = 0;
        var srcLine = 0;
        var srcCol = 0;
        var nameIndex = 0;
        var genLine = 0;
        foreach (var lineText in Mappings.Split(';'))
        {
            var segments = new List<Segment>();
            var genCol = 0;
            if (lineText.Length > 0)
            {
                foreach (var raw in lineText.Split(','))
                {
                    if (raw.Length == 0) continue;
                    var values = Vlq.Decode(raw);
                    if (values.Length is not (1 or 4 or 5))
                        throw new FormatException($"Segment has {values.Length} fields, expected 1, 4 or 5.");
                    genCol += values[0];
                    if (values.Length == 1)
                    {
                        // Unmapped segment: generated-only, kept with SrcIndex = -1.
                        segments.Add(new Segment(genLine, genCol, -1, -1, -1, -1));
                        continue;
                    }
                    srcIndex += values[1];
                    srcLine += values[2];
                    srcCol += values[3];
                    var name = -1;
                    if (values.Length == 5)
                    {
                        nameIndex += values[4];
                        name = nameIndex;
                    }
                    segments.Add(new Segment(genLine, genCol, srcIndex, srcLine, srcCol, name));
                }
            }
            lines.Add(segments);
            genLine++;
        }
        return lines;
    }

    /// <summary>Greatest-lower-bound segment lookup on a generated line.</summary>
    public Segment? FindSegment(int genLine, int genCol)
    {
        if (genLine < 0 || genLine >= Lines.Count) return null;
        var line = Lines[genLine];
        Segment? best = null;
        foreach (var seg in line)
        {
            if (seg.GenCol <= genCol) best = seg;
            else break;
        }
        return best;
    }
}

using System.Text.Json;

namespace SourceMapChain.Core;

public static class SourceMapParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static DecodedSourceMap Decode(string mapJson, string outputText, string stageId)
    {
        SourceMapInput? map;
        try
        {
            map = JsonSerializer.Deserialize<SourceMapInput>(mapJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SourceMapException($"Invalid source map JSON: {ex.Message}");
        }

        if (map is null)
        {
            throw new SourceMapException("Source map JSON must be an object.");
        }

        if (map.Version != 3)
        {
            throw new SourceMapException($"Unsupported source map version {map.Version}; only version 3 is supported.");
        }

        var encoding = NormalizeEncoding(map.XColumnEncoding);
        var segments = DecodeMappings(map, outputText, encoding, stageId);
        ValidateReferences(map, segments, outputText, encoding, stageId);
        return new DecodedSourceMap { Raw = map, Segments = segments };
    }

    public static string NormalizeEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return "utf16";
        }

        return encoding.Trim().ToLowerInvariant() switch
        {
            "utf16" or "utf-16" => "utf16",
            "unicode-scalars" or "unicode_scalars" or "utf32" or "utf-32" => "unicode-scalars",
            _ => throw new SourceMapException($"Unsupported xColumnEncoding '{encoding}'.")
        };
    }

    public static string Encoding(DecodedSourceMap map) => NormalizeEncoding(map.Raw.XColumnEncoding);

    private static List<MappingSegment> DecodeMappings(SourceMapInput map, string outputText, string encoding, string stageId)
    {
        var segments = new List<MappingSegment>();
        var generatedColumn = 0;
        var sourceIndex = 0;
        var originalLine = 0;
        var originalColumn = 0;
        var nameIndex = 0;
        var lineCount = TextLines.LineStarts(outputText).Count;
        var mappingLines = map.Mappings.Split(';');

        for (var generatedLine = 0; generatedLine < mappingLines.Length; generatedLine++)
        {
            generatedColumn = 0;
            if (generatedLine >= lineCount)
            {
                throw Error(stageId, "mappings-line-outside-output", generatedLine, null, "Mapping line does not exist in generated output.");
            }

            var line = mappingLines[generatedLine];
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(",", StringComparison.Ordinal) || line.EndsWith(",", StringComparison.Ordinal) || line.Contains(",,", StringComparison.Ordinal))
            {
                throw Error(stageId, "empty-vlq-segment", generatedLine, segments.Count, "Comma-separated VLQ segment must not be empty.");
            }

            var tokens = line.Split(',');
            for (var tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
            {
                var token = tokens[tokenIndex];
                if (token.Length == 0)
                {
                    throw Error(stageId, "empty-vlq-segment", generatedLine, segments.Count, "Comma-separated VLQ segment must not be empty.");
                }

                var fields = VlqCodec.DecodeSegment(token);
                generatedColumn += fields[0];
                if (generatedColumn < 0)
                {
                    throw Error(stageId, "negative-generated-column", generatedLine, segments.Count, "Generated column delta points before column zero.");
                }

                if (segments.Count > 0 && segments[^1].GeneratedLine == generatedLine && generatedColumn < segments[^1].GeneratedColumn)
                {
                    throw Error(stageId, "generated-column-not-monotonic", generatedLine, segments.Count, "Generated columns must be non-decreasing within a line.");
                }

                    var internalColumn = generatedColumn;
                    if (encoding == "unicode-scalars")
                    {
                        var lineText = TextLines.LineSlice(outputText, generatedLine);
                        internalColumn = TextLines.Utf16ColumnFromScalarColumn(lineText, generatedColumn);
                }

                var segment = new MappingSegment
                {
                    GeneratedLine = generatedLine,
                    GeneratedColumn = internalColumn
                };

                if (fields.Length >= 4)
                {
                    sourceIndex += fields[1];
                    originalLine += fields[2];
                    originalColumn += fields[3];
                    if (sourceIndex < 0 || originalLine < 0 || originalColumn < 0)
                    {
                        throw Error(stageId, "negative-mapped-field", generatedLine, segments.Count, "Source index and original position must not become negative.");
                    }

                    segment.SourceIndex = sourceIndex;
                    segment.OriginalLine = originalLine;
                    segment.OriginalColumn = originalColumn;
                }

                if (fields.Length == 5)
                {
                    nameIndex += fields[4];
                    if (nameIndex < 0)
                    {
                        throw Error(stageId, "negative-name-index", generatedLine, segments.Count, "Name index must not become negative.");
                    }

                    segment.NameIndex = nameIndex;
                }

                segments.Add(segment);
            }
        }

        return segments;
    }

    private static void ValidateReferences(SourceMapInput map, List<MappingSegment> segments, string outputText, string encoding, string stageId)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var generatedLineText = TextLines.LineSlice(outputText, segment.GeneratedLine);
            if (segment.GeneratedColumn > generatedLineText.Length)
            {
                throw Error(stageId, "generated-column-outside-line", segment.GeneratedLine, i, "Generated column exceeds the generated line boundary.");
            }

            if (segment.SourceIndex is { } sourceIndex)
            {
                if (sourceIndex >= map.Sources.Count)
                {
                    throw Error(stageId, "source-index-out-of-range", segment.GeneratedLine, i, "Segment source index is outside the sources array.");
                }

                if (segment.NameIndex is { } name && (map.Names is null || name >= map.Names.Count))
                {
                    throw Error(stageId, "name-index-out-of-range", segment.GeneratedLine, i, "Segment name index is outside the names array.");
                }

                var content = map.SourcesContent is { } contents && sourceIndex < contents.Count ? contents[sourceIndex] : null;
                if (content is not null)
                {
                    var normalized = TextLines.NormalizeInput(content);
                    var lineStarts = TextLines.LineStarts(normalized);
                    if (segment.OriginalLine!.Value >= lineStarts.Count)
                    {
                        throw Error(stageId, "original-line-outside-source", segment.GeneratedLine, i, "Original line is outside sourcesContent.");
                    }

                    var lineText = TextLines.LineSlice(normalized, segment.OriginalLine.Value);
                    var originalDeclared = segment.OriginalColumn!.Value;
                    var originalUtf16 = originalDeclared;
                    if (encoding == "unicode-scalars")
                    {
                        originalUtf16 = TextLines.Utf16ColumnFromScalarColumn(lineText, originalDeclared);
                        segment.OriginalColumn = originalUtf16;
                    }

                    if (originalUtf16 > lineText.Length)
                    {
                        throw Error(stageId, "original-column-outside-source", segment.GeneratedLine, i, "Original column exceeds the source line boundary.");
                    }
                }
            }
        }
    }

    private static SourceMapException Error(string stageId, string code, int line, int? segmentIndex, string message) =>
        new($"{stageId}: {message} (code {code}, generated line {line}, segment {segmentIndex?.ToString() ?? "-"})");
}

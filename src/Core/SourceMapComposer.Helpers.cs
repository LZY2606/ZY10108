using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SourceMapChain.Core;

public sealed partial class SourceMapComposer
{
    public sealed class Stage
    {
        public required StageInput Input { get; init; }
        public required string Output { get; init; }
        public required DecodedSourceMap Map { get; init; }
        public required List<string> ResolvedSources { get; init; }
        public required List<SourceResolution> Resolutions { get; init; }
        public required IReadOnlyList<RevisionProposal> Revisions { get; init; }
        public string? PathRevision { get; init; }
        public IReadOnlyList<RevisionProposal> SegmentRevisions => Revisions.Where(revision => revision.Kind == "segment").ToList();
        public string OutputPath => Input.Id;
    }

    private static Stage BuildStage(StageInput input, IReadOnlyList<RevisionProposal> revisions)
    {
        var output = TextLines.NormalizeInput(input.OutputText);
        var candidateMapJson = Parse.MapJsonWithRevisions(input, revisions, output, out var pathRevision);
        var map = SourceMapParser.Decode(candidateMapJson, output, input.Id);
        var resolved = map.Raw.Sources.Select(source => SourcePaths.Resolve(map.Raw.SourceRoot, source)).ToList();
        var resolutions = map.Raw.Sources.Select((source, index) =>
        {
            var content = map.Raw.SourcesContent is { } contents && index < contents.Count ? contents[index] : null;
            return new SourceResolution
            {
                StageId = input.Id,
                SourceIndex = index,
                DeclaredPath = source,
                ResolvedPath = resolved[index],
                Content = content,
                ContentSha256 = content is null ? null : TextLines.Sha256Fingerprint(TextLines.NormalizeInput(content))
            };
        }).ToList();

        return new Stage
        {
            Input = input,
            Output = output,
            Map = map,
            ResolvedSources = resolved,
            Resolutions = resolutions,
            Revisions = revisions,
            PathRevision = pathRevision
        };
    }

    private static int FindSegmentIndex(Stage stage, int line, int column)
    {
        var result = -1;
        for (var i = 0; i < stage.Map.Segments.Count; i++)
        {
            var segment = stage.Map.Segments[i];
            if (segment.GeneratedLine > line)
            {
                break;
            }

            if (segment.GeneratedLine == line && segment.GeneratedColumn <= column)
            {
                result = i;
            }
        }

        return result;
    }

    private static void EnsureGeneratedPosition(string text, int line, int column)
    {
        var starts = TextLines.LineStarts(text);
        if (line < 0 || line >= starts.Count)
        {
            throw new SourceMapException($"Line {line} is outside the generated text.");
        }

        var lineText = TextLines.LineSlice(text, line);
        if (column < 0 || column > lineText.Length)
        {
            throw new SourceMapException($"Column {column} is outside line {line}.");
        }
    }

    private static int ToInternalColumn(string text, int line, int column, string encoding)
    {
        if (encoding == "utf16")
        {
            return column;
        }

        return TextLines.Utf16ColumnFromScalarColumn(TextLines.LineSlice(text, line), column);
    }

    private static int ToDisplayColumn(string text, int line, int utf16Column, string encoding)
    {
        if (encoding == "utf16")
        {
            return utf16Column;
        }

        return TextLines.ScalarColumnFromUtf16Column(TextLines.LineSlice(text, line), utf16Column);
    }

    private string GetContent(Stage stage, string resolvedPath)
    {
        var outputStage = stages.FirstOrDefault(candidate => candidate.OutputPath == resolvedPath);
        if (outputStage is not null)
        {
            return outputStage.Output;
        }

        for (var i = 0; i < stage.ResolvedSources.Count; i++)
        {
            if (stage.ResolvedSources[i] == resolvedPath)
            {
                var content = stage.Map.Raw.SourcesContent is { } contents && i < contents.Count ? contents[i] : null;
                return content is null ? string.Empty : TextLines.NormalizeInput(content);
            }
        }

        return string.Empty;
    }

    private IEnumerable<(string StageId, string ResolvedPath)> SourceLinks(Stage stage)
    {
        var index = stages.IndexOf(stage);
        foreach (var resolved in stage.ResolvedSources.Distinct())
        {
            for (var previous = index - 1; previous >= 0; previous--)
            {
                if (stages[previous].OutputPath == resolved)
                {
                    yield return (stages[previous].Input.Id, resolved);
                }
            }
        }
    }

    private (int Line, int Column) SegmentGeneratedEnd(Stage stage, int segmentIndex)
    {
        var segment = stage.Map.Segments[segmentIndex];
        for (var i = segmentIndex + 1; i < stage.Map.Segments.Count; i++)
        {
            var next = stage.Map.Segments[i];
            if (next.GeneratedLine != segment.GeneratedLine || next.SourceIndex is null)
            {
                continue;
            }

            return (next.GeneratedLine, next.GeneratedColumn);
        }

        var lineText = TextLines.LineSlice(stage.Output, segment.GeneratedLine);
        return (segment.GeneratedLine, lineText.Length);
    }

    private (int Line, int Column) SegmentOriginalEnd(Stage stage, int segmentIndex)
    {
        var segment = stage.Map.Segments[segmentIndex];
        if (segment.SourceIndex is null || segment.OriginalLine is null)
        {
            return (segment.GeneratedLine, segment.GeneratedColumn);
        }

        var path = stage.ResolvedSources[segment.SourceIndex.Value];
        for (var i = segmentIndex + 1; i < stage.Map.Segments.Count; i++)
        {
            var next = stage.Map.Segments[i];
            if (next.SourceIndex == segment.SourceIndex && next.OriginalLine is not null && next.OriginalColumn is not null)
            {
                return (next.OriginalLine.Value, next.OriginalColumn.Value);
            }
        }

        var content = GetContent(stage, path);
        if (content.Length == 0 || segment.OriginalLine.Value >= TextLines.LineStarts(content).Count)
        {
            return (segment.OriginalLine.Value, segment.OriginalColumn!.Value);
        }

        return (segment.OriginalLine.Value, TextLines.LineSlice(content, segment.OriginalLine.Value).Length);
    }

    private static bool Intersects(int aLine, int aColumn, int aEndLine, int aEndColumn,
                                  int bLine, int bColumn, int bEndLine, int bEndColumn)
    {
        return Compare(aLine, aColumn, bEndLine, bEndColumn) < 0 &&
               Compare(bLine, bColumn, aEndLine, aEndColumn) < 0;
    }

    private static int Compare(int line, int column, int otherLine, int otherColumn) =>
        line == otherLine ? column.CompareTo(otherColumn) : line.CompareTo(otherLine);

    private static List<(int, int, int, int)> MergeRanges(List<(int, int, int, int)> ranges)
    {
        var sorted = ranges.OrderBy(range => range.Item1).ThenBy(range => range.Item2).ToList();
        var merged = new List<(int, int, int, int)>();
        foreach (var range in sorted)
        {
            if (merged.Count == 0 || Compare(range.Item1, range.Item2, merged[^1].Item3, merged[^1].Item4) >= 0)
            {
                merged.Add(range);
            }
            else if (Compare(range.Item3, range.Item4, merged[^1].Item3, merged[^1].Item4) > 0)
            {
                merged[^1] = (merged[^1].Item1, merged[^1].Item2, range.Item3, range.Item4);
            }
        }

        return merged;
    }

    private static class Parse
    {
        public static string MapJsonWithRevisions(StageInput input, IReadOnlyList<RevisionProposal> revisions, string output, out string? pathRevision)
        {
            var raw = System.Text.Json.JsonDocument.Parse(input.MapJson);
            var root = raw.RootElement.Clone();
            pathRevision = null;
            var mutableRoot = JsonNode.Parse(input.MapJson, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();

            foreach (var revision in revisions.Where(item => item.Kind == "path-rule"))
            {
                pathRevision = revision.SourcePathRule;
                if (string.IsNullOrWhiteSpace(revision.SourcePathRule))
                {
                    continue;
                }

                var rule = revision.SourcePathRule;
                if (rule.StartsWith("sourceRoot:", StringComparison.Ordinal))
                {
                    mutableRoot["sourceRoot"] = rule["sourceRoot:".Length..];
                }
                else if (rule.StartsWith("source[", StringComparison.Ordinal) && rule.Contains("]:", StringComparison.Ordinal))
                {
                    var close = rule.IndexOf("]:", StringComparison.Ordinal);
                    var indexText = rule["source[".Length..close];
                    var sourcesNode = mutableRoot["sources"] ?? mutableRoot["Sources"];
                    if (int.TryParse(indexText, out var index) && sourcesNode is JsonArray sourcesArray &&
                        index >= 0 && index < sourcesArray.Count)
                    {
                        sourcesArray[index] = rule[(close + 2)..];
                    }
                }

                root = System.Text.Json.JsonDocument.Parse(mutableRoot.ToJsonString()).RootElement.Clone();
            }

            var decoded = SourceMapParser.Decode(root.GetRawText(), output, input.Id);
            foreach (var revision in revisions.Where(item => item.Kind == "segment"))
            {
                if (revision.SegmentIndex < 0 || revision.SegmentIndex >= decoded.Segments.Count)
                {
                    throw new SourceMapException($"Revision '{revision.Id}' points to segment {revision.SegmentIndex}, which does not exist.");
                }

                var segment = decoded.Segments[revision.SegmentIndex];
                if (revision.SourceIndex is { } sourceIndex)
                {
                    if (sourceIndex < 0 || sourceIndex >= decoded.Raw.Sources.Count)
                    {
                        throw new SourceMapException($"Revision '{revision.Id}' source index is outside sources.");
                    }

                    if (segment.SourceIndex is null)
                    {
                        segment.SourceIndex = sourceIndex;
                        segment.OriginalLine ??= 0;
                        segment.OriginalColumn ??= 0;
                    }
                    else
                    {
                        segment.SourceIndex = sourceIndex;
                    }
                }

                if (revision.OriginalLine is { } originalLine)
                {
                    segment.OriginalLine = originalLine;
                }

                if (revision.OriginalColumn is { } originalColumn)
                {
                    segment.OriginalColumn = originalColumn;
                }

                if (segment.SourceIndex is not null && segment.OriginalLine is not null && segment.OriginalColumn is not null)
                {
                    var revisionEncoding = SourceMapParser.NormalizeEncoding(decoded.Raw.XColumnEncoding);
                    var content = decoded.Raw.SourcesContent is { } contents && segment.SourceIndex.Value < contents.Count
                        ? contents[segment.SourceIndex.Value]
                        : null;
                    if (content is not null)
                    {
                        var normalized = TextLines.NormalizeInput(content);
                        var lineText = TextLines.LineSlice(normalized, segment.OriginalLine.Value);
                        var internalColumn = revisionEncoding == "unicode-scalars"
                            ? TextLines.Utf16ColumnFromScalarColumn(lineText, segment.OriginalColumn.Value)
                            : segment.OriginalColumn.Value;
                        if (internalColumn > lineText.Length)
                        {
                            throw new SourceMapException($"Revision '{revision.Id}' places the segment outside its source line.");
                        }
                    }
                }
            }

            var encoding = SourceMapParser.NormalizeEncoding(decoded.Raw.XColumnEncoding);
            var mappings = EncodeMappings(decoded.Segments, output, encoding, stage =>
            {
                var content = stage.SourceIndex is { } sourceIndex && decoded.Raw.SourcesContent is { } contents && sourceIndex < contents.Count
                    ? contents[sourceIndex]
                    : null;
                return content is null ? string.Empty : TextLines.NormalizeInput(content);
            });

            var finalObject = System.Text.Json.JsonDocument.Parse(root.GetRawText()).RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone());
            finalObject["mappings"] = System.Text.Json.JsonSerializer.SerializeToElement(mappings);
            return Json.Build(finalObject).GetRawText();
        }

        private static string EncodeMappings(IReadOnlyList<MappingSegment> segments, string output, string encoding, Func<MappingSegment, string> sourceContent)
        {
            var lineCount = segments.Count == 0 ? 0 : segments.Max(segment => segment.GeneratedLine) + 1;
            var lines = Enumerable.Repeat(string.Empty, lineCount).ToList();
            var previousGeneratedColumns = new int[lineCount];
            var sourceIndex = 0;
            var originalLine = 0;
            var originalColumn = 0;
            var nameIndex = 0;

            foreach (var segment in segments)
            {
                var generatedDeclared = encoding == "unicode-scalars"
                    ? TextLines.ScalarColumnFromUtf16Column(TextLines.LineSlice(output, segment.GeneratedLine), segment.GeneratedColumn)
                    : segment.GeneratedColumn;
                var values = new List<int>();
                values.Add(generatedDeclared - previousGeneratedColumns[segment.GeneratedLine]);
                previousGeneratedColumns[segment.GeneratedLine] = generatedDeclared;

                if (segment.SourceIndex is { } currentSource && segment.OriginalLine is { } currentLine && segment.OriginalColumn is { } currentColumn)
                {
                    var originalDeclared = currentColumn;
                    if (encoding == "unicode-scalars")
                    {
                        originalDeclared = TextLines.ScalarColumnFromUtf16Column(
                            TextLines.LineSlice(sourceContent(segment), currentLine), currentColumn);
                    }

                    values.Add(currentSource - sourceIndex);
                    values.Add(currentLine - originalLine);
                    values.Add(originalDeclared - originalColumn);
                    sourceIndex = currentSource;
                    originalLine = currentLine;
                    originalColumn = originalDeclared;

                    if (segment.NameIndex is { } currentName)
                    {
                        values.Add(currentName - nameIndex);
                        nameIndex = currentName;
                    }
                }

                lines[segment.GeneratedLine] = lines[segment.GeneratedLine].Length == 0
                    ? VlqCodec.EncodeSegment(values)
                    : lines[segment.GeneratedLine] + "," + VlqCodec.EncodeSegment(values);
            }

            return string.Join(';', lines);
        }
    }

    private static class Json
    {
        public static JsonElement String(string value) => System.Text.Json.JsonSerializer.SerializeToElement(value);

        public static JsonElement Array(IEnumerable<string> values)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (var value in values)
                {
                    writer.WriteStringValue(value);
                }

                writer.WriteEndArray();
            }

            return System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement.Clone();
        }

        public static JsonElement Build(Dictionary<string, JsonElement> properties)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Key);
                    property.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement.Clone();
        }
    }
}

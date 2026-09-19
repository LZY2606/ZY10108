using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceMapChain.Core;

public sealed partial class SourceMapComposer
{
    public const string RuleVersionValue = "source-map-chain/v1";

    private readonly List<Stage> stages;

    public SourceMapComposer(IEnumerable<StageInput> inputs, IReadOnlyList<RevisionProposal>? revisions = null)
    {
        var revisionList = revisions ?? [];
        stages = inputs
            .Select(input => BuildStage(input, revisionList.Where(revision => revision.StageId == input.Id).ToList()))
            .ToList();
    }

    public IReadOnlyList<Stage> Stages => stages;

    public List<StageFingerprint> Fingerprints() =>
        stages.Select(stage => new StageFingerprint
        {
            StageId = stage.Input.Id,
            OutputSha256 = TextLines.Sha256Fingerprint(stage.Input.OutputText),
            MapSha256 = TextLines.Sha256Fingerprint(stage.Input.MapJson),
            RuleVersion = RuleVersionValue,
            SourceFingerprints = stage.Resolutions.Select(resolution => new SourceFingerprint
            {
                SourceIndex = resolution.SourceIndex,
                ResolvedPath = resolution.ResolvedPath,
                Sha256 = resolution.ContentSha256
            }).ToList()
        }).ToList();

    public string DeterministicSummary()
    {
        var builder = new StringBuilder();
        builder.Append(RuleVersionValue).Append('\n');
        foreach (var stage in stages)
        {
            builder.Append("stage:").Append(stage.Input.Id)
                .Append('|').Append(TextLines.Sha256Fingerprint(stage.Input.OutputText))
                .Append('|').Append(TextLines.Sha256Fingerprint(stage.Input.MapJson))
                .Append('|').Append(SourceMapParser.Encoding(stage.Map))
                .Append('|').Append(stage.PathRevision ?? "-")
                .Append('\n');
        }

        foreach (var revision in stages.SelectMany(stage => stage.SegmentRevisions).OrderBy(revision => revision.Id))
        {
            builder.Append("revision:").Append(revision.Id)
                .Append('|').Append(revision.StageId)
                .Append('|').Append(revision.SegmentIndex)
                .Append('|').Append(revision.SourceIndex?.ToString() ?? "-")
                .Append('|').Append(revision.OriginalLine?.ToString() ?? "-")
                .Append('|').Append(revision.OriginalColumn?.ToString() ?? "-")
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public PositionTrace TraceFinalPosition(int line, int column, string inputEncoding = "utf16")
    {
        if (stages.Count == 0)
        {
            throw new SourceMapException("At least one stage is required.");
        }

        var last = stages[^1];
        var internalLine = line;
        var internalColumn = ToInternalColumn(last.Output, internalLine, column, inputEncoding);
        EnsureGeneratedPosition(last.Output, internalLine, internalColumn);

        var trace = new PositionTrace();
        trace.Hops.Add(new TraceHop
        {
            StageId = last.Input.Id,
            StageLabel = last.Input.Label,
            Line = internalLine,
            Column = ToDisplayColumn(last.Output, internalLine, internalColumn, SourceMapParser.Encoding(last.Map)),
            ColumnEncoding = SourceMapParser.Encoding(last.Map)
        });

        for (var stageIndex = stages.Count - 1; stageIndex >= 0; stageIndex--)
        {
            var stage = stages[stageIndex];
            var segmentIndex = FindSegmentIndex(stage, internalLine, internalColumn);
            if (segmentIndex < 0)
            {
                trace.Breaks.Add(new ChainBreak
                {
                    FromStageId = stage.Input.Id,
                    MissingSource = string.Empty,
                    Reason = "No mapping segment covers this generated position."
                });
                trace.Complete = false;
                return trace;
            }

            var segment = stage.Map.Segments[segmentIndex];
            if (segment.SourceIndex is null || segment.OriginalLine is null || segment.OriginalColumn is null)
            {
                trace.Breaks.Add(new ChainBreak
                {
                    FromStageId = stage.Input.Id,
                    MissingSource = string.Empty,
                    Reason = "Covering segment has no source mapping."
                });
                trace.Complete = false;
                return trace;
            }

            var resolvedPath = stage.ResolvedSources[segment.SourceIndex.Value];
            var displayColumn = ToDisplayColumn(GetContent(stage, resolvedPath), segment.OriginalLine.Value, segment.OriginalColumn.Value, SourceMapParser.Encoding(stage.Map));
            var name = segment.NameIndex is { } nameIndex && stage.Map.Raw.Names is { } names && nameIndex < names.Count
                ? names[nameIndex]
                : null;
            trace.Hops[^1].SourcePath = resolvedPath;
            trace.Hops[^1].OriginalLine = segment.OriginalLine;
            trace.Hops[^1].OriginalColumn = displayColumn;
            trace.Hops[^1].Name = name;
            trace.Hops[^1].SegmentIndex = segmentIndex;

            internalLine = segment.OriginalLine.Value;
            internalColumn = segment.OriginalColumn.Value;
            if (stageIndex > 0)
            {
                var prior = stages[stageIndex - 1];
                trace.Hops.Add(new TraceHop
                {
                    StageId = prior.Input.Id,
                    StageLabel = prior.Input.Label,
                    Line = internalLine,
                    Column = ToDisplayColumn(prior.Output, internalLine, internalColumn, SourceMapParser.Encoding(prior.Map)),
                    ColumnEncoding = SourceMapParser.Encoding(prior.Map)
                });

                var links = SourceLinks(stage).Where(link => link.ResolvedPath == resolvedPath).ToList();
                if (!links.Any(link => link.StageId == prior.Input.Id))
                {
                    trace.Breaks.Add(new ChainBreak
                    {
                        FromStageId = stage.Input.Id,
                        MissingSource = resolvedPath,
                        Reason = $"Resolved source '{resolvedPath}' is not the output of previous stage '{prior.Input.Id}'."
                    });
                    trace.Complete = false;
                    return trace;
                }

                EnsureGeneratedPosition(prior.Output, internalLine, internalColumn);
            }
            else
            {
                var originalContent = GetContent(stage, resolvedPath);
                trace.Hops.Add(new TraceHop
                {
                    StageId = "original",
                    StageLabel = "Original source",
                    Line = internalLine,
                    Column = ToDisplayColumn(originalContent, internalLine, internalColumn, SourceMapParser.Encoding(stage.Map)),
                    ColumnEncoding = SourceMapParser.Encoding(stage.Map),
                    SourcePath = resolvedPath,
                    OriginalLine = internalLine,
                    OriginalColumn = ToDisplayColumn(originalContent, internalLine, internalColumn, SourceMapParser.Encoding(stage.Map)),
                    SegmentIndex = segmentIndex
                });
            }
        }

        trace.Complete = trace.Breaks.Count == 0;
        return trace;
    }

    public List<GeneratedRange> ReverseImpact(string sourcePath, int startLine, int startColumn, int endLine, int endColumn, string encoding = "utf16")
    {
        var sourceStage = stages.FirstOrDefault(stage => stage.ResolvedSources.Contains(SourcePaths.Resolve(null, sourcePath)));
        if (sourceStage is null)
        {
            return [];
        }

        var ranges = new List<GeneratedRange>();
        var currentStageIndex = stages.IndexOf(sourceStage);
        var content = GetContent(sourceStage, SourcePaths.Resolve(null, sourcePath));
        var current = new List<(int StartLine, int StartColumn, int EndLine, int EndColumn)>
        {
            (startLine, ToInternalColumn(content, startLine, startColumn, encoding),
             endLine, ToInternalColumn(content, endLine, endColumn, encoding))
        };

        for (var stageIndex = currentStageIndex; stageIndex < stages.Count; stageIndex++)
        {
            var stage = stages[stageIndex];
            var linked = new HashSet<string>(SourceLinks(stage).Select(link => link.ResolvedPath));
            if (stage != sourceStage)
            {
                linked.Add(sourceStage.OutputPath);
            }

            var next = new List<(int, int, int, int)>();
            for (var segmentIndex = 0; segmentIndex < stage.Map.Segments.Count; segmentIndex++)
            {
                var segment = stage.Map.Segments[segmentIndex];
                if (segment.SourceIndex is null || segment.OriginalLine is null || segment.OriginalColumn is null)
                {
                    continue;
                }

                var resolved = stage.ResolvedSources[segment.SourceIndex.Value];
                if (stage == sourceStage && resolved != SourcePaths.Resolve(null, sourcePath))
                {
                    continue;
                }

                if (stage != sourceStage && resolved != stages[stageIndex - 1].OutputPath)
                {
                    continue;
                }

                var sourceEnd = SegmentOriginalEnd(stage, segmentIndex);
                foreach (var range in current)
                {
                    if (Intersects(segment.OriginalLine.Value, segment.OriginalColumn.Value, sourceEnd.Line, sourceEnd.Column,
                                   range.Item1, range.Item2, range.Item3, range.Item4))
                    {
                        var generatedEnd = SegmentGeneratedEnd(stage, segmentIndex);
                        if (Compare(segment.GeneratedLine, segment.GeneratedColumn, generatedEnd.Line, generatedEnd.Column) >= 0)
                        {
                            continue;
                        }

                        var item = (segment.GeneratedLine, segment.GeneratedColumn, generatedEnd.Line, generatedEnd.Column);
                        next.Add(item);
                        var generatedRange = new GeneratedRange
                        {
                            StageId = stage.Input.Id,
                            StageLabel = stage.Input.Label,
                            StartLine = item.Item1,
                            StartColumn = ToDisplayColumn(stage.Output, item.Item1, item.Item2, SourceMapParser.Encoding(stage.Map)),
                            EndLine = item.Item3,
                            EndColumn = ToDisplayColumn(stage.Output, item.Item3, item.Item4, SourceMapParser.Encoding(stage.Map)),
                            SegmentIndex = segmentIndex
                        };
                        if (!ranges.Any(existing => existing.StageId == generatedRange.StageId && existing.SegmentIndex == generatedRange.SegmentIndex))
                        {
                            ranges.Add(generatedRange);
                        }
                    }
                }
            }

            current = MergeRanges(next);
            if (current.Count == 0)
            {
                break;
            }
        }

        ranges = ranges
            .GroupBy(range => new { range.StageId, range.SegmentIndex })
            .Select(group => group.First())
            .ToList();
        return ranges;
    }
}

using System.Text.Json.Serialization;
using System.Text.Json;

namespace SourceMapChain.Core;

public class SourceMapException : Exception
{
    public SourceMapException(string message) : base(message)
    {
    }
}

public sealed class SourceMapInput
{
    public int Version { get; set; }
    public string? File { get; set; }
    public string? SourceRoot { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<string?>? SourcesContent { get; set; }
    public List<string>? Names { get; set; }
    public string Mappings { get; set; } = string.Empty;

    [JsonPropertyName("xColumnEncoding")]
    public string? XColumnEncoding { get; set; }
}

public sealed class MappingSegment
{
    public int GeneratedLine { get; set; }
    public int GeneratedColumn { get; set; }
    public int? SourceIndex { get; set; }
    public int? OriginalLine { get; set; }
    public int? OriginalColumn { get; set; }
    public int? NameIndex { get; set; }

    public MappingSegment Clone() => new()
    {
        GeneratedLine = GeneratedLine,
        GeneratedColumn = GeneratedColumn,
        SourceIndex = SourceIndex,
        OriginalLine = OriginalLine,
        OriginalColumn = OriginalColumn,
        NameIndex = NameIndex
    };
}

public sealed class DecodedSourceMap
{
    public required SourceMapInput Raw { get; init; }
    public required List<MappingSegment> Segments { get; init; }
}

public sealed class StageInput
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public string MapJson { get; set; } = string.Empty;
}

public sealed class StageFingerprint
{
    public string StageId { get; set; } = string.Empty;
    public string OutputSha256 { get; set; } = string.Empty;
    public string MapSha256 { get; set; } = string.Empty;
    public string RuleVersion { get; set; } = string.Empty;
    public List<SourceFingerprint> SourceFingerprints { get; set; } = [];
}

public sealed class SourceFingerprint
{
    public int SourceIndex { get; set; }
    public string ResolvedPath { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
}

public sealed class ValidationIssue
{
    public string Severity { get; set; } = "error";
    public string StageId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? GeneratedLine { get; set; }
    public int? SegmentIndex { get; set; }
}

public sealed class ChainBreak
{
    public string FromStageId { get; set; } = string.Empty;
    public string MissingSource { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class SourceResolution
{
    public string StageId { get; set; } = string.Empty;
    public int SourceIndex { get; set; }
    public string DeclaredPath { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? ContentSha256 { get; set; }
}

public sealed class ChainValidation
{
    public bool Valid { get; set; }
    public bool Complete { get; set; }
    public List<ValidationIssue> Issues { get; set; } = [];
    public List<ChainBreak> Breaks { get; set; } = [];
    public List<SourceResolution> Sources { get; set; } = [];
    public List<StageFingerprint> Fingerprints { get; set; } = [];
}

public sealed class TraceHop
{
    public string StageId { get; set; } = string.Empty;
    public string StageLabel { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string ColumnEncoding { get; set; } = "utf16";
    public string? SourcePath { get; set; }
    public int? OriginalLine { get; set; }
    public int? OriginalColumn { get; set; }
    public string? Name { get; set; }
    public int SegmentIndex { get; set; } = -1;
}

public sealed class PositionTrace
{
    public bool Complete { get; set; }
    public List<TraceHop> Hops { get; set; } = [];
    public List<ChainBreak> Breaks { get; set; } = [];
}

public sealed class GeneratedRange
{
    public string StageId { get; set; } = string.Empty;
    public string StageLabel { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public int SegmentIndex { get; set; }
}

public sealed class RevisionProposal
{
    public string Id { get; set; } = string.Empty;
    public string StageId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int SegmentIndex { get; set; }
    public int? OriginalLine { get; set; }
    public int? OriginalColumn { get; set; }
    public int? SourceIndex { get; set; }
    public string? SourcePathRule { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class RevisionComparison
{
    public string RevisionId { get; set; } = string.Empty;
    public bool Valid { get; set; }
    public string EarliestChangedStageId { get; set; } = string.Empty;
    public List<GeneratedRange> ChangedRanges { get; set; } = [];
    public List<ValidationIssue> Issues { get; set; } = [];
}

public sealed class OverlapDetail
{
    public string StageId { get; set; } = string.Empty;
    public int SegmentIndex { get; set; }
    public string OriginalSegment { get; set; } = string.Empty;
    public List<string> Revisions { get; set; } = [];
    public List<GeneratedRange> DownstreamRanges { get; set; } = [];
}

public sealed class MergeReview
{
    public bool CanMerge { get; set; }
    public List<OverlapDetail> Overlaps { get; set; } = [];
    public RevisionComparison? CombinedComparison { get; set; }
}

public sealed class PublishedComposition
{
    public string Id { get; set; } = string.Empty;
    public string DeterministicSummary { get; set; } = string.Empty;
    public List<StageFingerprint> Fingerprints { get; set; } = [];
    public string RuleVersion { get; set; } = string.Empty;
    public List<string> RevisionIds { get; set; } = [];
    public bool Complete { get; set; }
    public List<ChainBreak> Breaks { get; set; } = [];
    public DateTimeOffset PublishedAt { get; set; }
}

public sealed class BusinessEvent
{
    public int Sequence { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class BackgroundJob
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public string? ResultSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class WorkspaceDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string WorkspaceId { get; set; } = "default";
    public string State { get; set; } = "draft";
    public string RuleVersion { get; set; } = SourceMapComposer.RuleVersionValue;
    public List<StageInput> Stages { get; set; } = [];
    public List<RevisionProposal> Revisions { get; set; } = [];
    public List<PublishedComposition> Publications { get; set; } = [];
    public List<BusinessEvent> Events { get; set; } = [];
    public List<BackgroundJob> Jobs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

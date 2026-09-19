namespace SourceMapChains.Core;

/// <summary>Stable identity of a source file: (sourceRoot, path). Same path under
/// different sourceRoots is a different file and must never be merged.</summary>
public sealed record FileIdentity(string? SourceRoot, string Path)
{
    public override string ToString() => $"{SourceRoot ?? ""}::{Path}";

    public static string NormalizeRoot(string? root)
    {
        if (string.IsNullOrEmpty(root)) return "";
        return root!.TrimEnd('/');
    }

    public static FileIdentity Of(string? sourceRoot, string path) =>
        new(NormalizeRoot(sourceRoot), path);
}

public sealed record SourceFileData(
    string Path,
    string? SourceRoot,
    string Content,
    ColumnSemantics Semantics,
    bool HadBom,
    bool EndsWithNewline,
    string Fingerprint)
{
    public FileIdentity Identity => FileIdentity.Of(SourceRoot, Path);

    public static SourceFileData Create(string path, string? sourceRoot, string content, ColumnSemantics semantics)
    {
        var stripped = TextLines.StripBom(content, out var hadBom);
        return new SourceFileData(
            path,
            FileIdentity.NormalizeRoot(sourceRoot),
            stripped,
            semantics,
            hadBom,
            stripped.EndsWith('\n'),
            SourceMapChains.Core.Fingerprint.OfText(stripped));
    }
}

public sealed record MapData(
    string GeneratedPath,
    string? GeneratedSourceRoot,
    SourceMapDocument Map)
{
    public FileIdentity GeneratedIdentity => FileIdentity.Of(GeneratedSourceRoot, GeneratedPath);
}

public sealed record LevelData(
    string Name,
    List<SourceFileData> Files,
    List<MapData> Maps)
{
    /// <summary>Deterministic fingerprint of everything imported at this level.</summary>
    public string Fingerprint() => SourceMapChains.Core.Fingerprint.OfCanonical(new
    {
        name = Name,
        files = Files.Select(f => new { id = f.Identity.ToString(), f.Fingerprint, semantics = f.Semantics.ToString(), f.HadBom, f.EndsWithNewline }).OrderBy(f => f.id).ToList(),
        maps = Maps.Select(m => new
        {
            id = m.GeneratedIdentity.ToString(),
            m.Map.SourceRoot,
            m.Map.Sources,
            m.Map.Mappings,
            semantics = m.Map.Semantics.ToString(),
            contentHashes = (m.Map.SourcesContent ?? new List<string?>()).Select(c => c == null ? null : SourceMapChains.Core.Fingerprint.OfText(c)).ToList(),
        }).OrderBy(m => m.id).ToList(),
    });
}

public sealed record ValidationIssue(
    string Severity,
    string Code,
    string Message,
    int? Level = null,
    string? Map = null,
    int? GenLine = null,
    int? GenCol = null);

public sealed record ChainStep(int Level, string Path, string? SourceRoot, int Line, int Col);

public sealed record ComposedEntry(
    string GeneratedPath,
    int GenLine,
    int GenCol,
    List<ChainStep> Steps,
    bool Complete,
    int? BreakLevel,
    string? BreakReason);

public sealed record CompositionResult(
    string Digest,
    string RevisionId,
    int RuleVersion,
    List<string> LevelFingerprints,
    List<ComposedEntry> Entries,
    List<ValidationIssue> Issues,
    DateTimeOffset CreatedAt);

// ---- Revisions ----

public sealed record SegmentPatch(int? SrcIndex, int? SrcLine, int? SrcCol);

public sealed record MappingEdit(
    string Type,              // "segment" | "pathRule"
    int Level,
    string Map,               // generated path of the map
    int? GenLine,
    int? GenCol,
    SegmentPatch? Patch,
    string? MatchSourceRoot,  // pathRule: identity to rewrite
    string? MatchPath,
    string? ReplaceSourceRoot,
    string? ReplacePath)
{
    /// <summary>Key of the segment slot this edit touches, used for overlap detection.</summary>
    public string SegmentKey() => Type == "segment"
        ? $"seg:{Level}:{Map}:{GenLine}:{GenCol}"
        : $"rule:{Level}:{Map}:{FileIdentity.Of(MatchSourceRoot, MatchPath ?? "")}";
}

public sealed record Revision(
    string Id,
    string Name,
    List<MappingEdit> Edits,
    string Status,            // draft | composed | merged | conflict
    DateTimeOffset CreatedAt);

public sealed record ConflictDetail(
    string SegmentKey,
    object? Original,
    List<MappingEdit> EditA,
    List<MappingEdit> EditB,
    List<string> DownstreamImpactA,
    List<string> DownstreamImpactB);

// ---- Project document (persisted) ----

public enum ProjectStatus
{
    Created = 0,
    Imported = 1,
    Composed = 2,
    Published = 3,
}

public sealed record EventRecord(long Seq, DateTimeOffset Ts, string Type, object Data);

public sealed record OperationRecord(string Key, string ResultJson, DateTimeOffset Ts);

public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending | running | completed | failed
    public string? RevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ProjectDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ProjectStatus Status { get; set; } = ProjectStatus.Created;
    public List<LevelData> Levels { get; set; } = new();
    public List<Revision> Revisions { get; set; } = new();
    public Dictionary<string, CompositionResult> Compositions { get; set; } = new(); // revisionId -> result
    public string? PublishedRevisionId { get; set; }
    public List<EventRecord> Events { get; set; } = new();
    public Dictionary<string, OperationRecord> Operations { get; set; } = new(); // idempotency keys
    public List<JobRecord> Jobs { get; set; } = new();
    public long NextSeq { get; set; } = 1;
    public int RuleVersion { get; set; } = 1;
}

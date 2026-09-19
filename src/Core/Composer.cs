namespace SourceMapChains.Core;

/// <summary>Validates levels and composes mapping chains from the final bundle
/// back to the original sources.</summary>
public static class Composer
{
    public static CompositionResult Compose(
        IReadOnlyList<LevelData> levels,
        string revisionId,
        int ruleVersion,
        DateTimeOffset now)
    {
        var issues = Validate(levels);
        var entries = new List<ComposedEntry>();
        if (levels.Count >= 2)
        {
            var finalLevel = levels[^1];
            foreach (var map in finalLevel.Maps)
            {
                var lines = map.Map.Lines;
                for (var genLine = 0; genLine < lines.Count; genLine++)
                {
                    foreach (var seg in lines[genLine])
                    {
                        if (seg.SrcIndex < 0) continue; // unmapped segment: no chain
                        entries.Add(BuildChain(levels, finalLevel, map, seg));
                    }
                }
            }
        }
        var levelFingerprints = levels.Select(l => l.Fingerprint()).ToList();
        var digest = Fingerprint.OfCanonical(new
        {
            revisionId,
            ruleVersion,
            levelFingerprints,
            entries = entries.Select(e => new
            {
                e.GeneratedPath, e.GenLine, e.GenCol, e.Steps, e.Complete, e.BreakLevel, e.BreakReason,
            }).ToList(),
        });
        return new CompositionResult(digest, revisionId, ruleVersion, levelFingerprints, entries, issues, now);
    }

    private static ComposedEntry BuildChain(
        IReadOnlyList<LevelData> levels, LevelData finalLevel, MapData map, Segment seg)
    {
        var steps = new List<ChainStep>();
        var identity = FileIdentity.Of(map.Map.SourceRoot, map.Map.Sources[seg.SrcIndex]);
        var line = seg.SrcLine;
        var col = seg.SrcCol;
        var level = levels.Count - 2; // level below the final one
        steps.Add(new ChainStep(level, identity.Path, identity.SourceRoot, line, col));
        while (true)
        {
            var file = levels[level].Files.FirstOrDefault(f => f.Identity == identity);
            if (file is null)
                return new ComposedEntry(map.GeneratedPath, seg.GenLine, seg.GenCol, steps, false, level,
                    $"missing-source: {identity} not present at level '{levels[level].Name}'");
            if (level == 0)
                return new ComposedEntry(map.GeneratedPath, seg.GenLine, seg.GenCol, steps, true, null, null);
            var nextMap = levels[level].Maps.FirstOrDefault(m => m.GeneratedIdentity == identity);
            if (nextMap is null)
                return new ComposedEntry(map.GeneratedPath, seg.GenLine, seg.GenCol, steps, false, level,
                    $"no-map: file '{identity.Path}' at level '{levels[level].Name}' has no source map");
            var nextSeg = nextMap.Map.FindSegment(line, col);
            if (nextSeg is null || nextSeg.SrcIndex < 0)
                return new ComposedEntry(map.GeneratedPath, seg.GenLine, seg.GenCol, steps, false, level,
                    $"unmapped-position: {identity.Path}:{line}:{col} is not covered by a mapped segment");
            identity = FileIdentity.Of(nextMap.Map.SourceRoot, nextMap.Map.Sources[nextSeg.SrcIndex]);
            line = nextSeg.SrcLine;
            col = nextSeg.SrcCol;
            level--;
            steps.Add(new ChainStep(level, identity.Path, identity.SourceRoot, line, col));
        }
    }

    public static List<ValidationIssue> Validate(IReadOnlyList<LevelData> levels)
    {
        var issues = new List<ValidationIssue>();
        for (var levelIdx = 1; levelIdx < levels.Count; levelIdx++)
        {
            var level = levels[levelIdx];
            var sources = levels[levelIdx - 1].Files;
            foreach (var mapData in level.Maps)
            {
                var map = mapData.Map;
                var mapName = mapData.GeneratedPath;
                var generated = level.Files.FirstOrDefault(f => f.Identity == mapData.GeneratedIdentity);
                if (generated is null)
                {
                    issues.Add(new("error", "GeneratedFileMissing",
                        $"Map '{mapName}' has no matching generated file at level '{level.Name}'.", levelIdx, mapName));
                }
                else if (generated.Semantics != map.Semantics)
                {
                    issues.Add(new("error", "ColumnSemanticsMismatch",
                        $"Map '{mapName}' uses {map.Semantics} columns but generated file uses {generated.Semantics}.",
                        levelIdx, mapName));
                }
                for (var i = 0; i < map.Sources.Count; i++)
                {
                    var identity = FileIdentity.Of(map.SourceRoot, map.Sources[i]);
                    var source = sources.FirstOrDefault(f => f.Identity == identity);
                    if (source is null)
                    {
                        issues.Add(new("warning", "SourceMissing",
                            $"Source '{identity}' referenced by map '{mapName}' is missing at level '{levels[levelIdx - 1].Name}'.",
                            levelIdx, mapName));
                        continue;
                    }
                    if (source.Semantics != map.Semantics)
                        issues.Add(new("error", "ColumnSemanticsMismatch",
                            $"Map '{mapName}' uses {map.Semantics} columns but source '{identity}' uses {source.Semantics}.",
                            levelIdx, mapName));
                    if (map.SourcesContent is not null && i < map.SourcesContent.Count && map.SourcesContent[i] is { } embedded)
                    {
                        var stripped = TextLines.StripBom(embedded, out _);
                        var embeddedHash = Fingerprint.OfText(stripped);
                        if (!string.Equals(embeddedHash, source.Fingerprint, StringComparison.Ordinal))
                            issues.Add(new("error", "SourcesContentFingerprintMismatch",
                                $"sourcesContent[{i}] fingerprint {embeddedHash[..12]}… != imported '{identity}' {source.Fingerprint[..12]}….",
                                levelIdx, mapName));
                    }
                }
                var lines = map.Lines;
                for (var genLine = 0; genLine < lines.Count; genLine++)
                {
                    var prevCol = -1;
                    foreach (var seg in lines[genLine])
                    {
                        if (seg.GenCol <= prevCol)
                            issues.Add(new("error", "GeneratedColumnNotMonotonic",
                                $"Generated column {seg.GenCol} is not strictly increasing on line {genLine}.",
                                levelIdx, mapName, genLine, seg.GenCol));
                        prevCol = seg.GenCol;
                        if (seg.SrcIndex < 0) continue;
                        if (seg.SrcIndex >= map.Sources.Count)
                        {
                            issues.Add(new("error", "SourceIndexOutOfRange",
                                $"Segment references source index {seg.SrcIndex} but map has {map.Sources.Count} sources.",
                                levelIdx, mapName, genLine, seg.GenCol));
                            continue;
                        }
                        var identity = FileIdentity.Of(map.SourceRoot, map.Sources[seg.SrcIndex]);
                        var source = sources.FirstOrDefault(f => f.Identity == identity);
                        if (source is null) continue; // already reported as SourceMissing
                        var srcLines = TextLines.Split(source.Content);
                        if (seg.SrcLine >= srcLines.Count)
                        {
                            issues.Add(new("error", "SourceLineOutOfRange",
                                $"Segment references line {seg.SrcLine} but '{identity.Path}' has {srcLines.Count} lines.",
                                levelIdx, mapName, genLine, seg.GenCol));
                            continue;
                        }
                        var lineLength = TextLines.ColumnLength(srcLines[seg.SrcLine], map.Semantics);
                        if (seg.SrcCol > lineLength)
                            issues.Add(new("error", "SourceColumnOutOfRange",
                                $"Segment references column {seg.SrcCol} but line {seg.SrcLine} of '{identity.Path}' is {lineLength} columns long ({map.Semantics}).",
                                levelIdx, mapName, genLine, seg.GenCol));
                    }
                }
            }
        }
        return issues;
    }

}

/// <summary>Forward trace and reverse lookup over a composition.</summary>
public static class CompositionQueries
{
    /// <summary>Trace a final generated position back through the levels.</summary>
    public static ComposedEntry? Trace(CompositionResult composition, string generatedPath, int genLine, int genCol)
    {
        ComposedEntry? best = null;
        foreach (var e in composition.Entries)
        {
            if (e.GeneratedPath != generatedPath || e.GenLine != genLine) continue;
            if (e.GenCol <= genCol && (best is null || e.GenCol > best.GenCol)) best = e;
        }
        return best;
    }

    /// <summary>Which final generated ranges are affected by a source span at a level.</summary>
    public static List<(string GeneratedPath, int GenLine, int GenCol, int Length)> ReverseLookup(
        CompositionResult composition, int level, string? sourceRoot, string path, int line, int col, int length)
    {
        var hits = new List<(string, int, int, int)>();
        var ordered = composition.Entries.OrderBy(e => e.GeneratedPath).ThenBy(e => e.GenLine).ThenBy(e => e.GenCol).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var step = e.Steps.FirstOrDefault(s =>
                s.Level == level && s.Path == path &&
                FileIdentity.NormalizeRoot(s.SourceRoot) == FileIdentity.NormalizeRoot(sourceRoot) &&
                s.Line == line && s.Col >= col && s.Col < col + Math.Max(1, length));
            if (step is null) continue;
            var spanEnd = NextSegmentStart(ordered, i);
            hits.Add((e.GeneratedPath, e.GenLine, e.GenCol, Math.Max(1, spanEnd - e.GenCol)));
        }
        return hits;
    }

    private static int NextSegmentStart(List<ComposedEntry> ordered, int index)
    {
        var current = ordered[index];
        for (var i = index + 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.GeneratedPath == current.GeneratedPath && next.GenLine == current.GenLine)
                return next.GenCol;
            if (next.GeneratedPath != current.GeneratedPath || next.GenLine > current.GenLine)
                break;
        }
        return current.GenCol + 1;
    }

    /// <summary>First generated position where two compositions differ, or null.</summary>
    public static (string Path, int Line, int Col, string Detail)? EarliestChange(
        CompositionResult baseline, CompositionResult candidate)
    {
        var baseMap = baseline.Entries.ToDictionary(EntryKey, e => e);
        var candMap = candidate.Entries.ToDictionary(EntryKey, e => e);
        var allKeys = baseMap.Keys.Union(candMap.Keys)
            .OrderBy(k => ParseKey(k).Path, StringComparer.Ordinal)
            .ThenBy(k => ParseKey(k).Line)
            .ThenBy(k => ParseKey(k).Col);
        foreach (var k in allKeys)
        {
            baseMap.TryGetValue(k, out var a);
            candMap.TryGetValue(k, out var b);
            var (path, line, col) = ParseKey(k);
            if (a is null || b is null)
                return (path, line, col, a is null ? "entry added" : "entry removed");
            var sa = CanonicalJson.Serialize(a.Steps);
            var sb = CanonicalJson.Serialize(b.Steps);
            if (sa != sb || a.Complete != b.Complete || a.BreakLevel != b.BreakLevel)
                return (a.GeneratedPath, a.GenLine, a.GenCol, "chain changed");
        }
        return null;
    }

    private static string EntryKey(ComposedEntry e) => $"{e.GeneratedPath}:{e.GenLine}:{e.GenCol}";

    private static (string Path, int Line, int Col) ParseKey(string key)
    {
        var lastColon = key.LastIndexOf(':');
        var prevColon = key.LastIndexOf(':', lastColon - 1);
        return (key[..prevColon],
            int.Parse(key[(prevColon + 1)..lastColon]),
            int.Parse(key[(lastColon + 1)..]));
    }
}

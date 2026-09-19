namespace SourceMapChains.Core;

/// <summary>Applies mapping edits to cloned levels (originals are never mutated),
/// and merges revisions.</summary>
public static class RevisionEngine
{
    /// <summary>Deep-clone levels and apply edits. Original project data is untouched.</summary>
    public static List<LevelData> ApplyEdits(IReadOnlyList<LevelData> levels, IReadOnlyList<MappingEdit> edits)
    {
        var cloned = levels.Select(CloneLevel).ToList();
        foreach (var edit in edits)
        {
            if (edit.Level < 0 || edit.Level >= cloned.Count)
                throw new InvalidOperationException($"Edit targets unknown level {edit.Level}.");
            var level = cloned[edit.Level];
            var mapIdx = level.Maps.FindIndex(m => m.GeneratedPath == edit.Map);
            if (mapIdx < 0)
                throw new InvalidOperationException($"Edit targets unknown map '{edit.Map}' at level {edit.Level}.");
            var mapData = level.Maps[mapIdx];
            var newMap = edit.Type switch
            {
                "segment" => WithSegmentEdit(mapData.Map, edit),
                "pathRule" => WithPathRule(mapData.Map, edit),
                _ => throw new InvalidOperationException($"Unknown edit type '{edit.Type}'."),
            };
            level.Maps[mapIdx] = mapData with { Map = newMap };
        }
        return cloned;
    }

    private static SourceMapDocument CloneDoc(SourceMapDocument map) => new()
    {
        Version = map.Version,
        File = map.File,
        SourceRoot = map.SourceRoot,
        Sources = new List<string>(map.Sources),
        SourcesContent = map.SourcesContent is null ? null : new List<string?>(map.SourcesContent),
        Names = new List<string>(map.Names),
        Mappings = map.Mappings,
        Semantics = map.Semantics,
    };

    private static SourceMapDocument WithSegmentEdit(SourceMapDocument map, MappingEdit edit)
    {
        var lines = map.Lines.Select(l => l.ToList()).ToList();
        var genLine = edit.GenLine ?? throw new InvalidOperationException("segment edit requires genLine");
        var genCol = edit.GenCol ?? throw new InvalidOperationException("segment edit requires genCol");
        if (genLine >= lines.Count) throw new InvalidOperationException($"genLine {genLine} out of range.");
        var idx = lines[genLine].FindIndex(s => s.GenCol == genCol);
        if (idx < 0) throw new InvalidOperationException($"No segment at {genLine}:{genCol} in map '{edit.Map}'.");
        var old = lines[genLine][idx];
        var patch = edit.Patch ?? new SegmentPatch(null, null, null);
        var updated = old with
        {
            SrcIndex = patch.SrcIndex ?? old.SrcIndex,
            SrcLine = patch.SrcLine ?? old.SrcLine,
            SrcCol = patch.SrcCol ?? old.SrcCol,
        };
        lines[genLine][idx] = updated;
        var result = CloneDoc(map);
        result.Mappings = EncodeMappings(lines);
        return result;
    }

    private static SourceMapDocument WithPathRule(SourceMapDocument map, MappingEdit edit)
    {
        var result = CloneDoc(map);
        var matchRoot = FileIdentity.NormalizeRoot(edit.MatchSourceRoot);
        var mapRoot = FileIdentity.NormalizeRoot(result.SourceRoot);
        for (var i = 0; i < result.Sources.Count; i++)
        {
            if (mapRoot == matchRoot && result.Sources[i] == edit.MatchPath)
            {
                if (edit.ReplacePath is not null) result.Sources[i] = edit.ReplacePath;
                if (edit.ReplaceSourceRoot is not null) result.SourceRoot = edit.ReplaceSourceRoot;
            }
        }
        return result;
    }

    /// <summary>Re-encode absolute segments back into the mappings string.</summary>
    private static string EncodeMappings(List<List<Segment>> lines)
    {
        var parts = new List<string>();
        var prevSrcIndex = 0;
        var prevSrcLine = 0;
        var prevSrcCol = 0;
        var prevName = 0;
        foreach (var line in lines)
        {
            var segs = new List<string>();
            var prevGenCol = 0;
            foreach (var seg in line.OrderBy(s => s.GenCol))
            {
                if (seg.SrcIndex < 0)
                {
                    segs.Add(Vlq.Encode(new[] { seg.GenCol - prevGenCol }));
                    prevGenCol = seg.GenCol;
                    continue;
                }
                var values = new List<int>
                {
                    seg.GenCol - prevGenCol,
                    seg.SrcIndex - prevSrcIndex,
                    seg.SrcLine - prevSrcLine,
                    seg.SrcCol - prevSrcCol,
                };
                if (seg.NameIndex >= 0) values.Add(seg.NameIndex - prevName);
                segs.Add(Vlq.Encode(values));
                prevGenCol = seg.GenCol;
                prevSrcIndex = seg.SrcIndex;
                prevSrcLine = seg.SrcLine;
                prevSrcCol = seg.SrcCol;
                if (seg.NameIndex >= 0) prevName = seg.NameIndex;
            }
            parts.Add(string.Join(',', segs));
        }
        return string.Join(';', parts);
    }

    private static LevelData CloneLevel(LevelData level) =>
        new(level.Name,
            level.Files.Select(f => f with { }).ToList(),
            level.Maps.Select(m => m with { Map = CloneDoc(m.Map) }).ToList());

    /// <summary>Segment keys touched by a set of edits.</summary>
    public static HashSet<string> TouchedKeys(IEnumerable<MappingEdit> edits) =>
        edits.Select(e => e.SegmentKey()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Merge two revisions. Returns merged edits, or a conflict report.</summary>
    public static (List<MappingEdit>? Merged, List<ConflictDetail>? Conflicts) Merge(
        IReadOnlyList<LevelData> baseLevels,
        Revision a, CompositionResult composedA,
        Revision b, CompositionResult composedB)
    {
        var keysA = TouchedKeys(a.Edits);
        var keysB = TouchedKeys(b.Edits);
        var overlap = keysA.Intersect(keysB, StringComparer.Ordinal).ToList();
        if (overlap.Count == 0)
            return (a.Edits.Concat(b.Edits).ToList(), null);

        var conflicts = new List<ConflictDetail>();
        foreach (var key in overlap)
        {
            var editA = a.Edits.Where(e => e.SegmentKey() == key).ToList();
            var editB = b.Edits.Where(e => e.SegmentKey() == key).ToList();
            var original = DescribeOriginal(baseLevels, editA[0]);
            conflicts.Add(new ConflictDetail(
                key,
                original,
                editA,
                editB,
                DownstreamImpact(composedA, key),
                DownstreamImpact(composedB, key)));
        }
        return (null, conflicts);
    }

    private static object? DescribeOriginal(IReadOnlyList<LevelData> levels, MappingEdit edit)
    {
        if (edit.Type != "segment") return new { edit.Type, edit.MatchSourceRoot, edit.MatchPath };
        var map = levels[edit.Level].Maps.FirstOrDefault(m => m.GeneratedPath == edit.Map);
        var seg = map?.Map.FindSegment(edit.GenLine ?? 0, edit.GenCol ?? 0);
        return seg is null ? null : new { seg.GenLine, seg.GenCol, seg.SrcIndex, seg.SrcLine, seg.SrcCol };
    }

    private static List<string> DownstreamImpact(CompositionResult composition, string segmentKey)
    {
        // Impact = final generated ranges whose chain crosses the edited level/map.
        // segmentKey: "seg:{level}:{map}:{genLine}:{genCol}" or "rule:{level}:{map}:{identity}"
        var parts = segmentKey.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var level)) return new List<string>();
        var mapName = parts[2];
        // An entry is impacted when its chain passes through the edited map's
        // generated file: a step at the map's level, or — for the final level —
        // the entry's own generated path.
        var finalLevel = composition.Entries.SelectMany(e => e.Steps).Select(s => s.Level).DefaultIfEmpty(-1).Max() + 1;
        return composition.Entries
            .Where(e => e.Steps.Any(s => s.Level == level && s.Path == mapName)
                || (level == finalLevel && e.GeneratedPath == mapName))
            .Select(e => $"{e.GeneratedPath}:{e.GenLine}:{e.GenCol}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
}

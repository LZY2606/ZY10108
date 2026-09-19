namespace SourceMapChain.Core;

public sealed partial class SourceMapComposer
{
    public ChainValidation Validate()
    {
        var result = new ChainValidation
        {
            Valid = true,
            Complete = true,
            Fingerprints = Fingerprints()
        };

        foreach (var stage in stages)
        {
            result.Sources.AddRange(stage.Resolutions);
            foreach (var resolution in stage.Resolutions)
            {
                if (resolution.Content is null)
                {
                    continue;
                }

                var outputStage = stages.FirstOrDefault(candidate => candidate.OutputPath == resolution.ResolvedPath);
                if (outputStage is not null)
                {
                    var outputFingerprint = TextLines.Sha256Fingerprint(TextLines.NormalizeInput(outputStage.Output));
                    if (!string.Equals(outputFingerprint, resolution.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Valid = false;
                        result.Issues.Add(new ValidationIssue
                        {
                            Severity = "error",
                            StageId = stage.Input.Id,
                            Code = "sources-content-fingerprint-mismatch",
                            Message = $"sourcesContent fingerprint for '{resolution.ResolvedPath}' does not match the previous output fingerprint."
                        });
                    }
                }

                var segments = stage.Map.Segments
                    .Where(segment => segment.SourceIndex == resolution.SourceIndex && segment.OriginalColumn is not null)
                    .ToList();
                if (segments.Count == 0)
                {
                    result.Issues.Add(new ValidationIssue
                    {
                        Severity = "warning",
                        StageId = stage.Input.Id,
                        Code = "unused-sources-content",
                        Message = $"Source '{resolution.ResolvedPath}' has sourcesContent but no mapped segment."
                    });
                }
            }

            for (var index = 1; index < stages.Count; index++)
            {
                var previousOutput = stages[index - 1].OutputPath;
                if (!stages[index].ResolvedSources.Contains(previousOutput))
                {
                    result.Complete = false;
                    result.Breaks.Add(new ChainBreak
                    {
                        FromStageId = stages[index].Input.Id,
                        MissingSource = previousOutput,
                        Reason = $"Stage '{stages[index].Input.Id}' does not include previous output '{previousOutput}' in its sources."
                    });
                }
            }
        }

        result.Valid = !result.Issues.Any(issue => issue.Severity == "error");
        return result;
    }

    public RevisionComparison CompareRevision(RevisionProposal revision)
    {
        var baseline = this;
        var baselineTrace = SafeTraceAtFirstAffected(revision);
        SourceMapComposer candidate;
        var comparison = new RevisionComparison { RevisionId = revision.Id };

        try
        {
            candidate = new SourceMapComposer(
                stages.Select(stage => stage.Input),
                stages.SelectMany(stage => stage.Revisions).Append(revision).ToList());
        }
        catch (SourceMapException ex)
        {
            comparison.Valid = false;
            comparison.EarliestChangedStageId = revision.StageId;
            comparison.Issues.Add(new ValidationIssue
            {
                StageId = revision.StageId,
                Code = "invalid-revision",
                Message = ex.Message
            });
            return comparison;
        }

        comparison.Valid = true;
        comparison.EarliestChangedStageId = revision.StageId;
        var candidateStage = candidate.Stages.First(stage => stage.Input.Id == revision.StageId);
        var baselineStage = baseline.Stages.First(stage => stage.Input.Id == revision.StageId);

        if (revision.Kind == "segment")
        {
            if (revision.SegmentIndex >= 0 && revision.SegmentIndex < baselineStage.Map.Segments.Count)
            {
                var segment = baselineStage.Map.Segments[revision.SegmentIndex];
                var end = baseline.SegmentGeneratedEnd(baselineStage, revision.SegmentIndex);
                comparison.ChangedRanges.Add(new GeneratedRange
                {
                    StageId = baselineStage.Input.Id,
                    StageLabel = baselineStage.Input.Label,
                    StartLine = segment.GeneratedLine,
                    StartColumn = ToDisplayColumn(baselineStage.Output, segment.GeneratedLine, segment.GeneratedColumn, SourceMapParser.Encoding(baselineStage.Map)),
                    EndLine = end.Line,
                    EndColumn = ToDisplayColumn(baselineStage.Output, end.Line, end.Column, SourceMapParser.Encoding(baselineStage.Map)),
                    SegmentIndex = revision.SegmentIndex
                });

                var original = candidateStage.Map.Segments[revision.SegmentIndex];
                if (original.SourceIndex is not null && original.OriginalLine is not null && original.OriginalColumn is not null)
                {
                    var sourcePath = candidateStage.ResolvedSources[original.SourceIndex.Value];
                    var downstream = candidate.ReverseImpact(
                        sourcePath,
                        original.OriginalLine.Value,
                        original.OriginalColumn.Value,
                        original.OriginalLine.Value,
                        original.OriginalColumn.Value + 1);
                    comparison.ChangedRanges.AddRange(downstream.Where(range => range.StageId != revision.StageId));
                }
            }
        }
        else
        {
            var changed = baselineStage.ResolvedSources
                .Zip(candidateStage.ResolvedSources)
                .Where(pair => pair.First != pair.Second)
                .ToList();
            comparison.EarliestChangedStageId = changed.Count == 0 ? string.Empty : revision.StageId;
            foreach (var pair in changed)
            {
                var ranges = baseline.ReverseImpact(pair.First, 0, 0, int.MaxValue, 0)
                    .Where(range => range.StageId == revision.StageId)
                    .Take(20);
                comparison.ChangedRanges.AddRange(ranges);
            }
        }

        return comparison;
    }

    private PositionTrace? SafeTraceAtFirstAffected(RevisionProposal revision)
    {
        var stage = stages.FirstOrDefault(candidate => candidate.Input.Id == revision.StageId);
        if (stage is null || revision.SegmentIndex < 0 || revision.SegmentIndex >= stage.Map.Segments.Count)
        {
            return null;
        }

        var segment = stage.Map.Segments[revision.SegmentIndex];
        try
        {
            var stageIndex = stages.IndexOf(stage);
            if (stageIndex == stages.Count - 1)
            {
                return TraceFinalPosition(segment.GeneratedLine, segment.GeneratedColumn, SourceMapParser.Encoding(stage.Map));
            }
        }
        catch (SourceMapException)
        {
            return null;
        }

        return null;
    }

    public MergeReview MergeRevisions(string firstRevisionId, string secondRevisionId, IReadOnlyList<RevisionProposal> allRevisions)
    {
        var first = allRevisions.First(revision => revision.Id == firstRevisionId);
        var second = allRevisions.First(revision => revision.Id == secondRevisionId);
        var pair = new List<RevisionProposal> { first, second };
        var review = new MergeReview { CanMerge = true };

        if (first.StageId == second.StageId && first.Kind == "segment" && second.Kind == "segment" &&
            first.SegmentIndex == second.SegmentIndex)
        {
            review.CanMerge = false;
            var stage = stages.First(item => item.Input.Id == first.StageId);
            var segment = stage.Map.Segments[first.SegmentIndex];
            var downstream = ReverseImpact(stage.ResolvedSources[segment.SourceIndex!.Value],
                segment.OriginalLine!.Value, segment.OriginalColumn!.Value,
                segment.OriginalLine.Value, segment.OriginalColumn.Value + 1)
                .Where(range => range.StageId != first.StageId)
                .ToList();
            review.Overlaps.Add(new OverlapDetail
            {
                StageId = first.StageId,
                SegmentIndex = first.SegmentIndex,
                OriginalSegment = FormatSegment(segment),
                Revisions = [firstRevisionId, secondRevisionId],
                DownstreamRanges = downstream
            });
            return review;
        }

        try
        {
            var candidate = new SourceMapComposer(stages.Select(stage => stage.Input),
                stages.SelectMany(stage => stage.Revisions).Concat(pair).ToList());
            var validation = candidate.Validate();
            review.CombinedComparison = new RevisionComparison
            {
                RevisionId = $"{firstRevisionId}+{secondRevisionId}",
                Valid = validation.Valid,
                EarliestChangedStageId = first.StageId.CompareTo(second.StageId) <= 0 ? first.StageId : second.StageId
            };
            review.CombinedComparison.Issues.AddRange(validation.Issues);
            review.CombinedComparison.ChangedRanges.AddRange(
                candidate.CompareRevision(first).ChangedRanges);
            review.CombinedComparison.ChangedRanges.AddRange(
                candidate.CompareRevision(second).ChangedRanges);
        }
        catch (SourceMapException ex)
        {
            review.CanMerge = false;
            review.Overlaps.Add(new OverlapDetail
            {
                StageId = first.StageId,
                OriginalSegment = ex.Message,
                Revisions = [firstRevisionId, secondRevisionId]
            });
        }

        return review;
    }

    private static string FormatSegment(MappingSegment segment) =>
        $"gen=({segment.GeneratedLine},{segment.GeneratedColumn}), src={segment.SourceIndex}, orig=({segment.OriginalLine},{segment.OriginalColumn}), name={segment.NameIndex}";
}

namespace SourceMapChain.Core;

public static class WorkspaceStateMachine
{
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
    {
        "draft", "analyzing", "revision-open", "publishing", "published", "failed"
    };

    public static string Apply(string currentState, string eventType)
    {
        return (currentState, eventType) switch
        {
            (_, "workspace-created") => "draft",
            ("draft" or "failed" or "published", "batch-submitted") => "analyzing",
            ("analyzing", "analysis-completed") => "draft",
            ("analyzing", "analysis-failed") => "failed",
            ("draft" or "failed" or "revision-open", "revision-opened") => "revision-open",
            ("revision-open", "revision-closed") => "draft",
            ("draft" or "revision-open", "publication-started") => "publishing",
            ("publishing", "composition-published") => "published",
            ("publishing", "publication-failed") => "failed",
            _ => throw new InvalidOperationException($"Cannot apply event '{eventType}' in state '{currentState}'.")
        };
    }
}

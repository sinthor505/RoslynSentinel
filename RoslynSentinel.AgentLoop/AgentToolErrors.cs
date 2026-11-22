namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Shared by every agent test that caps failed tool calls (<see cref="WholeFileRewriteAgentTests"/>
/// via its shared AssertFixApplied, <see cref="PlanImplementVerifyAgentTests"/>,
/// <see cref="PlanThenExecuteAgentTests"/>). A flat total-error-count cap can't distinguish a model
/// that tried 3 different tools once each while exploring (probably fine) from one that hit the
/// same tool 3 times in a row (thrashing -> see the CS0103/using-directive retry loop documented in
/// docs/current/project_directive_error_messages_wiggle_room_theory.md, 6 failed calls all on the
/// same root cause). Asserting per-tool, not just in total, makes that distinction visible directly
/// in the failure message instead of requiring a manual transcript read.
/// </summary>
public static class AgentToolErrors
{
    public sealed record ToolErrorSummary(int TotalErrors, IReadOnlyList<(string ToolName, int Count)> ByTool)
    {
        public int MaxPerTool => ByTool.Count == 0 ? 0 : ByTool.Max(t => t.Count);

        public override string ToString() =>
            ByTool.Count == 0 ? "none" : string.Join(", ", ByTool.Select(t => $"{t.ToolName}={t.Count}"));
    }

    public static ToolErrorSummary Summarize(AgentRunResult result)
    {
        var errorTools = result.Transcript.Turns.SelectMany(t => t.ToolCalls).Where(tc => tc.IsError).ToList();
        var byTool = errorTools
            .GroupBy(tc => tc.ToolName)
            .Select(g => (ToolName: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();
        return new ToolErrorSummary(errorTools.Count, byTool);
    }
}

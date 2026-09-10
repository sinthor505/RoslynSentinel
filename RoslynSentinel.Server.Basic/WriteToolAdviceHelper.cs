namespace RoslynSentinel.Server.Basic;

/// <summary>
/// The kind of escape hatch <see cref="WriteToolAdviceHelper"/> selected for an edit that a
/// narrow tool refused.
/// </summary>
public enum WriteEscapeRoute
{
    /// <summary>Rewrite the whole file (WriteFile).</summary>
    WholeFileRewrite,

    /// <summary>Apply the edit as a diff (ApplyUnifiedDiff/ApplyDiff).</summary>
    UnifiedDiff,

    /// <summary>Use a structural Roslyn tool instead of a text edit.</summary>
    StructuredEdit,

    /// <summary>
    /// Break the edit into several smaller calls of the tool that refused it. Always reachable,
    /// so advice is never empty.
    /// </summary>
    SplitIntoSmallerEdits
}

/// <summary>
/// Advice for a caller whose edit exceeded its tool's limits: the chosen route, the tool names
/// that route uses (all verified present on this server's surface), and ready-made phrasing.
/// </summary>
/// <param name="Route">Which escape hatch to take.</param>
/// <param name="ToolNames">Tools the caller may be told to use. Never contains an unregistered tool.</param>
/// <param name="Sentence">Ready-made phrasing for callers that just want a string.</param>
public sealed record WriteAdvice(
    WriteEscapeRoute Route,
    IReadOnlyList<string> ToolNames,
    string Sentence);

/// <summary>
/// Answers "which write tool may I tell the agent to use?" from the tool set this server actually
/// registered, and owns the phrasing that names them.
/// </summary>
/// <remarks>
/// <para>
/// Exists because error messages that name a gated-off tool are worse than no advice at all: they
/// send the agent looking for a tool it cannot call. Run 20260910-013550-398 livelocked for 24
/// turns on a <c>ReplaceSnippet</c> size error whose text directed it to
/// <c>WriteFile(operation=ReplaceFile)</c> — a tool the run had deliberately gated off (see
/// docs/current/project_wholefilewrite_gating_overnight_result_2026_09_08.md, where gating it off
/// scored 26/26 against a 47% baseline, so it stays gated).
/// </para>
/// <para>
/// This owns the phrasing rather than just exposing an <c>IsExposed</c> predicate: a bare predicate
/// leaves every call site to hardcode its own sentence, which is the same footgun one level down
/// and is exactly how the run-398 message came to name a tool that wasn't there.
/// </para>
/// <para>
/// Granularity matters here. <see cref="ToolClassRegistry"/> resolves at <em>class</em>
/// granularity, but advice names <em>tools</em>, and <c>SentinelWholeFileWriteTools</c> alone holds
/// four of them — so "is that class active?" cannot answer "may I mention ApplyUnifiedDiff?"
/// without the tool→class map below.
/// </para>
/// <para>
/// Basic-only by design. Every tool this can name is a gated whole-file-write tool, and all four
/// live together in Basic's <c>SentinelWholeFileWriteTools</c>; Advanced has no whole-file-write
/// tools at all. (Distinguish those from <em>mutating</em> tools generally, a much larger set
/// spanning both projects — Advanced's mutators are semantic refactorings, not escape hatches for
/// an oversized text edit, so they never appear here.)
/// </para>
/// </remarks>
public sealed class WriteToolAdviceHelper
{
    /// <summary>
    /// Tool name → the <c>[McpServerToolType]</c> class that declares it, for every tool this
    /// helper may name. Only escape-hatch tools need an entry; this is not a full tool census.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ToolToDeclaringClass =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WriteFile"] = "SentinelWholeFileWriteTools",
            ["DeleteFile"] = "SentinelWholeFileWriteTools",
            ["ApplyDiff"] = "SentinelWholeFileWriteTools",
            ["ApplyUnifiedDiff"] = "SentinelWholeFileWriteTools",
            ["Member"] = "SentinelRefactoringTools",
            ["RenameSymbol"] = "SentinelRefactoringTools",
            ["ChangeSignature"] = "SentinelRefactoringTools",
            ["ExtractMethodSafe"] = "SentinelRefactoringTools",
        };

    private readonly HashSet<string> _activeToolClasses;

    /// <param name="activeToolClasses">
    /// The resolved tool-class set for this server — the same value
    /// <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/> returns, after
    /// <c>--mode</c>/<c>--include-tools</c>/<c>--exclude-tools</c> have all been applied.
    /// </param>
    public WriteToolAdviceHelper(IEnumerable<string> activeToolClasses)
    {
        _activeToolClasses = new HashSet<string>(activeToolClasses, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A helper that considers every escape-hatch tool exposed. For test fixtures that construct a
    /// tool class directly and aren't exercising gating — they get the full-surface advice, which
    /// is what an ungated server would produce. Tests that <em>are</em> about gating should pass an
    /// explicit class list to the constructor instead.
    /// </summary>
    public static WriteToolAdviceHelper WithAllToolsExposed() =>
        new(ToolToDeclaringClass.Values.Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="toolName"/> is callable on this server. Unknown tool names return
    /// false: a name absent from <see cref="ToolToDeclaringClass"/> can't be vouched for, and the
    /// whole point here is never to name a tool that might not be there.
    /// </summary>
    public bool IsExposed(string toolName) =>
        ToolToDeclaringClass.TryGetValue(toolName, out var declaringClass) &&
        _activeToolClasses.Contains(declaringClass);

    /// <summary>
    /// Picks the best available way to land an edit that was rejected for being too large, in
    /// preference order: whole-file rewrite, then unified diff, then a structural Roslyn tool, then
    /// splitting the edit up. The last is always reachable, so this never returns "no options".
    /// </summary>
    /// <param name="rejectingToolName">
    /// The tool that refused the edit, named in the <see cref="WriteEscapeRoute.SplitIntoSmallerEdits"/>
    /// phrasing so the advice reads as "call this again, smaller" rather than something abstract.
    /// </param>
    public WriteAdvice AdviseForOversizedEdit(string rejectingToolName)
    {
        if (IsExposed("WriteFile"))
        {
            return new WriteAdvice(
                WriteEscapeRoute.WholeFileRewrite,
                ["WriteFile"],
                "For a whole-file rewrite, use WriteFile(operation=ReplaceFile).");
        }

        var diffTools = new[] { "ApplyUnifiedDiff", "ApplyDiff" }.Where(IsExposed).ToArray();
        if (diffTools.Length > 0)
        {
            return new WriteAdvice(
                WriteEscapeRoute.UnifiedDiff,
                diffTools,
                $"For an edit this size, use {diffTools[0]} instead.");
        }

        var structuralTools = new[] { "RenameSymbol", "ChangeSignature", "ExtractMethodSafe", "Member" }
            .Where(IsExposed).ToArray();
        if (structuralTools.Length > 0)
        {
            return new WriteAdvice(
                WriteEscapeRoute.StructuredEdit,
                structuralTools,
                $"If this is a structural change (rename, signature, extract, add/replace a member) rather than free text, use the matching Roslyn tool ({string.Join(", ", structuralTools)}) — those have no size limit. " +
                $"Otherwise split the edit into several smaller {rejectingToolName} calls, one per contiguous region.");
        }

        return new WriteAdvice(
            WriteEscapeRoute.SplitIntoSmallerEdits,
            [rejectingToolName],
            $"Split the edit into several smaller {rejectingToolName} calls, one per contiguous region. No whole-file write tool is available on this server.");
    }
}

namespace RoslynSentinel.Common;

/// <summary>
/// The kind of escape hatch <see cref="WriteToolAdviceHelper"/> selected for an edit that a
/// narrow tool refused.
/// </summary>
public enum WriteEscapeRoute
{
    /// <summary>
    /// Rewrite the whole file (WriteFile). Least surgical, so it is only ever named last and is
    /// never the primary route of oversize advice.
    /// </summary>
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
/// Advice for a caller whose edit exceeded its tool's limits: the primary (first-named) route,
/// every tool the advice names (all verified present on this server's surface), and ready-made
/// phrasing.
/// </summary>
/// <param name="Route">The first, most preferred escape hatch the sentence names.</param>
/// <param name="ToolNames">
/// Tools the caller may be told to use, in the order the sentence names them. Never contains an
/// unregistered tool; the rejecting tool itself appears for the split-into-smaller-calls step.
/// </param>
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
/// <c>WriteFile(operation=ReplaceFile)</c> -> a tool the run had deliberately gated off (see
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
/// granularity, but advice names <em>tools</em>, and <c>WholeFileWriteTools</c> alone holds
/// four of them -> so "is that class active?" cannot answer "may I mention ApplyUnifiedDiff?"
/// without the tool->class map below.
/// </para>
/// <para>
/// Basic-only by design. Every tool this can name lives in Basic: the gated whole-file-write
/// tools in <c>WholeFileWriteTools</c> and the structural tools in <c>RefactoringTools</c>;
/// Advanced has no whole-file-write tools at all. (Distinguish those from <em>mutating</em>
/// tools generally, a much larger set spanning both projects -> Advanced's mutators are semantic refactorings, not escape hatches for
/// an oversized text edit, so they never appear here.)
/// </para>
/// </remarks>
public sealed class WriteToolAdviceHelper
{
    /// <summary>
    /// Tool name -> the <c>[McpServerToolType]</c> class that declares it, for every tool this
    /// helper may name. Only escape-hatch tools need an entry; this is not a full tool census.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ToolToDeclaringClass =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WriteFile"] = "WholeFileWriteTools",
            ["DeleteFile"] = "WholeFileWriteTools",
            ["ApplyDiff"] = "WholeFileWriteTools",
            ["ApplyUnifiedDiff"] = "WholeFileWriteTools",
            ["Member"] = "RefactoringTools",
            ["RenameSymbol"] = "RefactoringTools",
            ["ChangeSignature"] = "RefactoringTools",
            ["ExtractMethodSafe"] = "RefactoringTools",
        };

    private readonly HashSet<string> _activeToolClasses;
    private readonly IReadOnlySet<string>? _allowedToolNames;

    /// <param name="activeToolClasses">
    /// The resolved tool-class set for this server -> the same value
    /// <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/> returns, after
    /// <c>--mode</c>/<c>--include-tools</c>/<c>--exclude-tools</c> have all been applied.
    /// </param>
    /// <param name="allowedToolNames">
    /// Per-tool allow-list (exclusive claude-lean mode), or null when every tool of an active class
    /// is exposed. A tool of an active class that is not listed is not callable, so it is not named.
    /// </param>
    public WriteToolAdviceHelper(IEnumerable<string> activeToolClasses, IReadOnlySet<string>? allowedToolNames = null)
    {
        _activeToolClasses = new HashSet<string>(activeToolClasses, StringComparer.OrdinalIgnoreCase);
        _allowedToolNames = allowedToolNames;
    }

    /// <summary>
    /// A helper that considers every escape-hatch tool exposed. For test fixtures that construct a
    /// tool class directly and aren't exercising gating -> they get the full-surface advice, which
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
        _activeToolClasses.Contains(declaringClass) &&
        (_allowedToolNames is null || _allowedToolNames.Contains(toolName));

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
        var clauses = new List<string>();
        var toolNames = new List<string>();
        WriteEscapeRoute? primaryRoute = null;

        // 1. Structural, Member(replace) first and by name: the common oversize case is replacing a
        // whole method/property/constructor body, which Member does with no size limit.
        if (IsExposed("Member"))
        {
            clauses.Add("If you are replacing a whole method/property/constructor, use Member(operation: replace) - it has no size limit.");
            toolNames.Add("Member");
            primaryRoute = WriteEscapeRoute.StructuredEdit;
        }

        var otherStructuralTools = new[] { "RenameSymbol", "ChangeSignature", "ExtractMethodSafe" }
            .Where(IsExposed).ToArray();
        if (otherStructuralTools.Length > 0)
        {
            clauses.Add($"For a rename, signature change or extraction, use {string.Join("/", otherStructuralTools)}.");
            toolNames.AddRange(otherStructuralTools);
            primaryRoute ??= WriteEscapeRoute.StructuredEdit;
        }

        // 2. Diff, for several scattered changes in one file.
        var diffTools = new[] { "ApplyDiff", "ApplyUnifiedDiff" }.Where(IsExposed).ToArray();
        if (diffTools.Length > 0)
        {
            clauses.Add($"For several scattered changes in one file, use {string.Join(" or ", diffTools)}.");
            toolNames.AddRange(diffTools);
            primaryRoute ??= WriteEscapeRoute.UnifiedDiff;
        }

        // 3. Split into smaller calls of the rejecting tool - always reachable, since the agent just
        // called it.
        clauses.Add(clauses.Count > 0
            ? $"Otherwise split the edit into several smaller {rejectingToolName} calls, one per contiguous region."
            : $"Split the edit into several smaller {rejectingToolName} calls, one per contiguous region.");
        toolNames.Add(rejectingToolName);
        primaryRoute ??= WriteEscapeRoute.SplitIntoSmallerEdits;

        // 4. Whole-file rewrite, last and explicitly conditional: it is the least surgical option.
        if (IsExposed("WriteFile"))
        {
            clauses.Add("Only if you are genuinely rewriting most of the file, use WriteFile(operation=ReplaceFile).");
            toolNames.Add("WriteFile");
        }
        else if (primaryRoute == WriteEscapeRoute.SplitIntoSmallerEdits)
        {
            clauses.Add("No whole-file write tool is available on this server.");
        }

        return new WriteAdvice(primaryRoute.Value, toolNames, string.Join(" ", clauses));
    }
}

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Named tool allow-lists for the model-facing tool surface. This is the single source for the ModelEval and
/// PlanStepRunner lists; the caller wraps its host in <see cref="FilteringToolHost"/> with one of these.
/// </summary>
public static class AgentToolAllowLists
{
    /// <summary>The lean 18-tool surface: the tools a model needs for read, edit, build and test work on a fixture.</summary>
    public static readonly IReadOnlyList<string> Lean =
    [
        "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet", "Member",
        "RenameSymbol", "CreateFile", "DeleteFile", "Build", "RunTest", "GetMethodSource", "UndoLastApply",
        "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
    ];

    /// <summary>
    /// The lean surface plus GetDiagnostics: the tools the PlanStepRunner model may call. The runner itself calls
    /// LoadSolution, Build and RunTest on the raw MCP client, not through this list.
    /// </summary>
    public static readonly IReadOnlyList<string> PlanStepRunnerSurface = [.. Lean, "GetDiagnostics"];
}

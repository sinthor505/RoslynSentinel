namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Canonical mode-name -> tool-class-name mapping, one entry per <c>[McpServerToolType]</c>
/// class. This is the single source of truth for which classes a given <c>--mode</c> value
/// activates; <c>--include-tools</c>/<c>--exclude-tools</c> then adjust the resulting class set
/// by name (see <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/>).
/// </summary>
public static class ToolClassRegistry
{
    /// <summary>Modes registered by Basic's <c>AddRoslynSentinelToolsBasic</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> BasicModeToToolClasses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Workspace"] = ["SentinelWorkspaceTools", "DocumentationTools", "SentinelSymbolTools", "GitTools"],
            ["Admin"] = ["AdminTools"],
            ["WholeFileWrite"] = ["WholeFileWriteTools"],
            ["Refactor"] = ["RefactoringTools"],
            // Modernize/Quality/Generation/Asyncify register no classes in Basic today (commented
            // out pending Advanced-only tool classes) -> omitted here since an empty array would
            // be indistinguishable from "mode not recognized" for expansion purposes.

            // Fine-grained sub-modes added by Decision 7 step 4 (docs/current/plans/
            // plan_split_workspace_refactoring_tools_for_di.md, Decision 4 + Addendum A/B):
            // independently opt-in-able, none require "Workspace"/"Refactor". "Workspace" itself
            // already only ever mapped to the 4 facade classes above (never the 5 split classes
            // directly), so Addendum B's narrowing requires no change here -> the one genuinely
            // new umbrella is "WorkspaceFileContent" below.
            ["WorkspaceFileIO"] = ["WorkspaceFileEditTools"],
            ["WorkspaceBuildTest"] = ["WorkspaceBuildTestTools"],
            ["WorkspaceProjectManagement"] = ["WorkspaceProjectManagementTools"],
            ["WorkspaceReadNav"] = ["WorkspaceReadNavigationTools"],
            ["WorkspaceHealthMisc"] = ["WorkspaceHealthMiscTools"],
            // New umbrella (Addendum B): file read/navigate/edit as its own coherent concern,
            // independent of solution/workspace lifecycle ("Workspace" above).
            ["WorkspaceFileContent"] = ["WorkspaceFileEditTools", "WorkspaceReadNavigationTools"],
            ["RefactorSignature"] = ["RefactoringSignatureTools"],
            ["RefactorStructural"] = ["RefactoringSignatureTools"],
            ["RefactorExtractionDocs"] = ["RefactoringExtractionDocsTools"],
            ["SymbolNavigation"] = ["SymbolNavigationTools"],
            ["SymbolRelationship"] = ["SymbolRelationshipTools"],
        };

    /// <summary>
    /// Modes registered by Advanced's <c>AddRoslynSentinelToolsAdvanced</c>, which include
    /// everything in <see cref="BasicModeToToolClasses"/> (Workspace/Admin/WholeFileWrite/Refactor
    /// carry over verbatim, with Refactor gaining one Advanced-only extra class) plus the
    /// Advanced-only modes below.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AdvancedModeToToolClasses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Workspace"] = ["SentinelWorkspaceTools", "DocumentationTools", "SentinelSymbolTools", "GitTools"],
            ["Admin"] = ["AdminTools"],
            ["WholeFileWrite"] = ["WholeFileWriteTools"],
            ["Refactor"] = ["RefactoringTools", "AdvancedRefactoringTools"],
            ["Intelligence"] = ["IntelligenceTools", "ScanTools"],
            ["Modernize"] = ["ModernizationTools"],
            ["Quality"] = ["QualityTools"],
            ["Generation"] = ["GenerationTools", "CommentingTools"],
            ["Asyncify"] = ["AsyncifyTools"],
        };

    /// <summary>
    /// Tool classes registered whenever any of Refactor/Modernize/Quality/Generation is active
    /// (Advanced only -> <c>CodemodTools</c> itself is Advanced-only, so this rule is a
    /// no-op for Basic). Kept separate from the per-mode maps above because it's an "any of"
    /// rule spanning four modes rather than a single mode's own class list.
    /// </summary>
    public static readonly string[] CodemodTriggerModes = ["Refactor", "Modernize", "Quality", "Generation"];
    public const string CodemodToolClass = "CodemodTools";
}

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Canonical mode-name -> tool-class-name mapping, one entry per <c>[McpServerToolType]</c>
/// class. This is the single source of truth for which classes a given <c>--mode</c> value
/// activates; <c>--include-tools</c>/<c>--exclude-tools</c> then adjust the resulting class set
/// by name (see <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/>).
/// </summary>
public static class ToolClassRegistry
{
    /// <summary>
    /// Name of the opt-in lean Claude toolset (proposal_reduce_tool_schema_token_cost.md, Step 4b,
    /// "Core"). Unlike every other mode it also applies a per-tool allow-list
    /// (<see cref="ClaudeLeanToolNames"/>), because the classes holding the Core tools also hold
    /// tools outside it. It is an exclusive mode: "all" never expands to it (see
    /// <see cref="ExclusiveModes"/>), and combining it with another mode narrows that mode's
    /// classes to the same allow-list.
    /// </summary>
    public const string ClaudeLeanMode = "claude-lean";

    /// <summary>Modes that "--mode=all" must not expand to, because they restrict rather than add tools.</summary>
    public static readonly IReadOnlySet<string> ExclusiveModes =
        new HashSet<string>([ClaudeLeanMode], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The 26 MCP tool names the <see cref="ClaudeLeanMode"/> mode exposes: the proposal's 25-tool Core set
    /// plus McpToolsetControl, which switches the on-demand toolsets (<see cref="ToolsetCatalog"/>) at runtime.
    /// McpServerStatus is declared by ServerStatusTools, which is registered outside the class
    /// registry, and passes through the same allow-list.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaudeLeanToolNames = new HashSet<string>(
        [
            "LoadSolution", "ReadFile", "GetFileOutline", "GetMethodSource", "GetLargeResult",
            "Search", "FindReferences", "InspectSymbol", "LocateSymbol", "GetDiagnostics",
            "Build", "RunTest", "Git", "ReplaceSnippet", "Member",
            "UsingDirective", "RenameSymbol", "WriteFile", "CreateFile", "DeleteFile",
            "UndoLastApply", "McpServerControl", "McpServerStatus", "AcknowledgeExternalFileChanges", "ListExternalDiskChanges",
            ToolsetCatalog.ControlToolName,
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Tool classes that declare at least one <see cref="ClaudeLeanToolNames"/> tool (the same in Basic and Advanced), plus
    /// DeclarationTools and ParameterEditTools, whose single tools (Declaration, ParameterEdit) are on-demand only: the allow-list keeps
    /// them out of the startup surface and the <c>declarations</c> toolset adds them. Listed here so the class exists in claude-lean and McpServerStatus can name its mode.
    /// </summary>
    private static readonly string[] ClaudeLeanToolClasses =
    [
        "WorkspaceTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools",
        "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools",
        "AdminTools", "WholeFileWriteTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools",
    ];

    /// <summary>
    /// Tool classes whose dependencies claude-lean registers WITHOUT registering their tools (the per-tool
    /// allow-list filters every non-Core tool out at startup), so <c>McpToolsetControl</c> can build any on-demand
    /// tool later and have its class constructed. These are the facade classes of the "Claude" mode; a tool name
    /// also declared by a split class behind a facade resolves to the facade. Only claude-lean uses this.
    /// DeclarationTools and ParameterEditTools (the merged Declaration and ParameterEdit tools) are the entries that are not facades
    /// of the "Claude" mode: they exist only in claude-lean, so their Impl dependencies are covered by the Structural/Signature entries above.
    /// </summary>
    public static readonly IReadOnlySet<string> ClaudeLeanOnDemandToolClasses = new HashSet<string>(
        [
            "WorkspaceTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools",
            "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools",
            "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools", "DeclarationTools", "ParameterEditTools",
        ],
        StringComparer.Ordinal);

    /// <summary>Modes registered by Basic's <c>AddRoslynSentinelToolsBasic</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> BasicModeToToolClasses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Workspace"] = ["WorkspaceTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools"],
            ["Admin"] = ["AdminTools"],
            ["WholeFileWrite"] = ["WholeFileWriteTools"],
            ["Refactor"] = ["RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools"],
            ["Claude"] = ["WorkspaceTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools", "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools", "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools"],
            [ClaudeLeanMode] = ClaudeLeanToolClasses,

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
            ["RefactoringStructural"] = ["RefactoringStructuralTools"],
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
            ["Workspace"] = ["WorkspaceTools", "DocumentationTools", "SymbolNavigationTools", "GitTools"],
            ["Admin"] = ["AdminTools"],
            ["WholeFileWrite"] = ["WholeFileWriteTools"],
            ["Refactor"] = ["RefactoringTools", "AdvancedRefactoringTools"],
            ["Intelligence"] = ["IntelligenceTools", "ScanTools"],
            ["Modernize"] = ["ModernizationTools"],
            ["Quality"] = ["QualityTools"],
            ["Generation"] = ["GenerationTools", "CommentingTools"],
            ["Asyncify"] = ["AsyncifyTools"],
            // Dedicated mode: the SubAgent tools spawn a model-driven child server and belong with none of
            // the concerns above. Activate via --mode=SubAgentEval (or --include-tools=SubAgentEvalTools).
            // Deliberately absent from "Claude": a child server must never expose them (see
            // SubAgentChildServerLauncher.ExcludedToolClasses).
            ["SubAgentEval"] = ["SubAgentEvalTools"],
            ["SubAgent"] = ["SubAgentTools"],
            ["Claude"] = ["WorkspaceTools", "DocumentationTools", "SymbolNavigationTools", "SymbolRelationshipTools", "GitTools", "RefactoringExtractionDocsTools", "RefactoringStructuralTools", "RefactoringSignatureTools", "AdvancedRefactoringTools", "AdminTools", "WholeFileWriteTools"],
            [ClaudeLeanMode] = ClaudeLeanToolClasses,
        };

    /// <summary>
    /// Tool classes registered whenever any of Refactor/Modernize/Quality/Generation is active
    /// (Advanced only -> <c>CodeTransformationTools</c> itself is Advanced-only, so this rule is a
    /// no-op for Basic). Kept separate from the per-mode maps above because it's an "any of"
    /// rule spanning four modes rather than a single mode's own class list.
    /// </summary>
    public static readonly string[] CodeTransformTriggerModes = ["Refactor", "Modernize", "Quality", "Generation"];
    public const string CodeTransformToolClass = "CodeTransformationTools";

    /// <summary>
    /// Inverts both mode maps into tool class name -> the mode names that enable it (Basic modes
    /// first, then Advanced-only ones, no duplicates). Feeds McpServerStatus's per-tool
    /// <c>enabledBy</c> hint; "Claude"/"all"-style umbrella modes are listed like any other.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildClassToModes()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var map in new[] { BasicModeToToolClasses, AdvancedModeToToolClasses })
        {
            foreach (var (mode, classes) in map)
            {
                foreach (var className in classes)
                {
                    if (!result.TryGetValue(className, out var modes))
                    {
                        modes = [];
                        result[className] = modes;
                    }

                    if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                    {
                        modes.Add(mode);
                    }
                }
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }
}

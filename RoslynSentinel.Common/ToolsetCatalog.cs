namespace RoslynSentinel.Common;

/// <summary>
/// The on-demand toolsets <c>McpToolsetControl</c> can switch on and off (proposal_reduce_tool_schema_token_cost.md,
/// Step 4b). A closed enum so the emitted schema lists every legal value. The proposal's hyphenated names map
/// as: <c>declarations</c>, <c>moveExtract</c> (move-extract), <c>projectAdmin</c> (project-admin).
/// Adding a toolset: (1) add the enum member here; (2) add it to <c>ToolsetCatalog.ToolsBySet</c>; (3) add it to
/// <c>ToolsetCatalog.Summaries</c>; (4) add its name and tools to the [Description] of <c>McpToolsetControl</c> in
/// RoslynSentinel.Tools.Basic/ToolsetControlTools.cs; (5) add a [TestCase] to
/// <c>Enable_AddsTheWholeSet_AndAccountsForEveryName</c> in RoslynSentinel.Tests.Server/McpToolsetControlTests.cs;
/// if the tools live in a new class, also list the class in <c>ToolClassRegistry.ClaudeLeanOnDemandToolClasses</c>.
/// </summary>
public enum ToolSetName
{
    declarations,
    moveExtract,
    projectAdmin,
    testCategories,
    modelEval,
    git,
}

/// <summary>
/// Static catalog of which tools each <see cref="ToolSetName"/> carries. Pure data (no state), so a static
/// class is appropriate; the mutable "which sets are on" state lives in <see cref="ToolsetService"/>.
/// </summary>
public static class ToolsetCatalog
{
    /// <summary>Name of the dynamic toolset tool; it is a member of the claude-lean Core set, never of a toolset.</summary>
    public const string ControlToolName = "McpToolsetControl";

    private static readonly IReadOnlyDictionary<ToolSetName, string[]> ToolsBySet =
        new Dictionary<ToolSetName, string[]>
        {
            [ToolSetName.declarations] =
            [
                // Declaration replaces ModifyModifier, ChangeAccessibility, ModifyAttribute and ModifyBaseType in claude-lean;
                // those four remain registered in claude and the other modes. ParameterEdit likewise replaces MethodSignature and
                // ConstructorParameter in claude-lean only.
                "Declaration", "ParameterEdit", "ModifyEnum", "ChangeSignature", "SyncTypeAndFilename", "UsingDirective", "TypedMember",
            ],
            [ToolSetName.moveExtract] =
            [
                "MoveMember", "MoveType", "MoveAllTypesToFiles", "ExtractLocalVariable", "ExtractMembers",
                "ExtractMethod", "Inline", "InlineClass", "Introduce", "IntroduceParameterObject", "WrapRange",
                "InvertAssignments", "ConvertAnonymousToNamed", "SyncInterface", "SummaryComment",
                "DeleteUnusedSymbol", "PreviewRenameImpact", "ApplyDiff", "ApplyUnifiedDiff", "SemanticFindReplace",
                "NamedArguments", "QualifyCrefs", "WriteFile",
            ],
            [ToolSetName.projectAdmin] =
            [
                "CreateProject", "SplitProjectByFolder", "ListSolutionItems", "ListWorkspaceSolutions",
                "ListProjectFrameworkTargets", "Features", "ProjectDoc", "GetWorkspaceHealth", "GetOperationDetail",
                "RetryFailedChanges", "GetTypeInfo", "QuerySymbolRelationships",
                "GetBestInsertionPoint", "GetDiagnostics", "InspectSymbol",
            ],
            [ToolSetName.testCategories] =
            [
                "TagTestCategories",
            ],
            [ToolSetName.modelEval] =
            [
                // SubAgentTools lives in Tools.Advanced: on a Basic server the name is reported unavailable.
                "SubAgent",
            ],
            [ToolSetName.git] =
            [
                "GitRemote",
            ],
        };

    private static readonly IReadOnlyDictionary<ToolSetName, string> Summaries =
        new Dictionary<ToolSetName, string>
        {
            [ToolSetName.declarations] = "change declarations: modifiers, accessibility, attributes, base types, signatures, constructor parameters, enums, sync type and filename, UsingDirective, TypedMember (generate a typed property or field)",
            [ToolSetName.moveExtract] = "move/extract/inline/introduce refactors, interface and doc sync, safe delete, rename preview, ApplyDiff, ApplyUnifiedDiff, SemanticFindReplace, NamedArguments, QualifyCrefs, WriteFile",
            [ToolSetName.projectAdmin] = "projects and workspace: create/split projects, solution and framework listings, Features, ProjectDoc, workspace health, operation details, retry, type info, symbol relationships, GetDiagnostics, InspectSymbol",
            [ToolSetName.testCategories] = "plan test-category attributes (NUnit/xUnit/MSTest) from the production types each test references (TagTestCategories, dry run by default, dryRun: false applies)",
            [ToolSetName.modelEval] = "dispatch a task to a local model in a child server (SubAgent, Advanced only)",
            [ToolSetName.git] = "git branch, checkout, push, fetch, pull, tag, stash and worktree (GitRemote)",
        };

    /// <summary>Every on-demand tool name across all sets.</summary>
    public static readonly IReadOnlySet<string> AllToolNames =
        new HashSet<string>(ToolsBySet.Values.SelectMany(n => n), StringComparer.Ordinal);

    /// <summary>The exact tool names in <paramref name="toolSet"/>, in declaration order.</summary>
    public static IReadOnlyList<string> GetToolNames(ToolSetName toolSet) => ToolsBySet[toolSet];

    /// <summary>The set a tool name belongs to, or null if it is in no set.</summary>
    public static ToolSetName? FindSet(string toolName)
    {
        foreach (var (set, names) in ToolsBySet)
        {
            if (names.Contains(toolName, StringComparer.Ordinal))
            {
                return set;
            }
        }

        return null;
    }

    /// <summary>
    /// One line per set: its enum value and tools, for the <c>McpToolsetControl</c> tool description.
    /// Kept short on purpose, since the description is paid for in every session.
    /// </summary>
    public static string DescribeSets() =>
        string.Join(" ", Enum.GetValues<ToolSetName>().Select(s => $"{s} = {Summaries[s]}."));
}

namespace RoslynSentinel.Common;

/// <summary>
/// The on-demand toolsets <c>McpToolsetControl</c> can switch on and off (proposal_reduce_tool_schema_token_cost.md,
/// Step 4b). A closed enum so the emitted schema lists every legal value. The proposal's hyphenated names map
/// as: <c>declarations</c>, <c>moveExtract</c> (move-extract), <c>projectAdmin</c> (project-admin).
/// </summary>
public enum ToolSetName
{
    declarations,
    moveExtract,
    projectAdmin,
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
                "Declaration", "ParameterEdit", "ModifyEnum", "ChangeSignature", "SyncTypeAndFilename",
            ],
            [ToolSetName.moveExtract] =
            [
                "MoveMember", "MoveType", "MoveAllTypesToFiles", "ExtractLocalVariable", "ExtractMembers",
                "ExtractMethodSafe", "Inline", "InlineClass", "Introduce", "IntroduceParameterObject", "WrapRange",
                "InvertAssignments", "ConvertAnonymousToNamed", "SyncInterface", "SummaryComment",
                "SafeDeleteUnusedSymbol", "PreviewRenameImpact", "ApplyDiff", "ApplyUnifiedDiff", "SemanticFindReplace",
            ],
            [ToolSetName.projectAdmin] =
            [
                "CreateProject", "SplitProjectByFolder", "ListSolutionItems", "ListWorkspaceSolutions",
                "ListProjectFrameworkTargets", "Features", "ProjectDoc", "GetWorkspaceHealth", "GetOperationDetail",
                "RetryFailedChanges", "IsSessionHalted", "GetTypeInfo", "QuerySymbolRelationships",
                "GetBestInsertionPoint",
            ],
        };

    private static readonly IReadOnlyDictionary<ToolSetName, string> Summaries =
        new Dictionary<ToolSetName, string>
        {
            [ToolSetName.declarations] = "change declarations: modifiers, accessibility, attributes, base types, signatures, constructor parameters, enums, sync type and filename",
            [ToolSetName.moveExtract] = "move/extract/inline/introduce refactors, interface and doc sync, safe delete, rename preview, ApplyDiff, ApplyUnifiedDiff, SemanticFindReplace",
            [ToolSetName.projectAdmin] = "projects and workspace: create/split projects, solution and framework listings, Features, ProjectDoc, workspace health, operation details, retry, type info, symbol relationships",
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

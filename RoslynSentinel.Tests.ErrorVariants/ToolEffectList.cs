namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>What a tool does to the workspace, as far as replaying a recorded result is concerned.</summary>
public enum ToolEffect
{
    /// <summary>Does not change workspace, repo, file or process state; a recorded result is a valid answer.</summary>
    Read,

    /// <summary>Edits files, git, build output or process state; a recorded result is not a valid answer.</summary>
    Write,

    /// <summary>Not on the list. The caller decides (plan Step 7 maps it to Other:&lt;tool&gt;).</summary>
    Unknown,
}

/// <summary>
/// The hand-classified read/write split of the MCP tools (no tool sets ReadOnly/Destructive, so the split is not derivable).
/// Every tool in the 55-tool header of the checked-in F transcript is listed EXPLICITLY; a tool that is not listed is
/// <see cref="ToolEffect.Unknown"/>, never silently a read. Whole-tool classification: a tool whose operation parameter picks
/// read vs write (view/verify vs add/replace) is Write, so a replay never answers an edit from a recorded view.
/// A static list holder with no data (DTO-EXEMPT: static list holder).
/// </summary>
public static class ToolEffectList
{
    private static readonly string[] ReadNames =
    [
        "ListSolutionItems",
        "GetDiagnostics",
        "ReadFile",
        "GetWorkspaceHealth",
        "ListProjectFrameworkTargets",
        "GetMethodSource",
        "GetFileOutline",
        "ListAll",
        "GetOperationDetail",
        "GetLargeResult",
        "Search",
        "LocateSymbol",
        "InspectSymbol",
        "GetTypeInfo",
        "QuerySymbolRelationships",
        "GetBestInsertionPoint",
        "PreviewRenameImpact",
        "FindReferences",
    ];

    private static readonly string[] WriteNames =
    [
        "ReplaceSnippet",
        "CreateFile",
        "RetryFailedChanges",
        "Build",
        "RunTest",
        "DeleteUnusedSymbol",
        "UndoLastApply",
        "DeleteFile",
        "RenameSymbol",
        "ChangeAccessibility",
        "ModifyEnum",
        "ModifyAttribute",
        "ModifyModifier",
        "ModifyBaseType",
        "SyncTypeAndFilename",
        "TypedMember",
        "ExtractLocalVariable",
        "ExtractMethod",
        "ChangeSignature",
        "MoveAllTypesToFiles",
        "MoveMember",
        "ConvertAnonymousToNamed",
        "InlineClass",
        "InvertAssignments",
        "IntroduceParameterObject",
        "Introduce",
        "ExtractMembers",
        "Inline",
        "WrapRange",
        "MoveType",

        // The tools below have an operation parameter that picks read (view/verify/list) or write, so the whole tool is Write.
        "Member", // defaulted to Write: unclear (view vs add/replace/remove)
        "MethodSignature", // defaulted to Write: unclear (view vs add/remove)
        "ConstructorParameter", // defaulted to Write: unclear (view vs add/remove)
        "UsingDirective", // defaulted to Write: unclear (view vs add/remove)
        "SummaryComment", // defaulted to Write: unclear (view vs add/remove)
        "SyncInterface", // defaulted to Write: unclear (verify coverage vs generate/sync stubs)
        "ProjectDoc", // defaulted to Write: unclear (read/list vs write/append/close)
    ];

    private static readonly HashSet<string> ReadSet = new(ReadNames, StringComparer.Ordinal);
    private static readonly HashSet<string> WriteSet = new(WriteNames, StringComparer.Ordinal);

    /// <summary>Tools whose recorded result may be served again, in list order.</summary>
    public static IReadOnlyCollection<string> ReadTools => ReadNames;

    /// <summary>Tools whose recorded result must not be served (they change state, or may), in list order.</summary>
    public static IReadOnlyCollection<string> WriteTools => WriteNames;

    /// <summary>Read or Write for a listed tool (ordinal, case-sensitive), Unknown for any other name.</summary>
    public static ToolEffect Classify(string toolName)
    {
        if (ReadSet.Contains(toolName))
        {
            return ToolEffect.Read;
        }

        return WriteSet.Contains(toolName) ? ToolEffect.Write : ToolEffect.Unknown;
    }

    /// <summary>True when <paramref name="toolName"/> is classified <see cref="ToolEffect.Read"/>.</summary>
    public static bool IsRead(string toolName) => Classify(toolName) == ToolEffect.Read;
}

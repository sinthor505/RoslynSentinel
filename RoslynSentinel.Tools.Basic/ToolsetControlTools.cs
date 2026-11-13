using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>McpToolsetControlTool</c>: switches on-demand toolsets on and off at runtime so a lean session
/// (<c>--mode claude-lean</c>, ~26 tools) can pull in the rest only when needed. Registered only in claude-lean
/// (see ServiceRegistrationExtensionsBasic); the state and the tool-collection mutation live in
/// <see cref="ToolsetService"/>, and the SDK turns the collection change into <c>tools/list_changed</c>.
/// proposal_reduce_tool_schema_token_cost.md, Step 4b stage 2.
/// </summary>
[McpServerToolType]
public class ToolsetControlTools
{
    private readonly ToolsetService _toolsets;

    public ToolsetControlTools(ToolsetService toolsets)
    {
        _toolsets = toolsets;
    }

    [McpServerTool(Name = ToolsetCatalog.ControlToolName)]
    [Produces(DataTag.ResultOnly)]
    [Description("Switches an on-demand toolset on or off (tools/list_changed refreshes your tool list). " +
        "declarations = Declaration, ParameterEdit, ModifyEnum, SyncTypeAndFilename, TypedMember. " +
        "moveExtract = MoveType, MoveAllTypesToFiles, Extract*, Inline*, Introduce*, WrapRange, InvertAssignments, ConvertAnonymousToNamed, SyncInterface, SummaryComment, DeleteUnusedSymbol, PreviewRenameImpact, ApplyDiff, ApplyUnifiedDiff, NamedArguments, QualifyCrefs. " +
        "projectAdmin = CreateProject, SplitProjectByFolder, ListSolutionItems, ListWorkspaceSolutions, ListProjectFrameworkTargets, Features, ProjectDoc, GetWorkspaceHealth, GetOperationDetail, RetryFailedChanges, GetTypeInfo, QuerySymbolRelationships, GetBestInsertionPoint. " +
        "testCategories = TagTestCategories (dry run by default). " +
        "modelEval = SubAgent (Advanced only). " +
        "git = GitRemote (branch, checkout, push, fetch, pull, tag, stash, worktree, config). Already on the startup surface, no enable needed: SemanticFindReplace, InspectSymbol, MoveMember, UsingDirective, GetDiagnostics.")]
    public SentinelCallToolResult<object> McpToolsetControlTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("The toolset to switch.")] ToolSetName toolSet,
        [Description("true = add the set's tools; false = remove them.")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        _ = reason;
        cancellationToken.ThrowIfCancellationRequested();

        ToolsetChange change;
        try
        {
            change = _toolsets.SetEnabled(toolSet, enabled);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(
                    ErrorCode: "UnknownToolset",
                    Message: ex.Message,
                    Detail: $"Valid values for toolSet: {string.Join(", ", Enum.GetNames<ToolSetName>())}."),
            };
        }
        catch (InvalidOperationException ex)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(
                    ErrorCode: "ToolsetsUnavailable",
                    Message: ex.Message,
                    Detail: "Restart the server in a mode with a mutable tool collection, or use a static mode such as --mode claude."),
            };
        }

        string state = change.Enabled ? "on" : "off";
        string summary = change.Changed
            ? $"Toolset {toolSet} is now {state}: {change.AddedTools.Count} tool(s) added, {change.RemovedTools.Count} removed."
            : $"Toolset {toolSet} was already {state}; nothing changed.";
        if (change.UnavailableTools.Count > 0)
        {
            summary += $" Not available on this server: {string.Join(", ", change.UnavailableTools)}.";
        }

        if (change.Changed)
        {
            summary += " A client that does not refresh its tool list after this call needs a /mcp reconnect, or a startup surface that already includes the tool (--toolsets=<set> adds a set at startup). The reconnect resets enabled toolsets, so make this same call again after it; restarting the server instead does not refresh the client's list.";
        }

        return new SentinelCallToolResult<object>
        {
            IsError = false,
            StatusMessage = summary,
            SuccessData = change,
        };
    }
}

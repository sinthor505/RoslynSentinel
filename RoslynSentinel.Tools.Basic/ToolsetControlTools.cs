using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>McpToolsetControl</c>: switches on-demand toolsets on and off at runtime so a lean session
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
    [Description("Turns an on-demand toolset on or off; the server then sends tools/list_changed so your tool list updates. Idempotent. " +
        "declarations = Declaration (modifier, accessibility, attribute, baseType), ParameterEdit (method, constructor), ModifyEnum, ChangeSignature, SyncTypeAndFilename. " +
        "moveExtract = MoveMember, MoveType, MoveAllTypesToFiles, Extract*, Inline*, Introduce*, WrapRange, InvertAssignments, ConvertAnonymousToNamed, SyncInterface, SummaryComment, SafeDeleteUnusedSymbol, PreviewRenameImpact, ApplyDiff, ApplyUnifiedDiff, SemanticFindReplace. " +
        "projectAdmin = CreateProject, SplitProjectByFolder, ListSolutionItems, ListWorkspaceSolutions, ListProjectFrameworkTargets, Features, ProjectDoc, GetWorkspaceHealth, GetOperationDetail, RetryFailedChanges, IsSessionHalted, GetTypeInfo, QuerySymbolRelationships, GetBestInsertionPoint.")]
    public SentinelCallToolResult<object> McpToolsetControl(
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
            summary += " If your client has not refreshed its tool list yet, the tools can still be called by name.";
        }

        return new SentinelCallToolResult<object>
        {
            IsError = false,
            StatusMessage = summary,
            SuccessData = change,
        };
    }
}

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Semantic find-replace: rename a bool property or field and invert its polarity at every site.
/// Resolves a symbol by docCommentId, classifies references by their syntactic role,
/// and rewrites each role with a fixed rule (not free-form templates). Edits go through
/// the compile gate before applying. Supports preview mode (list sites without writing)
/// or apply mode (write the changes).
/// </summary>
[McpServerToolType]
public class SemanticFindReplaceTools
{
    private readonly SemanticReplaceEngine _engine;
    private readonly ValidationEngine _validationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;

    public SemanticFindReplaceTools(
        SemanticReplaceEngine engine,
        ValidationEngine validationEngine,
        IWorkspaceManager workspaceManager,
        ILogger<SemanticFindReplaceTools> logger)
    {
        _engine = engine;
        _validationEngine = validationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "SemanticFindReplace")]
    [Produces(DataTag.ChangeId)]
    [Description("Rename a bool property or field and invert its polarity at every site. " +
        "symbol is a docCommentId (from Search or LocateSymbol). " +
        "operation: invertBoolean renames the symbol and flips its logic (true<->false, Read gets !, NegatedRead loses !). " +
        "mode: preview lists the sites (before/after text) without writing anything; apply performs the changes. " +
        "Mode is mandatory - use preview first to review. " +
        "If any reference is unsupported (e.g. compound assignment |=, ++, ref argument, property pattern, == true), " +
        "the entire operation is refused and the message lists each file:line so you can fix those by hand. " +
        "Returns sites and changeId on success, or a structured error naming the parameter and a correct value.")]
    public async Task<SentinelCallToolResult<object>> SemanticFindReplace(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("The operation to perform.")] SemanticReplaceOperation operation,
        [Description("A docCommentId identifying the bool property or field to rename and invert.")] string symbol,
        [Description("The new name for the symbol. Must be a valid C# identifier and not collide with other members.")] string newName,
        [Description("preview: list the sites (before/after text) without writing. apply: perform the changes.")] SemanticReplaceMode mode,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return InvalidArgument("symbol cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return InvalidArgument("newName cannot be empty.");
        }

        // Call the engine
        var outcome = await _engine.InvertBooleanAndRenameAsync(symbol, newName, cancellationToken);

        // If there's an error, return it immediately
        if (outcome.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = outcome.Error
            };
        }

        // Preview mode: return sites without writing
        if (mode == SemanticReplaceMode.preview)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = true,
                SuccessData = new
                {
                    mode = "preview",
                    sites = outcome.Sites
                }
            };
        }

        // Apply mode: validate and apply the changes
        var applyOutcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine,
            _workspaceManager,
            _logger,
            outcome.Changes,
            "SemanticFindReplace",
            dryRun: false,
            returnDiff: false,
            cancellationToken: cancellationToken);

        if (applyOutcome.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = applyOutcome.Error
            };
        }

        return new SentinelCallToolResult<object>
        {
            IsSuccess = true,
            SuccessData = new
            {
                mode = "apply",
                changeId = applyOutcome.ChangeId,
                sites = outcome.Sites,
                filesChanged = outcome.Changes.Count
            }
        };
    }

    private static SentinelCallToolResult<object> InvalidArgument(string message) =>
        new SentinelCallToolResult<object>
        {
            IsSuccess = false,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"SemanticFindReplace: {message}")
        };
}

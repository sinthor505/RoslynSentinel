using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Semantic find-replace with two operations. invertBoolean renames a bool property or field and
/// inverts its polarity at every site. replaceConstruction rewrites object-creation expressions of a
/// type into calls to a static factory method. Both resolve symbols by docCommentId and rewrite with
/// fixed rules (not free-form templates). Edits go through the compile gate before applying.
/// Supports preview mode (list sites without writing) or apply mode (write the changes).
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
    [Description("Semantic (compiler-aware, not text) find-replace with two operations. " +
        "operation: invertBoolean renames a bool property or field and inverts its polarity at every site (true<->false, Read gets !, NegatedRead loses !); " +
        "required: symbol (docCommentId of the bool member), newName, mode. " +
        "operation: replaceConstruction rewrites object-creation expressions of a type into calls to a static factory method, " +
        "e.g. new Engine(wm, log) -> TestEngines.Engine(wm, log), including target-typed new(...), keeping the argument text unchanged; " +
        "required: symbol (docCommentId of the constructed type T:Ns.Type, or of one constructor M:Ns.Type.#ctor(...) to match only that overload), factory (docCommentId of a static, non-generic method whose return type is the type or a subtype), mode; " +
        "optional: projectFilter. " +
        "replaceConstruction rewrites every site whose arguments the factory accepts (checked by overload resolution at the site) and SKIPS, never blocks on, the others: " +
        "sites with an object or collection initializer, sites whose argument shape does not match the factory, and sites inside the factory's own containing type. " +
        "Each skipped site is returned with file, line and unsupportedReason. The result carries rewritten and skipped counts. " +
        "If the factory's return type is not assignable to the constructed type the whole operation is refused. " +
        "mode: preview lists the sites (before/after text) without writing anything; apply performs the changes and returns a changeId for UndoLastApply. " +
        "Mode is mandatory - use preview first to review. " +
        "invertBoolean alias retarget: a computed inverse alias (a bool property with getter `!X` and setter/init `X = !value`, e.g. SentinelCallToolResult.IsSuccess over IsError) " +
        "cannot be renamed in place; instead pass newName = the existing sibling member (X, or the public property that wraps X). " +
        "Every reference to the alias is then rewritten to the sibling with inverted logic (r.IsSuccess -> !r.IsError, IsSuccess = true -> IsError = false) " +
        "while the alias declaration itself is left untouched, so you can delete it afterwards. " +
        "If any invertBoolean reference is unsupported (e.g. compound assignment |=, ++, ref argument, property pattern, == true), " +
        "the entire operation is refused and the message lists each file:line so you can fix those by hand. " +
        "Returns sites and changeId on success, or a structured error naming the parameter and a correct value.")]
    public async Task<SentinelCallToolResult<object>> SemanticFindReplaceTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("The operation to perform: invertBoolean or replaceConstruction.")] SemanticReplaceOperation operation,
        [Description("A docCommentId. invertBoolean: the bool property or field to rename and invert. replaceConstruction: the constructed type (T:Ns.Type) or one constructor (M:Ns.Type.#ctor(System.Int32)); a constructor id restricts matching to that overload. From Search(mode: symbol), pass exactly as returned, do not construct.")] string symbol,
        [Description("preview: list the sites (before/after text) without writing. apply: perform the changes.")] SemanticReplaceMode mode,
        [Description("invertBoolean only, and required there: the new name for the symbol. Must be a valid C# identifier and not collide with other members, except for an alias retarget where it must be the existing sibling member the computed inverse alias negates. Ignored by replaceConstruction.")] string? newName = null,
        [Description("replaceConstruction only, and required there: docCommentId of the static, non-generic factory method that replaces each construction, e.g. M:Ns.Factories.Create(System.Int32). Its return type must be the constructed type or a subtype. Ignored by invertBoolean. From Search(mode: symbol), pass exactly as returned, do not construct.")] string? factory = null,
        [Description("replaceConstruction only, optional: project name or names separated by commas; only creation sites in those projects are rewritten. Omit to rewrite in every project. Ignored by invertBoolean.")] string? projectFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return InvalidArgument("symbol cannot be empty.");
        }

        var isConstruction = operation == SemanticReplaceOperation.replaceConstruction;
        if (isConstruction)
        {
            if (string.IsNullOrWhiteSpace(factory))
            {
                return InvalidArgument("factory cannot be empty for operation replaceConstruction. Pass the docCommentId of a static factory method, for example 'M:Ns.Factories.CreateType(System.Int32)'.");
            }
        }
        else if (string.IsNullOrWhiteSpace(newName))
        {
            return InvalidArgument("newName cannot be empty (required for operation invertBoolean).");
        }

        // Call the engine
        SemanticReplaceOutcome outcome;
        if (isConstruction)
        {
            try
            {
                outcome = await _engine.ReplaceConstructionAsync(symbol, factory!, projectFilter, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SemanticFindReplace replaceConstruction failed");
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SemanticFindReplace")
                };
            }
        }
        else
        {
            outcome = await _engine.InvertBooleanAndRenameAsync(symbol, newName!, cancellationToken);
        }

        // If there's an error, return it immediately
        if (outcome.Error is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = outcome.Error
            };
        }

        var rewritten = outcome.Sites.Count(s => s.UnsupportedReason is null);
        var skipped = outcome.Sites.Count - rewritten;

        // Preview mode: return sites without writing
        if (mode == SemanticReplaceMode.preview)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = isConstruction
                    ? new
                    {
                        mode = "preview",
                        rewritten,
                        skipped,
                        sites = outcome.Sites
                    }
                    : new
                    {
                        mode = "preview",
                        sites = outcome.Sites
                    }
            };
        }

        // A replaceConstruction apply with nothing to rewrite writes nothing and issues no changeId.
        if (isConstruction && outcome.Changes.Count == 0)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new
                {
                    mode = "apply",
                    rewritten,
                    skipped,
                    filesChanged = 0,
                    message = "No creation site could be rewritten, so nothing was written. See sites for the construction sites that were found and why each was skipped.",
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
                IsError = true,
                ErrorData = applyOutcome.Error
            };
        }

        return new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = isConstruction
                ? new
                {
                    mode = "apply",
                    changeId = applyOutcome.ChangeId,
                    rewritten,
                    skipped,
                    sites = outcome.Sites,
                    filesChanged = outcome.Changes.Count
                }
                : new
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
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"SemanticFindReplace: {message}")
        };
}

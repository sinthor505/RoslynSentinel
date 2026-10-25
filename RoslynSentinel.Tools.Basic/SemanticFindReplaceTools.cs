using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Semantic find-replace with three operations. invertBoolean renames a bool property or field and
/// inverts its polarity at every site. replaceConstruction rewrites object-creation expressions of a
/// type into calls to a static factory method. removeNullForgiving deletes the redundant postfix ! on a
/// non-nullable property or field. All resolve symbols by docCommentId and rewrite with
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
    [Description("Semantic (compiler-aware, not text) find-replace with three operations. " +
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
        "operation: removeNullForgiving deletes the postfix ! from every expression bound to a property/field whose type is non-nullable (symbol = its docCommentId), across the solution or narrowed by projectFilter/fileFilter; preview first. " +
        "required: symbol, mode; optional: projectFilter, fileFilter; newName and factory are rejected. " +
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
        [Description("The operation to perform: invertBoolean, replaceConstruction or removeNullForgiving.")] SemanticReplaceOperation operation,
        [Description("A docCommentId. invertBoolean: the bool property or field to rename and invert. replaceConstruction: the constructed type (T:Ns.Type) or one constructor (M:Ns.Type.#ctor(System.Int32)); a constructor id restricts matching to that overload. removeNullForgiving: the property (P:Ns.Type.Name) or field (F:Ns.Type.name) whose declared type is non-nullable. From Search(mode: symbol), pass exactly as returned, do not construct.")] string symbol,
        [Description("preview: list the sites (before/after text) without writing. apply: perform the changes.")] SemanticReplaceMode mode,
        [Description("invertBoolean only, and required there: the new name for the symbol. Must be a valid C# identifier and not collide with other members, except for an alias retarget where it must be the existing sibling member the computed inverse alias negates. Ignored by replaceConstruction. Rejected by removeNullForgiving.")] string? newName = null,
        [Description("replaceConstruction only, and required there: docCommentId of the static, non-generic factory method that replaces each construction, e.g. M:Ns.Factories.Create(System.Int32). Its return type must be the constructed type or a subtype. Ignored by invertBoolean. Rejected by removeNullForgiving. From Search(mode: symbol), pass exactly as returned, do not construct.")] string? factory = null,
        [Description("Optional: project name or names separated by commas; only sites in those projects are changed. Used by replaceConstruction and removeNullForgiving; ignored by invertBoolean. Omit to cover every project.")] string? projectFilter = null,
        [Description("removeNullForgiving only, optional: glob on the solution-relative path ('*' within a folder, '**' across folders, '?' one character), e.g. 'RoslynSentinel.Tests.Basic/BasicRefactoringTests.cs'; a glob without '/' matches the bare file name. Omit to cover every file. Ignored by invertBoolean and replaceConstruction.")] string? fileFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Validate required parameters
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return InvalidArgument("symbol cannot be empty.");
        }

        switch (operation)
        {
            case SemanticReplaceOperation.replaceConstruction:
                if (string.IsNullOrWhiteSpace(factory))
                {
                    return InvalidArgument("factory cannot be empty for operation replaceConstruction. Pass the docCommentId of a static factory method, for example 'M:Ns.Factories.CreateType(System.Int32)'.");
                }

                break;
            case SemanticReplaceOperation.removeNullForgiving:
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    return InvalidArgument("newName is not accepted for operation removeNullForgiving; it is used only by invertBoolean. Omit it.");
                }

                if (!string.IsNullOrWhiteSpace(factory))
                {
                    return InvalidArgument("factory is not accepted for operation removeNullForgiving; it is used only by replaceConstruction. Omit it.");
                }

                break;
            case SemanticReplaceOperation.invertBoolean:
                if (string.IsNullOrWhiteSpace(newName))
                {
                    return InvalidArgument("newName cannot be empty (required for operation invertBoolean).");
                }

                break;
            default:
                return InvalidArgument($"operation '{operation}' is not supported. Use invertBoolean, replaceConstruction or removeNullForgiving.");
        }

        // Call the engine
        SemanticReplaceOutcome outcome;
        if (operation != SemanticReplaceOperation.invertBoolean)
        {
            try
            {
                outcome = operation == SemanticReplaceOperation.replaceConstruction
                    ? await _engine.ReplaceConstructionAsync(symbol, factory!, projectFilter, cancellationToken)
                    : await _engine.RemoveNullForgivingAsync(symbol, projectFilter, fileFilter, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SemanticFindReplace {Operation} failed", operation);
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
                SuccessData = operation switch
                {
                    SemanticReplaceOperation.replaceConstruction => new
                    {
                        mode = "preview",
                        rewritten,
                        skipped,
                        sites = outcome.Sites
                    },
                    SemanticReplaceOperation.removeNullForgiving => new
                    {
                        mode = "preview",
                        sites = outcome.Sites,
                        filesChanged = outcome.Changes.Count
                    },
                    _ => new
                    {
                        mode = "preview",
                        sites = outcome.Sites
                    }
                }
            };
        }

        // A replaceConstruction apply with nothing to rewrite writes nothing and issues no changeId.
        if (operation == SemanticReplaceOperation.replaceConstruction && outcome.Changes.Count == 0)
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

        // A removeNullForgiving apply with no matching expression writes nothing and issues no changeId.
        if (operation == SemanticReplaceOperation.removeNullForgiving && outcome.Changes.Count == 0)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new
                {
                    mode = "apply",
                    filesChanged = 0,
                    message = "No redundant null-forgiving '!' on this member was found in the selected projects and files, so nothing was written.",
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
            SuccessData = operation == SemanticReplaceOperation.replaceConstruction
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

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
    [Description("Semantic (compiler-aware, not text) find-replace. " +
        "invertBoolean renames a bool property or field and inverts its polarity at every site (true<->false, Read gets !, NegatedRead loses !). " +
        "replaceConstruction rewrites object-creation expressions of a type, including target-typed new(...), into calls to a static factory method with the argument text unchanged, e.g. new Engine(wm, log) -> TestEngines.Engine(wm, log). " +
        "removeNullForgiving deletes the postfix ! from every expression bound to a non-nullable property or field (symbol), across the solution or narrowed by projectFilter/fileFilter. " +
        "mode: preview lists the sites (before/after text) and writes nothing - use it first; apply performs the changes and returns a changeId for UndoLastApply. " +
        "replaceConstruction rewrites every site the factory accepts (overload resolution) and skips the rest (initializer or argument mismatches, sites inside the factory's own type); skipped sites carry file, line and unsupportedReason, at most 3 per reason, and skippedSitesNotListed counts the rest. A factory whose return type is not assignable to the constructed type refuses the whole operation. " +
        "invertBoolean alias retarget: a computed inverse alias (getter !X, setter X = !value, e.g. IsSuccess over IsError) cannot be renamed in place; pass newName = its sibling X and every reference is rewritten to X with inverted logic, the alias declaration stays for you to delete. " +
        "An unsupported invertBoolean reference (compound assignment, ++, ref argument, property pattern, == true) refuses the whole operation and lists each file:line.")]
    public async Task<SentinelCallToolResult<object>> SemanticFindReplaceTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("invertBoolean (required: symbol, newName, mode), replaceConstruction (required: symbol, factory, mode; optional: projectFilter) or removeNullForgiving (required: symbol, mode; optional: projectFilter, fileFilter; newName and factory are rejected).")] SemanticReplaceOperation operation,
        [Description("A docCommentId, passed exactly as returned by Search(mode: symbol), never constructed. invertBoolean: the bool property or field. replaceConstruction: the constructed type (T:Ns.Type), or one constructor (M:Ns.Type.#ctor(System.Int32)) to match only that overload. removeNullForgiving: the non-nullable property (P:Ns.Type.Name) or field (F:Ns.Type.name).")] string symbol,
        [Description("preview: list the sites (before/after text) without writing. apply: perform the changes.")] SemanticReplaceMode mode,
        [Description("invertBoolean only (required there): the new name, a valid C# identifier; for an alias retarget, the existing sibling member the alias negates. Ignored by replaceConstruction. Rejected by removeNullForgiving.")] string? newName = null,
        [Description("replaceConstruction only (required there): docCommentId of the static, non-generic factory method (e.g. M:Ns.Factories.Create(System.Int32)) whose return type is the constructed type or a subtype; pass exactly as returned by Search(mode: symbol). Ignored by invertBoolean. Rejected by removeNullForgiving.")] string? factory = null,
        [Description("Optional, for replaceConstruction and removeNullForgiving: project name(s), comma separated. Omit to cover every project.")] string? projectFilter = null,
        [Description("removeNullForgiving only, optional: glob on the solution-relative path ('*' within a folder, '**' across folders), e.g. 'Proj/File.cs'; without '/' it matches the bare file name. Omit to cover every file.")] string? fileFilter = null,
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
        var (listedSites, skippedSitesNotListed) = CapSkippedSites(outcome.Sites);

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
                        skippedSitesNotListed,
                        sites = listedSites
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
                    skippedSitesNotListed,
                    filesChanged = 0,
                    message = "No creation site could be rewritten, so nothing was written. See sites for the construction sites that were found and why each was skipped.",
                    sites = listedSites
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
                    skippedSitesNotListed,
                    sites = listedSites,
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

    /// <summary>Returns every rewritten site plus at most three skipped sites per distinct reason (first in order), and the count of skipped sites left out.</summary>
    private static (List<SemanticReplaceSite> Sites, int NotListed) CapSkippedSites(IEnumerable<SemanticReplaceSite> sites)
    {
        const int MaxSkippedPerReason = 3;
        var listed = new List<SemanticReplaceSite>();
        var perReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var notListed = 0;
        foreach (var site in sites)
        {
            if (site.UnsupportedReason is null)
            {
                listed.Add(site);
                continue;
            }

            perReason.TryGetValue(site.UnsupportedReason, out var count);
            if (count < MaxSkippedPerReason)
            {
                perReason[site.UnsupportedReason] = count + 1;
                listed.Add(site);
            }
            else
            {
                notListed++;
            }
        }

        return (listed, notListed);
    }

    private static SentinelCallToolResult<object> InvalidArgument(string message) =>
        new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"SemanticFindReplace: {message}")
        };
}

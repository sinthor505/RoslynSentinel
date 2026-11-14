using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>
/// The one not-found path behind <c>ISymbolResolver.ResolveFromWireAsync</c> (RenameSymbol, DeleteUnusedSymbol,
/// PreviewRenameImpact). <see cref="ResolveAsync"/> resolves the id; when it does not resolve it tries, in order: the shape
/// check (an id without a kind prefix can never resolve), the missing-parameter-list fallback
/// (<see cref="MissingParameterListResolver"/>: one method of that name is used and the resolution carries a note; several
/// overloads give an unresolved result with <see cref="EngineErrorCode.MissingParameterListOverloads"/> and their full ids),
/// and finally <see cref="SymbolNotFoundDetail.BuildAsync"/> over the named project (plan_not_found_error_orientation Step 4c).
/// Both <c>PersistentWorkspaceManager</c> and the test fake call this so they cannot disagree.
/// </summary>
public static class SymbolWireResolver
{
    /// <param name="resolveSymbol">Resolves a handle to a symbol, or null when it does not resolve.</param>
    /// <param name="getCommittedSolution">The committed solution, read only on the not-found path.</param>
    /// <param name="projectName">The project the caller named.</param>
    /// <param name="docCommentId">The id the caller passed.</param>
    /// <param name="retryCall">
    /// Optional, for the ranked not-found text only: maps a candidate's docCommentId to the calling tool's own call with only the id
    /// corrected (build it with <see cref="ToolCallHint.Call(string, (string Name, string Value)[])"/> from the request's other
    /// arguments), or null to keep the neutral Search call. With a delegate each candidate also states its exact id. Invoked at
    /// render time only. Last parameter so existing positional callers compile unchanged.
    /// </param>
    /// <param name="isCallable">Optional, for the ranked not-found text only: whether the caller can call a tool by name. Null means every tool is callable (full surface, today's text). Last parameter so existing positional callers compile unchanged.</param>
    public static async Task<SymbolResolution> ResolveAsync(
        Func<SymbolHandle, CancellationToken, Task<ISymbol?>> resolveSymbol,
        Func<CancellationToken, Task<Solution>> getCommittedSolution,
        string projectName,
        string docCommentId,
        CancellationToken cancellationToken,
        Func<string, string?>? retryCall = null,
        Func<string, bool>? isCallable = null)
    {
        SymbolHandle handle = new SymbolHandle(projectName, docCommentId);
        ISymbol? symbol = await resolveSymbol(handle, cancellationToken);
        if (symbol is not null)
        {
            return new SymbolResolution { Symbol = symbol, Handle = handle };
        }

        // Batch H4 R4 T6.1: a changeId from an undo hint ('0cbca485@...') was passed as a docCommentId and got told the symbol
        // "may have been renamed". An ID without a kind prefix can never resolve, so say what the shape is instead.
        if (!Regex.IsMatch(docCommentId ?? "", @"^[NTFPEM]:\S"))
        {
            return new SymbolResolution
            {
                Handle = handle,
                Error = new EngineError(
                    EngineErrorCode.SymbolNotResolved,
                    NotADocCommentIdText(docCommentId),
                    DataTag.SymbolHandle)
            };
        }

        var solution = await getCommittedSolution(cancellationToken);

        // A model often drops the parameter list from a method id (eval run 20261109-233953-684 T8/T10). One method of that
        // name on the type: use it and say so. Several: list the full ids. Anything else is the ranked not-found text below.
        var (unique, overloadIds) = await MissingParameterListResolver.FindAsync(solution, projectName, docCommentId!, cancellationToken);
        if (unique?.GetDocumentationCommentId() is { } fullId)
        {
            var fullHandle = new SymbolHandle(projectName, fullId);
            var byFullId = await resolveSymbol(fullHandle, cancellationToken);
            if (byFullId is not null)
            {
                return new SymbolResolution
                {
                    Symbol = byFullId,
                    Handle = fullHandle,
                    Note = $"docCommentId '{docCommentId}' had no parameter list; it was resolved to the only method of that name, whose full id is '{fullId}'. Pass the full id next time."
                };
            }
        }
        else if (overloadIds.Count > 1)
        {
            return new SymbolResolution
            {
                Handle = handle,
                Error = new EngineError(
                    EngineErrorCode.MissingParameterListOverloads,
                    $"docCommentId '{docCommentId}' is missing its parameter list, and '{docCommentId![(docCommentId!.LastIndexOf('.') + 1)..]}' has {overloadIds.Count} overloads. Pass the full docCommentId of the one you mean:\n"
                    + string.Join("\n", overloadIds.Select(id => "  " + id)),
                    DataTag.SymbolHandle)
            };
        }

        return new SymbolResolution
        {
            Handle = handle,
            Error = new EngineError(
                EngineErrorCode.SymbolNotResolved,
                await BuildNotFoundTextAsync(solution, projectName, docCommentId!, retryCall, isCallable, cancellationToken),
                DataTag.SymbolHandle)
        };
    }

    /// <summary>
    /// The tool-level error code for an unresolved <see cref="SymbolResolution"/>: a symbol that is not there is NotFound; a
    /// method id with several overloads is InvalidArgument (the caller must pass one full id); anything else is Exception.
    /// </summary>
    // error-policy: this mapping stays pure; the missing-target kind is recorded by ToResultError.
    public static string ToToolErrorCode(EngineError error) => error.Code switch
    {
        EngineErrorCode.SymbolNotResolved => ToolErrorCode.NotFound,
        EngineErrorCode.MissingParameterListOverloads => ToolErrorCode.InvalidArgument,
        _ => ToolErrorCode.Exception,
    };

    /// <summary>
    /// The structured tool error for an unresolved <see cref="SymbolResolution"/>: the code from <see cref="ToToolErrorCode"/>
    /// and the engine's message. A symbol that is not there also records the missing-target kind on the call context.
    /// </summary>
    public static ResultError ToResultError(EngineError error)
    {
        if (error.Code == EngineErrorCode.SymbolNotResolved)
        {
            // error-policy side channel (plan_error_policy_pointer Step 12); text-neutral
            ToolCallContext.Current?.RecordMissingTarget("symbol");
        }

        return new ResultError(ToToolErrorCode(error), error.Message);
    }

    private static string NotADocCommentIdText(string? docCommentId)
        => $"'{docCommentId}' is not a documentation-comment ID: those start with a kind letter and a colon (T: type, M: method, P: property, F: field, E: event, N: namespace), for example 'T:MyApp.Orders.OrderService'. It is not a changeId from an undo hint. Find the symbol with {ToolCallHint.Call("Search", ("mode", "symbol"))} and use the ID it returns.";

    /// <summary>
    /// The not-found text for an engine that resolves a docCommentId itself by walking every project of <paramref name="solution"/>
    /// (SemanticFindReplace's replaceConstruction and removeNullForgiving, invertBoolean) and has no project name to scope by: an id without a
    /// kind prefix gets the shape fact; otherwise <see cref="SymbolNotFoundDetail.BuildAsync"/> runs over every project (the ones the walk
    /// searched), so a similar name is offered with the exact docCommentId and, with <paramref name="retryCall"/>, the calling tool's own
    /// retry call, and with none the no-similar-name fact and one orientation call.
    /// </summary>
    /// <param name="retryCall">As on <see cref="ResolveAsync"/>: maps a candidate's docCommentId to the calling tool's call with only that id corrected, or null.</param>
    public static async Task<string> DescribeUnresolvedIdAsync(Solution solution, string docCommentId, Func<string, string?>? retryCall, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(docCommentId ?? "", @"^[NTFPEM]:\S"))
        {
            return NotADocCommentIdText(docCommentId);
        }

        var searched = solution.Projects.ToList();
        var (query, kind) = ParseId(docCommentId!);

        // TODO isCallable: no ActiveToolSurface is in scope here either, so hints name tools unconditionally
        return await SymbolNotFoundDetail.BuildAsync(searched, query, kind, $"all {searched.Count} project(s) of the loaded solution", null, isCallable: null, nextCall: null, cancellationToken, retryCall, wholeSolution: true);
    }

    // Runs the shared candidate builder over the named project (every project with that name, so a multi-target load is
    // covered); a project name that matches nothing falls back to every non-test project and says so in the scope text.
    private static async Task<string> BuildNotFoundTextAsync(Solution solution, string projectName, string docCommentId, Func<string, string?>? retryCall, Func<string, bool>? isCallable, CancellationToken cancellationToken)
    {
        var named = solution.Projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        IReadOnlyCollection<Project> searched;
        string scope;
        string? listAllProject;
        if (named.Count > 0)
        {
            searched = named;
            scope = $"project '{projectName}'";
            listAllProject = projectName;
        }
        else
        {
            searched = solution.Projects.Where(p => !IsTestProject(p)).ToList();
            scope = $"{searched.Count} project(s), test projects excluded (no project is named '{projectName}')";
            listAllProject = null;
        }

        var (query, kind) = ParseId(docCommentId);

        // The callable check comes from the caller (the workspace manager passes its ActiveToolSurface).
        // wholeSolution stays false: a named project, or the non-test fallback, is a narrowed search.
        return await SymbolNotFoundDetail.BuildAsync(searched, query, kind, scope, listAllProject, isCallable: isCallable, nextCall: null, cancellationToken, retryCall);
    }

    // "M:Ns.Type.Name(System.Int32)" -> ("Ns.Type.Name", "method"); generic arity markers are dropped; N and unknown kinds search any kind.
    private static (string Query, string Kind) ParseId(string docCommentId)
    {
        var body = docCommentId.Length > 2 ? docCommentId[2..] : string.Empty;
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body[..paren];
        }

        body = Regex.Replace(body, @"`+\d+", string.Empty).Trim();
        var kind = docCommentId.Length > 0
            ? docCommentId[0] switch
            {
                'T' => "type",
                'M' => "method",
                'P' => "property",
                'F' => "field",
                'E' => "event",
                _ => "any",
            }
            : "any";
        return (body, kind);
    }

    // TODO unify with RoslynSentinel.Engines.Basic.TestProjectDetector (same Microsoft.NET.Test.Sdk check, cached); it lives a layer up, so move it to Common and delete this copy
    private static bool IsTestProject(Project project)
    {
        var path = project.FilePath;
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return File.ReadAllText(path).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

public class SymbolNavigationImpl
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;

    public SymbolNavigationImpl(
        SymbolNavigationEngine symbolNavigationEngine,
        ImpactAnalyzer impactAnalyzer,
        IWorkspaceManager workspaceManager,
        ILogger logger)
    {
        _symbolNavigationEngine = symbolNavigationEngine;
        _impactAnalyzer = impactAnalyzer;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public async Task<SentinelCallToolResult<object>> LocateSymbol(
        ToolCallReason reason,
        string symbolName,
        SymbolKindFilter symbolKind = SymbolKindFilter.any,
        string? containingType = null,
        string? containingNamespace = null,
        string? projectName = null,
        string? filePath = null,
        bool exactMatch = true,
        bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);

        try
        {
            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch, includeTests, cancellationToken);

            // Count the test projects that the default search skipped, so the caller can see why a symbol is missing.
            int excluded = 0;
            Microsoft.CodeAnalysis.Solution? solution = null;
            if (!includeTests && projectName == null && string.IsNullOrEmpty(filePath))
            {
                // READCHOKEPOINT-CAST: same cast as SymbolNavigationEngine.LocateSymbolAsync; ISolutionProvider has no GetSolutionAsync.
                solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
                foreach (var project in solution.Projects)
                {
                    if (TestProjectDetector.IsTestProject(project))
                    {
                        excluded++;
                    }
                }
            }

            if (result.Count == 0)
            {
                // Only the empty path pays for the retry: say where the hidden test-project hits are, or that none exist.
                var hiddenNote = "";
                if (excluded > 0)
                {
                    var withTests = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch, true, cancellationToken);
                    hiddenNote = withTests.Count > 0
                        ? $" {withTests.Count} declaration(s) were found in test project(s) ({string.Join(", ", withTests.GroupBy(l => l.ProjectName).Select(g => $"{g.Key} {g.Count()}"))}), hidden because includeTests is false; pass includeTests: true to see them."
                        : $" {excluded} test project(s) were also searched and hold no match, so the name is not declared anywhere in the solution.";
                }

                // The searched projects mirror SymbolNavigationEngine.LocateSymbolAsync: a validated filePath pins the engine
                // to one file and keeps every project; otherwise test projects are dropped when includeTests is false.
                solution ??= await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
                var pinnedToFile = filePathResolved.Validated;
                var testsFiltered = !includeTests && projectName == null && !pinnedToFile;
                var searchedProjects = solution.Projects
                    .Where(p => projectName == null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
                    .Where(p => !testsFiltered || !TestProjectDetector.IsTestProject(p))
                    .ToList();
                var scope = projectName != null
                    ? $"project '{projectName}'"
                    : $"{searchedProjects.Count} project(s), test projects {(testsFiltered ? "excluded" : "included")}";
                if (pinnedToFile)
                {
                    scope += $" (declarations pinned to file '{filePath}', candidates from these projects)";
                }

                // TODO isCallable: SymbolNavigationImpl has no ActiveToolSurface, so hints name tools unconditionally (as the old text did)
                var message = await SymbolNotFoundDetail.BuildAsync(searchedProjects, symbolName, symbolKind.ToString(), scope, projectName, isCallable: null, nextCall: null, cancellationToken);

                // Only for exactMatch=true: a wanted name that is a substring of a declared name gets the retry hint.
                if (exactMatch)
                {
                    var substringMatches = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch: false, includeTests, cancellationToken);
                    if (substringMatches.Count > 0)
                    {
                        message += $" exactMatch=false would match {substringMatches.Count} declaration(s): {ToolCallHint.Call("Search", ("mode", "symbol"), ("query", symbolName), ("exactMatch", "false"))}.";
                    }
                }

                // error-policy side channel (plan_error_policy_pointer Step 5); text-neutral
                ToolCallContext.Current?.RecordMissingTarget("symbol");

                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, message + hiddenNote)
                };
            }

            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result,
                StatusMessage = excluded > 0 ? $"{excluded} test project(s) were excluded because includeTests is false; pass includeTests: true to include them." : null,
                TotalRecords = result.Count,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocateSymbol failed for '{SymbolName}'", symbolName);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "LocateSymbol")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> InspectSymbol(
        ToolCallReason reason,
        string filePath,
        string contextSnippet,
        InspectSymbolAspect aspect,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default
        )
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        // Prefilled option for a not-found snippet, shared by the info branch, its default text and the catch path.
        var outlineOption = $"{ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} shows the file's members and their lines; lineBefore/lineAfter pin a match.";

        try
        {
            // Same file-lookup guard as ReplaceSnippetBatch: a blank path or no loaded solution keeps its own text;
            // a misspelt or missing file gets the shared not-found text with its near matches.
            if (!filePathResolved.Validated
                && (filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded || string.IsNullOrWhiteSpace(filePathResolved.Absolute)))
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) : "path could not be resolved.")
                };
            }
            var lookup = await _workspaceManager.GetDocumentAsync(filePathResolved, ReadSource.Committed, cancellationToken);
            if (!lookup.TryGetDocument(out _))
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = lookup.ToResultError()
                };
            }

            if (aspect == InspectSymbolAspect.info)
            {
                var symbolInfo = await _symbolNavigationEngine.GetSymbolInfoAsync(filePathResolved, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (symbolInfo == null || symbolInfo.Kind == "Error")
                {
                    var snippetPreview = contextSnippet.Length > 60 ? contextSnippet[..60] + "..." : contextSnippet;
                    var errorMsg = string.IsNullOrEmpty(symbolInfo?.Error) ? 
                        $"Could not resolve a symbol in '{filePathResolved}' for contextSnippet \"{snippetPreview}\". " +
                        "This means one of: the snippet text does not appear verbatim in the file, it matched a " +
                        "location with no bindable symbol (e.g. whitespace, a keyword, or a comment), or it matched " +
                        "more than one location and lineBefore/lineAfter did not disambiguate. " +
                        outlineOption
                        : symbolInfo!.Error + " " + outlineOption;
                    // error-policy side channel (plan_error_policy_pointer Step 5); text-neutral
                    ToolCallContext.Current?.RecordMissingTarget("symbol");
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.NotFound, errorMsg)
                    };
                }
                // records the DECLARING file (metadata symbols have no DefinedInFile and record nothing)
                if (!string.IsNullOrEmpty(symbolInfo.DefinedInFile))
                {
                    await _workspaceManager.RecordWholeFileObservedAsync(_workspaceManager.ResolveFromWire(symbolInfo.DefinedInFile), cancellationToken);
                }
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = symbolInfo
                };
            }
            if (aspect == InspectSymbolAspect.blastRadius)
            {
                var result = await _impactAnalyzer.AnalyzeImpactAsync(filePathResolved, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    // error-policy side channel (plan_error_policy_pointer Step 5); text-neutral
                    // Only a symbol-resolution miss (ErrorCode NotFound) counts; a null code is a lookup miss or a syntax-root failure.
                    if (result.ErrorCode == ToolErrorCode.NotFound) { ToolCallContext.Current?.RecordMissingTarget("symbol"); }
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.NotFound, result.Error)
                    };
                }

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = result
                };
            }
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled aspect '{aspect}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InspectSymbol ({Aspect}) failed in '{FilePathWrapper}'", aspect, filePathResolved);
            if (ex is ToolNotFoundException && (aspect == InspectSymbolAspect.info || aspect == InspectSymbolAspect.blastRadius))
            {
                // The engine throws a snippet miss; the mapped text gets the same prefilled option as the info branch.
                var mapped = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InspectSymbol");
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, mapped.Message + " " + outlineOption)
                };
            }
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InspectSymbol")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> GetTypeInfo(
        ToolCallReason reason,
        string typeName,
        TypeInfoInclude include = TypeInfoInclude.both,
        string? projectName = null,
        bool includeInherited = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TypeHierarchyReport? hierarchy = null;
            List<TypeMemberDetail>? members = null;
            if (include == TypeInfoInclude.hierarchy || include == TypeInfoInclude.both)
            {
                hierarchy = await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName, cancellationToken);
                if (hierarchy.Error is not null)
                {
                    // GetTypeMembersDetailAsync silently returns an empty list for the same
                    // "type not found" condition, so surface the hierarchy lookup's explicit
                    // error here rather than letting either include mode return a bare IsError=false.
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        // TODO hierarchy.Error is the type-not-found text (SymbolNavigationEngine.GetTypeHierarchyAsync), a lookup miss like the others: code it NotFound (TypeHierarchyReport has no error code field yet)
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, hierarchy.Error)
                    };
                }
            }
            if (include == TypeInfoInclude.members || include == TypeInfoInclude.both)
            {
                members = await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited, cancellationToken: cancellationToken);
            }
            if (include == TypeInfoInclude.hierarchy)
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = hierarchy!
                };
            }
            if (include == TypeInfoInclude.members)
            {
                var warning = members!.Count == 0
                    ? $"No members found for '{typeName}'. This can mean the type doesn't exist in the solution - retry with include=hierarchy or include=both to confirm - or that it genuinely has no members."
                    : null;
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = members!,
                    WarningDetails = warning
                };
            }
            if (include == TypeInfoInclude.both)
            {
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = new { Hierarchy = hierarchy, Members = members }
                };
            }
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled include '{include}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTypeInfo ({Include}) failed for '{TypeName}'", include, typeName);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetTypeInfo")
            };
        }
    }
}

using System.ComponentModel;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Basic;

public class SymbolNavigationImpl
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger _logger;

    public SymbolNavigationImpl(
        SymbolNavigationEngine symbolNavigationEngine,
        ImpactAnalyzer impactAnalyzer,
        ISolutionProvider workspaceManager,
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
        string? filepath = null,
        bool exactMatch = true,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);

        try
        {
            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePathResolved, exactMatch, cancellationToken);
            if (result.Count == 0)
            {
                return new SentinelCallToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"Symbol '{symbolName}' not found in the solution" +
                        (projectName != null ? $" (project: {projectName})" : "") +
                        ". Try exactMatch=false for a broader search, verify the symbol name and symbolKind, or call ListAll for a cheap solution-wide orientation listing if you're not sure of the exact name.")
                };
            }

            return new SentinelCallToolResult<object>
            {
                Success = true,
                Data = result,
                TotalRecords = result.Count,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocateSymbol failed for '{SymbolName}'", symbolName);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "LocateSymbol")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> InspectSymbol(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string contextSnippet,
        InspectSymbolAspect aspect,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default
        )
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            if (aspect == InspectSymbolAspect.info)
            {
                var symbolInfo = await _symbolNavigationEngine.GetSymbolInfoAsync(filePathResolved, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (symbolInfo == null)
                {
                    var snippetPreview = contextSnippet.Length > 60 ? contextSnippet[..60] + "..." : contextSnippet;
                    return new SentinelCallToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception,
                            $"Could not resolve a symbol in '{filePathResolved}' for contextSnippet \"{snippetPreview}\". " +
                            "This means one of: the snippet text does not appear verbatim in the file, it matched a " +
                            "location with no bindable symbol (e.g. whitespace, a keyword, or a comment), or it matched " +
                            "more than one location and lineBefore/lineAfter did not disambiguate. Re-check the snippet " +
                            "against GetMethodSource/GetFileOutline output, or add lineBefore/lineAfter to pin the match.")
                    };
                }
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = symbolInfo
                };
            }
            if (aspect == InspectSymbolAspect.blastRadius)
            {
                var result = await _impactAnalyzer.AnalyzeImpactAsync(filePathResolved, contextSnippet, lineBefore, lineAfter);
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled aspect '{aspect}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InspectSymbol ({Aspect}) failed in '{FilePathWrapper}'", aspect, filePathResolved);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InspectSymbol")
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
                    // error here rather than letting either include mode return a bare Success=true.
                    return new SentinelCallToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, hierarchy.Error)
                    };
                }
            }
            if (include == TypeInfoInclude.members || include == TypeInfoInclude.both)
            {
                members = await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited);
            }
            if (include == TypeInfoInclude.hierarchy)
            {
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = hierarchy!
                };
            }
            if (include == TypeInfoInclude.members)
            {
                var warning = members!.Count == 0
                    ? $"No members found for '{typeName}'. This can mean the type doesn't exist in the solution - retry with include=hierarchy or include=both to confirm - or that it genuinely has no members."
                    : null;
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = members!,
                    Warning = warning
                };
            }
            if (include == TypeInfoInclude.both)
            {
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = new { Hierarchy = hierarchy, Members = members }
                };
            }
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled include '{include}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTypeInfo ({Include}) failed for '{TypeName}'", include, typeName);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetTypeInfo")
            };
        }
    }
}

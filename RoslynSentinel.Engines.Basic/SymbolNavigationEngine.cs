using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

public record CallerInfo(
    string CallerMethod,
    string CallerType,
    string FilePath,
    int Line,
    string CodeSnippet
);

public record SymbolHoverInfo(
    string Name,
    string Kind,
    string FullSignature,
    string? ContainingType,
    string? ContainingNamespace,
    string? Documentation,
    string Accessibility,
    string? DefinedInFile,
    int? DefinedAtLine,
    List<string> Modifiers,
    string? Error = null
) : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;
}

public record ImplementationInfo(
    string TypeName,
    string? FilePath,
    int? Line,
    string Kind
);

public record ReadonlyFieldCandidate(
    string ClassName,
    string FieldName,
    string FieldType,
    int Line
);

/// <summary>
/// The namespaces already visible in a file without qualification: its own declared namespace
/// plus every `using` directive (including any global usings from the project's compilation
/// options). Used to tell a genuinely-missing-using CS0103 apart from one where the candidate's
/// namespace is already in scope and the real fix is a qualifier or accessibility change instead.
/// </summary>
public record FileUsingContext(
    string? DeclaredNamespace,
    List<string> UsingNamespaces
)
{
    public bool IsNamespaceInScope(string? ns) =>
        ns == null || ns == DeclaredNamespace || UsingNamespaces.Contains(ns);
}

public record TypeMemberDetail(
    string Name,
    string Kind,
    string Signature,
    string Accessibility,
    bool IsInherited,
    bool IsOverride,
    bool IsAbstract,
    bool IsStatic,
    string? DefinedInType,
    string? FilePath,
    int? Line
);

public record InterfaceMemberCoverage(
    string MemberSignature,
    bool IsImplemented
);

public record InterfaceImplementorCoverage(
    string ImplementorName,
    string? FilePath,
    int? Line,
    bool IsFullyComplete,
    List<InterfaceMemberCoverage> Members
);

public record ExtensionMethodInfo(
    string MethodName,
    string Signature,
    string DefiningClass,
    string DefiningNamespace,
    string? FilePath,
    int? Line
);

/// <summary>
/// A single declaration site returned by LocateSymbolAsync.
/// All fields required by filePath-gated tools (filePath, contextSnippet, line) are included
/// so callers can feed results directly into inspect_symbol / find_references / get_call_graph
/// without a separate text-search step.
/// </summary>
public record SymbolLocation(
    /// <summary>Simple name without namespace or type prefix.</summary>
    string SymbolName,
    /// <summary>Documentation comment ID for symbol persistence. Null for symbols that don't support it (e.g. locals, lambdas). Pass as docCommentId to symbol-accepting tools.</summary>
    string? DocCommentId,
    /// <summary>Project containing the symbol, needed for SymbolHandle construction.</summary>
    string ProjectName,
    /// <summary>Fully-qualified name, e.g. "RoslynSentinel.Server.DiscoveryEngine.FindAttributeUsagesAsync".</summary>
    string FullyQualifiedName,
    /// <summary>Roslyn symbol kind: Method, Property, Field, NamedType, Event, etc.</summary>
    string SymbolKind,
    /// <summary>Full signature string suitable for display and disambiguation.</summary>
    string Signature,
    /// <summary>Declaring type simple name, null for top-level types.</summary>
    string? ContainingType,
    /// <summary>Declaring namespace, null for global namespace.</summary>
    string? ContainingNamespace,
    /// <summary>Absolute path to the declaring file. Feed directly to filePath parameters.</summary>
    string? FilePath,
    /// <summary>1-based declaration line number.</summary>
    int? Line,
    /// <summary>
    /// Pre-built contextSnippet: the declaration line text, ready to pass to
    /// inspect_symbol / find_references / rename_symbol contextSnippet parameters.
    /// </summary>
    string? ContextSnippet,
    /// <summary>Declared accessibility: Public, Internal, Private, Protected, etc.</summary>
    string Accessibility
);

public class SymbolNavigationEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<SymbolNavigationEngine> _logger;

    public SymbolNavigationEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = NullLogger<SymbolNavigationEngine>.Instance;
    }

    public SymbolNavigationEngine(ISolutionProvider workspaceManager, ILogger<SymbolNavigationEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Locates all declaration sites for a symbol by name without requiring a file path.
    /// Returns structured SymbolLocation records whose FilePathWrapper and ContextSnippet fields
    /// can be passed directly to inspect_symbol, find_references, get_call_graph, rename_symbol,
    /// and all other filePath-gated tools -> eliminating the search_solution_text bootstrap step.
    ///
    /// symbolName: simple or fully-qualified name (e.g. "GetById" or "Acme.SuccessData.Repo.GetById").
    /// symbolKind: optional filter -> "type", "method", "property", "field", "event", or "any" (default).
    /// projectName: optional -> restricts the search to a single project.
    /// exactMatch: true (default) for exact name match; false for prefix/contains (discovery failureMode).
    ///
    /// Returns all matches. Overloads appear as separate entries distinguishable by Signature.
    /// When multiple results are returned, inspect Signature and ContainingType to pick the target,
    /// then supply the chosen FilePathWrapper + ContextSnippet to the next tool call.
    ///
    /// Use this when you need a flattened, display-ready report of EVERY match's metadata at once
    /// (Signature, ContainingType, ContainingNamespace, ProjectName, DocCommentId, Accessibility,
    /// FilePath, Line, ContextSnippet) -- e.g. a "did you mean" hint or a solution-wide discovery
    /// listing. This is NOT obsolete and is not a duplicate of ResolveCandidates/
    /// ResolveCandidatesWithSemanticAsync: those two disambiguate down to ONE target declaration for
    /// a caller that already knows which symbol it wants; this method deliberately returns every
    /// match's data so the caller (or the agent reading the result) can compare them. If you only
    /// need to disambiguate to one target and act on it, use ResolveCandidates (syntax-only, no
    /// filePath needed) or ResolveCandidatesWithSemanticAsync (needs a solution-wide ISymbol)
    /// instead -- see docs/current/proposals/proposal_universal_symbol_resolver.md.
    /// </summary>
    public async Task<List<SymbolLocation>> LocateSymbolAsync(
        string symbolName,
        string symbolKind = "any",
        string? containingType = null,
        string? containingNamespace = null,
        string? projectName = null,
        FilePathWrapper filePath = default,
        bool exactMatch = true,
        bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: _workspaceManager is ISolutionProvider-typed (~11 production
        // construction sites across the solution as of 2026-09-25, including AdvancedRefactoringTools.cs,
        // AdvancedRefactoringEngine.cs, DiscoveryEngine.cs, BasicRefactoringEngine.cs,
        // RefactoringSignatureTools.cs, RefactoringStructuralTools.cs, GenerationTools.cs -- widening
        // the constructor to IWorkspaceManager would force touching all of them). Every real
        // ISolutionProvider implementation (PersistentWorkspaceManager, FakeWorkspaceManager) also
        // implements IWorkspaceManager, so this cast is safe today. Cleanup target: widen the
        // constructor and drop this cast once SymbolNavigationEngine's caller list has been
        // consolidated. See docs/current/design/design_read_chokepoint.md.
        // A supplied filePath pins the search to one declaring file. Resolve it through the shared
        // document lookup (case-insensitive; a miss names the closest real paths) instead of comparing
        // strings per location: the old per-location compare was case-sensitive, so a caller path whose
        // casing differed from the loaded solution's filtered out every location and surfaced as a
        // misleading "Symbol not found in the solution". The solution is taken from the resolved
        // document, so both reads see one snapshot.
        Solution solution;
        string? scopedFilePath = null;
        if (filePath.Validated)
        {
            var scopeDocument = (await ((IWorkspaceReader)_workspaceManager).GetDocumentAsync(filePath, ReadSource.Committed, cancellationToken)).GetDocumentOrThrow();
            solution = scopeDocument.Project.Solution;
            scopedFilePath = scopeDocument.FilePath ?? scopeDocument.Name;
        }
        else
        {
            solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        }

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;
        if (!includeTests && projectName == null && scopedFilePath == null)
        {
            searchProjects = solution.Projects.Where(p => !TestProjectDetector.IsTestProject(p));
        }

        var filter = symbolKind.ToLowerInvariant() switch
        {
            "type" => SymbolFilter.Type,
            "method" or "property" or "field" or "event" => SymbolFilter.Member,
            _ => SymbolFilter.TypeAndMember
        };

        var results = new ConcurrentBag<SymbolLocation>();
        // Written from Parallel.ForEachAsync below, so it must be a concurrent collection.
        var seen = new ConcurrentDictionary<string, byte>();

        var simpleName = symbolName.Contains('.')
            ? symbolName.Split('.').Last()
            : symbolName;

        await Parallel.ForEachAsync(searchProjects, async (project, cancellationToken) =>
        {
            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "LocateSymbol: could not compile project '{Project}'", project.Name);
            }

            if (compilation == null)
            {
                return;
            }

            var candidates = exactMatch
                ? compilation.GetSymbolsWithName(simpleName, filter, cancellationToken)
                : compilation.GetSymbolsWithName(
                    n => n.Contains(simpleName, StringComparison.OrdinalIgnoreCase),
                    filter,
                    cancellationToken);

            foreach (var symbol in candidates)
            {
                if (!MatchesKindFilter(symbol, symbolKind))
                {
                    continue;
                }

                if (symbolName.Contains('.') &&
                    !symbol.ToDisplayString().Contains(symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (containingType != null && symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) != containingType)
                {
                    continue;
                }

                if (containingNamespace != null && symbol.ContainingNamespace?.ToDisplayString() != containingNamespace)
                {
                    continue;
                }

                // Emit SymbolHandle once per symbol -> it's the same regardless of location.
                // Null for symbols that don't support it (e.g. locals, labels).
                var docCommentId = symbol.GetDocumentationCommentId();

                foreach (var location in symbol.Locations.Where(l => l.IsInSource))
                {
                    var filePath2 = location.SourceTree?.FilePath;
                    if (string.IsNullOrEmpty(filePath2))
                    {
                        continue;
                    }

                    if (scopedFilePath != null && !PathComparison.Comparer.Equals(scopedFilePath, filePath2))
                    {
                        continue;
                    }

                    var lineSpan = location.GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var dedupeKey = filePath2 + ":" + line + ":" + symbol.ToDisplayString();
                    if (!seen.TryAdd(dedupeKey, 0))
                    {
                        continue;
                    }

                    // Build ContextSnippet from the source text -> the exact declaration line.
                    string? contextSnippet = null;
                    try
                    {
                        var sourceText = location.SourceTree?.GetText(cancellationToken);
                        if (sourceText != null && line <= sourceText.Lines.Count)
                        {
                            contextSnippet = sourceText.Lines[line - 1].ToString().Trim();
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "LocateSymbol: could not read source text for context snippet");
                    }

                    var sig = symbol switch
                    {
                        IMethodSymbol m => m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                        IPropertySymbol p => p.Type.ToDisplayString() + " " + p.ToDisplayString(),
                        IFieldSymbol f => f.Type.ToDisplayString() + " " + f.ToDisplayString(),
                        INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                        _ => symbol.ToDisplayString()
                    };

                    results.Add(new SymbolLocation(
                        FullyQualifiedName: symbol.ToDisplayString(),
                        SymbolName: symbol.Name,
                        SymbolKind: symbol.Kind.ToString(),
                        Signature: sig,
                        ContainingType: symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        ContainingNamespace: symbol.ContainingNamespace?.IsGlobalNamespace == true
                            ? null
                            : symbol.ContainingNamespace?.ToDisplayString(),
                        ProjectName: project.Name,
                        DocCommentId: docCommentId,
                        Accessibility: symbol.DeclaredAccessibility.ToString(),
                        FilePath: filePath2,
                        Line: line,
                        ContextSnippet: contextSnippet
                    ));
                }
            }
        }).ConfigureAwait(false);

        return results
            .OrderBy(r => r.ContainingNamespace)
            .ThenBy(r => r.ContainingType)
            .ThenBy(r => r.SymbolName)
            .ToList();
    }

    /// <summary>
    /// Returns true when symbol matches the caller-supplied symbolKind string.
    /// SymbolFilter handles coarse type vs member filtering; this handles the fine
    /// sub-kinds (method vs property vs field vs event) that SymbolFilter cannot express.
    /// </summary>
    private static bool MatchesKindFilter(ISymbol symbol, string symbolKind)
    {
        return symbolKind.ToLowerInvariant() switch
        {
            "type" => symbol is INamedTypeSymbol,
            "method" => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation },
            "property" => symbol is IPropertySymbol,
            "field" => symbol is IFieldSymbol,
            "event" => symbol is IEventSymbol,
            "class" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
            "interface" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
            "record" => symbol is INamedTypeSymbol { IsRecord: true },
            "struct" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct },
            "enum" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum },
            "constructor" => symbol is IMethodSymbol { MethodKind: MethodKind.Constructor },
            "parameter" => symbol is IParameterSymbol,
            _ => true   // "any" or unrecognised - include everything
        };
    }

    public async Task<SymbolHoverInfo?> GetSymbolInfoAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        // Primary: use GetDeclaredSymbol/GetSymbolInfo (declaration-based -> more reliable for class/method/property lookups)
        ISymbol? symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken);

        // Fallback: position-based lookup using SymbolFinder (handles usage sites)
        if (symbol == null)
        {
            var text = await document.GetTextAsync(cancellationToken);
            // The primary lookup above already resolved this snippet, so this only fails when the primary returned
            // early (no syntax root or semantic model); the error then carries the candidate listing.
            if (!SnippetLookup.TryResolve(text, contextSnippet, lineBefore, lineAfter, out var snippetMatch, out var snippetError))
            {
                return ErrorHoverInfo(snippetError!);
            }

            var pos = snippetMatch.Start;

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                return ErrorHoverInfo("Could not obtain semantic model for file.");
            }

            // SymbolFinder needs the cursor on an identifier token
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root != null)
            {
                pos = ContextHelper.AdvanceToLastIdentifier(root, pos, snippetMatch.Length);
            }

            symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, pos, solution.Workspace, cancellationToken);
            if (symbol == null && root != null)
            {
                // The snippet matched, but at a node that is not a symbol: word it like the other wrong-node-kind sites.
                return ErrorHoverInfo(SnippetNodeKindError.Describe(text, root, snippetMatch, "symbol", "includes the identifier directly (e.g. the method name or property name)"));
            }
        }

        if (symbol == null)
        {
            // Only reached when the syntax root was unavailable; a resolved root with no symbol returns above.
            return ErrorHoverInfo($"No symbol found at snippet '{contextSnippet}'. " +
                "Try a snippet that includes the identifier directly (e.g. the method name or property name).");
        }

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        int? symLine = null;
        string? symFile = null;
        if (location != null)
        {
            symFile = location.SourceTree?.FilePath;
            symLine = location.GetLineSpan().StartLinePosition.Line + 1;
        }

        string? docSummary = null;
        try
        {
            var docXml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(docXml))
            {
                var match = System.Text.RegularExpressions.Regex.Match(docXml,
                    @"<summary>\s*(.*?)\s*</summary>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                if (match.Success)
                {
                    docSummary = System.Text.RegularExpressions.Regex.Replace(
                        match.Groups[1].Value.Trim(), @"\s+", " ");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var modifiers = new List<string>();
        switch (symbol)
        {
            case IMethodSymbol ms:
                if (ms.IsStatic)
                {
                    modifiers.Add("static");
                }

                if (ms.IsAsync)
                {
                    modifiers.Add("async");
                }

                if (ms.IsVirtual)
                {
                    modifiers.Add("virtual");
                }

                if (ms.IsOverride)
                {
                    modifiers.Add("override");
                }

                if (ms.IsAbstract)
                {
                    modifiers.Add("abstract");
                }

                if (ms.IsSealed)
                {
                    modifiers.Add("sealed");
                }

                break;
            case INamedTypeSymbol ts:
                if (ts.IsStatic)
                {
                    modifiers.Add("static");
                }

                if (ts.IsAbstract)
                {
                    modifiers.Add("abstract");
                }

                if (ts.IsSealed)
                {
                    modifiers.Add("sealed");
                }

                break;
            case IPropertySymbol ps:
                if (ps.IsStatic)
                {
                    modifiers.Add("static");
                }

                if (ps.IsReadOnly)
                {
                    modifiers.Add("readonly");
                }

                if (ps.IsVirtual)
                {
                    modifiers.Add("virtual");
                }

                if (ps.IsOverride)
                {
                    modifiers.Add("override");
                }

                break;
            case IFieldSymbol fs:
                if (fs.IsStatic)
                {
                    modifiers.Add("static");
                }

                if (fs.IsReadOnly)
                {
                    modifiers.Add("readonly");
                }

                if (fs.IsConst)
                {
                    modifiers.Add("const");
                }

                break;
        }

        var fullSig = symbol switch
        {
            IMethodSymbol m => m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            IPropertySymbol p => p.Type.ToDisplayString() + " " + p.ToDisplayString(),
            IFieldSymbol f => f.Type.ToDisplayString() + " " + f.ToDisplayString(),
            ILocalSymbol l => l.Type.ToDisplayString() + " " + l.Name,
            IParameterSymbol p => p.Type.ToDisplayString() + " " + p.Name,
            INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            _ => symbol.ToDisplayString()
        };

        return new SymbolHoverInfo(
            Name: symbol.Name,
            Kind: symbol.Kind.ToString(),
            FullSignature: fullSig,
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null : symbol.ContainingNamespace?.ToDisplayString(),
            Documentation: docSummary,
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            DefinedInFile: symFile,
            DefinedAtLine: symLine,
            Modifiers: modifiers
        );
    }

    private static SymbolHoverInfo ErrorHoverInfo(string error) =>
        new SymbolHoverInfo(
            Name: string.Empty,
            Kind: "Error",
            FullSignature: string.Empty,
            ContainingType: null,
            ContainingNamespace: null,
            Documentation: null,
            Accessibility: "Unknown",
            DefinedInFile: null,
            DefinedAtLine: null,
            Modifiers: new List<string>(),
            Error: error
        );

    public async Task<List<ImplementationInfo>> FindAllImplementationsAsync(
        string typeName, string? projectName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? targetSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            targetSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();
            if (targetSymbol != null)
            {
                break;
            }
        }

        if (targetSymbol == null)
        {
            return new List<ImplementationInfo>();
        }

        var results = new List<ImplementationInfo>();

        if (targetSymbol.TypeKind == TypeKind.Interface)
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(targetSymbol, solution, null, cancellationToken);
            foreach (var impl in implementations)
            {
                var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
                var kind = impl is INamedTypeSymbol nts ? nts.TypeKind.ToString() : impl.Kind.ToString();
                results.Add(new ImplementationInfo(
                    TypeName: impl.ToDisplayString(),
                    FilePath: loc?.SourceTree?.FilePath,
                    Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                    Kind: kind
                ));
            }
        }

        // Also find derived classes for both abstract and concrete base classes
        if (targetSymbol.TypeKind == TypeKind.Class)
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(targetSymbol, solution, null, cancellationToken);
            foreach (var d in derived)
            {
                if (results.Any(r => r.TypeName == d.ToDisplayString()))
                {
                    continue;
                }

                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                results.Add(new ImplementationInfo(
                    TypeName: d.ToDisplayString(),
                    FilePath: loc?.SourceTree?.FilePath,
                    Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                    Kind: d.TypeKind.ToString()
                ));
            }
        }

        return results;
    }

    public async Task<List<TypeMemberDetail>> GetTypeMembersDetailAsync(
        string typeName, string? projectName = null, bool includeInherited = true, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? typeSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            typeSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();
            if (typeSymbol != null)
            {
                break;
            }
        }

        if (typeSymbol == null)
        {
            return new List<TypeMemberDetail>();
        }

        var results = new List<TypeMemberDetail>();
        var seen = new HashSet<string>();

        // Enums never benefit from the inherited chain -> it's always System.Enum/System.ValueType
        // boilerplate (Parse, GetValues, ToString, ...) that drowns out the handful of values
        // callers actually asked about, so it's excluded regardless of includeInherited.
        var isEnum = typeSymbol.TypeKind == TypeKind.Enum;

        var typeChain = new List<(INamedTypeSymbol Type, bool IsTarget)> { (typeSymbol, true) };
        if (includeInherited && !isEnum)
        {
            var t = typeSymbol.BaseType;
            while (t != null && t.SpecialType != SpecialType.System_Object)
            {
                typeChain.Add((t, false));
                t = t.BaseType;
            }
        }

        foreach (var (type, isTarget) in typeChain)
        {
            foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared))
            {
                if (!isTarget && member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                // Enum members are IFieldSymbols whose default signature (just "Type.Name") hides
                // the one thing callers usually want -> the ordinal/explicit value -> so surface it.
                var sig = isEnum && member is IFieldSymbol { HasConstantValue: true } enumField
                    ? $"{type.Name}.{member.Name} = {enumField.ConstantValue}"
                    : member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (!seen.Add(member.Name + sig))
                {
                    continue;
                }

                var loc = member.Locations.FirstOrDefault(l => l.IsInSource);
                var isOverride = member switch
                {
                    IMethodSymbol m => m.IsOverride,
                    IPropertySymbol p => p.IsOverride,
                    IEventSymbol e => e.IsOverride,
                    _ => false
                };

                results.Add(new TypeMemberDetail(
                    Name: member.Name,
                    Kind: member.Kind.ToString(),
                    Signature: sig,
                    Accessibility: member.DeclaredAccessibility.ToString(),
                    IsInherited: !isTarget,
                    IsOverride: isOverride,
                    IsAbstract: member.IsAbstract,
                    IsStatic: member.IsStatic,
                    DefinedInType: isTarget ? null : type.ToDisplayString(),
                    FilePath: loc?.SourceTree?.FilePath,
                    Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null
                ));
            }
        }

        return results;
    }

    public async Task<List<InterfaceImplementorCoverage>> VerifyInterfaceCompletenessAsync(
        string interfaceName, string? projectName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? interfaceSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            interfaceSymbol = compilation
                .GetSymbolsWithName(interfaceName, SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.TypeKind == TypeKind.Interface);
            if (interfaceSymbol != null)
            {
                break;
            }
        }

        if (interfaceSymbol == null)
        {
            return new List<InterfaceImplementorCoverage>();
        }

        var interfaceMembers = interfaceSymbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .ToList();

        var implementations = await SymbolFinder.FindImplementationsAsync(interfaceSymbol, solution, null, cancellationToken);

        var results = new List<InterfaceImplementorCoverage>();
        foreach (var impl in implementations.OfType<INamedTypeSymbol>())
        {
            var memberCoverage = interfaceMembers.Select(member =>
            {
                var implementation = impl.FindImplementationForInterfaceMember(member);
                return new InterfaceMemberCoverage(
                    MemberSignature: member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    IsImplemented: implementation != null
                );
            }).ToList();

            var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
            results.Add(new InterfaceImplementorCoverage(
                ImplementorName: impl.ToDisplayString(),
                FilePath: loc?.SourceTree?.FilePath,
                Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                IsFullyComplete: memberCoverage.All(m => m.IsImplemented),
                Members: memberCoverage
            ));
        }

        return results;
    }

    public async Task<List<ExtensionMethodInfo>> FindExtensionMethodsAsync(
        string targetTypeName, string? projectName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var targetTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetTypeName };

        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            var typeSymbol = compilation
                .GetSymbolsWithName(targetTypeName, SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();
            if (typeSymbol == null)
            {
                continue;
            }

            var bt = typeSymbol.BaseType;
            while (bt != null && bt.SpecialType != SpecialType.System_Object)
            {
                targetTypeNames.Add(bt.Name);
                targetTypeNames.Add(bt.ToDisplayString());
                bt = bt.BaseType;
            }
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                targetTypeNames.Add(iface.Name);
                targetTypeNames.Add(iface.ToDisplayString());
            }
            break;
        }

        var results = new List<ExtensionMethodInfo>();
        var seen = new HashSet<string>();

        foreach (var project in searchProjects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    continue;
                }

                // Syntax-level pre-filter: only look at static classes
                var staticClasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    .ToList();

                bool hasExtMethods = false;
                foreach (var classDecl in staticClasses)
                {
                    foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword))
                            && m.ParameterList.Parameters.Count > 0
                            && m.ParameterList.Parameters[0].Modifiers.Any(mod => mod.IsKind(SyntaxKind.ThisKeyword))))
                    {
                        // Check receiver type name against our set before loading semantic model
                        var receiverTypeSyntax = method.ParameterList.Parameters[0].Type?.ToString() ?? "";
                        var receiverSimpleName = receiverTypeSyntax.Split('.').Last().Split('<').First();
                        if (!targetTypeNames.Contains(receiverSimpleName) && !targetTypeNames.Contains(receiverTypeSyntax))
                        {
                            continue;
                        }

                        hasExtMethods = true;
                        break;
                    }
                    if (hasExtMethods)
                    {
                        break;
                    }
                }

                if (!hasExtMethods)
                {
                    continue;
                }

                // Load semantic model only for documents that have matching extension methods
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (model == null)
                {
                    continue;
                }

                foreach (var classDecl in staticClasses)
                {
                    foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword))
                            && m.ParameterList.Parameters.Count > 0
                            && m.ParameterList.Parameters[0].Modifiers.Any(mod => mod.IsKind(SyntaxKind.ThisKeyword))))
                    {
                        var methodSymbol = model.GetDeclaredSymbol(method, cancellationToken) as IMethodSymbol;
                        if (methodSymbol == null || !methodSymbol.IsExtensionMethod)
                        {
                            continue;
                        }

                        var receiverType = methodSymbol.Parameters[0].Type;
                        if (!targetTypeNames.Contains(receiverType.Name) &&
                            !targetTypeNames.Contains(receiverType.ToDisplayString()))
                        {
                            continue;
                        }

                        var sig = methodSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        if (!seen.Add(sig))
                        {
                            continue;
                        }

                        var loc = methodSymbol.Locations.FirstOrDefault(l => l.IsInSource);
                        results.Add(new ExtensionMethodInfo(
                            MethodName: methodSymbol.Name,
                            Signature: sig,
                            DefiningClass: classDecl.Identifier.Text,
                            DefiningNamespace: methodSymbol.ContainingNamespace?.IsGlobalNamespace == true
                                ? "" : methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
                            FilePath: loc?.SourceTree?.FilePath,
                            Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null
                        ));
                    }
                }
            }
        }

        return results;
    }

    public async Task<List<ReadonlyFieldCandidate>> FindReadonlyFieldCandidatesAsync(
        FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new List<ReadonlyFieldCandidate>();
        }

        var results = new List<ReadonlyFieldCandidate>();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var candidates = classDecl.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(f =>
                    f.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) &&
                    !f.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)) &&
                    !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) &&
                    !f.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                .SelectMany(f => f.Declaration.Variables.Select(v => (Field: f, Name: v.Identifier.Text)))
                .ToList();

            foreach (var (field, name) in candidates)
            {
                var assignedOutsideCtor = classDecl.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Any(a =>
                    {
                        var refName = a.Left switch
                        {
                            IdentifierNameSyntax id => id.Identifier.Text,
                            MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax
                                => ma.Name.Identifier.Text,
                            _ => null
                        };
                        return refName == name &&
                               !a.Ancestors().OfType<ConstructorDeclarationSyntax>().Any();
                    });

                if (!assignedOutsideCtor)
                {
                    results.Add(new ReadonlyFieldCandidate(
                        ClassName: classDecl.Identifier.Text,
                        FieldName: name,
                        FieldType: field.Declaration.Type.ToString(),
                        Line: field.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                    ));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the namespaces already in scope in a file without qualification -> its own declared
    /// namespace plus every `using` directive local to the file and every `global using` anywhere
    /// in the containing project. Intended for CS0103 triage: a candidate symbol whose namespace is
    /// already in this set cannot be fixed by adding a `using`, so the real problem is a missing
    /// qualifier, accessibility, or a genuinely different symbol.
    /// </summary>
    public async Task<FileUsingContext?> GetFileUsingContextAsync(
        FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return null;
        }

        string? declaredNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString();

        var namespaces = new HashSet<string>(StringComparer.Ordinal);

        void CollectUsings(CompilationUnitSyntax compilationUnit, bool globalOnly)
        {
            foreach (var usingDirective in compilationUnit.Usings)
            {
                if (usingDirective.Alias != null || usingDirective.StaticKeyword != default)
                {
                    continue;
                }

                var isGlobal = usingDirective.GlobalKeyword != default;
                if (globalOnly && !isGlobal)
                {
                    continue;
                }

                namespaces.Add(usingDirective.Name?.ToString() ?? usingDirective.NamespaceOrType.ToString());
            }
        }

        CollectUsings(root, globalOnly: false);

        if (document.Project.CompilationOptions != null)
        {
            foreach (var otherDocument in document.Project.Documents)
            {
                if (otherDocument.Id == document.Id)
                {
                    continue;
                }

                if (await otherDocument.GetSyntaxRootAsync(cancellationToken) is CompilationUnitSyntax otherRoot)
                {
                    CollectUsings(otherRoot, globalOnly: true);
                }
            }
        }

        return new FileUsingContext(declaredNamespace, namespaces.ToList());
    }

    /// <summary>
    /// Returns the simple name of the type declaration enclosing the given source position, or
    /// null if the position isn't inside any type (e.g. top-level statements) or the document
    /// can't be found. Intended for CS0122 triage: naming the caller's enclosing type turns
    /// "inaccessible due to its protection level" into a concrete "accessible from X" instruction
    /// without needing full caller-method resolution.
    /// </summary>
    public async Task<string?> GetEnclosingTypeNameAsync(
        FilePathWrapper filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root == null || line < 1 || line > text.Lines.Count)
        {
            return null;
        }

        var position = text.Lines[line - 1].Start + Math.Max(0, column - 1);
        var token = root.FindToken(Math.Min(position, root.FullSpan.End));
        return token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
    }

    /// <summary>
    /// Resolves the one method declaration a call-graph request targets. With a contextSnippet (or lineBefore/lineAfter) the
    /// shared snippet resolver selects the overload, and a snippet that matches none or several throws the usual snippet error.
    /// Without one, class members are preferred over interface members of the same name, and a name that still has several
    /// declarations throws ToolAmbiguousMatchException whose retry hint is a complete call naming contextSnippet.
    /// Returns null when no method has that name.
    /// </summary>
    private static MethodDeclarationSyntax? ResolveCallGraphMethod(SyntaxNode root, SourceText sourceText, FilePathWrapper filePath, string methodName, string direction, string? contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken)
    {
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, methodName, cancellationToken)
            .Where(c => c.Kind == CandidateKind.Method).ToList();
        return ResolveCallGraphMethod(candidates, sourceText, filePath, methodName, direction, contextSnippet, lineBefore, lineAfter);
    }

    /// <summary>
    /// Candidate-list form of <c>ResolveCallGraphMethod</c> (public so the tree direction in Engines.Advanced shares it): the caller
    /// supplies the method candidates already filtered (for example by class); <paramref name="methodName"/> only feeds the retry hint.
    /// </summary>
    public static MethodDeclarationSyntax? ResolveCallGraphMethod(List<SyntaxNodeCandidate> candidates, SourceText sourceText, FilePathWrapper filePath, string methodName, string direction, string? contextSnippet, string? lineBefore, string? lineAfter)
    {
        if (!string.IsNullOrWhiteSpace(contextSnippet) || !string.IsNullOrWhiteSpace(lineBefore) || !string.IsNullOrWhiteSpace(lineAfter))
        {
            return SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node as MethodDeclarationSyntax;
        }

        // Prefer class methods over interface methods of the same name; only a preferred set that still holds several declarations is ambiguous.
        var preferred = candidates.Select(c => c.Node).OfType<MethodDeclarationSyntax>().Where(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any()).ToList();
        if (preferred.Count == 0)
        {
            preferred = candidates.Select(c => c.Node).OfType<MethodDeclarationSyntax>().ToList();
        }

        return NameTargetLookup.RequireSingle(preferred, "Method", methodName,
            retryCall: firstLine => ToolCallHint.Call("GetCallGraph", ("filePath", filePath.ToString()), ("methodName", methodName), ("direction", direction), ("contextSnippet", firstLine)));
    }

    public async Task<CallGraphNode> GetCallGraphAsync(
        FilePathWrapper filePath,
        string methodName,
        int maxDepth = 3,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root is unavailable for '{filePath}'; the file did not parse.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            throw new ToolInvalidArgumentException($"Semantic model is unavailable for '{filePath}'; the file could not be analysed.");
        }

        var methodDecl = ResolveCallGraphMethod(root, await document.GetTextAsync(cancellationToken), filePath, methodName, "forward", contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (methodDecl == null)
        {
            throw MemberRefactoringNotFound.Method(methodName, root, filePath.ToString());
        }

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' in '{filePath}' has no resolvable symbol. Next: {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} to confirm the method declaration.", "method", methodName);
        }

        var visited = new HashSet<string>();
        return await BuildCallGraphNodeAsync(methodSymbol, solution, 0, maxDepth, visited, cancellationToken);
    }

    private async Task<CallGraphNode> BuildCallGraphNodeAsync(
        IMethodSymbol method,
        Solution solution,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        CancellationToken cancellationToken = default)
    {
        var loc = method.Locations.FirstOrDefault(l => l.IsInSource);
        var filePath = loc?.SourceTree?.FilePath;
        var line = loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : (int?)null;

        var node = new CallGraphNode(
            MethodName: method.Name,
            ContainingType: method.ContainingType?.ToDisplayString() ?? string.Empty,
            FilePath: filePath,
            Line: line,
            Callees: new List<CallGraphNode>()
        );

        var fullKey = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            + "@" + (filePath ?? string.Empty) + ":" + (line?.ToString() ?? "?");

        if (depth >= maxDepth || !visited.Add(fullKey))
        {
            return node;
        }

        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            return node;
        }

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
        SyntaxNode? body = syntax switch
        {
            MethodDeclarationSyntax mds => (SyntaxNode?)mds.Body ?? mds.ExpressionBody,
            LocalFunctionStatementSyntax lfs => (SyntaxNode?)lfs.Body ?? lfs.ExpressionBody,
            _ => null
        };
        if (body == null)
        {
            return node;
        }

        Document? methodDoc = null;
        if (filePath != null)
        {
            methodDoc = DocumentLookup.FindDocumentOrNull(solution, filePath);
        }
        if (methodDoc == null)
        {
            return node;
        }

        var model = await methodDoc.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            return node;
        }

        var seenCallees = new HashSet<string>();
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var si = model.GetSymbolInfo(invocation, cancellationToken);
            var callee = si.Symbol as IMethodSymbol
                ?? si.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (callee == null)
            {
                continue;
            }

            if (!callee.Locations.Any(l => l.IsInSource))
            {
                continue;
            }

            var calleeKey = callee.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            if (!seenCallees.Add(calleeKey))
            {
                continue; // deduplicate at this level
            }

            var calleeNode = await BuildCallGraphNodeAsync(callee, solution, depth + 1, maxDepth, visited, cancellationToken);
            node.Callees.Add(calleeNode);
        }

        return node;
    }

    public async Task<ReverseCallGraphNode> GetReverseCallGraphAsync(
        FilePathWrapper filePath,
        string methodName,
        int maxDepth = 3,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root is unavailable for '{filePath}'; the file did not parse.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            throw new ToolInvalidArgumentException($"Semantic model is unavailable for '{filePath}'; the file could not be analysed.");
        }

        var methodDecl = ResolveCallGraphMethod(root, await document.GetTextAsync(cancellationToken), filePath, methodName, "reverse", contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (methodDecl == null)
        {
            throw MemberRefactoringNotFound.Method(methodName, root, filePath.ToString());
        }

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            throw new ToolNotFoundException($"Method '{methodName}' in '{filePath}' has no resolvable symbol. Next: {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} to confirm the method declaration.", "method", methodName);
        }

        var visited = new HashSet<string>();
        return await BuildReverseCallGraphNodeAsync(methodSymbol, solution, 0, maxDepth, visited, cancellationToken);
    }

    private async Task<ReverseCallGraphNode> BuildReverseCallGraphNodeAsync(
        IMethodSymbol method,
        Solution solution,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        CancellationToken cancellationToken = default)
    {
        var loc = method.Locations.FirstOrDefault(l => l.IsInSource);
        var filePath = loc?.SourceTree?.FilePath;
        var line = loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : (int?)null;

        var node = new ReverseCallGraphNode(
            MethodName: method.Name,
            ContainingType: method.ContainingType?.ToDisplayString() ?? string.Empty,
            FilePath: filePath,
            Line: line,
            Callers: new List<ReverseCallGraphNode>()
        );

        var fullKey = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            + "@" + (filePath ?? string.Empty) + ":" + (line?.ToString() ?? "?");

        if (depth >= maxDepth || !visited.Add(fullKey))
        {
            return node;
        }

        // Search direct references to this method AND any corresponding interface method declarations.
        // This ensures callers that use the interface type (e.g. IService.Method()) are included.
        var allReferences = new List<ReferencedSymbol>(
            await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken));

        var containingType = method.ContainingType;
        if (containingType != null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var ifaceMethod in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    var impl = containingType.FindImplementationForInterfaceMember(ifaceMethod) as IMethodSymbol;
                    if (impl != null && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, method.OriginalDefinition))
                    {
                        var ifaceRefs = await SymbolFinder.FindReferencesAsync(ifaceMethod, solution, cancellationToken);
                        allReferences.AddRange(ifaceRefs);
                    }
                }
            }
        }

        var seenCallers = new HashSet<string>();

        foreach (var referencedSymbol in allReferences)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (!location.Location.IsInSource)
                {
                    continue;
                }

                var refTree = location.Location.SourceTree;
                if (refTree == null)
                {
                    continue;
                }

                var refDoc = solution.GetDocument(refTree);
                if (refDoc == null)
                {
                    continue;
                }

                var refModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                if (refModel == null)
                {
                    continue;
                }

                var pos = location.Location.SourceSpan.Start;
                var callerSymbol = refModel.GetEnclosingSymbol(pos, cancellationToken) as IMethodSymbol;
                if (callerSymbol == null)
                {
                    continue;
                }

                if (!callerSymbol.Locations.Any(l => l.IsInSource))
                {
                    continue;
                }

                var callerKey = callerSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (!seenCallers.Add(callerKey))
                {
                    continue;
                }

                var callerNode = await BuildReverseCallGraphNodeAsync(callerSymbol, solution, depth + 1, maxDepth, visited, cancellationToken);
                node.Callers.Add(callerNode);
            }
        }

        return node;
    }

    // Two declarations share an overload key when they are the same overload: an interface member and its
    // implementer, or the parts of a partial type, have equal keys and are not ambiguous. Overloads and
    // generic-arity siblings differ in parameter types or type-parameter count.
    private static string GetOverloadKey(MemberDeclarationSyntax decl)
    {
        return decl switch
        {
            MethodDeclarationSyntax method => $"{method.TypeParameterList?.Parameters.Count ?? 0}|{ParameterOverloadKey(method.ParameterList.Parameters)}",
            BaseMethodDeclarationSyntax baseMethod => $"0|{ParameterOverloadKey(baseMethod.ParameterList.Parameters)}",
            TypeDeclarationSyntax type => (type.TypeParameterList?.Parameters.Count ?? 0).ToString(),
            BaseTypeDeclarationSyntax => "0",
            IndexerDeclarationSyntax indexer => "indexer:" + ParameterOverloadKey(indexer.ParameterList.Parameters),
            _ => string.Empty,
        };
    }

    private static string ParameterOverloadKey(SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        return string.Join(",", parameters.Select(p => string.Join(" ", p.Modifiers.Select(m => m.ValueText).Append(p.Type?.ToString().Trim() ?? string.Empty))));
    }

    /// <summary>
    /// Finds all call sites (references) to a symbol in the solution.
    /// symbolName: the member name to search for. contextSnippet: optional verbatim substring
    /// of the declaration to disambiguate overloads (e.g. the method signature line).
    /// Returns one CallerInfo per call site with the enclosing method, file, line, and code snippet.
    ///
    /// filePath is optional. When omitted, the symbol is resolved by name across the solution
    /// using GetSymbolsWithName. If the name is ambiguous, all candidate locations are searched.
    /// Supply filePath to pin the resolution to a specific declaring file.
    /// </summary>
    public async Task<List<CallerInfo>> FindCallersAsync(
        string? filePath,
        string symbolName,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        // The solution is fetched per branch below: the filePath branch takes it from the resolved
        // document (one snapshot for lookup and analysis), the by-name branch reads it directly.
        Solution solution;
        ISymbol? symbol = null;

        // The MCP tool layer resolves an omitted `filepath` to FilePathWrapper's empty-string default
        // (via ResolveFromWire), not a C# null -> so checking `filePath != null` alone always took this
        // branch, even when no filepath was actually supplied, silently bypassing the by-name
        // fallback below. Treat blank the same as null.
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            // Original path: resolve from the declaring file.
            // Shared document lookup: case-insensitive exact path, then a unique bare name. A miss throws
            // ToolNotFoundException / ToolAmbiguousMatchException naming the closest real paths or the
            // candidates (it used to surface as a bare InvalidOperationException -> errorCode: Exception).
            var document = (await ((IWorkspaceReader)_workspaceManager).GetDocumentAsync(_workspaceManager.ResolveFromWire(filePath), ReadSource.Committed, cancellationToken))
                .GetDocumentOrThrow();
            solution = document.Project.Solution;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
            {
                throw new InvalidOperationException(
                    $"could not obtain a syntax tree/semantic model for '{filePath}'. " +
                    "This is NOT a confirmed zero-references result - the lookup never ran.");
            }

            // Shared by both branches below so the contextSnippet-failure message (immediately
            // below) can report whether symbolName itself WAS found nearby, instead of leaving the
            // caller unsure whether the name or the snippet is the actual problem.
            // Uses ResolveCandidates (kind-complete: methods/properties/fields/types/etc.) rather
            // than a hand-written MethodDeclarationSyntax/PropertyDeclarationSyntax/
            // FieldDeclarationSyntax-only switch -> the old switch's `_ => false` silently dropped
            // ClassDeclarationSyntax (and every other type kind), so a top-level class's own
            // caller-precheck could never resolve the class itself. See
            // docs/current/proposals/proposal_universal_symbol_resolver.md's Motivation section (the "sixth
            // drifted lookup path").
            var declsSourceText = await document.GetTextAsync(cancellationToken);
            var declCandidates = SyntaxTargetResolver.ResolveCandidates(root, declsSourceText, symbolName, cancellationToken);
            var decls = declCandidates
                .Select(c => c.Node)
                .OfType<MemberDeclarationSyntax>()
                .ToList();

            if (contextSnippet != null)
            {
                try
                {
                    symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(document, contextSnippet, symbolName, lineBefore, lineAfter, cancellationToken);
                }
                catch (ToolNotFoundException ex) when (ex.Snippet is not null)
                {
                    // The snippet locator throws (rather than returning null) when the snippet matches nothing;
                    // keep its detail and append the ready-to-run next call for the declaring file.
                    throw new ToolNotFoundException(ex.Message + BuildFindReferencesNextCallForFile(symbolName, "callers", document.FilePath ?? filePath, decls), ex.MissingTargetKind, ex.MissingTargetName, ex.Snippet, ex.Candidates);
                }

                if (symbol == null)
                {
                    var positionalRecord = FindPositionalRecordProperty(model, root, symbolName);
                    throw new ToolNotFoundException(
                        $"contextSnippet did not resolve to a symbol in '{filePath}'. " +
                        $"This is NOT a confirmed zero-references result for '{symbolName}' - the lookup " +
                        "never ran. " + DescribeNameOnlyCandidates(decls.Cast<SyntaxNode>().Concat(FindPositionalRecordParameters(root, symbolName)).ToList(), symbolName) +
                        "Re-check the snippet against GetMethodSource/GetFileOutline output, " +
                        "or omit contextSnippet if the symbolName is unambiguous in this file." +
                        (positionalRecord != null
                            ? $" '{symbolName}' is a positional parameter of record {positionalRecord.ContainingType.Name}; point contextSnippet at that line or omit it to resolve the property by name."
                            : string.Empty) + BuildFindReferencesNextCallForFile(symbolName, "callers", document.FilePath ?? filePath, decls)); // hint-ok: names tools in prose
                }
            }
            else
            {
                // Preferred set: class members when any exist, otherwise all declarations. Only a preferred set that
                // still holds several distinct overloads is ambiguous (see GetOverloadKey); refuse rather than pick the first.
                var preferredDecls = decls.Where(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any()).ToList();
                if (preferredDecls.Count == 0)
                {
                    preferredDecls = decls;
                }

                if (preferredDecls.Select(d => GetOverloadKey(d)).Distinct().Count() > 1)
                {
                    var ambiguousCandidates = declCandidates.Where(c => preferredDecls.Any(p => ReferenceEquals(p, c.Node))).ToList();
                    throw new ToolAmbiguousMatchException(SyntaxTargetResolver.BuildHintForCandidates(ambiguousCandidates, [], "ambiguous"), SyntaxTargetResolver.BuildDeclarationCandidates(ambiguousCandidates, declsSourceText, document.FilePath ?? string.Empty));
                }

                var decl = preferredDecls.FirstOrDefault();
                if (decl != null)
                {
                    // GetDeclaredSymbol returns null directly on a FieldDeclarationSyntax (it can
                    // declare multiple variables, so the declared symbol lives on the matching
                    // VariableDeclaratorSyntax child instead) -> fall back to that child so a field
                    // match in `decls` doesn't spuriously read as "not found declared" below.
                    symbol = model.GetDeclaredSymbol(decl, cancellationToken)
                        ?? (decl as FieldDeclarationSyntax)?.Declaration.Variables
                            .Where(v => v.Identifier.Text == symbolName)
                            .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                            .FirstOrDefault(s => s != null);
                }

                symbol ??= FindPositionalRecordProperty(model, root, symbolName);
                if (symbol == null)
                {
                    throw new ToolNotFoundException(
                        MemberRefactoringNotFound.DeclarationMessage("symbol", symbolName, "symbolName", MemberRefactoringNotFound.MemberNames(root), filePath) +
                        " The lookup never ran, so this is not a zero-references result. Omitting filePath resolves symbolName by name across the solution.");
                }
            }
        }
        else
        {
            solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

            // Defect-3 fix: no filePath supplied -> resolve by name across the solution.
            // When multiple overloads exist, contextSnippet is used to pick one if supplied;
            // otherwise the by-name resolution below throws unless narrowing leaves exactly one
            // candidate - see the ambiguous-fallback fix comment further down.
            // ResolveCandidatesWithSemanticAsync's semantic half already scans every project in the
            // solution regardless of which document's root/text is passed for the free syntax half,
            // so any parseable document works here -- there is no "current file" in the by-name path.
            var anyDoc = solution.Projects.SelectMany(p => p.Documents).First();
            var anyRoot = (await anyDoc.GetSyntaxRootAsync(cancellationToken))!;
            var anyText = await anyDoc.GetTextAsync(cancellationToken);
            var byNameCandidate = await ResolveCandidatesWithSemanticAsync(anyRoot, anyText, symbolName, includeSemantic: true, cancellationToken);
            var allMatches = byNameCandidate.SemanticMatches ?? [];
            var memberMatches = allMatches
                .Where(c => c.Symbol is not INamedTypeSymbol)
                .ToList();

            // Types are valid targets ("who uses record Foo"), not noise: a lone type match, or a type plus
            // its own constructors, resolves to the type. A type plus an unrelated same-named member is
            // ambiguous. PreferClassMember narrows the member subset only. The contextSnippet scan below
            // stays member-only (snippet-based type resolution is a separate path).
            var (typeMatches, otherMembers) = SplitTypeAndMemberMatches(allMatches, symbolName);
            if (contextSnippet == null && typeMatches.Count > 0)
            {
                symbol = ResolveTypeNameMatch(symbolName, typeMatches, otherMembers, PreferClassMember);
            }
            else if (contextSnippet != null)
            {
                ToolAmbiguousMatchException? firstAmbiguous = null;
                foreach (var candidate in memberMatches)
                {
                    if (candidate.FilePath == null)
                    {
                        continue;
                    }

                    var doc = DocumentLookup.FindDocumentOrNull(solution, candidate.FilePath);
                    if (doc == null)
                    {
                        continue;
                    }

                    try
                    {
                        symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(doc, contextSnippet, symbolName, lineBefore, lineAfter, cancellationToken);
                        // A snippet can land on a neighbouring declaration or call site; only accept a symbol whose name matches symbolName.
                        if (symbol != null && symbol.Name != symbolName
                            && !(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctorSymbol && ctorSymbol.ContainingType.Name == symbolName))
                        {
                            symbol = null;
                        }
                        if (symbol != null)
                        {
                            break;
                        }
                    }
                    catch (ToolAmbiguousMatchException ambiguous)
                    {
                        // Keep the first ambiguity: if no candidate resolves, surface it instead of a misleading NotFound.
                        firstAmbiguous ??= ambiguous;
                    }
                    catch (ToolNotFoundException)
                    {
                        // snippet not found in this document -> continue (other ToolException kinds propagate)
                    }
                }

                if (symbol == null && firstAmbiguous != null)
                {
                    throw firstAmbiguous;
                }

                if (symbol == null)
                {
                    // The snippet was supplied specifically to disambiguate -> a miss must not
                    // silently fall through to "pick one anyway" below. Without this guard, a
                    // caller-supplied contextSnippet that doesn't match any candidate was dropped
                    // on the floor and PreferClassMember(...).FirstOrDefault() picked an arbitrary
                    // same-named symbol from anywhere in the solution instead - the root cause
                    // traced in docs/current/blockers/closed/blocking_error_member_replace_containername_regression_setup_overload.md's
                    // investigation, reproduced here in the solution-wide by-name path.
                    throw new ToolNotFoundException(
                        $"contextSnippet did not resolve to a symbol for symbolName " +
                        $"'{symbolName}' in any of the {memberMatches.Count} candidate location(s) found " +
                        "across the solution. This is NOT a confirmed zero-references result - the lookup " +
                        "never ran. " + DescribeCandidateLocations(memberMatches) + DescribeTypeMatchHint(allMatches, symbolName) +
                        "Re-check the snippet against GetMethodSource/GetFileOutline output for the " +
                        "specific candidate you meant, or supply filePath to pin resolution to one file." + BuildFindReferencesNextCall(symbolName, "callers", allMatches)); // hint-ok: names tools in prose
                }
            }
            else
            {
                var narrowed = PreferClassMember(memberMatches);
                if (narrowed.Count > 1)
                {
                    // No contextSnippet was supplied and narrowing (class-vs-interface-member)
                    // still leaves more than one same-named candidate -> FirstOrDefault() here
                    // would silently pick an arbitrary one across the whole solution. Force the
                    // caller to disambiguate instead of returning results for the wrong symbol.
                    throw new ToolAmbiguousMatchException(
                        $"symbolName '{symbolName}' is ambiguous - {narrowed.Count} candidates " +
                        "found across the solution and no contextSnippet was supplied to disambiguate. " +
                        "This is NOT a confirmed result for any single symbol - resolution stopped before " +
                        "the lookup ran. " + DescribeCandidateLocations(narrowed, memberMatches.Where(m => narrowed.All(n => !SymbolEqualityComparer.Default.Equals(n.Symbol, m.Symbol))).ToList()) +
                        "Supply contextSnippet (a verbatim substring near the intended declaration) or " +
                        "filePath to pin resolution to the one you mean.", BuildAmbiguousCandidates(narrowed, memberMatches.Where(m => narrowed.All(n => !SymbolEqualityComparer.Default.Equals(n.Symbol, m.Symbol))).ToList()));
                }

                symbol = narrowed.FirstOrDefault()?.Symbol;
            }
        }

        if (symbol == null)
        {
            var missDetail = await DescribeSymbolMissInSolutionAsync(solution, symbolName, "callers", cancellationToken);
            throw new ToolNotFoundException(
                $"symbolName '{symbolName}' could not be resolved anywhere in the solution" +
                (contextSnippet != null ? " with the supplied contextSnippet" : "") + ". " +
                "The lookup never ran, so this is not a zero-references result. " + missDetail);
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var results = new List<CallerInfo>();
        var seen = new HashSet<string>();

        foreach (var refGroup in references)
        {
            foreach (var location in refGroup.Locations)
            {
                if (!location.Location.IsInSource)
                {
                    continue;
                }

                var refTree = location.Location.SourceTree;
                if (refTree == null)
                {
                    continue;
                }

                var refDoc = solution.GetDocument(refTree);
                if (refDoc == null)
                {
                    continue;
                }

                var refModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                if (refModel == null)
                {
                    continue;
                }

                var pos = location.Location.SourceSpan.Start;
                var enclosing = refModel.GetEnclosingSymbol(pos, cancellationToken);
                if (enclosing == null)
                {
                    continue;
                }

                // An object-creation or return-type reference inside an expression-bodied member sits on the type's
                // declaration line, so GetEnclosingSymbol reports the type; attribute the nearest member instead.
                if (enclosing is INamedTypeSymbol)
                {
                    var refNode = refTree.GetRoot(cancellationToken).FindNode(location.Location.SourceSpan);
                    var memberSyntax = refNode.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                    var memberSymbol = memberSyntax switch
                    {
                        FieldDeclarationSyntax field => refModel.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken),
                        EventFieldDeclarationSyntax evt => refModel.GetDeclaredSymbol(evt.Declaration.Variables[0], cancellationToken),
                        null => null,
                        _ => refModel.GetDeclaredSymbol(memberSyntax, cancellationToken),
                    };
                    enclosing = memberSymbol ?? enclosing;
                }

                var lineSpan = location.Location.GetLineSpan();
                var line = lineSpan.StartLinePosition.Line + 1;

                var key = $"{refTree.FilePath}:{line}";
                if (!seen.Add(key))
                {
                    continue;
                }

                var sourceText = await refDoc.GetTextAsync(cancellationToken);
                var lineText = line <= sourceText.Lines.Count
                    ? sourceText.Lines[line - 1].ToString().Trim()
                    : string.Empty;

                results.Add(new CallerInfo(
                    CallerMethod: enclosing.Name,
                    CallerType: enclosing.ContainingType?.Name ?? enclosing.ContainingNamespace?.Name ?? string.Empty,
                    FilePath: refTree.FilePath ?? string.Empty,
                    Line: line,
                    CodeSnippet: lineText
                ));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the synthesized property for a positional record parameter (e.g. `Detail` in
    /// `record R(string Detail)`). Such parameters are not MemberDeclarationSyntax, so the
    /// declaration-based lookup cannot see them. Returns null when no positional record
    /// parameter of that name exists in the file.
    /// </summary>
    private static IPropertySymbol? FindPositionalRecordProperty(SemanticModel model, SyntaxNode root, string name)
    {
        var parameter = FindPositionalRecordParameters(root, name).FirstOrDefault();
        if (parameter == null || model.GetDeclaredSymbol((RecordDeclarationSyntax)parameter.Parent!.Parent!) is not INamedTypeSymbol recordSymbol)
        {
            return null;
        }

        return recordSymbol.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
    }

    /// <summary>
    /// Finds the positional record parameters named `name` (e.g. `Detail` in `record R(string Detail)`).
    /// They are not MemberDeclarationSyntax, so declaration-based candidate lookups cannot see them.
    /// Returns an empty list when none exist in the file.
    /// </summary>
    private static List<ParameterSyntax> FindPositionalRecordParameters(SyntaxNode root, string name)
    {
        return root.DescendantNodes().OfType<ParameterSyntax>()
            .Where(p => p.Identifier.ValueText == name && p.Parent is ParameterListSyntax { Parent: RecordDeclarationSyntax })
            .ToList();
    }

    /// <summary>
    /// Finds all implementations of an interface member or virtual/abstract method in the solution.
    /// Unlike the built-in find_implementations (which requires line numbers), this uses symbolName
    /// with an optional contextSnippet to locate the symbol without coordinates.
    /// Returns ImplementationInfo with type name, file, line, and kind.
    ///
    /// filePath is optional. When omitted, the symbol is resolved by name across the solution.
    /// Supply filePath to pin resolution to a specific declaring file.
    /// </summary>
    public async Task<List<ImplementationInfo>> FindImplementationsForMemberAsync(
        string? filePath,
        string symbolName,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        // The solution is fetched per branch below: the filePath branch takes it from the resolved
        // document (one snapshot for lookup and analysis), the by-name branch reads it directly.
        Solution solution;
        ISymbol? symbol = null;
        // Non-null only when the filePath/contextSnippet-scoped lookup ran but definitively failed
        // to resolve (as opposed to "no filePath was supplied, use the by-name paths below") -> used
        // to build an actionable error if every fallback is exhausted, instead of silently returning
        // an empty list that reads identically to a confirmed zero-implementations result.
        string? scopedResolutionFailure = null;
        // True when scopedResolutionFailure already carries ranked candidates (or the no-similar-name fact) for the pinned file,
        // so the solution-wide candidate text is not appended a second time.
        var scopedFailureNamesCandidates = false;

        // The MCP tool layer resolves an omitted `filepath` to FilePathWrapper's empty-string default
        // (via ResolveFromWire), not a C# null -> so checking `filePath != null` alone always took this
        // branch, even when no filepath was actually supplied, silently bypassing the by-name
        // fallback below. Treat blank the same as null.
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            // Original path: resolve from the declaring file.
            // Shared document lookup: case-insensitive exact path, then a unique bare name. A miss throws
            // ToolNotFoundException / ToolAmbiguousMatchException naming the closest real paths or the
            // candidates, like FindCallersAsync (this used to fall through to the by-type-name fallback
            // below and could silently answer for a different symbol than the file named).
            var document = (await ((IWorkspaceReader)_workspaceManager).GetDocumentAsync(_workspaceManager.ResolveFromWire(filePath), ReadSource.Committed, cancellationToken))
                .GetDocumentOrThrow();
            solution = document.Project.Solution;
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                {
                    scopedResolutionFailure = $"could not obtain a syntax tree/semantic model for '{filePath}'.";
                }
                else if (contextSnippet != null)
                {
                    try
                    {
                        symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(document, contextSnippet, symbolName, lineBefore, lineAfter, cancellationToken);
                    }
                    catch (ToolNotFoundException ex) when (ex.Snippet is not null)
                    {
                        // The snippet locator throws (rather than returning null) when the snippet matches nothing;
                        // keep its detail and append the ready-to-run next call for the declaring file.
                        var snippetText = await document.GetTextAsync(cancellationToken);
                        var snippetDecls = SyntaxTargetResolver.ResolveCandidates(root, snippetText, symbolName, cancellationToken)
                            .Select(c => c.Node)
                            .OfType<MemberDeclarationSyntax>()
                            .ToList();
                        throw new ToolNotFoundException(ex.Message + BuildFindReferencesNextCallForFile(symbolName, "implementations", document.FilePath ?? filePath, snippetDecls), ex.MissingTargetKind, ex.MissingTargetName, ex.Snippet, ex.Candidates);
                    }
                    if (symbol == null)
                    {
                        // Same name-only lookup as the no-snippet branch below, used purely to
                        // enrich this message with "symbolName WAS found at line N" when possible ->
                        // does not change resolution/matching behavior, only what the error reports.
                        // Kind-complete (ResolveCandidates), so a type name declared in this file is
                        // reported too, matching FindCallersAsync's precheck.
                        var nameOnlyText = await document.GetTextAsync(cancellationToken);
                        var nameOnlyCandidates = SyntaxTargetResolver.ResolveCandidates(root, nameOnlyText, symbolName, cancellationToken)
                            .Select(c => c.Node)
                            .OfType<MemberDeclarationSyntax>()
                            .Cast<SyntaxNode>()
                            .Concat(FindPositionalRecordParameters(root, symbolName))
                            .ToList();
                        var positionalRecord = FindPositionalRecordProperty(model, root, symbolName);
                        scopedResolutionFailure = $"contextSnippet did not resolve to a symbol in '{filePath}'. " +
                            DescribeNameOnlyCandidates(nameOnlyCandidates, symbolName) +
                            (positionalRecord != null
                                ? $" '{symbolName}' is a positional parameter of record {positionalRecord.ContainingType.Name}; point contextSnippet at that line or omit it to resolve the property by name."
                                : string.Empty);
                    }
                }
                else
                {
                    // Kind-complete lookup (methods, properties, fields, constructors and every type
                    // kind) instead of a Method/Property/Field-only switch, which silently dropped a
                    // class/interface/record/struct/enum declared in this file. A constructor is
                    // demoted behind any other match: for `class Foo` the name matches both the type
                    // and its constructor, and the type is the intended target.
                    var declsText = await document.GetTextAsync(cancellationToken);
                    var declCandidates = SyntaxTargetResolver.ResolveCandidates(root, declsText, symbolName, cancellationToken);
                    var decls = declCandidates
                        .Select(c => c.Node)
                        .OfType<MemberDeclarationSyntax>()
                        .ToList();
                    // Preferred set: non-constructor declarations when any exist, otherwise all. Only a preferred set that
                    // still holds several distinct overloads is ambiguous (see GetOverloadKey). Thrown directly (no try/catch wraps this block), so the
                    // caller gets ToolAmbiguousMatchException rather than a scopedResolutionFailure NotFound.
                    var preferredDecls = decls.Where(m => m is not ConstructorDeclarationSyntax).ToList();
                    if (preferredDecls.Count == 0)
                    {
                        preferredDecls = decls;
                    }

                    if (preferredDecls.Select(d => GetOverloadKey(d)).Distinct().Count() > 1)
                    {
                        var ambiguousCandidates = declCandidates.Where(c => preferredDecls.Any(p => ReferenceEquals(p, c.Node))).ToList();
                        throw new ToolAmbiguousMatchException(SyntaxTargetResolver.BuildHintForCandidates(ambiguousCandidates, [], "ambiguous"), SyntaxTargetResolver.BuildDeclarationCandidates(ambiguousCandidates, declsText, document.FilePath ?? string.Empty));
                    }

                    var decl = preferredDecls.FirstOrDefault();
                    if (decl != null)
                    {
                        // GetDeclaredSymbol returns null directly on a FieldDeclarationSyntax (it can
                        // declare multiple variables, so the declared symbol lives on the matching
                        // VariableDeclaratorSyntax child instead) -> fall back to that child so a field
                        // match here doesn't spuriously read as "not found declared" below.
                        symbol = model.GetDeclaredSymbol(decl, cancellationToken)
                            ?? (decl as FieldDeclarationSyntax)?.Declaration.Variables
                                .Where(v => v.Identifier.Text == symbolName)
                                .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                                .FirstOrDefault(s => s != null);
                    }

                    symbol ??= FindPositionalRecordProperty(model, root, symbolName);
                    if (symbol == null)
                    {
                        scopedResolutionFailure = MemberRefactoringNotFound.DeclarationMessage("symbol", symbolName, "symbolName", MemberRefactoringNotFound.MemberNames(root), filePath);
                        scopedFailureNamesCandidates = true;
                    }
                }
            }
        }
        else
        {
            solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

            // Defect-3 fix: no filePath -> resolve by name across the solution.
            // ResolveCandidatesWithSemanticAsync's semantic half already scans every project in the
            // solution regardless of which document's root/text is passed for the free syntax half,
            // so any parseable document works here -- there is no "current file" in the by-name path.
            var anyDoc = solution.Projects.SelectMany(p => p.Documents).First();
            var anyRoot = (await anyDoc.GetSyntaxRootAsync(cancellationToken))!;
            var anyText = await anyDoc.GetTextAsync(cancellationToken);
            var byNameCandidate = await ResolveCandidatesWithSemanticAsync(anyRoot, anyText, symbolName, includeSemantic: true, cancellationToken);
            var allMatches = byNameCandidate.SemanticMatches ?? [];
            var memberMatches = allMatches
                .Where(c => c.Symbol is not INamedTypeSymbol)
                .ToList();

            // Same type-aware split as FindCallersAsync: a lone type (or a type plus its own constructors)
            // resolves to the type; a type plus an unrelated same-named member is ambiguous.
            // PreferImplementableMember narrows the member subset only; the contextSnippet scan stays member-only.
            var (typeMatches, otherMembers) = SplitTypeAndMemberMatches(allMatches, symbolName);
            if (contextSnippet == null && typeMatches.Count > 0)
            {
                symbol = ResolveTypeNameMatch(symbolName, typeMatches, otherMembers, PreferImplementableMember);
            }
            else if (contextSnippet != null)
            {
                ToolAmbiguousMatchException? firstAmbiguous = null;
                foreach (var candidate in memberMatches)
                {
                    if (candidate.FilePath == null)
                    {
                        continue;
                    }

                    var doc = DocumentLookup.FindDocumentOrNull(solution, candidate.FilePath);
                    if (doc == null)
                    {
                        continue;
                    }

                    try
                    {
                        symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(doc, contextSnippet, symbolName, lineBefore, lineAfter, cancellationToken);
                        // A snippet can land on a neighbouring declaration or call site; only accept a symbol whose name matches symbolName.
                        if (symbol != null && symbol.Name != symbolName
                            && !(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctorSymbol && ctorSymbol.ContainingType.Name == symbolName))
                        {
                            symbol = null;
                        }
                        if (symbol != null)
                        {
                            break;
                        }
                    }
                    catch (ToolAmbiguousMatchException ambiguous)
                    {
                        // Keep the first ambiguity: if no candidate resolves, surface it instead of a misleading NotFound.
                        firstAmbiguous ??= ambiguous;
                    }
                    catch (ToolNotFoundException)
                    {
                        // snippet not found in this document -> continue (other ToolException kinds propagate)
                    }
                }

                if (symbol == null && firstAmbiguous != null)
                {
                    throw firstAmbiguous;
                }

                if (symbol == null)
                {
                    // The snippet was supplied specifically to disambiguate -> a miss must not
                    // silently fall through to "pick one anyway" below. Without this guard, a
                    // caller-supplied contextSnippet that doesn't match any candidate was dropped
                    // on the floor and PreferImplementableMember(...).FirstOrDefault() picked an
                    // arbitrary same-named symbol from anywhere in the solution instead - the same
                    // defect shape fixed above in FindCallersAsync.
                    throw new ToolNotFoundException(
                        $"FindImplementations: contextSnippet did not resolve to a symbol for symbolName " +
                        $"'{symbolName}' in any of the {memberMatches.Count} candidate location(s) found " +
                        "across the solution. This is NOT a confirmed zero-implementations result - the " +
                        "lookup never ran. " + DescribeCandidateLocations(memberMatches) + DescribeTypeMatchHint(allMatches, symbolName) +
                        "Re-check the snippet against GetMethodSource/GetFileOutline output for the " +
                        "specific candidate you meant, or supply filePath to pin resolution to one file." + BuildFindReferencesNextCall(symbolName, "implementations", allMatches)); // hint-ok: names tools in prose
                }
            }
            else
            {
                var narrowed = PreferImplementableMember(memberMatches);
                if (narrowed.Count > 1)
                {
                    // No contextSnippet was supplied and narrowing (implementable-vs-concrete)
                    // still leaves more than one same-named candidate -> FirstOrDefault() here
                    // would silently pick an arbitrary one across the whole solution. Force the
                    // caller to disambiguate instead of returning results for the wrong symbol.
                    throw new ToolAmbiguousMatchException(
                        $"symbolName '{symbolName}' is ambiguous - {narrowed.Count} " +
                        "candidates found across the solution and no contextSnippet was supplied to " +
                        "disambiguate. This is NOT a confirmed result for any single symbol - resolution " +
                        "stopped before the lookup ran. " + DescribeCandidateLocations(narrowed, memberMatches.Where(m => narrowed.All(n => !SymbolEqualityComparer.Default.Equals(n.Symbol, m.Symbol))).ToList()) +
                        "Supply contextSnippet (a verbatim substring near the intended declaration) or " +
                        "filePath to pin resolution to the one you mean.", BuildAmbiguousCandidates(narrowed, memberMatches.Where(m => narrowed.All(n => !SymbolEqualityComparer.Default.Equals(n.Symbol, m.Symbol))).ToList()));
                }

                symbol = narrowed.FirstOrDefault()?.Symbol;
            }
        }

        // Gated on scopedResolutionFailure == null: when a filePath/contextSnippet-scoped lookup ran and
        // failed, falling back to a solution-wide type lookup would silently answer for a different
        // same-named type than the file named. Report the scoped failure below instead.
        if (symbol == null && scopedResolutionFailure == null)
        {
            // Fallback: try to find as a named type (e.g., user passed an interface name, not a member name).
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null)
                {
                    continue;
                }

                symbol = compilation
                    .GetSymbolsWithName(symbolName, SymbolFilter.Type, cancellationToken)
                    .OfType<INamedTypeSymbol>()
                    .FirstOrDefault();
                if (symbol != null)
                {
                    break;
                }
            }
        }

        if (symbol == null)
        {
            var missDetail = scopedFailureNamesCandidates
                ? string.Empty
                : await DescribeSymbolMissInSolutionAsync(solution, symbolName, "implementations", cancellationToken);
            throw new ToolNotFoundException(
                "FindImplementations: " + (scopedResolutionFailure ??
                    ($"symbolName '{symbolName}' could not be resolved anywhere in the solution" +
                    (contextSnippet != null ? " with the supplied contextSnippet" : "") + ".")) +
                " The lookup never ran, so this is not a zero-implementations result. " + missDetail);
        }

        // The resolved symbol may still be structurally incapable of having implementations - e.g.
        // ResolveSymbolByNameAsync's preferImplementable heuristic found no abstract/virtual/override/
        // interface candidate at all and fell back to a concrete one. SymbolFinder.FindImplementationsAsync
        // would silently return an empty list for such a symbol, indistinguishable from a genuine
        // "confirmed zero implementations" answer -> detect it up front and say so explicitly instead.
        //
        // Scoped to the by-name path only (filePath blank): when the caller supplies filePath, they
        // pinned resolution to one specific declaration themselves, so a concrete/non-virtual method
        // with zero implementations is a trustworthy, non-ambiguous "confirmed zero" answer, not a
        // symptom of the wrong-candidate disambiguation problem this check exists to catch.
        var skipStructuralCheck = !string.IsNullOrWhiteSpace(filePath);
        // A type is implementable when something can derive from / implement it: an interface, an abstract
        // class or any non-sealed, non-static class. Sealed classes, structs, enums, delegates and static
        // classes cannot have implementations.
        var isImplementable = symbol is INamedTypeSymbol namedTarget
            ? namedTarget.TypeKind is TypeKind.Class or TypeKind.Interface && !namedTarget.IsSealed && !namedTarget.IsStatic
            : symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride
                || symbol.ContainingType?.TypeKind == TypeKind.Interface;
        // Type eligibility applies on every path, including filePath: a type resolved from the named
        // file is just as unable to have implementations when sealed/static/struct/enum/delegate.
        // skipStructuralCheck only relaxes the member check below (a pinned concrete member is a
        // trustworthy "confirmed zero" answer), not the type check.
        if (!isImplementable && symbol is INamedTypeSymbol ineligibleType)
        {
            throw new ToolTargetIneligibleException(
                $"FindImplementations: symbolName '{symbolName}' resolved to {DescribeKind(ineligibleType)} " +
                $"'{ineligibleType.ToDisplayString()}', which is sealed, static or not a class/interface - nothing can " +
                "derive from or implement it, so this is NOT a confirmed zero-implementations result for any " +
                "other symbol of that name. For usages of the type itself use references with kind callers.");
        }

        if (!skipStructuralCheck && !isImplementable)
        {
            throw new ToolTargetIneligibleException(
                $"FindImplementations: symbolName '{symbolName}' resolved to {symbol.Kind} " +
                $"'{symbol.ToDisplayString()}' on {symbol.ContainingType?.TypeKind.ToString().ToLowerInvariant() ?? "an unknown container"} " +
                $"'{symbol.ContainingType?.Name ?? "?"}', which is concrete, non-virtual, and not an interface " +
                "member - it is structurally incapable of having implementations, so this is NOT a confirmed " +
                "zero-implementations result. If you meant the interface/virtual declaration this member " +
                "implements or overrides, disambiguate with contextSnippet or filepath pointing at that " +
                $"declaration specifically, or call {ToolCallHint.Call("Search", ("mode", "symbol"), ("exactMatch", "true"))} first to see every " +
                $"same-named candidate ('{symbolName}') with its containing type and kind.");
        }

        IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, null, cancellationToken);

        // SymbolFinder.FindImplementationsAsync only finds interface implementers and returns nothing for a
        // class symbol, so an abstract/non-sealed class would read as "confirmed zero implementations" even
        // with derived classes present. Union in the transitive derived classes for a class target.
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } classTarget)
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(classTarget, solution, true, null, cancellationToken);
            implementations = implementations.Concat(derived).Distinct(SymbolEqualityComparer.Default).ToList();
        }

        var results = new List<ImplementationInfo>();

        foreach (var impl in implementations)
        {
            var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
            results.Add(new ImplementationInfo(
                TypeName: impl.ToDisplayString(),
                FilePath: loc?.SourceTree?.FilePath,
                Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                Kind: impl.Kind.ToString()
            ));
        }

        return results;
    }

    /// <summary>
    /// Traces a variable's full lifetime from declaration through every read, write, and capture
    /// across all code paths (loops, conditionals, try/catch) in the enclosing method.
    /// </summary>
    public async Task<VariableLifetimeReport> TraceVariableLifetimeAsync(
        FilePathWrapper filePath,
        string variableName,
        int lineNumber,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new VariableLifetimeReport { Error = "Could not obtain syntax tree or semantic model." };
        }

        var text = await document.GetTextAsync(cancellationToken);
        if (lineNumber < 1 || lineNumber > text.Lines.Count)
        {
            return new VariableLifetimeReport { Error = $"Line {lineNumber} is out of range.", ErrorCode = ToolErrorCode.InvalidArgument };
        }

        // Find declaration node at/near the requested line
        var targetLineSpan = text.Lines[lineNumber - 1].Span;
        ISymbol? symbol = null;
        SyntaxNode? declNode = null;

        // Same-named declarations in the phase that resolves the variable; the first is traced, 2+ produce a warning.
        List<SyntaxNode> candidates = [];

        // Check variable declarators
        var declarators = root.DescendantNodes(n => n.Span.IntersectsWith(targetLineSpan))
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == variableName)
            .ToList();
        if (declarators.Count > 0)
        {
            symbol = semanticModel.GetDeclaredSymbol(declarators[0], cancellationToken);
            declNode = declarators[0];
            candidates.AddRange(declarators);
        }

        // Check parameters
        if (symbol == null)
        {
            var parameters = root.DescendantNodes(n => n.Span.IntersectsWith(targetLineSpan))
                .OfType<ParameterSyntax>()
                .Where(p => p.Identifier.Text == variableName)
                .ToList();
            if (parameters.Count > 0)
            {
                symbol = semanticModel.GetDeclaredSymbol(parameters[0], cancellationToken);
                declNode = parameters[0];
                candidates.AddRange(parameters);
            }
        }

        // Widen search to nearby lines if not found on the exact line
        if (symbol == null)
        {
            var nearby = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.Text == variableName
                    && Math.Abs(v.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - lineNumber) <= 5)
                .ToList();
            if (nearby.Count > 0)
            {
                symbol = semanticModel.GetDeclaredSymbol(nearby[0], cancellationToken);
                declNode = nearby[0];
                candidates.AddRange(nearby);
            }
        }

        if (symbol == null)
        {
            return new VariableLifetimeReport { Error = DescribeVariableMiss(root, targetLineSpan, variableName, lineNumber, filePath.ToString()), ErrorCode = ToolErrorCode.NotFound };
        }

        // Read path: trace the first candidate but never silently; name the others and the retry (lineNumber is the selector).
        string? ambiguityWarning = null;
        if (candidates.Count > 1)
        {
            var candidateLines = string.Join(", ", candidates.Select(c => c.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            ambiguityWarning = $"'{variableName}' matches {candidates.Count} declarations near line {lineNumber} (lines {candidateLines}); traced the first. To trace another, retry with lineNumber set to its line.";
        }

        var declLoc = declNode?.GetLocation();
        var declLine = (declLoc?.GetLineSpan().StartLinePosition.Line + 1) ?? lineNumber;
        var declFilePath = declLoc?.SourceTree?.FilePath ?? filePath;

        // Get type name
        string typeName = symbol switch
        {
            ILocalSymbol ls => ls.Type.ToDisplayString(),
            IParameterSymbol ps => ps.Type.ToDisplayString(),
            IFieldSymbol fs => fs.Type.ToDisplayString(),
            _ => "unknown"
        };

        // Scope description from enclosing method
        var enclosingMethod = declNode?.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();
        string scopeDesc = enclosingMethod != null
            ? $"method: {enclosingMethod.Identifier.Text}"
            : "unknown scope";

        // SuccessData flow analysis on the enclosing method body
        bool definitelyAssigned = false, alwaysAssigned = false, capturedInClosure = false;
        if (enclosingMethod?.Body != null)
        {
            try
            {
                var dataFlow = semanticModel.AnalyzeDataFlow(enclosingMethod.Body);
                if (dataFlow?.Succeeded == true)
                {
                    definitelyAssigned = dataFlow.DefinitelyAssignedOnEntry.Any(s =>
                        SymbolEqualityComparer.Default.Equals(s, symbol));
                    alwaysAssigned = dataFlow.AlwaysAssigned.Any(s =>
                        SymbolEqualityComparer.Default.Equals(s, symbol));
                    capturedInClosure = dataFlow.CapturedInside.Any(s =>
                        SymbolEqualityComparer.Default.Equals(s, symbol));
                }
            }
            catch { /* data flow may fail on complex bodies */ }
        }

        // Find all references via SymbolFinder (cross-file safe)
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var accesses = new List<VariableAccess>();

        // Add declaration entry
        accesses.Add(new VariableAccess(
            FilePath: declFilePath,
            Line: declLine,
            Column: (declLoc?.GetLineSpan().StartLinePosition.Character + 1) ?? 0,
            AccessKind: "Declaration",
            ContextStack: BuildContextStack(declNode),
            IsInLoop: IsInsideLoop(declNode),
            IsInConditional: IsInsideConditional(declNode)
        ));

        foreach (var refGroup in references)
        {
            foreach (var loc in refGroup.Locations)
            {
                if (!loc.Location.IsInSource)
                {
                    continue;
                }

                var lineSpan = loc.Location.GetLineSpan();
                var refLine = lineSpan.StartLinePosition.Line + 1;
                var refCol = lineSpan.StartLinePosition.Character + 1;
                var refFilePath = loc.Location.SourceTree?.FilePath ?? filePath;

                // Determine access kind from surrounding AST
                var refDoc = DocumentLookup.FindDocumentOrNull(solution, refFilePath);
                if (refDoc == null)
                {
                    continue;
                }

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null)
                {
                    continue;
                }

                var tokenNode = refRoot.FindToken(loc.Location.SourceSpan.Start).Parent;
                string accessKind = DetermineAccessKind(tokenNode, variableName);

                accesses.Add(new VariableAccess(
                    FilePath: refFilePath,
                    Line: refLine,
                    Column: refCol,
                    AccessKind: accessKind,
                    ContextStack: BuildContextStack(tokenNode),
                    IsInLoop: IsInsideLoop(tokenNode),
                    IsInConditional: IsInsideConditional(tokenNode)
                ));
            }
        }

        accesses.Sort((a, b) =>
        {
            var fc = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            return fc != 0 ? fc : a.Line.CompareTo(b.Line);
        });

        return new VariableLifetimeReport
        {
            VariableName = variableName,
            TypeName = typeName,
            DeclarationFile = declFilePath,
            DeclarationLine = declLine,
            ScopeDescription = scopeDesc,
            IsDefinitelyAssigned = definitelyAssigned,
            IsAlwaysAssigned = alwaysAssigned,
            IsCapturedInClosure = capturedInClosure,
            Warning = ambiguityWarning,
            Accesses = accesses
        };
    }

    /// <summary>
    /// The not-found text of TraceVariableLifetimeAsync: what was searched, then the ranked variables and parameters declared in the
    /// member that contains the requested line (the whole file when the line is outside every member), each with its declaration line
    /// and a prefilled retry, or the no-similar-name fact with the names the scope does declare. The calling tool appends its own
    /// GetFileOutline option for a NotFound result, so none is added here. Ranking is <see cref="NameCandidates.Rank{T}"/>.
    /// </summary>
    private static string DescribeVariableMiss(SyntaxNode root, TextSpan targetLineSpan, string variableName, int lineNumber, string filePath)
    {
        const int MaxListedNames = 15;
        var scopeNode = root.FindToken(targetLineSpan.Start).Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? root;
        var scope = scopeNode is MemberDeclarationSyntax ? $"the member that contains line {lineNumber}" : $"'{filePath}'";

        var declared = new List<(string Name, int Line)>();
        foreach (var node in scopeNode.DescendantNodesAndSelf())
        {
            var identifier = node switch
            {
                VariableDeclaratorSyntax declarator => declarator.Identifier,
                ParameterSyntax parameter => parameter.Identifier,
                _ => default,
            };
            if (identifier.Text.Length > 0)
            {
                declared.Add((identifier.Text, identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
        }

        var distinct = declared.GroupBy(d => d.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var parts = new List<string>
        {
            $"Variable '{variableName}' was not found near line {lineNumber} in '{filePath}' (searched variable declarations and parameters on that line, then variable declarations within 5 lines of it)."
        };

        var ranked = NameCandidates.Rank(variableName, distinct, d => d.Name);
        foreach (var (candidate, _) in ranked)
        {
            parts.Add($"Did you mean '{candidate.Name}'? Retry with variableName: '{candidate.Name}'. It is declared at line {candidate.Line}; lineNumber: {candidate.Line} selects it.");
        }

        if (ranked.Count == 0)
        {
            parts.Add(distinct.Count == 0
                ? $"No variable or parameter is declared in {scope}."
                : $"No variable or parameter declared in {scope} has a similar name to '{variableName}'. Declared there: {string.Join(", ", distinct.Take(MaxListedNames).Select(d => d.Name))}{(distinct.Count > MaxListedNames ? $" (+{distinct.Count - MaxListedNames} more)" : string.Empty)}.");
        }

        return string.Join(" ", parts);
    }

    private static string DetermineAccessKind(SyntaxNode? node, string varName)
    {
        if (node == null)
        {
            return "Read";
        }

        // ref/out argument
        var argList = node.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argList != null)
        {
            if (argList.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
            {
                return "Ref";
            }

            if (argList.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                return "Out";
            }
        }

        // return statement
        if (node.Ancestors().OfType<ReturnStatementSyntax>().Any())
        {
            return "Return";
        }

        // assignment -> LHS?
        var assignment = node.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
        if (assignment != null)
        {
            var lhsText = assignment.Left.ToString();
            if (lhsText == varName || lhsText.EndsWith("." + varName))
            {
                return "Write";
            }
        }

        // local variable declaration initializer
        var declarator = node.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (declarator?.Identifier.Text == varName)
        {
            return "Declaration";
        }

        // lambda/anonymous method capture
        if (node.Ancestors().Any(a => a is LambdaExpressionSyntax or AnonymousFunctionExpressionSyntax))
        {
            return "Capture";
        }

        return "Read";
    }

    private static string BuildContextStack(SyntaxNode? node)
    {
        if (node == null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m: parts.Add($"method:{m.Identifier.Text}"); break;
                case ConstructorDeclarationSyntax c: parts.Add($"ctor:{c.Identifier.Text}"); break;
                case ForStatementSyntax: parts.Add("for"); break;
                case ForEachStatementSyntax fe: parts.Add($"foreach({fe.Identifier.Text})"); break;
                case WhileStatementSyntax: parts.Add("while"); break;
                case DoStatementSyntax: parts.Add("do-while"); break;
                case IfStatementSyntax: parts.Add("if"); break;
                case SwitchStatementSyntax: parts.Add("switch"); break;
                case TryStatementSyntax: parts.Add("try"); break;
                case CatchClauseSyntax: parts.Add("catch"); break;
                case FinallyClauseSyntax: parts.Add("finally"); break;
                case LambdaExpressionSyntax: parts.Add("lambda"); break;
            }
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static bool IsInsideLoop(SyntaxNode? node) =>
        node?.Ancestors().Any(a =>
            a is ForStatementSyntax or
            ForEachStatementSyntax or
            WhileStatementSyntax or
            DoStatementSyntax) == true;

    private static bool IsInsideConditional(SyntaxNode? node) =>
        node?.Ancestors().Any(a =>
            a is IfStatementSyntax or
            SwitchStatementSyntax or
            ConditionalExpressionSyntax) == true;

    /// <summary>
    /// Builds a "symbolName 'X' WAS found declared at: ..." hint for a contextSnippet-resolution
    /// failure, from the name-only candidate declarations (syntax nodes) already available at the call
    /// site. Input: syntax nodes, expected to come from ONE file (the body does not check this; each
    /// line number comes from the node's own syntax tree, -1 when it has none). Output: the first 3
    /// nodes as "line N `first line`", the first line trimmed to 50 chars (47 plus "..." when longer),
    /// joined by ", ", with " (+N more)" appended when there are more than 3, then ". " (trailing
    /// space). Returns an empty string when there are no candidates. Mirrors BasicRefactoringEngine's
    /// NearMissList hint shape (line + first-line preview, up to 3, "+N more" suffix) so an agent sees
    /// the same style of actionable error across every tool in this codebase that resolves by
    /// name+contextSnippet, not just the BasicRefactoringEngine mutation tools. Use it when a
    /// contextSnippet did not match although the name exists in the file. For a name that resolved to
    /// nothing, use <see cref="DescribeNearMissCandidatesAsync"/>; for an ambiguous solution-wide name
    /// that needs a retry, use <see cref="DescribeCandidateLocations"/>.
    /// <seealso cref="DescribeNearMissCandidatesAsync"/>
    /// <seealso cref="DescribeCandidateLocations"/>
    /// </summary>
    public static string DescribeNameOnlyCandidates(IReadOnlyList<SyntaxNode> candidates, string symbolName)
    {
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var previews = candidates.Take(3).Select(c =>
        {
            var line = (c.SyntaxTree?.GetLineSpan(c.Span).StartLinePosition.Line + 1) ?? -1;
            var text = c.ToString().Split('\n').First().Trim();
            if (text.Length > 50)
            {
                text = text.Substring(0, 47) + "...";
            }

            return $"line {line} `{text}`";
        });

        var count = candidates.Count;
        var suffix = count > 3 ? $" (+{count - 3} more)" : "";
        return $"symbolName '{symbolName}' WAS found declared at: {string.Join(", ", previews)}{suffix}. ";
    }

    /// <summary>
    /// Builds a "Did you mean: ...?" hint for a symbolName that resolved to zero candidates anywhere
    /// in the solution (ResolveSymbolByNameAsync returned null). Input: the Solution, symbolName, and
    /// kind (passed to the follow-up call). Scans every project's compilation with a case-insensitive
    /// substring predicate on Compilation.GetSymbolsWithName (SymbolFilter.TypeAndMember), so an
    /// approximately-remembered name still surfaces real candidates inline. Collects up to 40 distinct
    /// matches (label "Name (ContainingType)", or just the name when the containing namespace is the
    /// global one), orders exact-name matches first and then by RankCandidate, and lists at most 5
    /// labels. When matches exist the returned string ends with the FindReferences next call from
    /// BuildFindReferencesNextCall for the first-ranked match. Returns an empty string when nothing
    /// similar is found, so the caller message degrades to a plain "not found". Only ever called on the
    /// already-failing path, so a successful resolution pays no cost. Capped at 5 labels (wider than the
    /// 3 of <see cref="DescribeNameOnlyCandidates"/>) because these hits span the whole solution. Use it
    /// for a name that resolved to nothing; for a name found in one file whose contextSnippet missed, use
    /// <see cref="DescribeNameOnlyCandidates"/>; for an ambiguous name needing a retry, use
    /// <see cref="DescribeCandidateLocations"/>.
    /// <seealso cref="DescribeNameOnlyCandidates"/>
    /// <seealso cref="DescribeCandidateLocations"/>
    /// </summary>
    // TODO remove DescribeNearMissCandidatesAsync (and the cref mentions of it) once no document refers to it; FindCallers and FindImplementations moved to DescribeSymbolMissInSolutionAsync
    [Obsolete("Use DescribeSymbolMissInSolutionAsync, which ranks through SymbolNotFoundDetail")]
    public static async Task<string> DescribeNearMissCandidatesAsync(
        Solution solution, string symbolName, string kind, CancellationToken cancellationToken)
    {
        var matches = new List<(string Label, ISymbol Symbol)>();

        foreach (var project in solution.Projects)
        {
            if (matches.Count >= 40)
            {
                break;
            }

            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same "unbuildable project can't be scanned" tolerance as ResolveSymbolByNameAsync -
                // a near-miss hint is best-effort, not worth failing the whole error message over.
            }

            if (compilation == null)
            {
                continue;
            }

            var found = compilation.GetSymbolsWithName(
                name => name.Contains(symbolName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.TypeAndMember,
                cancellationToken);

            foreach (var match in found)
            {
                if (matches.Count >= 40)
                {
                    break;
                }

                var containingType = match.ContainingType?.Name ?? match.ContainingNamespace?.Name ?? "?";
                // A type in the global namespace has an empty containing-namespace name -> render just
                // the name instead of a dangling "Foo ()".
                var label = containingType.Length == 0 ? match.Name : $"{match.Name} ({containingType})";
                if (!matches.Any(m => m.Label == label))
                {
                    matches.Add((label, match));
                }
            }
        }

        if (matches.Count == 0)
        {
            return string.Empty;
        }

        var ordered = matches
            .OrderByDescending(m => string.Equals(m.Symbol.Name, symbolName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => RankCandidate(m.Symbol))
            .ToList();
        var labels = ordered.Take(5).Select(m => m.Label);
        var first = ordered[0].Symbol;
        var candidate = new SemanticSymbolCandidate(
            first,
            first.ContainingAssembly?.Name ?? string.Empty,
            first.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
            first.Locations.Any(l => l.IsInSource));
        return $"Did you mean: {string.Join(", ", labels)}?" + BuildFindReferencesNextCall(first.Name, kind, new[] { candidate });
    }

    /// <summary>
    /// The detail that follows a by-name symbol miss (FindCallers, FindImplementations): <see cref="SymbolNotFoundDetail.BuildAsync"/>
    /// over every project of <paramref name="solution"/>, so the ranked "did you mean" candidates (each with a prefilled
    /// FindReferences call for <paramref name="kind"/> pinned to the candidate's file) or, when none is similar, the no-similar-name
    /// fact and one orientation call. Replaces <see cref="DescribeNearMissCandidatesAsync"/>.
    /// </summary>
    private static async Task<string> DescribeSymbolMissInSolutionAsync(Solution solution, string symbolName, string kind, CancellationToken cancellationToken)
    {
        var projects = solution.Projects.ToList();

        // TODO isCallable: SymbolNavigationEngine has no ActiveToolSurface, so hints name tools unconditionally (as the old text did)
        return await SymbolNotFoundDetail.BuildAsync(
            projects, symbolName, "any", $"{projects.Count} project(s)", null, isCallable: null,
            nextCall: candidate => string.IsNullOrEmpty(candidate.FilePath)
                ? ToolCallHint.Call("FindReferences", ("symbolName", candidate.Name), ("kind", kind))
                : ToolCallHint.Call("FindReferences", ("symbolName", candidate.Name), ("kind", kind), ("filePath", candidate.FilePath)),
            cancellationToken: cancellationToken);
    }

    private static readonly string[] TestAttributeNames =
        ["TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", "FactAttribute", "TheoryAttribute", "TestMethodAttribute"];

    /// <summary>Candidate rank for hints: 0 = non-test type, 1 = non-test member, 2 = test code (lower is listed first).</summary>
    internal static int RankCandidate(ISymbol symbol) => IsTestSymbol(symbol) ? 2 : symbol is INamedTypeSymbol ? 0 : 1;

    /// <summary>True for a test-attributed symbol or one declared in a directory whose name contains "Tests".</summary>
    private static bool IsTestSymbol(ISymbol symbol)
    {
        if (symbol.GetAttributes().Any(a => a.AttributeClass is { } c && TestAttributeNames.Contains(c.Name)))
        {
            return true;
        }

        var path = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath;
        var directory = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(directory)
            && directory.Split('\\', '/').Any(s => s.Contains("Tests", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns "" or a string that starts with one space: " Next: FindReferences(...)" built for the best-ranked candidate
    /// (RankCandidate, stable). A type is pinned by filePath only (types resolve by name); a member also gets its
    /// verbatim declaration-line contextSnippet (plus lineBefore when that line is not unique in its file).
    /// </summary>
    public static string BuildFindReferencesNextCall(string symbolName, string kind, IReadOnlyList<SemanticSymbolCandidate> candidates)
    {
        var best = candidates.OrderBy(c => RankCandidate(c.Symbol)).FirstOrDefault();
        if (best == null)
        {
            return string.Empty;
        }

        var site = ResolveDeclarationSite(best);
        var isType = best.Symbol is INamedTypeSymbol;
        return FormatFindReferencesNextCall(symbolName, kind, site.File, isType ? null : site.Snippet, site.IsUnique ? null : site.LineBefore);
    }

    /// <summary>Same as the candidates overload for a file-pinned miss: the first declaration of symbolName in that file.</summary>
    private static string BuildFindReferencesNextCallForFile(string symbolName, string kind, string filePath, IReadOnlyList<MemberDeclarationSyntax> declarations)
    {
        var decl = declarations.FirstOrDefault();
        if (decl?.SyntaxTree == null)
        {
            return string.Empty;
        }

        var token = decl.DescendantTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == symbolName);
        if (token == default)
        {
            token = decl.GetFirstToken();
        }

        var lines = decl.SyntaxTree.GetText().Lines;
        var index = token.GetLocation().GetLineSpan().StartLinePosition.Line;
        var snippet = lines[index].ToString().Trim();
        var isType = decl is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;
        string? lineBefore = null;
        if (!isType && index > 0 && lines.Count(l => l.ToString().Trim() == snippet) > 1)
        {
            lineBefore = lines[index - 1].ToString().Trim();
        }

        return FormatFindReferencesNextCall(symbolName, kind, filePath, isType ? null : snippet, lineBefore);
    }

    private static string FormatFindReferencesNextCall(string symbolName, string kind, string filePath, string? contextSnippet, string? lineBefore)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath == "?")
        {
            return string.Empty;
        }

        var known = new List<(string Name, string Value)> { ("symbolName", symbolName), ("kind", kind), ("filePath", filePath) };
        if (!string.IsNullOrWhiteSpace(contextSnippet))
        {
            known.Add(("contextSnippet", contextSnippet));
            if (!string.IsNullOrWhiteSpace(lineBefore))
            {
                known.Add(("lineBefore", lineBefore));
            }
        }

        return " " + ToolCallHint.Next("FindReferences", string.Empty, known.ToArray());
    }

    /// <summary>
    /// Cheap pre-check so callers (e.g. Member's dispatch for remove/replace, which only take a bare
    /// memberName) can detect that the named member is actually an enum member and route to
    /// RemoveEnumMemberAsync/ReplaceEnumMemberAsync instead of RemoveMemberAsync/ReplaceMemberAsync
    /// (whose resolver, ResolveMemberByNameOrSnippet, can never match an EnumMemberDeclarationSyntax ->
    /// it isn't a MemberDeclarationSyntax). Returns null if memberName doesn't resolve to an enum
    /// member at all (including "not found" and "ambiguous") -> callers should let the normal
    /// resolution path in whichever method they call next surface the real error in that case.
    /// </summary>
    public async Task<string?> TryGetEnumMemberContainerNameAsync(FilePathWrapper filePath, string memberName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return null;
        }

        var candidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.PreferConstructorOverType(
            SyntaxTargetResolver.ResolveCandidates(root, sourceText, memberName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum))
                .ToList()));
        try
        {
            var target = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode));
            return target?.Kind == CandidateKind.EnumMember && target.Node.Parent is EnumDeclarationSyntax enumDecl ? enumDecl.Identifier.Text : null;
        }
        catch (ToolException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap pre-check so callers (e.g. Member's dispatch) can route to the enum-specific
    /// Add/Remove/ReplaceEnumMemberAsync methods instead of the class/struct/interface/record-only
    /// AddMemberAsync/RemoveMemberAsync/ReplaceMemberAsync/InsertMemberAfterAsync/InsertMemberBeforeAsync
    /// family. Returns false (not an error) if the container isn't found at all -> callers should let
    /// the normal resolution path in whichever method they call next surface the real "not found"
    /// error, rather than this pre-check swallowing it.
    /// </summary>
    public async Task<bool> IsEnumContainerAsync(FilePathWrapper filePath, string containerName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.FindDocumentOrNull(solution, filePath);
        if (document == null)
        {
            return false;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return false;
        }

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, containerName, cancellationToken)
            .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
            .ToList();
        try
        {
            var resolved = SyntaxTargetResolver.ResolveBySnippetOrThrow(candidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode));
            return resolved?.Kind == CandidateKind.Enum;
        }
        catch (ToolException)
        {
            return false;
        }
    }

    public record ContainerMemberInfo(string? Name, string Kind, string Signature, int StartLine, int EndLine);

    /// <summary>
    /// One member's declaration as written (leading doc comment, attributes, body/initializer),
    /// as returned by GetMemberSourceAsync. StartLine/EndLine are 1-based; EndLine is the member's
    /// real last line even when Source was truncated (IsComplete == false).
    /// </summary>
    public record MemberSourceInfo(string? Name, string Kind, string Source, int StartLine, int EndLine, bool IsComplete);
    /// <summary>
    /// Lists the direct members of one container (class/struct/interface/record) in one file,
    /// syntax-scoped rather than symbol-scoped -> unlike GetTypeInfo/GetTypeMembersDetailAsync
    /// (which resolve by type name across the whole solution's compilation and include inherited
    /// members), this only looks at the exact container the caller is about to edit, so its
    /// output lines up with what RemoveMember/ReplaceMember need: an exact memberName plus enough
    /// signature text to build a contextSnippet if the name turns out to be overloaded.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, List<ContainerMemberInfo> Members)> GetContainerMembersAsync(FilePathWrapper filePath, string containerName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException($"Syntax root or source text is unavailable for '{filePath}'; the file did not parse.");
        }

        var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, containerName, cancellationToken)
            .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
            .ToList();
        var containerNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (c, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(c, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;

        if (containerNode is EnumDeclarationSyntax enumDecl)
        {
            var enumLines = sourceText.Lines;
            var enumResult = enumDecl.Members.Select(m =>
            {
                var signature = m.EqualsValue != null ? $"{m.Identifier.Text} = {m.EqualsValue.Value}" : m.Identifier.Text;
                return new ContainerMemberInfo(m.Identifier.Text, "enumMember", signature, enumLines.GetLineFromPosition(m.SpanStart).LineNumber + 1, enumLines.GetLineFromPosition(m.Span.End).LineNumber + 1);
            }).ToList();
            return (EditOutcome.Modified, null, enumResult);
        }

        if (containerNode == null || containerNode is not TypeDeclarationSyntax typeDecl)
        {
            throw new ToolNotFoundException(
                MemberRefactoringNotFound.DeclarationMessage("container", containerName, "containerName", MemberRefactoringNotFound.TypeNames(root), filePath.ToString()) +
                " Searched: class, interface, struct, record and enum declarations matching this name and the contextSnippet/lineBefore/lineAfter filter. " +
                $"{ToolCallHint.Call("Search", ("mode", "text"), ("query", containerName))} finds the name as text.", "container", containerName);
        }

        // TODO tuple outcome slot is always Modified; return only the list
        var lines = sourceText.Lines;
        var result = typeDecl.Members.Select(m =>
        {
            var kind = m switch
            {
                MethodDeclarationSyntax => "method",
                PropertyDeclarationSyntax => "property",
                FieldDeclarationSyntax => "field",
                ConstructorDeclarationSyntax => "constructor",
                EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
                IndexerDeclarationSyntax => "indexer",
                _ => m.Kind().ToString()
            };
            var declStart = m.AttributeLists.Count > 0 ? m.AttributeLists.Last().Span.End : m.SpanStart;
            var signature = sourceText.ToString(TextSpan.FromBounds(declStart, m.Span.End)).Trim();
            var firstLineEnd = signature.IndexOfAny(['\n', '{', ';']);
            if (firstLineEnd > 0)
            {
                signature = signature[..firstLineEnd].Trim();
            }

            return new ContainerMemberInfo(SyntaxTargetResolver.GetMemberName(m), kind, signature, lines.GetLineFromPosition(m.SpanStart).LineNumber + 1, lines.GetLineFromPosition(m.Span.End).LineNumber + 1);
        }).ToList();
        return (EditOutcome.Modified, null, result);
    }

    /// <summary>
    /// Returns one member's declaration source as written: the leading documentation comment (if
    /// any), attribute lists, signature, and body/initializer/accessors, taken verbatim from the
    /// file (the first line starts at the first doc-comment or declaration token, so it carries no
    /// leading indentation; later lines keep the file's own). Supports class-level members and enum
    /// members; whole types are refused (TargetIneligible) because the file-read tool / GetFileOutline serve
    /// that. Resolution mirrors Member(remove)/Member(replace): by name, narrowed by containerName,
    /// then contextSnippet/lineBefore/lineAfter. With requireContainerMatch a containerName that matches
    /// no candidate is refused (NotFound, listing the types that declare the member) instead of ignored;
    /// without it the lenient fallback applies. Unlike remove/replace, an overloaded name with no
    /// contextSnippet is reported as Ambiguous rather than silently resolved to the first overload,
    /// since a read that returns the wrong overload is worse than an error. Output is capped at
    /// 200 lines; past the cap Source is the first 200 lines, IsComplete is false, and EndLine is
    /// still the member's real last line so a follow-up file-read call is one precise call.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, string? ErrorCode, MemberSourceInfo? Source)> GetMemberSourceAsync(FilePathWrapper filePath, string memberName, string? containerName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, bool requireContainerMatch = false, CancellationToken cancellationToken = default)
    {
        const int MaxSourceLines = 200;

        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            throw new ToolInvalidArgumentException($"Cannot read '{filePath}': syntax root or source text is unavailable; the file did not parse.");
        }

        var allNameCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, memberName, cancellationToken);
        var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(allNameCandidates
            .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum))
            .ToList());
        if (requireContainerMatch && !string.IsNullOrWhiteSpace(containerName))
        {
            // Deliberate deviation from the lenient filter below: a named container that holds no candidate is refused, not ignored.
            var narrowed = SyntaxTargetResolver.FilterByContainingTypeStrict(memberCandidates, containerName);
            if (narrowed.Count == 0 && (memberCandidates.Count > 0 || allNameCandidates.Count == 0))
            {
                var (message, candidates) = await SyntaxTargetResolver.BuildContainerMissDetailAsync(solution, root, sourceText, memberName, containerName, document.FilePath ?? string.Empty, ToolCallHint.Call("ReadFile", ("filePath", "{file}"), ("memberName", "{member}"), ("containerName", "{container}")), methodsOnly: false, cancellationToken: cancellationToken);
                if (memberCandidates.Count == 0)
                {
                    // The member is in no type of the file: add what the file does declare, similar names and the next call.
                    message += " " + await MemberNotFoundDetail.BuildAsync(document, root, memberName, containerName, includeHead: false, cancellationToken);
                }

                throw new ToolNotFoundException(message, "member", memberName, candidates: candidates);
            }

            memberCandidates = narrowed;
        }
        else
        {
            memberCandidates = SyntaxTargetResolver.FilterByContainingType(memberCandidates, containerName);
        }

        if (memberCandidates.Count == 0)
        {
            if (allNameCandidates.Count > 0)
            {
                var kindNames = string.Join(", ", allNameCandidates.Select(c => c.Kind).Distinct());
                throw new ToolTargetIneligibleException(
                    $"'{memberName}' is a type-level declaration ({kindNames}). Member source only supports class-level members " +
                    $"(methods, properties, fields, constructors, events, indexers) and enum members; use {ToolCallHint.Call("ReadFile")} or {ToolCallHint.Call("GetFileOutline")} for a whole type.");
            }

            throw new ToolNotFoundException(
                await MemberNotFoundDetail.BuildAsync(document, root, memberName, containerName, includeHead: true, cancellationToken), "member", memberName);
        }

        // TODO overload ambiguity here still gets the generic contextSnippet hint: this method is shared by Member(view) and the ReadFile memberName read, so use SyntaxTargetResolver.BuildOverloadHintForCandidates once the calling tool is passed in
        var resolved = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
            (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode), filePath: document.FilePath ?? string.Empty);

        if (resolved == null)
        {
            throw new ToolNotFoundException(await MemberNotFoundDetail.BuildAsync(document, root, memberName, containerName, includeHead: true, cancellationToken));
        }

        var member = resolved.Node;
        var docComment = member.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        // Use FullSpan: a documentation-comment trivia's Span starts after its leading "///" (or "/**") marker.
        var start = docComment != default ? docComment.FullSpan.Start : member.SpanStart;
        var end = member.Span.End;

        var lines = sourceText.Lines;
        var startLineIndex = lines.GetLineFromPosition(start).LineNumber;
        var endLineIndex = lines.GetLineFromPosition(end).LineNumber;
        var isComplete = true;
        if (endLineIndex - startLineIndex + 1 > MaxSourceLines)
        {
            end = lines[startLineIndex + MaxSourceLines - 1].End;
            isComplete = false;
        }

        var kind = resolved.Kind switch
        {
            CandidateKind.Method => "method",
            CandidateKind.Property => "property",
            CandidateKind.Field => "field",
            CandidateKind.Constructor => "constructor",
            CandidateKind.Event => "event",
            CandidateKind.Indexer => "indexer",
            CandidateKind.EnumMember => "enumMember",
            _ => resolved.Kind.ToString()
        };

        var info = new MemberSourceInfo(resolved.Name, kind, sourceText.ToString(TextSpan.FromBounds(start, end)), startLineIndex + 1, endLineIndex + 1, isComplete);
        return (EditOutcome.Modified, null, null, info);
    }

    /// <summary>
    /// Solution-wide semantic companion to ResolveCandidates' syntax-only list. Opt-in only: callers
    /// that don't need cross-file ISymbol resolution should call ResolveCandidates directly rather
    /// than pay compilation cost for a Compilation they won't use.
    ///
    /// Use this when you need an actual ISymbol per match -- e.g. to hand to
    /// SymbolFinder.FindReferencesAsync/FindImplementationsAsync, or to resolve a name with no
    /// filePath pinned down (see FindCallersAsync/FindImplementationsForMemberAsync) -- then apply a
    /// disambiguation helper (PreferClassMember, PreferImplementableMember, etc.) over
    /// SemanticMatches to pick ONE target. SemanticSymbolCandidate is deliberately lean (Symbol,
    /// ContainingAssembly, FilePath, IsFromSource); it does not flatten display-ready fields like
    /// Signature/DocCommentId/ContainingNamespace onto every match. For that -- a report of EVERY
    /// match's metadata at once, e.g. a "did you mean" hint or a solution-wide discovery listing --
    /// use LocateSymbolAsync instead; that is a different consumer shape than this method solves.
    /// </summary>
    public async Task<SymbolCandidate> ResolveCandidatesWithSemanticAsync(SyntaxNode root, SourceText sourceText, string name, bool includeSemantic, CancellationToken cancellationToken = default)
    {
        var syntaxMatches = SyntaxTargetResolver.ResolveCandidates(root, sourceText, name, cancellationToken);
        if (!includeSemantic)
        {
            return new SymbolCandidate(name, syntaxMatches, null);
        }

        var semanticMatches = new List<SemanticSymbolCandidate>();
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        foreach (var project in solution.Projects)
        {
            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ResolveCandidatesWithSemanticAsync: could not compile project '{Project}'", project.Name);
            }

            if (compilation == null)
            {
                continue;
            }

            foreach (var symbol in compilation.GetSymbolsWithName(name, cancellationToken: cancellationToken))
            {
                var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                semanticMatches.Add(new SemanticSymbolCandidate(symbol, project.AssemblyName, loc?.SourceTree?.FilePath, loc != null));
            }
        }

        return new SymbolCandidate(name, syntaxMatches, semanticMatches);
    }

    /// <summary>
    /// Semantic-side counterpart to PreferNonInterfaceMember: given same-named ISymbol candidates,
    /// prefer one that SymbolFinder.FindImplementationsAsync can actually act on (abstract, virtual,
    /// override, or an interface member) over a plain concrete member -- otherwise FindImplementations
    /// risks resolving to a symbol structurally incapable of having implementations, producing an
    /// empty result indistinguishable from a genuine zero-implementations answer.
    /// </summary>
    public static List<SemanticSymbolCandidate> PreferImplementableMember(List<SemanticSymbolCandidate> candidates)
    {
        var implementable = candidates.Where(c =>
            c.Symbol.IsAbstract || c.Symbol.IsVirtual || c.Symbol.IsOverride
                || c.Symbol.ContainingType?.TypeKind == TypeKind.Interface).ToList();
        return implementable.Count > 0 ? implementable : candidates;
    }

    /// <summary>
    /// Semantic-side counterpart to PreferNonInterfaceMember, expressed over ISymbol candidates:
    /// when an interface member and a same-named class member both match, prefer the class member --
    /// any resolvable candidate finds the same call sites, so FindCallersAsync only needs a concrete
    /// symbol to hand to SymbolFinder.FindReferencesAsync, not specifically the interface declaration.
    /// </summary>
    public static List<SemanticSymbolCandidate> PreferClassMember(List<SemanticSymbolCandidate> candidates)
    {
        var classMembers = candidates.Where(c => c.Symbol.ContainingType?.TypeKind == TypeKind.Class).ToList();
        return classMembers.Count > 0 ? classMembers : candidates;
    }

    /// <summary>
    /// Returns the full type hierarchy for a named type: base class chain, implemented interfaces,
    /// derived classes, and (if an interface) all implementing types.
    /// </summary>
    public async Task<TypeHierarchyReport> GetTypeHierarchyAsync(
        string typeName,
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? typeSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue;
            }

            typeSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, cancellationToken)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();
            if (typeSymbol != null)
            {
                break;
            }
        }

        if (typeSymbol == null)
        {
            var searched = searchProjects.ToList();
            var scope = projectName != null ? $"project '{projectName}'" : $"{searched.Count} project(s)";

            // TODO isCallable: SymbolNavigationEngine has no ActiveToolSurface, so hints name tools unconditionally (as the old text did)
            var detail = await SymbolNotFoundDetail.BuildAsync(
                searched, typeName, "type", scope, projectName, isCallable: null,
                nextCall: candidate => candidate.Kind == "type" ? ToolCallHint.Call("GetTypeInfo", ("typeName", candidate.Name)) : string.Empty,
                cancellationToken: cancellationToken);
            return new TypeHierarchyReport { Error = detail };
        }

        // Base class chain (excluding System.Object)
        var baseChain = new List<string>();
        var bt = typeSymbol.BaseType;
        while (bt != null && bt.SpecialType != SpecialType.System_Object)
        {
            baseChain.Add(bt.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            bt = bt.BaseType;
        }

        // Implemented interfaces (direct only at type level, AllInterfaces for full set)
        var interfaces = typeSymbol.AllInterfaces
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();

        // Derived classes
        var derivedEntries = new List<TypeHierarchyEntry>();
        if (typeSymbol.TypeKind == TypeKind.Class)
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, solution, null, cancellationToken);
            foreach (var d in derived)
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                derivedEntries.Add(new TypeHierarchyEntry(
                    TypeName: d.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    FilePath: loc?.SourceTree?.FilePath,
                    Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                    Kind: d.TypeKind.ToString()
                ));
            }
        }

        // Implementing types (interface only)
        var implementingEntries = new List<TypeHierarchyEntry>();
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, solution, null, cancellationToken);
            foreach (var impl in impls.OfType<INamedTypeSymbol>())
            {
                var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
                implementingEntries.Add(new TypeHierarchyEntry(
                    TypeName: impl.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    FilePath: loc?.SourceTree?.FilePath,
                    Line: loc != null ? loc.GetLineSpan().StartLinePosition.Line + 1 : null,
                    Kind: impl.TypeKind.ToString()
                ));
            }
        }

        return new TypeHierarchyReport
        {
            TypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            BaseClass = typeSymbol.BaseType?.SpecialType == SpecialType.System_Object
                ? null
                : typeSymbol.BaseType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            BaseClassChain = baseChain,
            ImplementedInterfaces = interfaces,
            DerivedTypes = derivedEntries,
            ImplementingTypes = implementingEntries,
            IsInterface = typeSymbol.TypeKind == TypeKind.Interface,
            IsAbstract = typeSymbol.IsAbstract,
            IsSealed = typeSymbol.IsSealed
        };
    }

    /// <summary>
    /// Structured form of the candidate hint used in ambiguity errors: up to 10 listed candidates
    /// (Counted=true), then up to 10 narrowed-away candidates (Counted=false). Fields come from the same
    /// data DescribeCandidateLocations renders.
    /// </summary>
    public static IReadOnlyList<AmbiguousCandidate> BuildAmbiguousCandidates(
        List<SemanticSymbolCandidate> candidates,
        IReadOnlyList<SemanticSymbolCandidate>? excluded = null)
    {
        const int maxCounted = 10;
        const int maxExcluded = 10;

        static AmbiguousCandidate Describe(SemanticSymbolCandidate c, bool counted)
        {
            var site = ResolveDeclarationSite(c);
            var signature = c.Symbol is IMethodSymbol m
                ? m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
                : c.Symbol.Name;
            var containingType = c.Symbol.ContainingType?.Name ?? c.Symbol.ContainingNamespace?.Name ?? "?";
            var kind = !counted && c.Symbol.ContainingType?.TypeKind == TypeKind.Interface
                ? "interface member"
                : DescribeKind(c.Symbol);
            return new AmbiguousCandidate(
                site.File, site.Line, kind, signature, containingType, site.Snippet,
                site.IsUnique ? null : site.LineBefore, counted);
        }

        var result = new List<AmbiguousCandidate>();
        foreach (var c in candidates.OrderBy(c => RankCandidate(c.Symbol)).Take(maxCounted))
        {
            result.Add(Describe(c, counted: true));
        }

        if (excluded is { Count: > 0 })
        {
            foreach (var c in excluded.Take(maxExcluded))
            {
                result.Add(Describe(c, counted: false));
            }
        }

        return result;
    }

    /// <summary>
    /// Solution-wide counterpart to DescribeNameOnlyCandidates: builds a "Candidates (retry with filePath
    /// + contextSnippet of the one you mean):" block from SemanticSymbolCandidate (ISymbol-backed matches,
    /// not one file's syntax nodes). Input: candidates (solution-wide, as the callers pass them) and
    /// optional excluded candidates that a contextSnippet narrowed away. Output: up to 10 counted
    /// candidates in RankCandidate order, each as "  - kind signature in containingType at file:line,
    /// contextSnippet="..."" plus lineBefore="..." when the declaration line is not unique in its file,
    /// then a "(+N more)" line when there are more than 10. If excluded candidates were passed, a
    /// "Not counted (pass filePath to select): file:line (Type.Name, kind), ..." tail lists all of them
    /// (no cap). Returns an empty string for no candidates. Used by FindCallersAsync/
    /// FindImplementationsForMemberAsync's by-name-across-solution path when a contextSnippet misses
    /// every candidate, or when no contextSnippet was supplied and narrowing still leaves more than
    /// one candidate - both cases where silently picking FirstOrDefault() would resolve to an
    /// arbitrary same-named symbol instead of the one the caller meant. Use it for an ambiguous name
    /// that needs a retry; for a name found in one file whose contextSnippet missed, see
    /// <see cref="DescribeNameOnlyCandidates"/>; for a name that resolved to nothing, see
    /// <see cref="DescribeNearMissCandidatesAsync"/>.
    /// <seealso cref="DescribeNameOnlyCandidates"/>
    /// <seealso cref="DescribeNearMissCandidatesAsync"/>
    /// </summary>
    public static string DescribeCandidateLocations(
        List<SemanticSymbolCandidate> candidates,
        IReadOnlyList<SemanticSymbolCandidate>? excluded = null)
    {
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        const int maxListed = 10;
        var sb = new System.Text.StringBuilder("Candidates (retry with filePath + contextSnippet of the one you mean):");
        foreach (var a in BuildAmbiguousCandidates(candidates))
        {
            sb.Append($"\n  - {a.Kind} {a.Signature} in {a.ContainingType} at {a.FilePath}:{a.Line}");
            sb.Append($", contextSnippet=\"{a.ContextSnippet}\"");
            if (a.LineBefore is not null)
            {
                sb.Append($", lineBefore=\"{a.LineBefore}\"");
            }
        }

        if (candidates.Count > maxListed)
        {
            sb.Append($"\n  (+{candidates.Count - maxListed} more)");
        }

        if (excluded is { Count: > 0 })
        {
            var dropped = excluded.Select(c =>
            {
                var site = ResolveDeclarationSite(c);
                var kind = c.Symbol.ContainingType?.TypeKind == TypeKind.Interface
                    ? "interface member"
                    : DescribeKind(c.Symbol);
                var containingType = c.Symbol.ContainingType?.Name ?? c.Symbol.ContainingNamespace?.Name ?? "?";
                return $"{site.File}:{site.Line} ({containingType}.{c.Symbol.Name}, {kind})";
            });
            sb.Append("\nNot counted (pass filePath to select): ").Append(string.Join(", ", dropped));
        }

        sb.Append('\n');
        return sb.ToString();
    }

    private static string DescribeKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        _ => symbol.Kind.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Splits a by-name semantic match list into type matches and the remaining member matches.
    /// A constructor whose containing type is named <paramref name="symbolName"/> counts as that type
    /// (it is dropped from the member list, and its containing type is added to the type list when no
    /// type match already covers it), so "usages of Foo" covers <c>new Foo(...)</c> without the ctor
    /// being reported as an unrelated same-named member.
    /// </summary>
    private static (List<SemanticSymbolCandidate> Types, List<SemanticSymbolCandidate> Members) SplitTypeAndMemberMatches(
        IReadOnlyList<SemanticSymbolCandidate> matches, string symbolName)
    {
        var types = new List<SemanticSymbolCandidate>();
        var members = new List<SemanticSymbolCandidate>();
        var ownConstructors = new List<SemanticSymbolCandidate>();
        foreach (var match in matches)
        {
            if (match.Symbol is INamedTypeSymbol)
            {
                types.Add(match);
            }
            else if (match.Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor
                && ctor.ContainingType.Name == symbolName)
            {
                ownConstructors.Add(match);
            }
            else
            {
                members.Add(match);
            }
        }

        foreach (var ctorMatch in ownConstructors)
        {
            var owner = ctorMatch.Symbol.ContainingType.OriginalDefinition;
            if (types.Any(t => SymbolEqualityComparer.Default.Equals(t.Symbol.OriginalDefinition, owner)))
            {
                continue;
            }

            var loc = owner.Locations.FirstOrDefault(l => l.IsInSource);
            types.Add(new SemanticSymbolCandidate(owner, ctorMatch.ContainingAssembly, loc?.SourceTree?.FilePath ?? ctorMatch.FilePath, loc != null));
        }

        return (types, members);
    }

    /// <summary>
    /// Resolves a by-name lookup in which at least one type matched. A single type with no unrelated
    /// same-named member resolves to that type; otherwise (several types, or a type plus an unrelated
    /// same-named member) the existing ambiguity error lists every candidate. <paramref name="preferMember"/>
    /// (PreferClassMember / PreferImplementableMember) narrows the member subset only.
    /// </summary>
    private static ISymbol ResolveTypeNameMatch(
        string symbolName,
        List<SemanticSymbolCandidate> typeMatches,
        List<SemanticSymbolCandidate> otherMembers,
        Func<List<SemanticSymbolCandidate>, List<SemanticSymbolCandidate>> preferMember)
    {
        var narrowedMembers = otherMembers.Count > 0 ? preferMember(otherMembers) : otherMembers;
        var candidates = typeMatches.Concat(narrowedMembers).ToList();
        if (candidates.Count == 1)
        {
            return candidates[0].Symbol;
        }

        var excluded = otherMembers
            .Where(m => narrowedMembers.All(n => !SymbolEqualityComparer.Default.Equals(n.Symbol, m.Symbol)))
            .ToList();
        throw new ToolAmbiguousMatchException(
            $"symbolName '{symbolName}' is ambiguous - it matches {typeMatches.Count} type(s) and " +
            $"{narrowedMembers.Count} unrelated member(s) across the solution. This is NOT a confirmed " +
            "result for any single symbol - resolution stopped before the lookup ran. " +
            DescribeCandidateLocations(candidates, excluded) +
            "Supply filePath (plus contextSnippet when the target is a member) to pin resolution to the one you mean.",
            BuildAmbiguousCandidates(candidates, excluded));
    }

    /// <summary>
    /// Returns "'X' is a type ..." when the by-name semantic search found a type that the member-only
    /// contextSnippet scan could not use, so a not-found message says why instead of implying the name
    /// does not exist. Empty when no type matched.
    /// </summary>
    private static string DescribeTypeMatchHint(IReadOnlyList<SemanticSymbolCandidate> matches, string symbolName)
    {
        var typeMatch = matches.FirstOrDefault(c => c.Symbol is INamedTypeSymbol);
        if (typeMatch == null)
        {
            return string.Empty;
        }

        var site = ResolveDeclarationSite(typeMatch);
        return $"'{symbolName}' is a type ({DescribeKind(typeMatch.Symbol)} at {site.File}:{site.Line}); a type resolves " +
            "by name alone, so omit contextSnippet, or pass filePath of its declaration. ";
    }

    private static (string File, int Line, string Snippet, string LineBefore, bool IsUnique) ResolveDeclarationSite(SemanticSymbolCandidate c)
    {
        var loc = c.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var file = loc?.SourceTree?.FilePath ?? c.FilePath ?? "?";
        if (loc?.SourceTree == null)
        {
            return (file, -1, string.Empty, string.Empty, true);
        }

        var index = loc.GetLineSpan().StartLinePosition.Line;
        var lines = loc.SourceTree.GetText().Lines;
        if (index >= lines.Count)
        {
            return (file, index + 1, string.Empty, string.Empty, true);
        }

        var snippet = lines[index].ToString().Trim();
        var lineBefore = index > 0 ? lines[index - 1].ToString().Trim() : string.Empty;
        var isUnique = lines.Where(l => l.ToString().Trim() == snippet).Count() == 1;
        return (file, index + 1, snippet, lineBefore, isUnique);
    }
}
/// <summary>
/// A closed set of declaration kinds recognized by ResolveCandidates, replacing the ad-hoc
/// per-call-site MemberDeclarationSyntax/BaseTypeDeclarationSyntax/EnumMemberDeclarationSyntax
/// switch statements previously duplicated across GetMemberName, GetContainerMembersAsync, and
/// FindImplementationsForMemberAsync (see docs/current/proposals/proposal_universal_symbol_resolver.md).
/// </summary>

/// <summary>
/// A solution-wide semantic match for a name, from ResolveCandidates' opt-in includeSemantic path.
/// Requires a compiled Compilation -- meaningfully more expensive than the always-populated
/// syntax-level SyntaxNodeCandidate list, so callers only pay this cost when they ask for it.
/// </summary>
public sealed record SemanticSymbolCandidate(
    ISymbol Symbol,
    string ContainingAssembly,
    string? FilePath,
    bool IsFromSource);
/// <summary>
/// The full candidate set for a name: this file's syntax matches (always populated, cheap, a
/// free side effect of already having the parsed root) plus solution-wide semantic matches
/// (null unless the caller opts in with includeSemantic: true, since resolving ISymbols needs a
/// compiled Compilation).
/// </summary>
public sealed record SymbolCandidate(
    string Name,
    List<SyntaxNodeCandidate> SyntaxMatches,
    List<SemanticSymbolCandidate>? SemanticMatches);

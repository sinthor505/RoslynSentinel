using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Basic;

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
);

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
    /// exactMatch: true (default) for exact name match; false for prefix/contains (discovery mode).
    ///
    /// Returns all matches. Overloads appear as separate entries distinguishable by Signature.
    /// When multiple results are returned, inspect Signature and ContainingType to pick the target,
    /// then supply the chosen FilePathWrapper + ContextSnippet to the next tool call.
    /// </summary>    
    public async Task<List<SymbolLocation>> LocateSymbolAsync(
        string symbolName,
        string symbolKind = "any",
        string? containingType = null,
        string? containingNamespace = null,
        string? projectName = null,
        FilePathWrapper filePath = default,
        bool exactMatch = true,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: _workspaceManager is ISolutionProvider-typed (~11 production
        // construction sites across the solution as of 2026-09-25, including AdvancedRefactoringTools.cs,
        // AdvancedRefactoringEngine.cs, DiscoveryEngine.cs, RefactoringEngine.cs,
        // RefactoringSignatureTools.cs, RefactoringStructuralTools.cs, GenerationTools.cs -- widening
        // the constructor to IWorkspaceManager would force touching all of them). Every real
        // ISolutionProvider implementation (PersistentWorkspaceManager, FakeWorkspaceManager) also
        // implements IWorkspaceManager, so this cast is safe today. Cleanup target: widen the
        // constructor and drop this cast once SymbolNavigationEngine's caller list has been
        // consolidated. See docs/current/design_read_chokepoint.md.
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var filter = symbolKind.ToLowerInvariant() switch
        {
            "type" => SymbolFilter.Type,
            "method" or "property" or "field" or "event" => SymbolFilter.Member,
            _ => SymbolFilter.TypeAndMember
        };

        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>();

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
            catch (Exception ex)
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

                    if (filePath.Validated && !filePath.Absolute.Equals(filePath2))
                    {
                        continue;
                    }

                    var lineSpan = location.GetLineSpan();
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var dedupeKey = filePath2 + ":" + line + ":" + symbol.ToDisplayString();
                    if (!seen.Add(dedupeKey))
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
                    catch (Exception ex)
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
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return ErrorHoverInfo($"File not found: '{filePath}'");
        }

        // Primary: use GetDeclaredSymbol/GetSymbolInfo (declaration-based -> more reliable for class/method/property lookups)
        ISymbol? symbol = null;
        try
        {
            symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken);
        }
        catch (ToolException ex) when (ex.Message.Contains("matched") || ex.Message.Contains("No match"))
        {
            return ErrorHoverInfo($"Snippet not found: {ex.Message}");
        }
        catch (ToolException)
        {
            // fallthrough to position-based approach
        }

        // Fallback: position-based lookup using SymbolFinder (handles usage sites)
        if (symbol == null)
        {
            var text = await document.GetTextAsync(cancellationToken);
            int pos;
            try
            {
                pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
            }
            catch (ToolException ex)
            {
                return ErrorHoverInfo(ex.Message);
            }

            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null)
            {
                return ErrorHoverInfo("Could not obtain semantic model for file.");
            }

            // SymbolFinder needs the cursor on an identifier token
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root != null)
            {
                pos = ContextHelper.AdvanceToLastIdentifier(root, pos, contextSnippet.Length);
            }

            symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, pos, solution.Workspace, cancellationToken);
        }

        if (symbol == null)
        {
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
        catch { }

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
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new List<ReadonlyFieldCandidate>();
        }

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
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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

    public async Task<CallGraphNode?> GetCallGraphAsync(
        FilePathWrapper filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            return null;
        }

        // Prefer class method over interface method when both exist in the same file
        var methodDecls = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName).ToList();
        var methodDecl = methodDecls.FirstOrDefault(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any())
            ?? methodDecls.FirstOrDefault();
        if (methodDecl == null)
        {
            return null;
        }

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return null;
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
            methodDoc = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == filePath);
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

    public async Task<ReverseCallGraphNode?> GetReverseCallGraphAsync(
        FilePathWrapper filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see LocateSymbolAsync above for rationale (production-caller cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model == null)
        {
            return null;
        }

        // Prefer class method over interface method when both exist in the same file
        var methodDecls2 = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName).ToList();
        var methodDecl = methodDecls2.FirstOrDefault(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any())
            ?? methodDecls2.FirstOrDefault();
        if (methodDecl == null)
        {
            return null;
        }

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return null;
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

                var refDoc = solution.Projects.SelectMany(p => p.Documents)
                    .FirstOrDefault(d => d.FilePath == refTree.FilePath);
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
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        ISymbol? symbol = null;

        // The MCP tool layer resolves an omitted `filepath` to FilePathWrapper's empty-string default
        // (via SetFilePath), not a C# null -> so checking `filePath != null` alone always took this
        // branch, even when no filepath was actually supplied, silently bypassing the by-name
        // fallback below. Treat blank the same as null.
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            // Original path: resolve from the declaring file.
            var document = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath) ?? throw new InvalidOperationException(
                    $"FindCallers: filePath '{filePath}' was not found in the loaded solution. " +
                    "Verify the path against ListSolutionItems/GetFileOutline, or omit filePath to " +
                    "resolve by symbolName across the whole solution instead. This is NOT a confirmed " +
                    "zero-references result - the lookup never ran.");
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
            {
                throw new InvalidOperationException(
                    $"FindCallers: could not obtain a syntax tree/semantic model for '{filePath}'. " +
                    "This is NOT a confirmed zero-references result - the lookup never ran.");
            }

            // Shared by both branches below so the contextSnippet-failure message (immediately
            // below) can report whether symbolName itself WAS found nearby, instead of leaving the
            // caller unsure whether the name or the snippet is the actual problem.
            var decls = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(m => m switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text == symbolName,
                    PropertyDeclarationSyntax pd => pd.Identifier.Text == symbolName,
                    FieldDeclarationSyntax fd => fd.Declaration.Variables.Any(v => v.Identifier.Text == symbolName),
                    _ => false
                }).ToList();

            if (contextSnippet != null)
            {
                symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken) ?? throw new InvalidOperationException(
                        $"FindCallers: contextSnippet did not resolve to a symbol in '{filePath}'. " +
                        $"This is NOT a confirmed zero-references result for '{symbolName}' - the lookup " +
                        "never ran. " + DescribeNameOnlyCandidates(decls, symbolName) +
                        "Re-check the snippet against GetMethodSource/GetFileOutline output, " +
                        "or omit contextSnippet if the symbolName is unambiguous in this file.");
            }
            else
            {
                var decl = decls.FirstOrDefault(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any())
                    ?? decls.FirstOrDefault();
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

                if (symbol == null)
                {
                    throw new InvalidOperationException(
                        $"FindCallers: symbolName '{symbolName}' was not found declared in '{filePath}'. " +
                        "This is NOT a confirmed zero-references result - the lookup never ran. Verify the " +
                        "name against GetFileOutline, or omit filePath to search by name across the solution.");
                }
            }
        }
        else
        {
            // Defect-3 fix: no filePath supplied -> resolve by name across the solution.
            // When multiple overloads exist, contextSnippet is used to pick one if supplied;
            // otherwise all matching symbols are searched (union of references).
            symbol = await ResolveSymbolByNameAsync(solution, symbolName, contextSnippet, preferImplementable: false, cancellationToken);
        }

        if (symbol == null)
        {
            var nearMissHint = await DescribeNearMissCandidatesAsync(solution, symbolName, cancellationToken);
            throw new InvalidOperationException(
                $"FindCallers: symbolName '{symbolName}' could not be resolved anywhere in the solution" +
                (contextSnippet != null ? " with the supplied contextSnippet" : "") + ". " +
                "This is NOT a confirmed zero-references result - the lookup never ran. " + nearMissHint +
                "Verify the name via LocateSymbol/GetFileOutline before treating this as evidence the symbol is unused.");
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

                var refDoc = solution.Projects.SelectMany(p => p.Documents)
                    .FirstOrDefault(d => d.FilePath == refTree.FilePath);
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
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);

        ISymbol? symbol = null;
        // Non-null only when the filePath/contextSnippet-scoped lookup ran but definitively failed
        // to resolve (as opposed to "no filePath was supplied, use the by-name paths below") -> used
        // to build an actionable error if every fallback is exhausted, instead of silently returning
        // an empty list that reads identically to a confirmed zero-implementations result.
        string? scopedResolutionFailure = null;

        // The MCP tool layer resolves an omitted `filepath` to FilePathWrapper's empty-string default
        // (via SetFilePath), not a C# null -> so checking `filePath != null` alone always took this
        // branch, even when no filepath was actually supplied, silently bypassing the by-name
        // fallback below. Treat blank the same as null.
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            // Original path: resolve from the declaring file.
            var document = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
            if (document == null)
            {
                scopedResolutionFailure = $"filePath '{filePath}' was not found in the loaded solution.";
            }
            else
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || model == null)
                {
                    scopedResolutionFailure = $"could not obtain a syntax tree/semantic model for '{filePath}'.";
                }
                else if (contextSnippet != null)
                {
                    symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken);
                    if (symbol == null)
                    {
                        // Same name-only lookup as the no-snippet branch below, used purely to
                        // enrich this message with "symbolName WAS found at line N" when possible ->
                        // does not change resolution/matching behavior, only what the error reports.
                        var nameOnlyCandidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                            .Where(m => m switch
                            {
                                MethodDeclarationSyntax md => md.Identifier.Text == symbolName,
                                PropertyDeclarationSyntax pd => pd.Identifier.Text == symbolName,
                                _ => false
                            }).ToList();
                        scopedResolutionFailure = $"contextSnippet did not resolve to a symbol in '{filePath}'. " +
                            DescribeNameOnlyCandidates(nameOnlyCandidates, symbolName);
                    }
                }
                else
                {
                    var decl = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                        .FirstOrDefault(m => m switch
                        {
                            MethodDeclarationSyntax md => md.Identifier.Text == symbolName,
                            PropertyDeclarationSyntax pd => pd.Identifier.Text == symbolName,
                            FieldDeclarationSyntax fd => fd.Declaration.Variables.Any(v => v.Identifier.Text == symbolName),
                            _ => false
                        });
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

                    if (symbol == null)
                    {
                        scopedResolutionFailure = $"symbolName '{symbolName}' was not found declared in '{filePath}'.";
                    }
                }
            }
        }
        else
        {
            // Defect-3 fix: no filePath -> resolve by name across the solution.
            symbol = await ResolveSymbolByNameAsync(solution, symbolName, contextSnippet, preferImplementable: true, cancellationToken);
        }

        if (symbol == null)
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
            var nearMissHint = await DescribeNearMissCandidatesAsync(solution, symbolName, cancellationToken);
            throw new InvalidOperationException(
                "FindImplementations: " + (scopedResolutionFailure ??
                    ($"symbolName '{symbolName}' could not be resolved anywhere in the solution" +
                    (contextSnippet != null ? " with the supplied contextSnippet" : "") + ".")) +
                " This is NOT a confirmed zero-implementations result - the lookup never ran. " + nearMissHint +
                "Verify the name via LocateSymbol/GetFileOutline before treating this as evidence of no implementations.");
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
        var isImplementable = symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride
            || symbol.ContainingType?.TypeKind == TypeKind.Interface;
        if (!skipStructuralCheck && !isImplementable)
        {
            throw new InvalidOperationException(
                $"FindImplementations: symbolName '{symbolName}' resolved to {symbol.Kind} " +
                $"'{symbol.ToDisplayString()}' on {symbol.ContainingType?.TypeKind.ToString().ToLowerInvariant() ?? "an unknown container"} " +
                $"'{symbol.ContainingType?.Name ?? "?"}', which is concrete, non-virtual, and not an interface " +
                "member - it is structurally incapable of having implementations, so this is NOT a confirmed " +
                "zero-implementations result. If you meant the interface/virtual declaration this member " +
                "implements or overrides, disambiguate with contextSnippet or filepath pointing at that " +
                "declaration specifically, or call LocateSymbol(exactMatch: true) first to see every " +
                $"same-named candidate ('{symbolName}') with its containing type and kind.");
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, null, cancellationToken);
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
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new VariableLifetimeReport { Error = $"File not found: '{filePath}'" };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new VariableLifetimeReport { Error = "Could not obtain syntax tree or semantic model." };
        }

        var text = await document.GetTextAsync(cancellationToken);
        if (lineNumber < 1 || lineNumber > text.Lines.Count)
        {
            return new VariableLifetimeReport { Error = $"Line {lineNumber} is out of range." };
        }

        // Find declaration node at/near the requested line
        var targetLineSpan = text.Lines[lineNumber - 1].Span;
        ISymbol? symbol = null;
        SyntaxNode? declNode = null;

        // Check variable declarators
        var declarator = root.DescendantNodes(n => n.Span.IntersectsWith(targetLineSpan))
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == variableName);
        if (declarator != null)
        {
            symbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
            declNode = declarator;
        }

        // Check parameters
        if (symbol == null)
        {
            var param = root.DescendantNodes(n => n.Span.IntersectsWith(targetLineSpan))
                .OfType<ParameterSyntax>()
                .FirstOrDefault(p => p.Identifier.Text == variableName);
            if (param != null)
            {
                symbol = semanticModel.GetDeclaredSymbol(param, cancellationToken);
                declNode = param;
            }
        }

        // Widen search to nearby lines if not found on the exact line
        if (symbol == null)
        {
            foreach (var varDecl in root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.Text == variableName))
            {
                var varLine = varDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (Math.Abs(varLine - lineNumber) <= 5)
                {
                    symbol = semanticModel.GetDeclaredSymbol(varDecl, cancellationToken);
                    declNode = varDecl;
                    break;
                }
            }
        }

        if (symbol == null)
        {
            return new VariableLifetimeReport { Error = $"Variable '{variableName}' not found near line {lineNumber}." };
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
                var refDoc = solution.Projects.SelectMany(p => p.Documents)
                    .FirstOrDefault(d => d.FilePath == refFilePath);
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
            Accesses = accesses
        };
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
    /// Builds a "symbolName WAS found at these locations, but contextSnippet didn't match" hint for
    /// a contextSnippet-resolution failure, given the name-only candidate declarations already
    /// available at the call site. Mirrors RefactoringEngine's NearMissList hint shape (line +
    /// first-line preview, up to 3, "+N more" suffix) so an agent sees the same style of actionable
    /// error across every tool in this codebase that resolves by name+contextSnippet, not just the
    /// RefactoringEngine mutation tools. Returns an empty string (not a sentence fragment) when
    /// symbolName itself doesn't resolve to anything nearby, since there's nothing to list.
    /// </summary>
    public static string DescribeNameOnlyCandidates(List<MemberDeclarationSyntax> candidates, string symbolName)
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
    /// Builds a "Did you mean: ..." hint for a symbolName that resolved to zero candidates anywhere
    /// in the solution (i.e. ResolveSymbolByNameAsync returned null) - as opposed to
    /// DescribeNameOnlyCandidates above, which handles the exact-name-found-but-contextSnippet-failed
    /// case. Runs a case-insensitive substring scan across every project's compilation using
    /// Compilation.GetSymbolsWithName's predicate overload, so a typo'd or approximately-remembered
    /// name still surfaces real candidates inline instead of forcing a second LocateSymbol round-trip.
    /// Only ever called on the already-failing path (symbol == null after every other fallback), so
    /// this adds no cost to a normal, successful resolution. Capped at 5 suggestions (wider than
    /// DescribeNameOnlyCandidates's 3, since these hits span the whole solution rather than one file
    /// and are more likely to include irrelevant noise). Returns an empty string when nothing similar
    /// is found either, so the caller message degrades to a plain "not found" with no dangling hint.
    /// </summary>
    public static async Task<string> DescribeNearMissCandidatesAsync(
        Solution solution, string symbolName, CancellationToken cancellationToken)
    {
        var suggestions = new List<string>();

        foreach (var project in solution.Projects)
        {
            if (suggestions.Count >= 5)
            {
                break;
            }

            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Same "unbuildable project can't be scanned" tolerance as ResolveSymbolByNameAsync -
                // a near-miss hint is best-effort, not worth failing the whole error message over.
            }

            if (compilation == null)
            {
                continue;
            }

            var matches = compilation.GetSymbolsWithName(
                name => name.Contains(symbolName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.Member,
                cancellationToken);

            foreach (var match in matches)
            {
                if (suggestions.Count >= 5)
                {
                    break;
                }

                var containingType = match.ContainingType?.Name ?? match.ContainingNamespace?.Name ?? "?";
                var label = $"{match.Name} ({containingType})";
                if (!suggestions.Contains(label))
                {
                    suggestions.Add(label);
                }
            }
        }

        if (suggestions.Count == 0)
        {
            return string.Empty;
        }

        return $"Did you mean: {string.Join(", ", suggestions)}? ";
    }

    /// <summary>
    /// Resolves a member symbol by name across the solution without requiring a file path.
    /// Used by FindCallersAsync and FindImplementationsForMemberAsync when filePath is null.
    /// When contextSnippet is supplied, it is used to identify the specific overload.
    ///
    /// preferImplementable selects which disambiguation heuristic runs when multiple same-named
    /// candidates are found and contextSnippet doesn't (or can't) narrow them: false prefers class
    /// members over interface members (FindCallersAsync's need - any resolvable candidate finds the
    /// same call sites); true prefers a candidate that SymbolFinder.FindImplementationsAsync can
    /// actually act on - abstract/virtual/override/interface-member - since FindImplementationsForMemberAsync
    /// otherwise risks resolving to a concrete, non-virtual method that structurally can never have
    /// implementations, producing an empty result indistinguishable from a genuine zero-implementations answer.
    /// </summary>
    public async Task<ISymbol?> ResolveSymbolByNameAsync(
        Solution solution,
        string symbolName,
        string? contextSnippet,
        bool preferImplementable,
        CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects)
        {
            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResolveSymbolByName: could not compile project '{Project}'", project.Name);
            }

            if (compilation == null)
            {
                continue;
            }

            var candidates = compilation
                .GetSymbolsWithName(symbolName, SymbolFilter.Member, cancellationToken: cancellationToken)
                .ToList();

            if (candidates.Count == 0)
            {
                continue;
            }

            if (contextSnippet != null)
            {
                // Use context snippet to pick the right overload: find the declaring document,
                // then resolve via ContextHelper exactly as the filePath path does.
                foreach (var candidate in candidates)
                {
                    foreach (var loc in candidate.Locations.Where(l => l.IsInSource))
                    {
                        var declFilePath = loc.SourceTree?.FilePath;
                        if (declFilePath == null)
                        {
                            continue;
                        }

                        var doc = solution.Projects.SelectMany(p => p.Documents)
                            .FirstOrDefault(d => d.FilePath == declFilePath);
                        if (doc == null)
                        {
                            continue;
                        }

                        try
                        {
                            var found = await ContextHelper.FindSymbolAtSnippetAsync(doc, contextSnippet, null, null, cancellationToken);
                            if (found != null)
                            {
                                return found;
                            }
                        }
                        catch (ToolException)
                        {
                            // snippet not found in this document -> continue
                        }
                    }
                }
            }

            // No contextSnippet or snippet resolution failed -> apply the caller-appropriate preference.
            var preferred = preferImplementable
                ? candidates.FirstOrDefault(s =>
                    s.IsAbstract || s.IsVirtual || s.IsOverride || s.ContainingType?.TypeKind == TypeKind.Interface)
                    ?? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(s =>
                    s.ContainingType?.TypeKind == TypeKind.Class) ?? candidates.FirstOrDefault();

            if (preferred != null)
            {
                return preferred;
            }
        }

        return null;
    }

    public string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ClassDeclarationSyntax c => c.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            // EnumDeclarationSyntax/RecordDeclarationSyntax/StructDeclarationSyntax are all
            // MemberDeclarationSyntax (via BaseTypeDeclarationSyntax) and so are already collected
            // as candidates by ResolveMemberByNameOrSnippet's DescendantNodes().OfType<...>() scan ->
            // omitting them here didn't exclude them, it silently made GetMemberName return null for
            // them, so the `GetMemberName(m) == memberName` filter dropped them regardless of what
            // name was searched for. Confirmed: AddSummaryCommentAsync("OrderStatus", ...) against a
            // real, unambiguous top-level enum failed "target not found" purely because of this gap.
            EnumDeclarationSyntax e => e.Identifier.Text,
            RecordDeclarationSyntax r => r.Identifier.Text,
            StructDeclarationSyntax s => s.Identifier.Text,
            _ => null
        };
    }

    // Task I evaluation (docs/plan-tool-disambiguation-remediation-v1.md, addendum under Task I):
    // NearMissList won over NearestSnippet/CorrectedCoordinates because it's the only strategy that
    // shows an agent every real candidate instead of just the first one -> on a genuinely ambiguous
    // snippet (2+ real matches), the other two strategies only ever surfaced candidate #1, which is
    // actively misleading (an agent can't tell there were other matches worth choosing between, let
    // alone which one it meant). NearMissList's per-candidate line + declaration preview is also the
    // only shape that gives an agent enough to construct a corrected contextSnippet in one try. The
    // other 2 strategies' dead code was deleted here per the plan's own Task I instruction.
    /// <summary>
    /// Resolves a member by name, optionally disambiguating with a contextSnippet when the name
    /// matches more than one declaration. Falls back to first-match-by-name when contextSnippet is
    /// null, preserving existing behavior for callers that don't supply one. On an unresolvable or
    /// still-ambiguous contextSnippet, throws with a NearMissList-style hint (see BuildMemberHint).
    /// </summary>
    public MemberDeclarationSyntax? ResolveMemberByNameOrSnippet(SyntaxNode root, SourceText sourceText, string memberName, string? contextSnippet, string? lineBefore, string? lineAfter, Func<MemberDeclarationSyntax, bool>? extraFilter = null, bool excludeInterfaceMembers = true)
    {
        var candidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => GetMemberName(m) == memberName).Where(m => !excludeInterfaceMembers || m.Parent is not InterfaceDeclarationSyntax).Where(m => extraFilter == null || extraFilter(m)).ToList();
        // A type's own name and its constructor's name are identical (both read from
        // ClassDeclarationSyntax/StructDeclarationSyntax.Identifier and
        // ConstructorDeclarationSyntax.Identifier), so "OrderService" matches both the class
        // declaration and its constructor here. None of these tools operate on whole type
        // declarations (ReplaceMember/ChangeAccessibility/etc. target "a method, property, or
        // field"), so when a constructor shares the name, prefer it over the enclosing type ->
        // otherwise the type declaration (found first, being the ancestor node) silently wins
        // and callers asking for "the OrderService member" get the whole class back.
        if (candidates.Count > 1 && candidates.Any(c => c is ConstructorDeclarationSyntax))
        {
            candidates = candidates.Where(c => c is not BaseTypeDeclarationSyntax).ToList();
        }

        // When excludeInterfaceMembers is false, an interface and its implementer can both
        // contribute a same-named candidate (e.g. IGreeter.Greet() and Greeter.Greet() in the
        // same file) with nothing to tell them apart by name alone. Prefer the implementer, the
        // same way the constructor-vs-type check above prefers the more specific member over the
        // type declaration -> a caller resolving "Greet" almost always means the concrete member,
        // not the interface's abstract declaration of it.
        if (candidates.Count > 1 && candidates.Any(c => c.Parent is InterfaceDeclarationSyntax) && candidates.Any(c => c.Parent is not InterfaceDeclarationSyntax))
        {
            candidates = candidates.Where(c => c.Parent is not InterfaceDeclarationSyntax).ToList();
        }

        if (contextSnippet == null || candidates.Count <= 1)
        {
            // memberName alone already resolves unambiguously (zero or one candidate) -> a
            // contextSnippet exists only to disambiguate between multiple same-named candidates,
            // so there's nothing for it to do here. A caller that includes one defensively (or
            // whose snippet has a whitespace/formatting mismatch against the file, unrelated to
            // *which* member is meant) should not have the whole call fail over a match that was
            // never actually needed -> confirmed regression: ContosoOrders ApplyDiscount (a single,
            // non-overloaded method) failed ReplaceMember twice on contextSnippet mismatches that
            // had no bearing on which member was targeted, before the caller gave up and switched
            // tools entirely.
            return candidates.FirstOrDefault();
        }

        var matches = ContextHelper.FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(BuildMemberHint(candidates.Cast<SyntaxNode>().ToList(), matches, "not found"));
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            var matchedMember = candidates.FirstOrDefault(c => c.Span.Contains(match));
            if (matchedMember != null)
            {
                return matchedMember;
            }

            // Snippet matched but didn't align to a candidate member -> treat as ambiguous
            throw new InvalidOperationException(BuildMemberHint(candidates.Cast<SyntaxNode>().ToList(), matches, "ambiguous"));
        }

        // 2+ matches
        throw new InvalidOperationException(BuildMemberHint(candidates.Cast<SyntaxNode>().ToList(), matches, "ambiguous"));
    }

    // EnumMemberDeclarationSyntax does not derive from MemberDeclarationSyntax (it hangs directly
    // off EnumDeclarationSyntax, not off a Members list of MemberDeclarationSyntax), so it is
    // invisible to ResolveMemberByNameOrSnippet's DescendantNodes().OfType<MemberDeclarationSyntax>()
    // scan regardless of what GetMemberName returns for it. Confirmed: AddSummaryCommentAsync
    // against an enum member (e.g. OrderStatus.Pending) fails "target not found" even though the
    // enum type itself resolves fine. SummaryComment's three operations only ever call SyntaxNode-
    // level trivia APIs (GetLeadingTrivia/WithLeadingTrivia) on the resolved target, never anything
    // MemberDeclarationSyntax-specific, so this dedicated resolver returns the broader SyntaxNode
    // and is used only by those three methods -> other tools (ReplaceMember, ModifyModifier, etc.)
    // keep using ResolveMemberByNameOrSnippet as-is, since they need MemberDeclarationSyntax-only
    // APIs (e.g. .Modifiers) that an enum member does not have.
    public SyntaxNode? ResolveMemberOrEnumMemberByNameOrSnippet(SyntaxNode root, SourceText sourceText, string memberName, string? contextSnippet, string? lineBefore, string? lineAfter, string? containingTypeName = null, bool excludeInterfaceMembers = true)
    {
        var candidates = new List<SyntaxNode>();
        candidates.AddRange(root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => GetMemberName(m) == memberName && (!excludeInterfaceMembers || m.Parent is not InterfaceDeclarationSyntax)));
        candidates.AddRange(root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>().Where(m => m.Identifier.Text == memberName));

        if (candidates.Count > 1 && candidates.Any(c => c is ConstructorDeclarationSyntax))
        {
            candidates = candidates.Where(c => c is not BaseTypeDeclarationSyntax).ToList();
        }

        // Same rationale as ResolveMemberByNameOrSnippet's equivalent check: with
        // excludeInterfaceMembers false, an interface and its implementer can both match the same
        // name with nothing to disambiguate by name alone -> prefer the implementer.
        if (candidates.Count > 1 && candidates.Any(c => c.Parent is InterfaceDeclarationSyntax) && candidates.Any(c => c.Parent is not InterfaceDeclarationSyntax))
        {
            candidates = candidates.Where(c => c.Parent is not InterfaceDeclarationSyntax).ToList();
        }

        // Sibling types can declare members with byte-identical text (e.g. two records each with
        // "public string Name { get; set; } = "";"), which no line-based contextSnippet can tell
        // apart -> narrowing by the member's own containing type first resolves that case without
        // ever reaching snippet matching. Applied whenever the hint is given and actually narrows
        // the set (never to an empty result, in case the caller's hint doesn't match reality).
        if (containingTypeName != null && candidates.Count > 1)
        {
            var narrowed = candidates.Where(c => c.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text == containingTypeName).ToList();
            if (narrowed.Count > 0)
            {
                candidates = narrowed;
            }
        }

        if (contextSnippet == null || candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var matches = ContextHelper.FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(BuildMemberHint(candidates, matches, "not found"));
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            var matchedMember = candidates.FirstOrDefault(c => c.Span.Contains(match));
            if (matchedMember != null)
            {
                return matchedMember;
            }

            throw new InvalidOperationException(BuildMemberHint(candidates, matches, "ambiguous"));
        }

        // 2+ matches
        throw new InvalidOperationException(BuildMemberHint(candidates, matches, "ambiguous"));
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
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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

        try
        {
            var target = ResolveMemberOrEnumMemberByNameOrSnippet(root, sourceText, memberName, contextSnippet, lineBefore, lineAfter);
            return target is EnumMemberDeclarationSyntax enumMember && enumMember.Parent is EnumDeclarationSyntax enumDecl ? enumDecl.Identifier.Text : null;
        }
        catch (InvalidOperationException)
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
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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

        try
        {
            return ResolveTypeByNameOrSnippet(root, sourceText, containerName, contextSnippet, lineBefore, lineAfter) is EnumDeclarationSyntax;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public record ContainerMemberInfo(string? Name, string Kind, string Signature, int StartLine, int EndLine);
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
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return (EditOutcome.DocumentNotFound, "// Document not found.", []);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: syntax root not found.", []);
        }

        BaseTypeDeclarationSyntax? containerNode;
        try
        {
            containerNode = ResolveTypeByNameOrSnippet(root, sourceText, containerName, contextSnippet, lineBefore, lineAfter);
        }
        catch (InvalidOperationException ex)
        {
            return (EditOutcome.CannotEdit, ex.Message, []);
        }

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
            return (EditOutcome.CannotEdit, "// Cannot edit: container not found.", []);
        }

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
            var signature = m.WithLeadingTrivia().WithTrailingTrivia().ToFullString().Trim();
            var firstLineEnd = signature.IndexOfAny(['\n', '{', ';']);
            if (firstLineEnd > 0)
            {
                signature = signature[..firstLineEnd].Trim();
            }

            return new ContainerMemberInfo(GetMemberName(m), kind, signature, lines.GetLineFromPosition(m.SpanStart).LineNumber + 1, lines.GetLineFromPosition(m.Span.End).LineNumber + 1);
        }).ToList();
        return (EditOutcome.Modified, null, result);
    }

    /// <summary>
    /// Reduces a type name to the bare identifier Roslyn's <c>Identifier.Text</c> exposes, by
    /// stripping a trailing type-argument list (<c>Foo<T></c>, <c>Foo<TKey, TValue></c>)
    /// or backtick arity (<c>Foo`1</c>).
    /// </summary>
    /// <remarks>
    /// Needed because <c>Identifier.Text</c> is already arity-stripped, so comparing it against a
    /// caller's raw string rejected the type's own declared spelling: run 20260910-013550-398 had
    /// <c>containerName: "EngineResultWrapper<T>"</c> fail and the bare
    /// <c>"EngineResultWrapper"</c> succeed on the next turn. Third recorded instance
    /// (cf. project_qwen36_35b_smoketest_and_member_containername_gap). Applied to both sides of
    /// the comparison so all three spellings resolve identically.
    /// </remarks>
    public static string NormalizeTypeName(string typeName)
    {
        var name = typeName.Trim();

        var backtick = name.IndexOf('`');
        if (backtick > 0)
        {
            return name[..backtick];
        }

        // Only a trailing argument list is stripped -> an angle bracket anywhere else isn't arity
        // (a caller passing a whole declaration line, say), and truncating there would silently
        // resolve to the wrong type rather than reporting a miss.
        var open = name.IndexOf('<');
        return open > 0 && name.EndsWith('>') ? name[..open].TrimEnd() : name;
    }

    /// <summary>
    /// Resolves a type by name, optionally disambiguating with a contextSnippet when the name
    /// matches more than one declaration. Falls back to first-match-by-name when contextSnippet is
    /// null, preserving existing behavior for callers that don't supply one. On an unresolvable or
    /// still-ambiguous contextSnippet, throws with a NearMissList-style hint (see BuildTypeHint).
    /// </summary>
    /// <remarks>
    /// The single chokepoint for 13 call sites across Member, ModifyEnum, ModifyBaseType and
    /// others, so the generic-name normalization here covers all of them.
    /// </remarks>
    public BaseTypeDeclarationSyntax? ResolveTypeByNameOrSnippet(SyntaxNode root, SourceText sourceText, string typeName, string? contextSnippet, string? lineBefore, string? lineAfter, Func<BaseTypeDeclarationSyntax, bool>? extraFilter = null)
    {
        var normalizedRequest = NormalizeTypeName(typeName);
        var candidates = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => NormalizeTypeName(t.Identifier.Text) == normalizedRequest).Where(t => extraFilter == null || extraFilter(t)).ToList();
        if (contextSnippet == null || candidates.Count <= 1)
        {
            // typeName alone already resolves unambiguously -> see the identical guard and
            // rationale in ResolveMemberByNameOrSnippet above.
            return candidates.FirstOrDefault();
        }

        var matches = ContextHelper.FindAllSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(BuildTypeHint(candidates, matches, "not found"));
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            var matchedType = candidates.FirstOrDefault(c => c.Span.Contains(match));
            if (matchedType != null)
            {
                return matchedType;
            }

            throw new InvalidOperationException(BuildTypeHint(candidates, matches, "ambiguous"));
        }

        throw new InvalidOperationException(BuildTypeHint(candidates, matches, "ambiguous"));
    }

    public string BuildMemberHint(List<SyntaxNode> candidates, List<int> matches, string failureMode)
    {
        if (candidates.Count == 0)
        {
            return $"contextSnippet {failureMode}: no candidates found.";
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
        return $"contextSnippet {failureMode} ({count} candidates): {string.Join(", ", previews)}{suffix}. " + "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
    }

    /// <summary>
    /// Builds the message for a container that could not be found, listing the type names the file
    /// actually declares.
    /// </summary>
    /// <remarks>
    /// Replaces three identical <c>"// Container not found."</c> literals, which said nothing
    /// actionable and -> being prefixed with <c>//</c> -> read as commented-out code rather than an
    /// error. Listing the available names is the single most useful thing to return here: the
    /// caller's next move is always to pick one, and it also reveals a wrong-file mistake
    /// immediately. Generic spellings resolve, so a listed name can be given back verbatim; see
    /// <see cref="NormalizeTypeName"/>.
    /// </remarks>
    public static string BuildContainerNotFoundMessage(SyntaxNode root, string requestedName)
    {
        var declared = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (declared.Count == 0)
        {
            return $"No type named '{requestedName}' was found - this file declares no types at all. " +
                   "Check the filePath.";
        }

        var shown = declared.Take(10).ToList();
        var suffix = declared.Count > shown.Count ? $" (+{declared.Count - shown.Count} more)" : "";
        return $"No type named '{requestedName}' was found in this file. Types declared here: " +
               $"{string.Join(", ", shown)}{suffix}. Pass one of those as containerName - a type " +
               "argument list is optional, so both 'Foo' and 'Foo<T>' resolve.";
    }

    private string BuildTypeHint(List<BaseTypeDeclarationSyntax> candidates, List<int> matches, string failureMode)
    {
        if (candidates.Count == 0)
        {
            return $"contextSnippet {failureMode}: no candidates found.";
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
        return $"contextSnippet {failureMode} ({count} candidates): {string.Join(", ", previews)}{suffix}. " + "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
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
            return new TypeHierarchyReport { Error = $"Type '{typeName}' not found in solution." };
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
}

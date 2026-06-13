using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

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
    /// <summary>Session identifier for the current analysis session.</summary>
    string? SessionId,
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
    /// <summary>Declared accessibility: Public, Internal, Private, Protected, etc.</summary>
    string Accessibility
);

public class SymbolNavigationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SymbolNavigationEngine> _logger;

    public SymbolNavigationEngine(PersistentWorkspaceManager workspaceManager, ILogger<SymbolNavigationEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Locates all declaration sites for a symbol by name without requiring a file path.
    /// Returns structured SymbolLocation records whose FilePath and ContextSnippet fields
    /// can be passed directly to inspect_symbol, find_references, get_call_graph, rename_symbol,
    /// and all other filePath-gated tools — eliminating the search_solution_text bootstrap step.
    ///
    /// symbolName: simple or fully-qualified name (e.g. "GetById" or "Acme.Data.Repo.GetById").
    /// symbolKind: optional filter — "type", "method", "property", "field", "event", or "any" (default).
    /// projectName: optional — restricts the search to a single project.
    /// exactMatch: true (default) for exact name match; false for prefix/contains (discovery mode).
    ///
    /// Returns all matches. Overloads appear as separate entries distinguishable by Signature.
    /// When multiple results are returned, inspect Signature and ContainingType to pick the target,
    /// then supply the chosen FilePath + ContextSnippet to the next tool call.
    /// </summary>    
    public async Task<List<SymbolLocation>> LocateSymbolAsync(
        string symbolName,
        string symbolKind = "any",
        string? containingType = null,
        string? containingNamespace = null,
        string? projectName = null,
        bool exactMatch = true,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

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

        foreach (var project in searchProjects)
        {
            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LocateSymbol: could not compile project '{Project}'", project.Name);
            }

            if (compilation == null)
            {
                continue;
            }

            var candidates = exactMatch
                ? compilation.GetSymbolsWithName(simpleName, filter, ct)
                : compilation.GetSymbolsWithName(
                    n => n.Contains(simpleName, StringComparison.OrdinalIgnoreCase),
                    filter,
                    ct);

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

                // Emit SymbolHandle once per symbol — it's the same regardless of location.
                // Null for symbols that don't support it (e.g. locals, labels).
                var docCommentId = symbol.GetDocumentationCommentId();

                foreach (var location in symbol.Locations.Where(l => l.IsInSource))
                {
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
                        SessionId: _workspaceManager.SessionId.ToString()
                    ));
                }
            }
        }

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
            _ => true   // "any" or unrecognised — include everything
        };
    }

    public async Task<SymbolHoverInfo?> GetSymbolInfoAsync(FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return ErrorHoverInfo($"File not found: '{filePath}'");
        }

        // Primary: use GetDeclaredSymbol/GetSymbolInfo (declaration-based — more reliable for class/method/property lookups)
        ISymbol? symbol = null;
        try
        {
            symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("matched") || ex.Message.Contains("No match"))
        {
            return ErrorHoverInfo($"Snippet not found: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            // fallthrough to position-based approach
        }

        // Fallback: position-based lookup using SymbolFinder (handles usage sites)
        if (symbol == null)
        {
            var text = await document.GetTextAsync(ct);
            int pos;
            try
            {
                pos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
            }
            catch (InvalidOperationException ex)
            {
                return ErrorHoverInfo(ex.Message);
            }

            var model = await document.GetSemanticModelAsync(ct);
            if (model == null)
            {
                return ErrorHoverInfo("Could not obtain semantic model for file.");
            }

            // SymbolFinder needs the cursor on an identifier token
            var root = await document.GetSyntaxRootAsync(ct);
            if (root != null)
            {
                pos = ContextHelper.AdvanceToLastIdentifier(root, pos, contextSnippet.Length);
            }

            symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, pos, solution.Workspace, ct);
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
            var docXml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
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
        string typeName, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? targetSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            targetSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, ct)
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
            var implementations = await SymbolFinder.FindImplementationsAsync(targetSymbol, solution, null, ct);
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
            var derived = await SymbolFinder.FindDerivedClassesAsync(targetSymbol, solution, null, ct);
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
        string typeName, string? projectName = null, bool includeInherited = true, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? typeSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            typeSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, ct)
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

        var typeChain = new List<(INamedTypeSymbol Type, bool IsTarget)> { (typeSymbol, true) };
        if (includeInherited)
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

                var sig = member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
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
        string interfaceName, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? interfaceSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            interfaceSymbol = compilation
                .GetSymbolsWithName(interfaceName, SymbolFilter.Type, ct)
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

        var implementations = await SymbolFinder.FindImplementationsAsync(interfaceSymbol, solution, null, ct);

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
        string targetTypeName, string? projectName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        var targetTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetTypeName };

        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            var typeSymbol = compilation
                .GetSymbolsWithName(targetTypeName, SymbolFilter.Type, ct)
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
                var root = await document.GetSyntaxRootAsync(ct);
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
                var model = await document.GetSemanticModelAsync(ct);
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
                        var methodSymbol = model.GetDeclaredSymbol(method, ct) as IMethodSymbol;
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
        FilePath filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new List<ReadonlyFieldCandidate>();
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
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

    public async Task<CallGraphNode?> GetCallGraphAsync(
        FilePath filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(ct);
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

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return null;
        }

        var visited = new HashSet<string>();
        return await BuildCallGraphNodeAsync(methodSymbol, solution, 0, maxDepth, visited, ct);
    }

    private async Task<CallGraphNode> BuildCallGraphNodeAsync(
        IMethodSymbol method,
        Solution solution,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        CancellationToken ct)
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

        var syntax = await syntaxRef.GetSyntaxAsync(ct);
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

        var model = await methodDoc.GetSemanticModelAsync(ct);
        if (model == null)
        {
            return node;
        }

        var seenCallees = new HashSet<string>();
        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var si = model.GetSymbolInfo(invocation, ct);
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

            var calleeNode = await BuildCallGraphNodeAsync(callee, solution, depth + 1, maxDepth, visited, ct);
            node.Callees.Add(calleeNode);
        }

        return node;
    }

    public async Task<ReverseCallGraphNode?> GetReverseCallGraphAsync(
        FilePath filePath,
        string methodName,
        int maxDepth = 3,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(ct);
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

        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return null;
        }

        var visited = new HashSet<string>();
        return await BuildReverseCallGraphNodeAsync(methodSymbol, solution, 0, maxDepth, visited, ct);
    }

    private async Task<ReverseCallGraphNode> BuildReverseCallGraphNodeAsync(
        IMethodSymbol method,
        Solution solution,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        CancellationToken ct)
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
            await SymbolFinder.FindReferencesAsync(method, solution, ct));

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
                        var ifaceRefs = await SymbolFinder.FindReferencesAsync(ifaceMethod, solution, ct);
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

                var refModel = await refDoc.GetSemanticModelAsync(ct);
                if (refModel == null)
                {
                    continue;
                }

                var pos = location.Location.SourceSpan.Start;
                var callerSymbol = refModel.GetEnclosingSymbol(pos, ct) as IMethodSymbol;
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

                var callerNode = await BuildReverseCallGraphNodeAsync(callerSymbol, solution, depth + 1, maxDepth, visited, ct);
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
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        ISymbol? symbol = null;

        if (filePath != null)
        {
            // Original path: resolve from the declaring file.
            var document = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
            if (document == null)
            {
                return new List<CallerInfo>();
            }

            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root == null || model == null)
            {
                return new List<CallerInfo>();
            }

            if (contextSnippet != null)
            {
                symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, ct);
            }
            else
            {
                var decls = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                    .Where(m => m switch
                    {
                        MethodDeclarationSyntax md => md.Identifier.Text == symbolName,
                        PropertyDeclarationSyntax pd => pd.Identifier.Text == symbolName,
                        FieldDeclarationSyntax fd => fd.Declaration.Variables.Any(v => v.Identifier.Text == symbolName),
                        _ => false
                    }).ToList();
                var decl = decls.FirstOrDefault(m => m.Ancestors().OfType<ClassDeclarationSyntax>().Any())
                    ?? decls.FirstOrDefault();
                if (decl != null)
                {
                    symbol = model.GetDeclaredSymbol(decl, ct);
                }
            }
        }
        else
        {
            // Defect-3 fix: no filePath supplied — resolve by name across the solution.
            // When multiple overloads exist, contextSnippet is used to pick one if supplied;
            // otherwise all matching symbols are searched (union of references).
            symbol = await ResolveSymbolByNameAsync(solution, symbolName, contextSnippet, ct);
        }

        if (symbol == null)
        {
            return new List<CallerInfo>();
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
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

                var refModel = await refDoc.GetSemanticModelAsync(ct);
                if (refModel == null)
                {
                    continue;
                }

                var pos = location.Location.SourceSpan.Start;
                var enclosing = refModel.GetEnclosingSymbol(pos, ct);
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

                var sourceText = await refDoc.GetTextAsync(ct);
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
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        ISymbol? symbol = null;

        if (filePath != null)
        {
            // Original path: resolve from the declaring file.
            var document = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
            if (document == null)
            {
                return new List<ImplementationInfo>();
            }

            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root == null || model == null)
            {
                return new List<ImplementationInfo>();
            }

            if (contextSnippet != null)
            {
                symbol = await ContextHelper.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, ct);
            }
            else
            {
                var decl = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                    .FirstOrDefault(m => m switch
                    {
                        MethodDeclarationSyntax md => md.Identifier.Text == symbolName,
                        PropertyDeclarationSyntax pd => pd.Identifier.Text == symbolName,
                        _ => false
                    });
                if (decl != null)
                {
                    symbol = model.GetDeclaredSymbol(decl, ct);
                }
            }
        }
        else
        {
            // Defect-3 fix: no filePath — resolve by name across the solution.
            symbol = await ResolveSymbolByNameAsync(solution, symbolName, contextSnippet, ct);
        }

        if (symbol == null)
        {
            // Fallback: try to find as a named type (e.g., user passed an interface name, not a member name).
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation == null)
                {
                    continue;
                }

                symbol = compilation
                    .GetSymbolsWithName(symbolName, SymbolFilter.Type, ct)
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
            return new List<ImplementationInfo>();
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, null, ct);
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
        FilePath filePath,
        string variableName,
        int lineNumber,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new VariableLifetimeReport { Error = $"File not found: '{filePath}'" };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (root == null || semanticModel == null)
        {
            return new VariableLifetimeReport { Error = "Could not obtain syntax tree or semantic model." };
        }

        var text = await document.GetTextAsync(ct);
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
            symbol = semanticModel.GetDeclaredSymbol(declarator, ct);
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
                symbol = semanticModel.GetDeclaredSymbol(param, ct);
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
                    symbol = semanticModel.GetDeclaredSymbol(varDecl, ct);
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
        var declLine = declLoc?.GetLineSpan().StartLinePosition.Line + 1 ?? lineNumber;
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

        // Data flow analysis on the enclosing method body
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
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        var accesses = new List<VariableAccess>();

        // Add declaration entry
        accesses.Add(new VariableAccess(
            FilePath: declFilePath,
            Line: declLine,
            Column: declLoc?.GetLineSpan().StartLinePosition.Character + 1 ?? 0,
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

                var refRoot = await refDoc.GetSyntaxRootAsync(ct);
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

        // assignment — LHS?
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
    /// Resolves a member symbol by name across the solution without requiring a file path.
    /// Used by FindCallersAsync and FindImplementationsForMemberAsync when filePath is null.
    /// Prefers class members over interface members to match original disambiguation logic.
    /// When contextSnippet is supplied, it is used to identify the specific overload.
    /// </summary>
    private async Task<ISymbol?> ResolveSymbolByNameAsync(
        Solution solution,
        string symbolName,
        string? contextSnippet,
        CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            Compilation? compilation = null;
            try
            {
                compilation = await project.GetCompilationAsync(ct);
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
                .GetSymbolsWithName(symbolName, SymbolFilter.Member, ct)
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
                            var found = await ContextHelper.FindSymbolAtSnippetAsync(doc, contextSnippet, null, null, ct);
                            if (found != null)
                            {
                                return found;
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // snippet not found in this document — continue
                        }
                    }
                }
            }

            // No contextSnippet or snippet resolution failed — prefer class members over interface members.
            var preferred = candidates.FirstOrDefault(s =>
                s.ContainingType?.TypeKind == TypeKind.Class) ?? candidates.FirstOrDefault();

            if (preferred != null)
            {
                return preferred;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the full type hierarchy for a named type: base class chain, implemented interfaces,
    /// derived classes, and (if an interface) all implementing types.
    /// </summary>
    public async Task<TypeHierarchyReport> GetTypeHierarchyAsync(
        string typeName,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? typeSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            typeSymbol = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type, ct)
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
            var derived = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, solution, null, ct);
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
            var impls = await SymbolFinder.FindImplementationsAsync(typeSymbol, solution, null, ct);
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

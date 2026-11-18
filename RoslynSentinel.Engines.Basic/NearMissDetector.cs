using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

// Aliased: RoslynSentinel.Engines.Basic declares an unrelated SymbolCandidate (SymbolNavigationEngine.cs) that shadows the Common one.
using CommonSymbolCandidate = RoslynSentinel.Common.SymbolCandidate;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Finds unresolved member names in a rejected edit that sit one close spelling away from a declared name on their receiver type,
/// and reports each as a <see cref="NearMissName"/>. Static and stateless, no MCP types.
/// </summary>
public static class NearMissDetector
{
    private const int MaxCandidates = 5;

    /// <summary>
    /// Detects near-miss names for the compiler diagnostics of a rejected edit. <paramref name="baseline"/> is the committed solution,
    /// <paramref name="changes"/> the candidate text per file, and <paramref name="diagnostics"/> the compile gate's diagnostics.
    /// Handles CS0117 and CS1061 on a member access (<c>Receiver.Name</c>) and CS0103 on a bare name (<c>case Name:</c>, with <c>ReceiverType</c> null and the replacement the name alone). Returns empty, without touching the solution, when there is no such
    /// diagnostic; skips diagnostics in files that are not documents of <paramref name="baseline"/> (a created file). Any failure other than
    /// cancellation yields an empty list, because a detector failure must never break the rejection it decorates.
    /// </summary>
    public static async Task<IReadOnlyList<NearMissName>> DetectAsync(Solution baseline, IReadOnlyDictionary<FilePathWrapper, string> changes, IReadOnlyList<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        var memberDiagnostics = diagnostics.Where(d => d.Id is "CS0117" or "CS1061" or "CS0103").ToList();
        if (memberDiagnostics.Count == 0)
        {
            return [];
        }

        try
        {
            // Fork the baseline with the proposed text of each changed document that already exists; a created file has no baseline document.
            var forked = baseline;
            foreach (var (path, text) in changes)
            {
                foreach (var documentId in baseline.GetDocumentIdsWithFilePath(path))
                {
                    forked = forked.WithDocumentText(documentId, SourceText.From(text));
                }
            }

            var declaredElsewhere = new Dictionary<string, bool>(StringComparer.Ordinal);
            var found = new List<NearMissName>();
            foreach (var diagnostic in memberDiagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = await DetectOneAsync(baseline, forked, diagnostic, declaredElsewhere, cancellationToken);
                if (item is not null)
                {
                    found.Add(item);
                }
            }

            return found;
        }
        // The near-miss list is an optional extra on a rejection that is already being reported (as in EditImpactGate.AnalyzeAsync): any
        // binding or workspace failure returns empty instead of masking the real compiler errors. The engine has no logger, so nothing is logged.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<NearMissName?> DetectOneAsync(Solution baseline, Solution forked, DiagnosticInfo diagnostic, Dictionary<string, bool> declaredElsewhere, CancellationToken cancellationToken)
    {
        // A diagnostic in a file that is not a baseline document (a CreateFile target) is out of scope.
        if (!baseline.GetDocumentIdsWithFilePath(diagnostic.FilePath).Any())
        {
            return null;
        }

        var document = FindDocument(forked, diagnostic);
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(cancellationToken);
        var lineIndex = diagnostic.StartLine - 1;
        if (lineIndex < 0 || lineIndex >= text.Lines.Count || diagnostic.StartColumn < 1)
        {
            return null;
        }

        var line = text.Lines[lineIndex];
        var position = line.Start + diagnostic.StartColumn - 1;
        if (position >= line.End)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return null;
        }

        // CS0103 is a bare name (case Oovernight:); CS0117 and CS1061 are the Name of a member access (Receiver.Oovernight).
        var isBareName = diagnostic.Id == "CS0103";
        var slip = isBareName
            ? FindBareNameSlip(model, root, position)
            : FindMemberAccessSlip(model, root, position, cancellationToken);
        if (slip is null)
        {
            return null;
        }

        var (wrongName, receiverType, qualifier, members) = slip.Value;
        var closest = CorrectedLineHint.FindClosest(wrongName, members.Select(m => m.Name));
        if (closest is null)
        {
            return null;
        }

        var candidates = NameCandidates.Rank(wrongName, members, m => m.Name, MaxCandidates, strict: true)
            .Select(r => r.Item with { Distance = r.Distance })
            .ToList();
        var correctedLine = CorrectedLineHint.RewriteLine(line.ToString(), wrongName, closest, diagnostic.StartColumn, memberAccess: !isBareName)?.Trim();

        if (!declaredElsewhere.TryGetValue(wrongName, out var isDeclaredElsewhere))
        {
            isDeclaredElsewhere = await IsDeclaredAnywhereAsync(baseline, wrongName, cancellationToken);
            declaredElsewhere[wrongName] = isDeclaredElsewhere;
        }

        return new NearMissName(
            DiagnosticId: diagnostic.Id,
            FilePath: diagnostic.FilePath.ToString(),
            Line: diagnostic.StartLine,
            Column: diagnostic.StartColumn,
            ReceiverType: receiverType,
            Replacement: qualifier + closest,
            CorrectedLine: correctedLine,
            WrongNameDeclaredElsewhere: isDeclaredElsewhere,
            Candidates: candidates);
    }

    // CS0117/CS1061: binds the receiver of the member access whose Name covers the position and lists the members it exposes.
    // Qualifier is the receiver text plus a dot, so Replacement reads as written (ShippingKinds.Overnight, order.Total).
    private static (string WrongName, string? ReceiverType, string Qualifier, List<CommonSymbolCandidate> Members)? FindMemberAccessSlip(SemanticModel model, SyntaxNode root, int position, CancellationToken cancellationToken)
    {
        var access = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(a => a.Name.Span.Contains(position));
        if (access is null)
        {
            return null;
        }

        var wrongName = access.Name.Identifier.ValueText;
        if (wrongName.Length == 0)
        {
            return null;
        }

        // A receiver that binds to a type is a static access (ShippingKinds.Oovernight); anything else is an instance access.
        ITypeSymbol? type;
        var isStatic = false;
        if (model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is INamedTypeSymbol receiverType)
        {
            type = receiverType;
            isStatic = true;
        }
        else
        {
            type = model.GetTypeInfo(access.Expression, cancellationToken).Type;
        }

        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
        {
            return null;
        }

        var lookupPosition = access.Name.SpanStart;
        var symbols = isStatic
            ? model.LookupStaticMembers(lookupPosition, type)
            : model.LookupSymbols(lookupPosition, type, includeReducedExtensionMethods: true);
        return (wrongName, type.ToDisplayString(), access.Expression.ToString() + ".", ToCandidates(symbols));
    }

    // CS0103: the unresolved identifier at the position, with every name in scope there (locals, parameters, members of the enclosing
    // and base types, types, and members imported by using static). Namespaces, labels, type parameters and aliases are left out: a
    // bare-name slip is retyped as a value, member or type, and a namespace-shaped name would invite a wrong replacement.
    private static (string WrongName, string? ReceiverType, string Qualifier, List<CommonSymbolCandidate> Members)? FindBareNameSlip(SemanticModel model, SyntaxNode root, int position)
    {
        var identifier = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault(n => n.Identifier.Span.Contains(position));
        if (identifier is null)
        {
            return null;
        }

        // The Name of a member access is not a bare name (CS0117/CS1061 own that shape, and its scope is the receiver's members).
        if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
        {
            return null;
        }

        var wrongName = identifier.Identifier.ValueText;
        if (wrongName.Length == 0)
        {
            return null;
        }

        return (wrongName, null, string.Empty, ToCandidates(model.LookupSymbols(identifier.SpanStart)));
    }

    private static List<CommonSymbolCandidate> ToCandidates(IEnumerable<ISymbol> symbols)
        => symbols
            .Where(IsCandidateMember)
            .Select(ToCandidate)
            .OfType<CommonSymbolCandidate>()
            .DistinctBy(c => (c.Name, c.Container, c.Kind))
            .ToList();

    private static Document? FindDocument(Solution solution, DiagnosticInfo diagnostic)
    {
        // A file linked into several projects has several document ids: prefer the one in the diagnostic's project, else the first.
        var ids = solution.GetDocumentIdsWithFilePath(diagnostic.FilePath);
        var project = ProjectLookup.FindProject(solution, diagnostic.Project);
        var id = ids.FirstOrDefault(i => project is not null && i.ProjectId == project.Id)
                 ?? ids.FirstOrDefault();
        return id is null ? null : solution.GetDocument(id);
    }

    private static async Task<bool> IsDeclaredAnywhereAsync(Solution baseline, string name, CancellationToken cancellationToken)
    {
        foreach (var project in baseline.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is not null && compilation.ContainsSymbolsWithName(name, SymbolFilter.Member, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    // Keeps what a model can name after a dot, or as a bare name: types, ordinary and extension methods, properties (not indexers), fields
    // (enum members included), events, and for a bare name also locals and parameters. Implicitly declared symbols, constructors, operators
    // and accessors are dropped.
    private static bool IsCandidateMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return false;
        }

        return symbol switch
        {
            IMethodSymbol method => method.MethodKind is MethodKind.Ordinary or MethodKind.ReducedExtension,
            IPropertySymbol property => !property.IsIndexer,
            IFieldSymbol or IEventSymbol or INamedTypeSymbol or ILocalSymbol or IParameterSymbol => true,
            _ => false,
        };
    }

    // Kind uses Search's symbolKind vocabulary; Container is the containing type's simple name, or the namespace for a top-level type.
    // No file, line or docCommentId: the near-miss item carries names only.
    private static CommonSymbolCandidate? ToCandidate(ISymbol symbol)
    {
        var kind = symbol switch
        {
            INamedTypeSymbol => "type",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            ILocalSymbol => "local",
            IParameterSymbol => "parameter",
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var container = symbol.ContainingType?.Name
                        ?? (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null);
        return new CommonSymbolCandidate(symbol.Name, kind, container, null, null, null, 0);
    }
}

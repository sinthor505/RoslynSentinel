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
    /// Handles CS0117 and CS1061 on a member access (<c>Receiver.Name</c>). Returns empty, without touching the solution, when there is no such
    /// diagnostic; skips diagnostics in files that are not documents of <paramref name="baseline"/> (a created file). Any failure other than
    /// cancellation yields an empty list, because a detector failure must never break the rejection it decorates.
    /// </summary>
    public static async Task<IReadOnlyList<NearMissName>> DetectAsync(Solution baseline, IReadOnlyDictionary<FilePathWrapper, string> changes, IReadOnlyList<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        var memberDiagnostics = diagnostics.Where(d => d.Id is "CS0117" or "CS1061").ToList();
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
        var members = symbols
            .Where(IsCandidateMember)
            .Select(ToCandidate)
            .OfType<CommonSymbolCandidate>()
            .DistinctBy(c => (c.Name, c.Container, c.Kind))
            .ToList();

        var closest = CorrectedLineHint.FindClosest(wrongName, members.Select(m => m.Name));
        if (closest is null)
        {
            return null;
        }

        var candidates = NameCandidates.Rank(wrongName, members, m => m.Name, MaxCandidates, strict: true)
            .Select(r => r.Item with { Distance = r.Distance })
            .ToList();
        var correctedLine = CorrectedLineHint.RewriteLine(line.ToString(), wrongName, closest, diagnostic.StartColumn, memberAccess: true)?.Trim();

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
            ReceiverType: type.ToDisplayString(),
            Replacement: access.Expression.ToString() + "." + closest,
            CorrectedLine: correctedLine,
            WrongNameDeclaredElsewhere: isDeclaredElsewhere,
            Candidates: candidates);
    }

    private static Document? FindDocument(Solution solution, DiagnosticInfo diagnostic)
    {
        // A file linked into several projects has several document ids: prefer the one in the diagnostic's project, else the first.
        var ids = solution.GetDocumentIdsWithFilePath(diagnostic.FilePath);
        var id = ids.FirstOrDefault(i => diagnostic.Project is not null && string.Equals(solution.GetProject(i.ProjectId)?.Name, diagnostic.Project, StringComparison.Ordinal))
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

    // Keeps what a model can name after a dot: types, ordinary and extension methods, properties (not indexers), fields (enum members
    // included) and events. Implicitly declared members, constructors, operators and accessors are dropped.
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
            IFieldSymbol or IEventSymbol or INamedTypeSymbol => true,
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

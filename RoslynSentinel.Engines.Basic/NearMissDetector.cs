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

    /// <summary>
    /// EVALUATION feature (see <see cref="NearMissOptions"/>): builds the corrected changeset for a rejected edit when the strict rule holds, otherwise null.
    /// Pure: it reads only its arguments (and <see cref="NearMissOptions.NameDisplay"/> when <paramref name="nameDisplay"/> is null). The rule, all required:
    /// (1) every error diagnostic of the report is covered by one item of <paramref name="nearMisses"/> (same id, file, line and column; warnings are ignored),
    /// so any other error means no correction; (2) each item has exactly one distinct candidate name within <see cref="CorrectedLineHint"/>'s distance limit;
    /// (3) no item has <see cref="NearMissName.WrongNameDeclaredElsewhere"/>; (4, checked by the caller, which re-validates the result) the corrected changes
    /// compile clean. The wrong name is read from <paramref name="changes"/> at each item's line and column, and must agree with
    /// <see cref="CompilerErrorLookupHelper.WrongNameOf"/> when that can parse the message. Only the wrong-name token is replaced (not the receiver).
    /// The returned spans are positions in the proposed text of <paramref name="changes"/> (see <see cref="NearMissCorrection.CorrectionSpans"/>).
    /// </summary>
    /// <param name="changes">The proposed text per file that the compile gate rejected. Not modified.</param>
    /// <param name="diagnostics">The compile gate's diagnostics for those changes.</param>
    /// <param name="nearMisses">The items from <see cref="DetectAsync"/> for the same changes and diagnostics.</param>
    /// <param name="operationName">The tool name written into each <see cref="NearMissSubstitution"/>.</param>
    /// <param name="nameDisplay">Wording of the findings: show names the substitution, hide states only the corrected line. Null reads <see cref="NearMissOptions.NameDisplay"/>.</param>
    public static NearMissCorrection? BuildCorrection(
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        IReadOnlyList<DiagnosticInfo> diagnostics,
        IReadOnlyList<NearMissName> nearMisses,
        string operationName,
        NearMissNameDisplay? nameDisplay = null)
    {
        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        if (errors.Count == 0 || nearMisses.Count == 0)
        {
            return null;
        }

        var display = nameDisplay ?? NearMissOptions.NameDisplay;
        var slips = new List<(FilePathWrapper Key, int Offset, string Wrong, string Closest, NearMissName Item)>();
        foreach (var error in errors)
        {
            // Rule 1 (an uncovered error), rule 3 (declared elsewhere) and rule 2 (not exactly one name in range) each mean no correction at all.
            var item = nearMisses.FirstOrDefault(n => IsItemFor(n, error));
            if (item is null || item.WrongNameDeclaredElsewhere)
            {
                return null;
            }

            var located = LocateWrongName(changes, item, error);
            if (located is null)
            {
                return null;
            }

            var (key, offset, wrong) = located.Value;
            var names = item.Candidates.Select(c => c.Name).ToList();
            var closest = CorrectedLineHint.CountWithinLimit(wrong, names) == 1 ? CorrectedLineHint.FindClosest(wrong, names) : null;
            if (closest is null)
            {
                return null;
            }

            // Two diagnostics at one token (a duplicate report) are one replacement.
            if (!slips.Any(s => s.Key.Equals(key) && s.Offset == offset))
            {
                slips.Add((key, offset, wrong, closest, item));
            }
        }

        var corrected = changes.ToDictionary(entry => entry.Key, entry => entry.Value);
        var spans = new Dictionary<FilePathWrapper, IReadOnlyList<TextChange>>();
        foreach (var file in slips.GroupBy(s => s.Key))
        {
            IReadOnlyList<TextChange> edits = file.OrderBy(s => s.Offset).Select(s => new TextChange(new TextSpan(s.Offset, s.Wrong.Length), s.Closest)).ToList();
            spans[file.Key] = edits;
            corrected[file.Key] = CollateralChangeGuard.ApplyEdits(changes[file.Key], edits);
        }

        var substitutions = new List<NearMissSubstitution>();
        var findings = new List<Finding>();
        foreach (var slip in slips)
        {
            var correctedLine = LineOf(corrected[slip.Key], slip.Item.Line);
            var location = $"{Path.GetFileName((string)slip.Key)}:{slip.Item.Line}";
            substitutions.Add(new NearMissSubstitution(operationName, slip.Item.FilePath, slip.Item.Line, slip.Wrong, slip.Closest, slip.Item.DiagnosticId, 1, DisplayModeOf(display), Applied: true));
            findings.Add(new Finding("NearMiss", display == NearMissNameDisplay.Hide
                ? $"Corrected a misspelled name at {location}. Corrected line: {correctedLine}"
                : $"Corrected a misspelled name at {location}: '{slip.Wrong}' -> '{slip.Closest}'. Corrected line: {correctedLine}", FindingSeverity.Caution));
        }

        return new NearMissCorrection(corrected, spans, substitutions, findings);
    }

    /// <summary>
    /// EVALUATION feature: records each item of a rejection that was NOT corrected (the rejection text carried its hint) through
    /// <see cref="NearMissRecorder"/> with Applied false. The caller that builds the rejection calls this once per rejection with the same
    /// <paramref name="changes"/> and items it passed to <see cref="DetectAsync"/>; a recorded item is never one that
    /// <see cref="BuildCorrection"/> applied, because an applied correction is not a rejection. A no-op unless <see cref="NearMissRecorder.IsActive"/>.
    /// An item whose token cannot be located in <paramref name="changes"/> is skipped.
    /// </summary>
    public static void RecordAmbiguous(IReadOnlyDictionary<FilePathWrapper, string> changes, IReadOnlyList<NearMissName> nearMisses, string operationName, NearMissNameDisplay? nameDisplay = null)
    {
        if (!NearMissRecorder.IsActive)
        {
            return;
        }

        var display = nameDisplay ?? NearMissOptions.NameDisplay;
        foreach (var item in nearMisses)
        {
            var located = LocateWrongName(changes, item, null);
            if (located is null)
            {
                continue;
            }

            var wrong = located.Value.Wrong;
            var names = item.Candidates.Select(c => c.Name).ToList();
            var closest = CorrectedLineHint.FindClosest(wrong, names) ?? item.Replacement;
            NearMissRecorder.Record(new NearMissSubstitution(operationName, item.FilePath, item.Line, wrong, closest, item.DiagnosticId, CorrectedLineHint.CountWithinLimit(wrong, names), DisplayModeOf(display), Applied: false));
        }
    }

    private static string DisplayModeOf(NearMissNameDisplay display) => display == NearMissNameDisplay.Hide ? "hide" : "show";

    // An item is the near miss of a diagnostic when both name the same id and position (the detector builds one item per member diagnostic).
    private static bool IsItemFor(NearMissName item, DiagnosticInfo diagnostic)
        => item.DiagnosticId == diagnostic.Id
           && item.Line == diagnostic.StartLine
           && item.Column == diagnostic.StartColumn
           && string.Equals(item.FilePath, diagnostic.FilePath.ToString(), StringComparison.OrdinalIgnoreCase);

    // Finds the identifier token at the item's line and column in the proposed text. Null when the file is not in the changes, the position is outside
    // the text or not the start of an identifier, or the diagnostic's own message names a different identifier (then the position is not trustworthy).
    private static (FilePathWrapper Key, int Offset, string Wrong)? LocateWrongName(IReadOnlyDictionary<FilePathWrapper, string> changes, NearMissName item, DiagnosticInfo? diagnostic)
    {
        FilePathWrapper? key = null;
        foreach (var candidate in changes.Keys)
        {
            if (string.Equals((string)candidate, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                key = candidate;
                break;
            }
        }

        if (key is null || item.Line < 1 || item.Column < 1)
        {
            return null;
        }

        var text = SourceText.From(changes[key.Value]);
        if (item.Line > text.Lines.Count)
        {
            return null;
        }

        var line = text.Lines[item.Line - 1];
        var offset = line.Start + item.Column - 1;
        if (offset >= line.End || !(char.IsLetter(text[offset]) || text[offset] == '_'))
        {
            return null;
        }

        // The column must be the start of the token, not the middle of a longer name or an @-escaped identifier.
        if (offset > line.Start && (char.IsLetterOrDigit(text[offset - 1]) || text[offset - 1] is '_' or '@'))
        {
            return null;
        }

        var end = offset;
        while (end < line.End && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        var wrong = text.ToString(TextSpan.FromBounds(offset, end));
        var reported = diagnostic is null ? null : CompilerErrorLookupHelper.WrongNameOf(diagnostic);
        return reported is not null && !string.Equals(reported, wrong, StringComparison.Ordinal) ? null : (key.Value, offset, wrong);
    }

    // The trimmed text of a 1-based line, capped so a finding never quotes a long line.
    private static string LineOf(string text, int line)
    {
        var source = SourceText.From(text);
        var value = line >= 1 && line <= source.Lines.Count ? source.Lines[line - 1].ToString().Trim() : string.Empty;
        return value.Length > 200 ? value[..200] + "..." : value;
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

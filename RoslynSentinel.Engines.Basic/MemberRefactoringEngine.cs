using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Engines.Basic;

public record PreviewCallSite(FilePathWrapper FilePath, int Line, string CallExpression, CallSiteStatus Status, string? BlockReason, string? SuggestedFix, IReadOnlyList<string> Candidates);
public record SkippedCallSite(FilePathWrapper FilePath, int LineNumber, string Reason);

public record MoveMemberResult(Dictionary<FilePathWrapper, string> Changes, List<SkippedCallSite> SkippedCallSites, List<CallSiteLedgerEntry>? PendingLedgerEntries = null, string? PendingLedgerOperationName = null, List<AppliedCallSiteFixup>? AppliedFixups = null, IReadOnlyList<string>? Notes = null);
/// <summary>
/// One call site rewritten from a caller-supplied callSiteFixups entry (not an auto-resolved one).
/// <paramref name = "Line"/> is the 1-based line in the PROPOSED (post-rewrite, post-normalization)
/// content, so it lines up with the compile gate's diagnostics - the MoveMember tool uses it to tell
/// the caller when a rejection's errors sit on lines their own fixup value produced.
/// </summary>
public record AppliedCallSiteFixup(string FilePath, int Line, string FixupKey, string FixupValue);

public class MemberRefactoringEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly SentinelConfiguration? _config;

    public MemberRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ValidationEngine validationEngine, SentinelConfiguration? config = null)
    {
        _workspaceManager = workspaceManager;
        _symbolNavigationEngine = symbolNavigationEngine;
        _validationEngine = validationEngine;
        _config = config ?? new SentinelConfiguration();
    }

    /// <summary>
    /// Parsed MoveMember callSiteFixups. Per unresolved call site, precedence is: an exact
    /// "FilePath:Line" key, then "FilePath:*" (every unresolved site in that file), then "*" (every
    /// unresolved site anywhere). Paths match case-insensitively after Path.GetFullPath normalization;
    /// solution-relative paths resolve against the solution root. Keys and values are trimmed. The
    /// "new" shorthand is matched case-SENSITIVELY on purpose: "new" is a reserved keyword and can never
    /// be an identifier, whereas "New"/"NEW" are legal C# identifiers that could name a real field.
    /// </summary>
    private sealed class CallSiteFixupMap
    {
        public const string NewKeyword = "new";
        private readonly Dictionary<string, (string Key, string Value)> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Key, string Value)> _perFile = new(StringComparer.OrdinalIgnoreCase);
        private (string Key, string Value)? _global;
        public static CallSiteFixupMap Parse(Dictionary<string, string>? fixups, string? solutionRoot)
        {
            var map = new CallSiteFixupMap();
            if (fixups == null)
            {
                return map;
            }

            var unusable = new List<string>();
            foreach (var (rawKey, rawValue) in fixups)
            {
                var key = (rawKey ?? string.Empty).Trim();
                var value = (rawValue ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    unusable.Add($"\"{rawKey}\" (empty value)");
                    continue;
                }

                if (key == "*")
                {
                    map._global = (key, value);
                    continue;
                }

                // Split on the LAST ':' - the first one is usually a drive letter.
                int colon = key.LastIndexOf(':');
                var fullPath = colon > 0 ? NormalizePath(key[..colon], solutionRoot) : null;
                var linePart = colon > 0 ? key[(colon + 1)..].Trim() : string.Empty;
                if (fullPath == null)
                {
                    unusable.Add($"\"{rawKey}\"");
                }
                else if (linePart == "*")
                {
                    map._perFile[fullPath] = (key, value);
                }
                else if (int.TryParse(linePart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line) && line > 0)
                {
                    map._exact[$"{fullPath}:{line}"] = (key, value);
                }
                else
                {
                    unusable.Add($"\"{rawKey}\"");
                }
            }

            if (unusable.Count > 0)
            {
                const int maxShown = 5;
                throw new ToolInvalidArgumentException($"{unusable.Count} callSiteFixups entr{(unusable.Count == 1 ? "y is" : "ies are")} unusable: {string.Join(", ", unusable.Take(maxShown))}" + (unusable.Count > maxShown ? $" (+{unusable.Count - maxShown} more)" : "") + ". Each key must be \"FilePath:Line\" (1-based line), \"FilePath:*\" (every unresolved call site in that file) or \"*\" (every unresolved call site), " + "and each value a non-empty receiver expression or \"new\". No changes were made.");
            }

            return map;
        }

        /// <summary>The fixup for an unresolved call site, by precedence, or null if none applies.</summary>
        public (string Key, string Value)? Match(string filePath, int line)
        {
            var fullPath = NormalizePath(filePath, null) ?? filePath;
            if (_exact.TryGetValue($"{fullPath}:{line}", out var exact))
            {
                return exact;
            }

            if (_perFile.TryGetValue(fullPath, out var perFile))
            {
                return perFile;
            }

            return _global;
        }

        /// <summary>The normalized "{fullPath}:{line}" form <see cref="Match"/> looks exact keys up by.</summary>
        public static string SiteKey(string filePath, int line) => $"{NormalizePath(filePath, null) ?? filePath}:{line}";

        /// <summary>
        /// Exact "FilePath:Line" entries (spelled as the caller wrote them) whose normalized key is not in
        /// <paramref name="siteKeys"/> - i.e. keys that name no real call site, almost always a typo or a stale line.
        /// </summary>
        public IReadOnlyList<string> ExactKeysMatchingNoSite(ISet<string> siteKeys) =>
            _exact.Where(kv => !siteKeys.Contains(kv.Key)).Select(kv => kv.Value.Key).ToList();

        private static string? NormalizePath(string path, string? solutionRoot)
        {
            try
            {
                var wrapped = FilePathWrapper.ResolveFromWire(path, solutionRoot);
                return string.IsNullOrEmpty(wrapped.Absolute) ? null : Path.GetFullPath(wrapped.Absolute);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Resolves the class an AddConstructorParameter edit targets. Shared by the single-file path and the
    /// callSiteFixups path so both locate the class identically. Returns the class, or a CannotEdit result
    /// describing why it could not be resolved.
    /// </summary>
    private (ClassDeclarationSyntax? Class, DocumentEditResult? Error) ResolveAddConstructorTargetClass(SyntaxNode root, SourceText sourceText, FilePathWrapper filePath, string className, string? contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken)
    {
        BaseTypeDeclarationSyntax? classNode;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, className, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            classNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return (null, new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            });
        }

        if (classNode is not ClassDeclarationSyntax classDecl)
        {
            return (null, new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: class not found."
            });
        }

        return (classDecl, null);
    }

    public async Task<DocumentEditResult> AddConstructorParameterAsync(FilePathWrapper filePath, string className, string paramName, string paramType, string? fieldName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default, string? defaultValue = null, bool nullDefault = false)
    {
        if (nullDefault && defaultValue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: nullDefault and defaultValue are mutually exclusive - pass only one."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        return await AddConstructorParameterToDocumentAsync(document, filePath, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, defaultValue, nullDefault, cancellationToken);
    }

    /// <summary>
    /// Per-document body of <see cref="AddConstructorParameterAsync"/>: adds the parameter, backing field and
    /// assignment to the target class inside <paramref name="document"/>. Split out so the callSiteFixups path
    /// can run it on a document whose call sites were already rewritten, producing one merged text per file.
    /// </summary>
    private async Task<DocumentEditResult> AddConstructorParameterToDocumentAsync(Document document, FilePathWrapper filePath, string className, string paramName, string paramType, string? fieldName, string? contextSnippet, string? lineBefore, string? lineAfter, string? defaultValue, bool nullDefault, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var (classDecl, resolveError) = ResolveAddConstructorTargetClass(root, sourceText, filePath, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (classDecl == null)
        {
            return resolveError!;
        }

        // Derive the backing field name, disambiguating from paramName so the generated
        // assignment can never degenerate into a no-op self-assignment (e.g. `stopwatch = stopwatch;`
        // instead of assigning the parameter into a distinct field -> confirmed regression:
        // ContosoOrders OrderService, caller passed fieldName == paramName == "stopwatch"). A
        // caller-supplied fieldName that collides with paramName, with or without a leading
        // underscore, is treated the same as omitting fieldName: it falls back to the default
        // "_camelCase(paramName)" derivation, which always differs from paramName.
        var defaultFieldName = $"_{char.ToLower(paramName[0])}{paramName[1..]}";
        string derivedFieldName = fieldName == null || fieldName == paramName || fieldName == $"_{paramName}" ? defaultFieldName : fieldName;
        var fieldDecl = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private readonly {paramType} {derivedFieldName};")!;
        //.WithAddedByComment("AddConstructorParameter");
        var assignmentStatement = SyntaxFactory.ParseStatement($"{derivedFieldName} = {paramName};");
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramType).WithTrailingTrivia(SyntaxFactory.Space));
        if (nullDefault)
        {
            newParam = newParam.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }
        else if (defaultValue != null)
        {
            newParam = newParam.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(defaultValue)));
        }

        var ctor = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        ConstructorDeclarationSyntax newCtor;
        if (ctor != null)
        {
            var newParams = ctor.ParameterList.Parameters.Count == 0 ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList([newParam])) : ctor.ParameterList.AddParameters(newParam);
            BlockSyntax body;
            if (ctor.Body != null)
            {
                body = ctor.Body.AddStatements(assignmentStatement);
            }
            else
            {
                // expression body -> convert to block
                var exprStatement = SyntaxFactory.ExpressionStatement(ctor.ExpressionBody!.Expression);
                body = SyntaxFactory.Block(exprStatement, assignmentStatement);
            }

            newCtor = ctor.WithParameterList(newParams).WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
        }
        else
        {
            var paramList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList([newParam]));
            var body = SyntaxFactory.Block(assignmentStatement);
            newCtor = SyntaxFactory.ConstructorDeclaration(className).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))).WithParameterList(paramList).WithBody(body);
            //.WithAddedByComment("AddConstructorParameter");
        }

        var newMembers = new List<MemberDeclarationSyntax>
        {
            fieldDecl
        };
        foreach (var m in classDecl.Members)
        {
            if (ctor != null && m == ctor)
            {
                newMembers.Add(newCtor);
            }
            else
            {
                newMembers.Add(m);
            }
        }

        if (ctor == null)
        {
            newMembers.Add(newCtor);
        }

        var newClassNode = classDecl.WithMembers(SyntaxFactory.List(newMembers));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, classDecl, newClassNode, cancellationToken),
            Message = $"// paramName='{paramName}', fieldName='{derivedFieldName}'"
        };
    }

    /// <summary>
    /// <see cref="AddConstructorParameterAsync"/> plus the new argument at every call site of the target constructor
    /// (object creation, target-typed new, <c>: this(...)</c>, <c>: base(...)</c>, and - for a parameterless target - the
    /// implicit <c>base()</c> of derived constructors), as ONE multi-file change set. Per site the argument comes from
    /// <paramref name="callSiteFixups"/> (exact "FilePath:Line" beats "FilePath:*" beats "*"); a site with no entry is left
    /// alone when <paramref name="defaultValue"/>/<paramref name="nullDefault"/> covers it, else it is reported in
    /// <see cref="AddConstructorParameterCascadeResult.UnresolvedSites"/> and nothing is edited. An exact key that names no
    /// real call site is rejected up front. The class is targeted exactly as <see cref="AddConstructorParameterAsync"/>
    /// targets it (its first declared constructor, or a synthesized one).
    /// </summary>
    public async Task<AddConstructorParameterCascadeResult> AddConstructorParameterWithCallSitesAsync(FilePathWrapper filePath, string className, string paramName, string paramType, Dictionary<string, string> callSiteFixups, string? fieldName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? defaultValue = null, bool nullDefault = false, CancellationToken cancellationToken = default)
    {
        AddConstructorParameterCascadeResult Failed(EditOutcome outcome, string message) =>
            new(new DocumentEditResult { Outcome = outcome, FilePath = filePath, Message = message }, new Dictionary<FilePathWrapper, string>(), [], null, 0, 0);

        AddConstructorParameterCascadeResult Invalid(string message) =>
            new(new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = message }, new Dictionary<FilePathWrapper, string>(), [], message, 0, 0);

        if (nullDefault && defaultValue != null)
        {
            return Invalid("nullDefault and defaultValue are mutually exclusive - pass only one.");
        }

        CallSiteFixupMap fixupMap;
        try
        {
            fixupMap = CallSiteFixupMap.Parse(callSiteFixups, _workspaceManager.GetSolutionRoot());
        }
        catch (ToolInvalidArgumentException ex)
        {
            return Invalid(ex.Message);
        }

        var badValues = callSiteFixups
            .Select(kv => (Key: kv.Key, Value: (kv.Value ?? string.Empty).Trim()))
            .Where(kv => kv.Value == CallSiteFixupMap.NewKeyword || SyntaxFactory.ParseExpression(kv.Value).ContainsDiagnostics)
            .Select(kv => $"\"{kv.Key}\" -> \"{kv.Value}\"")
            .ToList();
        if (badValues.Count > 0)
        {
            return Invalid($"{badValues.Count} callSiteFixups value(s) are not a valid C# argument expression: {string.Join(", ", badValues.Take(5))}. Each value is the expression passed for '{paramName}' at that call site, e.g. \"config\" or \"new SentinelConfiguration()\". No changes were made.");
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return Failed(EditOutcome.DocumentNotFound, "// Document not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || sourceText == null || semanticModel == null)
        {
            return Failed(EditOutcome.CannotEdit, "// Cannot edit: syntax root not found.");
        }

        var (classDecl, resolveError) = ResolveAddConstructorTargetClass(root, sourceText, filePath, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (classDecl == null)
        {
            return new AddConstructorParameterCascadeResult(resolveError!, new Dictionary<FilePathWrapper, string>(), [], null, 0, 0);
        }

        var targetCtorDecl = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (targetCtorDecl != null && targetCtorDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return Failed(EditOutcome.CannotEdit, $"// Cannot edit: the first constructor of '{className}' is static, so call sites cannot be located.");
        }

        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken) as INamedTypeSymbol;
        var targetCtor = targetCtorDecl != null
            ? semanticModel.GetDeclaredSymbol(targetCtorDecl, cancellationToken) as IMethodSymbol
            : classSymbol?.InstanceConstructors.FirstOrDefault(c => c.IsImplicitlyDeclared && c.Parameters.Length == 0);
        if (targetCtor == null)
        {
            return Failed(EditOutcome.CannotEdit, $"// Cannot edit: could not resolve the constructor symbol of '{className}' to locate its call sites.");
        }

        var sites = await ConstructorCallSiteFinder.FindAsync(solution, targetCtor, cancellationToken);

        var unmatchedKeys = fixupMap.ExactKeysMatchingNoSite(sites.Select(s => CallSiteFixupMap.SiteKey(s.FilePath, s.Line)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (unmatchedKeys.Count > 0)
        {
            const int maxShown = 20;
            var realKeys = sites.Select(s => $"{s.FilePath}:{s.Line}").Distinct().ToList();
            return Invalid($"{unmatchedKeys.Count} callSiteFixups key(s) match no call site of '{className}' constructor: {string.Join(", ", unmatchedKeys.Take(maxShown))}. "
                + (realKeys.Count == 0
                    ? "No call sites were found. "
                    : $"The call sites found are: {string.Join(", ", realKeys.Take(maxShown))}{(realKeys.Count > maxShown ? $" (+{realKeys.Count - maxShown} more)" : string.Empty)}. ")
                + "No changes were made.");
        }

        var hasDefault = nullDefault || defaultValue != null;
        var edits = new List<(ConstructorCallSite Site, string Expression)>();
        var unresolved = new List<UnresolvedConstructorCallSite>();
        var leftToDefault = 0;
        foreach (var site in sites)
        {
            var match = site.IsEditable ? fixupMap.Match(site.FilePath, site.Line) : null;
            if (match is { } fixup)
            {
                edits.Add((site, fixup.Value));
            }
            else if (hasDefault)
            {
                leftToDefault++;
            }
            else
            {
                unresolved.Add(new UnresolvedConstructorCallSite(site.FilePath, site.Line, site.CallText, site.UnfixableReason));
            }
        }

        if (unresolved.Count > 0)
        {
            return new AddConstructorParameterCascadeResult(
                new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = $"// Cannot edit: {unresolved.Count} call site(s) have no callSiteFixups entry and no defaultValue/nullDefault." },
                new Dictionary<FilePathWrapper, string>(), unresolved, null, 0, 0);
        }

        var editedDocuments = await ConstructorCallSiteFinder.ApplyAsync(edits, paramName, cancellationToken);

        // The class's own file may itself hold call sites (a this(...) initializer, or a new X() in another type
        // declared there): run the class edit on the already-rewritten document so both land in one text.
        var classDocument = editedDocuments.TryGetValue(document.Id, out var rewrittenClassDocument) ? rewrittenClassDocument : document;
        var classEdit = await AddConstructorParameterToDocumentAsync(classDocument, filePath, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, defaultValue, nullDefault, cancellationToken);
        if (classEdit.Outcome != EditOutcome.Modified || classEdit.UpdatedText == null)
        {
            return new AddConstructorParameterCascadeResult(classEdit, new Dictionary<FilePathWrapper, string>(), [], null, 0, 0);
        }

        var changes = new Dictionary<FilePathWrapper, string> { [filePath] = classEdit.UpdatedText };
        foreach (var (documentId, editedDocument) in editedDocuments)
        {
            if (documentId == document.Id || editedDocument.FilePath == null)
            {
                continue;
            }

            var originalText = await solution.GetDocument(documentId)!.GetTextAsync(cancellationToken);
            var editedText = (await editedDocument.GetTextAsync(cancellationToken)).ToString();
            changes[(FilePathWrapper)editedDocument.FilePath] = EolUtilities.NormalizeEol(editedText, EolUtilities.DetectDominantEol(originalText));
        }

        return new AddConstructorParameterCascadeResult(classEdit, changes, [], null, edits.Count, leftToDefault);
    }

    /// <summary>
    /// Removes a DI constructor parameter and its assignment statement. The backing field is only
    /// deleted when a solution-wide reference check (SymbolFinder.FindReferencesAsync) confirms
    /// nothing outside the removed assignment reads or writes it -> otherwise the field is left in
    /// place so removal never silently breaks code that still depends on it.
    /// </summary>
    public async Task<DocumentEditResult> RemoveConstructorParameterAsync(FilePathWrapper filePath, string className, string paramName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        BaseTypeDeclarationSyntax? classNode;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, className, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            classNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (classNode == null || classNode is not ClassDeclarationSyntax classDecl)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: class not found."
            };
        }

        var ctor = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        var targetParam = ctor?.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == paramName);
        if (ctor == null || targetParam == null || ctor.Body == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Constructor parameter '{paramName}' not found on '{className}'."
            };
        }

        // Locate the `<field> = <paramName>;` assignment this parameter feeds, so we know which
        // field is the removal candidate and which statement to drop alongside the parameter.
        var assignment = ctor.Body.Statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax { Right: IdentifierNameSyntax rhs } assign && rhs.Identifier.Text == paramName && (assign.Left is IdentifierNameSyntax || (assign.Left is MemberAccessExpressionSyntax ma && ma.Expression is ThisExpressionSyntax)));
        string? candidateFieldName = assignment != null && assignment.Expression is AssignmentExpressionSyntax a ? a.Left switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax memberId } => memberId.Identifier.Text,
            _ => null
        } : null;
        var newParams = ctor.ParameterList.WithParameters(SyntaxFactory.SeparatedList(ctor.ParameterList.Parameters.Where(p => p != targetParam)));
        var newStatements = assignment != null ? ctor.Body.Statements.Where(s => s != assignment).ToList() : ctor.Body.Statements.ToList();
        var newCtor = ctor.WithParameterList(newParams).WithBody(ctor.Body.WithStatements(SyntaxFactory.List(newStatements)));
        var fieldDecl = candidateFieldName != null ? classDecl.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == candidateFieldName)) : null;
        var newMembers = classDecl.Members.Select(m => m == ctor ? (MemberDeclarationSyntax)newCtor : m).ToList();
        if (fieldDecl != null)
        {
            var semanticModel = await document.Project.GetCompilationAsync(cancellationToken) is { } compilation ? compilation.GetSemanticModel(root.SyntaxTree) : null;
            var fieldSymbol = semanticModel?.GetDeclaredSymbol(fieldDecl.Declaration.Variables.First(), cancellationToken);
            var fieldStillUsedElsewhere = false;
            if (fieldSymbol != null)
            {
                var references = await SymbolFinder.FindReferencesAsync(fieldSymbol, solution, cancellationToken);
                foreach (var reference in references)
                {
                    foreach (var location in reference.Locations)
                    {
                        if (location.IsImplicit)
                        {
                            continue;
                        }

                        // The assignment statement we're removing is itself a reference to the field ->
                        // don't let it count against "still used elsewhere".
                        if (assignment != null && location.Document.FilePath == filePath && assignment.Span.Contains(location.Location.SourceSpan))
                        {
                            continue;
                        }

                        fieldStillUsedElsewhere = true;
                        break;
                    }

                    if (fieldStillUsedElsewhere)
                    {
                        break;
                    }
                }
            }
            else
            {
                // No semantic model / symbol available -> can't prove the field is unused, so err
                // conservative and leave it in place rather than risk deleting something still live.
                fieldStillUsedElsewhere = true;
            }

            if (!fieldStillUsedElsewhere)
            {
                newMembers = newMembers.Where(m => m != fieldDecl).ToList();
            }
        }

        var newClassNode = classDecl.WithMembers(SyntaxFactory.List(newMembers));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, classDecl, newClassNode, cancellationToken),
            Message = fieldDecl != null ? $"// paramName='{paramName}', fieldName='{candidateFieldName}', fieldRemoved='{newMembers.All(m => m != fieldDecl)}'" : $"// paramName='{paramName}'"
        };
    }

    public record ConstructorParameterInfo(string ParamName, string ParamType, string? FieldName);
    /// <summary>
    /// Lists a class's primary constructor parameters alongside their best-guess backing field,
    /// inferred from a `<field> = <paramName>;` (or `this.<field> = <paramName>;`) assignment
    /// statement in the constructor body -> the same convention AddConstructorParameterAsync writes.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, List<ConstructorParameterInfo> Parameters)> GetConstructorParametersAsync(FilePathWrapper filePath, string className, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
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

        BaseTypeDeclarationSyntax? classNode;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, className, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            classNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return (EditOutcome.CannotEdit, ex.Message, []);
        }

        if (classNode == null || classNode is not ClassDeclarationSyntax classDecl)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: class not found.", []);
        }

        var ctor = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null)
        {
            return (EditOutcome.Modified, null, []);
        }

        var assignments = ctor.Body?.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression as AssignmentExpressionSyntax).Where(a => a != null && a!.Right is IdentifierNameSyntax).ToList() ?? [];
        var result = new List<ConstructorParameterInfo>();
        foreach (var param in ctor.ParameterList.Parameters)
        {
            var paramName = param.Identifier.Text;
            var match = assignments.FirstOrDefault(a => ((IdentifierNameSyntax)a!.Right).Identifier.Text == paramName);
            string? fieldName = match?.Left switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Name: IdentifierNameSyntax memberId } => memberId.Identifier.Text,
                _ => null
            };
            result.Add(new ConstructorParameterInfo(paramName, param.Type?.ToString() ?? "", fieldName));
        }

        return (EditOutcome.Modified, null, result);
    }

    public record MethodParameterInfo(string ParamName, string ParamType, string? DefaultValue);

    /// <summary>
    /// Sets an enum's complete member list in one pass -> covers add, remove, and reorder.
    /// <paramref name = "values"/> is a comma-separated "Name[=IntValue]" list (or a JSON array of
    /// such strings - both shapes are accepted) in the desired final
    /// order. Members whose name is retained keep their existing explicit value unless the caller
    /// supplies an override; members omitted from <paramref name = "values"/> are removed; names not
    /// currently present are added (in the position given). The returned DocumentEditResult.Message
    /// summarizes what was added/removed/reordered so callers can verify the diff matched intent.
    /// Members that were already explicit in the source keep their literal value regardless of new
    /// position (same as a hand-edit would); members that were implicit take the next ordinal from
    /// their predecessor in the NEW order -> same renumbering behavior as manually retyping the enum
    /// body -> so a mid-list insert or removal can shift a retained implicit member's underlying
    /// value. Pass "=N" explicitly for any member whose numeric value must not move.
    /// </summary>
    public async Task<DocumentEditResult> ModifyEnumAsync(FilePathWrapper filePath, string enumName, string values, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Cannot edit: cannot parse file."
            };
        }

        BaseTypeDeclarationSyntax? enumNode = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, enumName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            enumNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (enumNode == null || enumNode is not EnumDeclarationSyntax)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Cannot edit: enum '{enumName}' not found."
            };
        }

        var enumDecl = (EnumDeclarationSyntax)enumNode;
        var requested = new List<(string Name, int? Value)>();
        var parsedValues = DelimitedListParser.ParseStringOrJsonArrayToList(values, out var valuesError);
        if (valuesError != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: {valuesError}"
            };
        }

        foreach (var raw in parsedValues!)
        {
            var token = raw.Trim();
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                requested.Add((token, null));
                continue;
            }

            var name = token[..eq].Trim();
            var valueText = token[(eq + 1)..].Trim();
            if (!int.TryParse(valueText, out var explicitValue))
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = $"// Cannot edit: '{valueText}' in '{token}' is not a valid integer explicit value."
                };
            }

            requested.Add((name, explicitValue));
        }

        if (requested.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: values must contain at least one member name."
            };
        }

        var duplicates = requested.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: duplicate member name(s) in values: {string.Join(", ", duplicates)}."
            };
        }

        var existingMembers = enumDecl.Members.ToList();
        var existingByName = existingMembers.ToDictionary(m => m.Identifier.Text, m => m, StringComparer.Ordinal);
        var existingNamesInOrder = existingMembers.Select(m => m.Identifier.Text).ToList();
        var requestedNames = requested.Select(r => r.Name).ToList();
        var added = requestedNames.Where(n => !existingByName.ContainsKey(n)).ToList();
        var removed = existingNamesInOrder.Where(n => !requestedNames.Contains(n)).ToList();
        var retainedOldOrder = existingNamesInOrder.Where(requestedNames.Contains).ToList();
        var retainedNewOrder = requestedNames.Where(existingByName.ContainsKey).ToList();
        bool reordered = !retainedOldOrder.SequenceEqual(retainedNewOrder);
        bool valueChanged = requested.Any(r => r.Value.HasValue && existingByName.TryGetValue(r.Name, out var existingMember) && GetExistingExplicitValue(existingMember) != r.Value);
        if (added.Count == 0 && removed.Count == 0 && !reordered && !valueChanged)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.AlreadyInTargetState,
                FilePath = filePath,
                Message = "// No change: requested values already match the current member list and order."
            };
        }

        var newMembers = new List<EnumMemberDeclarationSyntax>();
        foreach (var (name, explicitValue) in requested)
        {
            EnumMemberDeclarationSyntax member = existingByName.TryGetValue(name, out var existingMember) ? existingMember : SyntaxFactory.EnumMemberDeclaration(name);
            if (explicitValue.HasValue)
            {
                member = member.WithEqualsValue(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(explicitValue.Value))));
            }

            newMembers.Add(member);
        }

        // Detect duplicate effective values -> e.g. inserting a new implicit member ahead of an
        // already-explicit one shifts the implicit member's auto-numbered value into a collision
        // that C# permits silently. Fail loudly instead of producing a duplicate-valued enum.
        var effectiveValues = new List<(string Name, int Value)>();
        int nextImplicit = 0;
        foreach (var member in newMembers)
        {
            int value = member.EqualsValue?.Value is LiteralExpressionSyntax { Token.Value: int explicitVal } ? explicitVal : nextImplicit;
            effectiveValues.Add((member.Identifier.Text, value));
            nextImplicit = value + 1;
        }

        var valueCollisions = effectiveValues.GroupBy(v => v.Value).Where(g => g.Count() > 1).Select(g => $"{string.Join(" and ", g.Select(m => m.Name))} both = {g.Key}").ToList();
        if (valueCollisions.Count > 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: this would produce duplicate enum values ({string.Join("; ", valueCollisions)}). " + "Pass an explicit '=N' for the member(s) whose value must change to avoid the collision."
            };
        }

        var newEnumNode = enumDecl.WithMembers(SyntaxFactory.SeparatedList(newMembers));
        var updatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, enumDecl, newEnumNode, cancellationToken);
        var summary = new List<string>();
        if (added.Count > 0)
        {
            summary.Add($"added {string.Join(", ", added)}");
        }

        if (removed.Count > 0)
        {
            summary.Add($"removed {string.Join(", ", removed)}");
        }

        if (reordered)
        {
            summary.Add("reordered");
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText,
            Message = string.Join("; ", summary)
        };
        static int? GetExistingExplicitValue(EnumMemberDeclarationSyntax member) => member.EqualsValue?.Value is LiteralExpressionSyntax { Token.Value: int existingValue } ? existingValue : null;
    }

    /// <summary>
    /// Enum equivalent of AddMemberAsync/InsertMemberAfterAsync/InsertMemberBeforeAsync -> enums can't
    /// use those (EnumMemberDeclarationSyntax isn't a MemberDeclarationSyntax, and enum bodies use
    /// comma-separated syntax, not the member grammar SyntaxFactory.ParseMemberDeclaration expects).
    /// <paramref name="newMemberToken"/> is a single "Name" or "Name=IntValue" token, matching one
    /// entry of ModifyEnumAsync's values list -> not a full member declaration. Internally reads the
    /// enum's current members, splices the new token at the position implied by
    /// afterMemberName/beforeMemberName (append to the end when both are null), and delegates the
    /// actual edit to ModifyEnumAsync so renumbering/collision-detection logic isn't duplicated.
    /// </summary>
    public async Task<DocumentEditResult> AddEnumMemberAsync(FilePathWrapper filePath, string enumName, string newMemberToken, string? afterMemberName = null, string? beforeMemberName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var (outcome, message, existing) = await _symbolNavigationEngine.GetContainerMembersAsync(filePath, enumName, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (outcome != EditOutcome.Modified)
        {
            return new DocumentEditResult { Outcome = outcome, FilePath = filePath, Message = message ?? $"// Cannot edit: enum '{enumName}' not found." };
        }

        var newToken = newMemberToken.Trim();
        var newName = newToken.Split('=')[0].Trim();
        if (existing.Any(m => m.Name == newName))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: enum '{enumName}' already has a member named '{newName}'. Use operation 'replace' to change it."
            };
        }

        var names = existing.Select(m => m.Signature).ToList();
        if (afterMemberName != null)
        {
            var idx = existing.FindIndex(m => m.Name == afterMemberName);
            if (idx < 0)
            {
                return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, FilePath = filePath, Message = $"// Cannot edit: member '{afterMemberName}' not found in enum '{enumName}'." };
            }

            names.Insert(idx + 1, newToken);
        }
        else if (beforeMemberName != null)
        {
            var idx = existing.FindIndex(m => m.Name == beforeMemberName);
            if (idx < 0)
            {
                return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, FilePath = filePath, Message = $"// Cannot edit: member '{beforeMemberName}' not found in enum '{enumName}'." };
            }

            names.Insert(idx, newToken);
        }
        else
        {
            names.Add(newToken);
        }

        return await ModifyEnumAsync(filePath, enumName, string.Join(",", names), contextSnippet, lineBefore, lineAfter, cancellationToken);
    }

    /// <summary>
    /// Enum equivalent of RemoveMemberAsync. Reads the enum's current members, drops the named one,
    /// and delegates to ModifyEnumAsync (which treats "in existing but not in the requested list" as
    /// a removal) so renumbering logic isn't duplicated.
    /// </summary>
    public async Task<DocumentEditResult> RemoveEnumMemberAsync(FilePathWrapper filePath, string enumName, string memberName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var (outcome, message, existing) = await _symbolNavigationEngine.GetContainerMembersAsync(filePath, enumName, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (outcome != EditOutcome.Modified)
        {
            return new DocumentEditResult { Outcome = outcome, FilePath = filePath, Message = message ?? $"// Cannot edit: enum '{enumName}' not found." };
        }

        if (!existing.Any(m => m.Name == memberName))
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, FilePath = filePath, Message = $"// Cannot edit: member '{memberName}' not found in enum '{enumName}'." };
        }

        var names = existing.Where(m => m.Name != memberName).Select(m => m.Signature).ToList();
        if (names.Count == 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotRemove, FilePath = filePath, Message = $"// Cannot edit: removing '{memberName}' would leave enum '{enumName}' with no members." };
        }

        return await ModifyEnumAsync(filePath, enumName, string.Join(",", names), contextSnippet, lineBefore, lineAfter, cancellationToken);
    }

    /// <summary>
    /// Enum equivalent of ReplaceMemberAsync. Reads the enum's current members, substitutes the named
    /// one with <paramref name="newMemberToken"/> ("Name" or "Name=IntValue") at the same position,
    /// and delegates to ModifyEnumAsync.
    /// </summary>
    public async Task<DocumentEditResult> ReplaceEnumMemberAsync(FilePathWrapper filePath, string enumName, string memberName, string newMemberToken, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var (outcome, message, existing) = await _symbolNavigationEngine.GetContainerMembersAsync(filePath, enumName, contextSnippet, lineBefore, lineAfter, cancellationToken);
        if (outcome != EditOutcome.Modified)
        {
            return new DocumentEditResult { Outcome = outcome, FilePath = filePath, Message = message ?? $"// Cannot edit: enum '{enumName}' not found." };
        }

        var idx = existing.FindIndex(m => m.Name == memberName);
        if (idx < 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, FilePath = filePath, Message = $"// Cannot edit: member '{memberName}' not found in enum '{enumName}'." };
        }

        var names = existing.Select(m => m.Signature).ToList();
        names[idx] = newMemberToken.Trim();
        return await ModifyEnumAsync(filePath, enumName, string.Join(",", names), contextSnippet, lineBefore, lineAfter, cancellationToken);
    }

    /// <summary>
    /// SyntaxFactory.ParseMemberDeclaration parses only the FIRST declaration in a multi-declaration
    /// string and silently discards the rest - it does not throw and the returned node's FullSpan
    /// covers the whole input (the unconsumed text is folded in as SkippedTokensTrivia, not left as
    /// unconsumed suffix), so comparing span lengths against the input length can never detect this.
    /// The one reliable signal is ContainsDiagnostics: a second declaration's leading token (e.g.
    /// "private") is unexpected inside the first declaration's grammar and is reported as CS1073
    /// ("Unexpected token"). See blocking_error_member_addmember_silent_partial_write.md and
    /// blocking_error_member_addmember_silently_drops_second_declaration.md.
    /// </summary>
    private static string? DetectTrailingUnparsedDeclaration(string source, MemberDeclarationSyntax parsed)
    {
        if (!parsed.ContainsDiagnostics)
        {
            return null;
        }

        // Reconstructing preview text from SkippedTokensTrivia loses the original inter-token
        // whitespace (each skipped token's own trivia isn't preserved the same way), so locate the
        // skipped region's offset in the original source string instead and slice from there -
        // that keeps the preview readable instead of "privatereadonlyint_b;...".
        var firstSkippedTokenStart = parsed.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.SkippedTokensTrivia))
            .SelectMany(t => t.GetStructure()!.DescendantTokens())
            .Select(tok => (int?)tok.SpanStart)
            .FirstOrDefault();

        var trailingPreview = firstSkippedTokenStart is int offset && offset < source.Length
            ? source[offset..].Trim()
            : string.Empty;

        var extraDeclaration = trailingPreview.Length > 0 ? SyntaxFactory.ParseMemberDeclaration(trailingPreview) : null;
        return extraDeclaration != null
            ? $"The source contains more than one declaration; only the first ({parsed.Kind()}) would be used and the rest silently dropped. Split into separate calls, one declaration per call. First dropped declaration starts with: \"{trailingPreview[..Math.Min(80, trailingPreview.Length)]}\"."
            : $"The source does not parse as a single valid declaration ({parsed.Kind()}, with parse errors): {string.Join("; ", parsed.GetDiagnostics().Select(d => d.GetMessage()))}";
    }

    /// <summary>
    /// Batch form of <see cref="AddModifierAsync"/>/<see cref="RemoveModifierAsync"/>: applies every
    /// edit in <paramref name="edits"/> to <paramref name="filePath"/> against ONE original syntax root,
    /// then folds all replacements into a single <see cref="RoslynFormattingHelper.ReplaceNodesFormattedAsync"/>
    /// call. Calling the single-edit methods N times independently would be wrong for a multi-edit batch
    /// targeting the same file: each call _symbolNavigationEngine. Resolves against its own fresh GetCurrentSolutionAsync root, so
    /// the second call's UpdatedText would silently discard the first edit instead of compounding it.
    /// Returns one <see cref="DocumentEditResult"/> for the whole file: <see cref="EditOutcome.Modified"/>
    /// with UpdatedText on full success, or <see cref="EditOutcome.CannotEdit"/> with every per-index
    /// failure joined into Message (never a partial write) when any edit in this file's group fails to
    /// _symbolNavigationEngine. Resolve or two edits collide on the same target node.
    /// </summary>
    public async Task<DocumentEditResult> ApplyModifierBatchAsync(FilePathWrapper filePath, IReadOnlyList<(int Index, string TargetName, string Modifier, AddRemoveAction Action, string? ContextSnippet, string? LineBefore, string? LineAfter)> edits, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, FilePath = filePath, Message = "// Document not found." };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = "// Cannot edit: syntax root not found." };
        }

        var errors = new List<string>();
        var resolvedTargets = new Dictionary<int, MemberDeclarationSyntax>();
        foreach (var edit in edits)
        {
            try
            {
                var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, edit.TargetName, cancellationToken)
                    .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
                var target = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, edit.ContextSnippet, edit.LineBefore, edit.LineAfter,
                    (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
                if (target == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): target not found.");
                    continue;
                }
                resolvedTargets[edit.Index] = target;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"edits[{edit.Index}] ({edit.TargetName}): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        // Same-node collision check: two edits resolving to the identical declaration is the node-identity
        // equivalent of ReplaceSnippet batch's span-overlap rejection -> reject before building any replacement.
        var seen = new Dictionary<MemberDeclarationSyntax, int>();
        foreach (var kvp in resolvedTargets)
        {
            if (seen.TryGetValue(kvp.Value, out var firstIndex))
            {
                errors.Add($"edits[{firstIndex}] and edits[{kvp.Key}] both _symbolNavigationEngine. Resolve to the same target in '{filePath}'. Split these into separate calls.");
            }
            else
            {
                seen[kvp.Value] = kvp.Key;
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var edit in edits)
        {
            var target = resolvedTargets[edit.Index];
            var kind = SyntaxFacts.GetKeywordKind(edit.Modifier);
            if (kind == SyntaxKind.None)
            {
                kind = SyntaxFacts.GetContextualKeywordKind(edit.Modifier);
            }

            if (edit.Action == AddRemoveAction.add)
            {
                if (target.Modifiers.Any(m => m.IsKind(kind)))
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): modifier already exists.");
                    continue;
                }
                var token = SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space);
                replacements[target] = target.WithModifiers(target.Modifiers.Add(token));
            }
            else
            {
                if (!target.Modifiers.Any(m => m.IsKind(kind)))
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): modifier not found.");
                    continue;
                }
                replacements[target] = target.WithModifiers(SyntaxFactory.TokenList(target.Modifiers.Where(m => !m.IsKind(kind))));
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var replaced = await RoslynFormattingHelper.ReplaceNodesFormattedAsync(document, root, replacements, cancellationToken);
        if (!replaced.AllReplacementsApplied)
        {
            // A replacement whose target could not be located in the evolving tree used to be skipped silently, so the
            // tool reported success for an edit that was never written. Name each affected edit and refuse to write.
            var unlocated = edits
                .Where(e => resolvedTargets.TryGetValue(e.Index, out var node) && replaced.UnlocatedNodes.Contains(node))
                .Select(e => $"edits[{e.Index}] ({e.TargetName}): the target could not be located after other edits in this batch changed the tree (it is nested inside, or overlaps, another edit's target in '{filePath}'). Split these edits into separate calls.")
                .ToList();
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", unlocated.Count > 0 ? unlocated : [$"'{filePath}': {replaced.UnlocatedNodes.Count} edit(s) could not be applied because their targets could not be located. Split these edits into separate calls."]) };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = replaced.Text
        };
    }

    /// <summary>
    /// ModifyModifier add static for methods and properties: makes each requested member static and rewrites every
    /// instance-qualified caller (<c>receiver.M(...)</c> becomes <c>Type.M(...)</c>) in ONE change set made of minimal
    /// TextChanges against each document's original text. Refuses, naming each offending use, when a member still uses
    /// instance state (this/base, instance fields, properties, methods or events of its type, including through an
    /// implicit this); a member that only uses other members converted in the same call is fine. Requests the
    /// conversion does not apply to (targets that are not methods or properties, or are already static, or do not
    /// resolve) are left out of <see cref="StaticConversionResult.HandledIndexes"/> for the plain modifier path to report.
    /// </summary>
    public async Task<StaticConversionResult> ConvertMembersToStaticAsync(IReadOnlyList<StaticConversionRequest> requests, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = new List<(StaticConversionRequest Request, Document Document, SemanticModel Model, MemberDeclarationSyntax Declaration, ISymbol Symbol)>();
        foreach (var request in requests)
        {
            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == request.FilePath || d.FilePath == request.FilePath);
            var root = document == null ? null : await document.GetSyntaxRootAsync(cancellationToken);
            var sourceText = document == null ? null : await document.GetTextAsync(cancellationToken);
            var model = document == null ? null : await document.GetSemanticModelAsync(cancellationToken);
            if (document == null || root == null || sourceText == null || model == null)
            {
                continue;
            }

            MemberDeclarationSyntax? declaration;
            try
            {
                var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, request.TargetName, cancellationToken).Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
                declaration = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, request.ContextSnippet, request.LineBefore, request.LineAfter, (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
            }
            catch (InvalidOperationException)
            {
                // Not resolvable here: the plain modifier path reports the real resolution error for this request.
                continue;
            }

            if (declaration is not (MethodDeclarationSyntax or PropertyDeclarationSyntax) || declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            {
                continue;
            }

            var symbol = model.GetDeclaredSymbol(declaration, cancellationToken);
            if (symbol != null)
            {
                targets.Add((request, document, model, declaration, symbol));
            }
        }

        var handled = new HashSet<int>(targets.Select(t => t.Request.Index));
        if (targets.Count == 0)
        {
            return new StaticConversionResult(handled, new Dictionary<FilePathWrapper, string>(), new List<string>());
        }

        var duplicate = targets.GroupBy(t => t.Symbol, SymbolEqualityComparer.Default).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ToolInvalidArgumentException($"edits [{string.Join(", ", duplicate.Select(t => t.Request.Index))}] all resolve to '{duplicate.Key!.ContainingType?.Name}.{duplicate.Key.Name}'. List each member once.");
        }

        var refusals = new List<string>();
        foreach (var target in targets)
        {
            var reason = StaticConversionEdits.GetIneligibilityReason(target.Declaration, target.Symbol);
            if (reason != null)
            {
                refusals.Add($"'{target.Symbol.ContainingType?.Name}.{target.Symbol.Name}': {reason}");
            }
        }

        if (refusals.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot make the member(s) static - {string.Join("; ", refusals)}.");
        }

        var converted = new HashSet<ISymbol>(targets.Select(t => t.Symbol.OriginalDefinition), SymbolEqualityComparer.Default);
        var stateRefusals = new List<string>();
        foreach (var target in targets)
        {
            var uses = StaticConversionEdits.FindInstanceStateUses(target.Declaration, target.Model, target.Symbol.ContainingType!, converted, cancellationToken);
            if (uses.Count > 0)
            {
                stateRefusals.Add($"'{target.Symbol.ContainingType!.Name}.{target.Symbol.Name}' uses instance state: {string.Join(", ", uses.Select(u => u.ToString()))}");
            }
        }

        if (stateRefusals.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot make the member(s) static - {string.Join("; ", stateRefusals)}. A static member cannot read instance state: pass what it needs in as parameters, or add the members it uses to the same ModifyModifier call if they are themselves stateless.");
        }

        var collector = new StaticConversionReferenceCollector(solution);
        foreach (var target in targets)
        {
            await collector.AddReferencesAsync(target.Symbol, target.Symbol.ContainingType!, skipAllBareReferences: true, isInsideMovedMember: null, cancellationToken);
        }

        if (collector.Problems.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot rewrite every caller of the member(s) being made static - {string.Join("; ", collector.Problems)}.");
        }

        await collector.FinalizeAsync(cancellationToken);
        foreach (var target in targets)
        {
            collector.AddEdit(target.Document.Id, StaticConversionEdits.BuildStaticModifierEdit(target.Declaration));
        }

        var changes = await MaterializeDocumentEditsAsync(solution, collector.EditsByDocument, new Dictionary<DocumentId, SourceText>(), cancellationToken);
        return new StaticConversionResult(handled, changes, collector.Notes);
    }

    /// <summary>
    /// Lists a method's parameters. methodName is resolved via SyntaxTargetResolver.ResolveCandidates, so
    /// contextSnippet/lineBefore/lineAfter disambiguate overloads the same way every other
    /// member-targeting tool does.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, List<MethodParameterInfo> Parameters)> GetMethodParametersAsync(FilePathWrapper filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
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

        MemberDeclarationSyntax? memberNode;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, methodName, cancellationToken)
                .Where(c => c.Kind == CandidateKind.Method).ToList());
            memberNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return (EditOutcome.CannotEdit, ex.Message, []);
        }

        if (memberNode is not MethodDeclarationSyntax methodDecl)
        {
            return (EditOutcome.TargetNotFound, $"// Method '{methodName}' not found.", []);
        }

        var result = methodDecl.ParameterList.Parameters.Select(p => new MethodParameterInfo(p.Identifier.Text, p.Type?.ToString() ?? "", p.Default?.Value.ToString())).ToList();
        return (EditOutcome.Modified, null, result);
    }

    /// <summary>
    /// Appends a parameter to a method's parameter list. The new parameter always goes last ->
    /// this keeps every existing positional call site valid without rewriting it, so unlike
    /// RemoveMethodParameterAsync there is no call-site safety analysis to do: an added parameter
    /// with no default is required at every call site (an intentional break the caller opted
    /// into, same as ConstructorParameter's add), while an added parameter with a default is
    /// backward-compatible with zero call-site changes.
    ///
    /// nullDefault bypasses defaultValue entirely and forces the default straight to a null
    /// literal -> see anthropics/claude-code#81911: some MCP clients serialize the literal string
    /// "null" as an actual JSON null before it ever reaches this method, so defaultValue:"null"
    /// can silently arrive here as a C# null (the same as "no default"), producing a required
    /// parameter instead of one defaulted to null. nullDefault:true sidesteps that entirely since
    /// it never depends on a string surviving the trip.
    /// </summary>
    public async Task<DocumentEditResult> AddMethodParameterAsync(FilePathWrapper filePath, string methodName, string paramName, string paramType, string? defaultValue = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default, bool nullDefault = false)
    {
        if (nullDefault && defaultValue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: nullDefault and defaultValue are mutually exclusive - pass only one."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        MemberDeclarationSyntax? memberNode;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, methodName, cancellationToken)
                .Where(c => c.Kind == CandidateKind.Method).ToList());
            memberNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (memberNode is not MethodDeclarationSyntax methodDecl)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Cannot edit: method '{methodName}' not found."
            };
        }

        if (methodDecl.ParameterList.Parameters.Any(p => p.Identifier.Text == paramName))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: '{methodName}' already has a parameter named '{paramName}'."
            };
        }

        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(paramType).WithTrailingTrivia(SyntaxFactory.Space));
        if (nullDefault)
        {
            newParam = newParam.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
        }
        else if (defaultValue != null)
        {
            newParam = newParam.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(defaultValue)));
        }

        var newParams = methodDecl.ParameterList.Parameters.Count == 0 ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList([newParam])) : methodDecl.ParameterList.AddParameters(newParam);
        var newMethodDecl = methodDecl.WithParameterList(newParams);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, methodDecl, newMethodDecl, cancellationToken),
            Message = $"// paramName='{paramName}'"
        };
    }

    /// <summary>
    /// Removes a method's last parameter and drops the matching trailing argument from every
    /// simple-positional call site so ValidateAndApplyAsync compiles the real post-removal state
    /// (a declaration-only edit would let a still-passing caller slip past validation, since
    /// validation only recompiles files present in the changeset). Restricted to the last
    /// parameter deliberately: removing any other position would require reordering every
    /// remaining positional argument at every call site, which reintroduces the same
    /// named-argument/params-expansion ambiguity ChangeSignatureAsync already has to skip around ->
    /// here a skip can't be tolerated (see above), so those cases are refused outright instead of
    /// silently left broken.
    /// </summary>
    public async Task<DocumentEditResult> RemoveMethodParameterAsync(FilePathWrapper filePath, string methodName, string paramName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var sourceText = await document.GetTextAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || sourceText == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root or semantic model not found."
            };
        }

        MemberDeclarationSyntax? memberNode;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, methodName, cancellationToken)
                .Where(c => c.Kind == CandidateKind.Method).ToList());
            memberNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (memberNode is not MethodDeclarationSyntax methodDecl)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Cannot edit: method '{methodName}' not found."
            };
        }

        var parameters = methodDecl.ParameterList.Parameters;
        if (parameters.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Cannot edit: '{methodName}' has no parameters."
            };
        }

        var lastParam = parameters[^1];
        if (lastParam.Identifier.Text != paramName)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotRemove,
                FilePath = filePath,
                Message = $"// Cannot remove '{paramName}': MethodSignature(remove) only supports the last parameter (currently '{lastParam.Identifier.Text}') - removing an earlier parameter would require reordering every call site's remaining positional arguments, which cannot always be done safely."
            };
        }

        var targetParamCount = parameters.Count;
        var newParams = methodDecl.ParameterList.WithParameters(SyntaxFactory.SeparatedList(parameters.Take(targetParamCount - 1)));
        var newMethodDecl = methodDecl.WithParameterList(newParams);
        var pendingChanges = new Dictionary<FilePathWrapper, string>
        {
            [filePath] = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, methodDecl, newMethodDecl, cancellationToken)
        };

        var symbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (symbol != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    if (location.IsImplicit || location.Document?.FilePath == null)
                    {
                        continue;
                    }

                    var refDoc = location.Document;
                    var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                    if (refRoot == null)
                    {
                        continue;
                    }

                    var span = location.Location.SourceSpan;
                    var refLineNumber = refRoot.SyntaxTree.GetLineSpan(span, cancellationToken: cancellationToken).StartLinePosition.Line + 1;
                    var token = refRoot.FindToken(span.Start);
                    var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation == null)
                    {
                        return new DocumentEditResult
                        {
                            Outcome = EditOutcome.CannotRemove,
                            FilePath = filePath,
                            Message = $"// Cannot remove '{paramName}': call site at {refDoc.FilePath}:{refLineNumber} is not a simple invocation expression (e.g. method group or delegate conversion) - cannot safely update it."
                        };
                    }

                    var args = invocation.ArgumentList.Arguments;
                    if (args.Any(a => a.NameColon != null))
                    {
                        return new DocumentEditResult
                        {
                            Outcome = EditOutcome.CannotRemove,
                            FilePath = filePath,
                            Message = $"// Cannot remove '{paramName}': call site at {refDoc.FilePath}:{refLineNumber} uses named arguments - cannot safely update it."
                        };
                    }

                    if (args.Count != targetParamCount)
                    {
                        // Caller already omits this parameter (relying on its default) or the
                        // invocation doesn't reach it (e.g. params expansion) -> either way there's
                        // no trailing argument here that corresponds to the removed parameter, so
                        // this call site needs no edit and stays valid as-is.
                        continue;
                    }

                    var docPath = refDoc.FilePath!;
                    string currentContent = pendingChanges.TryGetValue(docPath, out var prev) ? prev : (await refDoc.GetTextAsync(cancellationToken)).ToString();
                    var currentRoot = SyntaxFactory.ParseCompilationUnit(currentContent);
                    var targetInv = currentRoot.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(inv => inv.Span == invocation.Span);
                    if (targetInv == null)
                    {
                        return new DocumentEditResult
                        {
                            Outcome = EditOutcome.CannotRemove,
                            FilePath = filePath,
                            Message = $"// Cannot remove '{paramName}': call site at {refDoc.FilePath}:{refLineNumber} could not be re-located after an earlier edit to the same file - cannot safely update it."
                        };
                    }

                    var newArgList = targetInv.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(targetInv.ArgumentList.Arguments.Take(targetParamCount - 1)));
                    var updatedInv = targetInv.WithArgumentList(newArgList);
                    pendingChanges[docPath] = currentRoot.ReplaceNode(targetInv, updatedInv).ToFullString();
                }
            }
        }

        // Format every touched file (the declaration was already formatted via
        // ReplaceNodeFormattedAsync above; call-site files were edited via raw ReplaceNode/
        // ToFullString and still need it).
        var result = new Dictionary<FilePathWrapper, string>();
        foreach (var kvp in pendingChanges)
        {
            if (kvp.Key == filePath)
            {
                result[kvp.Key] = kvp.Value;
                continue;
            }

            var doc = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.FilePath == kvp.Key);
            if (doc != null)
            {
                var formatted = await Formatter.FormatAsync(doc.WithSyntaxRoot(SyntaxFactory.ParseCompilationUnit(kvp.Value)), null, cancellationToken);
                result[kvp.Key] = (await formatted.GetTextAsync(cancellationToken)).ToString();
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = result[filePath],
            Changes = result,
            Message = $"// paramName='{paramName}', callSitesUpdated='{result.Count - 1}'"
        };
    }

    /// <summary>
    /// Batch form of <see cref="AddAttributeAsync"/>/<see cref="ReplaceAttributeAsync"/>/
    /// <see cref="RemoveAttributeAsync"/>: same execution model as <see cref="ApplyModifierBatchAsync"/> ->
    /// _symbolNavigationEngine. Resolve every edit's target against ONE original root, reject same-node collisions, fold all
    /// replacements into one <see cref="RoslynFormattingHelper.ReplaceNodesFormattedAsync"/> call.
    /// </summary>
    public async Task<DocumentEditResult> ApplyAttributeBatchAsync(FilePathWrapper filePath, IReadOnlyList<(int Index, string TargetName, string ExistingAttribute, AttributeModifyAction Action, string? NewAttribute, string? ContextSnippet, string? LineBefore, string? LineAfter)> edits, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, FilePath = filePath, Message = "// Document not found." };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = "// Cannot edit: syntax root not found." };
        }

        var errors = new List<string>();
        var resolvedTargets = new Dictionary<int, SyntaxNode>();
        foreach (var edit in edits)
        {
            try
            {
                var allCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, edit.TargetName, cancellationToken);
                var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(allCandidates.Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
                var memberTarget = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, edit.ContextSnippet, edit.LineBefore, edit.LineAfter,
                    (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
                SyntaxNode? targetNode = memberTarget;
                if (targetNode == null)
                {
                    var typeCandidates = allCandidates
                        .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                        .ToList();
                    targetNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, edit.ContextSnippet, edit.LineBefore, edit.LineAfter,
                        (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
                }

                if (targetNode == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): target not found.");
                    continue;
                }
                resolvedTargets[edit.Index] = targetNode;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"edits[{edit.Index}] ({edit.TargetName}): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        // Same-node collision: two edits on the identical declaration. A type and a member inside it are NOT a collision
        // any more - text-span edits compose across ancestor/descendant targets.
        var seen = new Dictionary<SyntaxNode, int>();
        foreach (var kvp in resolvedTargets)
        {
            if (seen.TryGetValue(kvp.Value, out var firstIndex))
            {
                errors.Add($"edits[{firstIndex}] and edits[{kvp.Key}] resolve to the same target in '{filePath}'. Split these into separate calls.");
            }
            else
            {
                seen[kvp.Value] = kvp.Key;
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var eol = EolUtilities.DetectDominantEol(sourceText);
        var textEdits = new List<AttributeTextEditBuilder.TextEdit>();
        var appliedIndexes = new List<int>();
        foreach (var edit in edits)
        {
            var targetNode = resolvedTargets[edit.Index];
            var attrLists = targetNode is MemberDeclarationSyntax memberForRead ? memberForRead.AttributeLists : default;

            if (edit.Action == AttributeModifyAction.add)
            {
                var attrList = AttributeTextEditBuilder.ParseAttributeList(edit.ExistingAttribute);
                if (attrList == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): invalid attribute source.");
                    continue;
                }
                textEdits.Add(AttributeTextEditBuilder.BuildAddEdit(edit.Index, targetNode, attrList, sourceText, eol));
                appliedIndexes.Add(edit.Index);
            }
            else if (edit.Action == AttributeModifyAction.replace)
            {
                if (string.IsNullOrEmpty(edit.NewAttribute))
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): newAttribute is required for action 'replace'.");
                    continue;
                }
                var newAttrList = AttributeTextEditBuilder.ParseAttributeList(edit.NewAttribute);
                if (newAttrList == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): invalid new attribute source.");
                    continue;
                }
                var oldAttr = attrLists.SelectMany(al => al.Attributes).FirstOrDefault(a => GetAttributeName(a) == edit.ExistingAttribute);
                if (oldAttr == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): attribute '{edit.ExistingAttribute}' not found on target.");
                    continue;
                }
                textEdits.Add(AttributeTextEditBuilder.BuildReplaceEdit(edit.Index, oldAttr, newAttrList.Attributes.First()));
                appliedIndexes.Add(edit.Index);
            }
            else
            {
                var attrCore = edit.ExistingAttribute.EndsWith("Attribute") ? edit.ExistingAttribute[..^9] : edit.ExistingAttribute;
                bool AttrMatches(AttributeSyntax a)
                {
                    var name = a.Name.ToString();
                    return name == edit.ExistingAttribute || name == attrCore || name == attrCore + "Attribute";
                }
                if (targetNode is not MemberDeclarationSyntax memberTarget2)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TargetName}): action 'remove' requires a declaration target.");
                    continue;
                }
                var removeEdits = AttributeTextEditBuilder.BuildRemoveEdits(edit.Index, memberTarget2, AttrMatches, sourceText);
                if (removeEdits.Count > 0)
                {
                    textEdits.AddRange(removeEdits);
                    appliedIndexes.Add(edit.Index);
                }
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var updatedText = AttributeTextEditBuilder.TryApply(sourceText, textEdits, out var applyError);
        if (updatedText == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = applyError ?? "Attribute edits could not be applied." };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText,
            AppliedEditIndexes = appliedIndexes
        };
    }

    public async Task<DocumentEditResult> ChangeAccessibilityAsync(FilePathWrapper filePath, string targetName, AccessibilityLevel accessibility, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        MemberDeclarationSyntax? target = null;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            target = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        SyntaxKind[] newKinds = accessibility switch
        {
            AccessibilityLevel.@public => [SyntaxKind.PublicKeyword],
            AccessibilityLevel.@private => [SyntaxKind.PrivateKeyword],
            AccessibilityLevel.@internal => [SyntaxKind.InternalKeyword],
            AccessibilityLevel.@protected => [SyntaxKind.ProtectedKeyword],
            AccessibilityLevel.protectedInternal => [SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword],
            AccessibilityLevel.privateProtected => [SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword],
            _ => throw new ArgumentOutOfRangeException(nameof(accessibility), accessibility, null)
        };
        var accessModifierKinds = new HashSet<SyntaxKind>
        {
            SyntaxKind.PublicKeyword,
            SyntaxKind.PrivateKeyword,
            SyntaxKind.InternalKeyword,
            SyntaxKind.ProtectedKeyword
        };
        var remaining = target.Modifiers.Where(m => !accessModifierKinds.Contains(m.Kind())).ToList();
        var newTokens = newKinds.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space));
        var newModifiers = SyntaxFactory.TokenList(newTokens.Concat(remaining));
        var updatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, target, target.WithModifiers(newModifiers), cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText
        };
    }

    public async Task<DocumentEditResult> AddModifierAsync(FilePathWrapper filePath, string targetName, string modifier, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        MemberDeclarationSyntax? target = null;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            target = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None)
        {
            kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        }

        if (target.Modifiers.Any(m => m.IsKind(kind)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: modifier already exists.",
                UpdatedText = root.ToFullString()
            };
        }

        var token = SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space);
        var newModifiers = target.Modifiers.Add(token);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, target, target.WithModifiers(newModifiers), cancellationToken)
        };
    }

    public async Task<DocumentEditResult> RemoveModifierAsync(FilePathWrapper filePath, string targetName, string modifier, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        MemberDeclarationSyntax? target = null;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            target = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None)
        {
            kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        }

        if (!target.Modifiers.Any(m => m.IsKind(kind)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: modifier not found.",
                UpdatedText = root.ToFullString()
            };
        }

        var newModifiers = SyntaxFactory.TokenList(target.Modifiers.Where(m => !m.IsKind(kind)));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, target, target.WithModifiers(newModifiers), cancellationToken)
        };
    }

    public async Task<DocumentEditResult> AddAttributeAsync(FilePathWrapper filePath, string targetName, string attributeSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid member source."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var normalizedSource = attributeSource.Trim();
        if (!normalizedSource.StartsWith("["))
        {
            normalizedSource = $"[{normalizedSource}]";
        }

        // Parse by embedding in a dummy class declaration
        var snippet = SyntaxFactory.ParseCompilationUnit($"{normalizedSource}\npublic class __Dummy__ {{}}");
        var attrList = snippet.DescendantNodes().OfType<AttributeListSyntax>().FirstOrDefault();
        if (attrList == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid attribute source."
            };
        }

        // Try member first, then type declaration
        SyntaxNode? targetNode = null;
        try
        {
            var allCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken);
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(allCandidates.Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            var memberNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
            if (memberNode != null)
            {
                targetNode = memberNode;
            }
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (targetNode == null)
        {
            try
            {
                var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken)
                    .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                    .ToList();
                var typeNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                    (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
                if (typeNode != null)
                {
                    targetNode = typeNode;
                }
            }
            catch (InvalidOperationException ex)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = ex.Message
                };
            }
        }

        if (targetNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        // Insert only the attribute list as a text edit: replacing the target node and formatting it would re-format
        // every member of a type that merely needed one attribute line (see AttributeTextEditBuilder).
        var addEdit = AttributeTextEditBuilder.BuildAddEdit(0, targetNode, attrList, sourceText, EolUtilities.DetectDominantEol(sourceText));
        var updatedText = AttributeTextEditBuilder.TryApply(sourceText, [addEdit], out var applyError);
        if (updatedText == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = applyError ?? "Attribute edit could not be applied." };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText
        };
    }

    public async Task<DocumentEditResult> ReplaceAttributeAsync(FilePathWrapper filePath, string targetName, string oldAttributeName, string newAttributeSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid syntax root."
            };
        }

        // Parse new attribute
        var normalizedNew = newAttributeSource.Trim();
        if (!normalizedNew.StartsWith("["))
        {
            normalizedNew = $"[{normalizedNew}]";
        }

        var snippet = SyntaxFactory.ParseCompilationUnit($"{normalizedNew}\npublic class __Dummy__ {{}}");
        var newAttrList = snippet.DescendantNodes().OfType<AttributeListSyntax>().FirstOrDefault();
        if (newAttrList == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid new attribute source."
            };
        }

        var newAttr = newAttrList.Attributes.First();
        // Locate target node (member first, then type)
        SyntaxNode? targetNode = null;
        try
        {
            var allCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken);
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(allCandidates.Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            var memberTarget = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
            if (memberTarget != null)
            {
                targetNode = memberTarget;
            }
            else
            {
                var typeCandidates = allCandidates
                    .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                    .ToList();
                targetNode = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                    (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
            }
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (targetNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        // Find the attribute by name within the target's attribute lists
        var attrLists = targetNode is MemberDeclarationSyntax m2 ? m2.AttributeLists : ((BaseTypeDeclarationSyntax)targetNode).AttributeLists;
        AttributeSyntax? oldAttr = attrLists.SelectMany(al => al.Attributes).FirstOrDefault(a => GetAttributeName(a) == oldAttributeName);
        if (oldAttr == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: attribute '{oldAttributeName}' not found on target."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, oldAttr, newAttr, cancellationToken)
        };
    }

    private static string GetAttributeName(AttributeSyntax attr)
    {
        return attr.Name switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            _ => attr.Name.ToString()
        };
    }

    public async Task<DocumentEditResult> RemoveAttributeAsync(FilePathWrapper filePath, string targetName, string attributeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var attrCore = attributeName.EndsWith("Attribute") ? attributeName[..^9] : attributeName;
        bool AttrMatches(AttributeSyntax a)
        {
            var name = a.Name.ToString();
            return name == attributeName || name == attrCore || name == attrCore + "Attribute";
        }

        MemberDeclarationSyntax? memberTarget = null;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, targetName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)).ToList());
            memberTarget = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as MemberDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (memberTarget == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: target not found."
            };
        }

        var newAttrLists = memberTarget.AttributeLists.Select(al => al.WithAttributes(SyntaxFactory.SeparatedList(al.Attributes.Where(a => !AttrMatches(a))))).Where(al => al.Attributes.Count > 0).ToList();
        var newMember = memberTarget.WithAttributeLists(SyntaxFactory.List(newAttrLists));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, memberTarget, newMember, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> InsertMemberAfterAsync(FilePathWrapper filePath, string containerName, string afterMemberName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        BaseTypeDeclarationSyntax? container = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, containerName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = SyntaxTargetResolver.BuildContainerNotFoundMessage(root, containerName)
            };
        }

        if (container is not TypeDeclarationSyntax typeDecl)
        {
            // Fallback: append (also covers enum containers, which AddMemberAsync rejects loudly
            // rather than silently no-op'ing - see docs/current/issue_member_add_silent_persistence.md).
            return await AddMemberAsync(filePath, containerName, newMemberSource, null, null, null, cancellationToken);
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid member source."
            };
        }

        var trailingIssue = DetectMultipleMemberDeclarations(newMemberSource, "addMember")
            ?? DetectTrailingUnparsedDeclaration(newMemberSource, newMember);
        if (trailingIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// newMemberSource is invalid: {trailingIssue}"
            };
        }

        var insertedDescription = DescribeParsedMember(newMember);
        //newMember = newMember.WithAddedByComment("InsertMemberAfter");
        var membersList = typeDecl.Members.ToList();
        var idx = membersList.FindIndex(m => SyntaxTargetResolver.GetMemberName(m) == afterMemberName);
        var insertIndex = idx < 0 ? membersList.Count : idx + 1;

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Added {insertedDescription}.",
            UpdatedText = await RoslynFormattingHelper.InsertMemberFormattedAsync(document, root!, typeDecl, insertIndex, newMember, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> InsertMemberBeforeAsync(FilePathWrapper filePath, string containerName, string beforeMemberName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        BaseTypeDeclarationSyntax? container = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, containerName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = SyntaxTargetResolver.BuildContainerNotFoundMessage(root, containerName)
            };
        }

        if (container is not TypeDeclarationSyntax typeDecl)
        {
            // Fallback: append (also covers enum containers, which AddMemberAsync rejects loudly
            // rather than silently no-op'ing - see docs/current/issue_member_add_silent_persistence.md).
            return await AddMemberAsync(filePath, containerName, newMemberSource, null, null, null, cancellationToken);
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: invalid member source."
            };
        }

        var trailingIssue = DetectMultipleMemberDeclarations(newMemberSource, "addMember")
            ?? DetectTrailingUnparsedDeclaration(newMemberSource, newMember);
        if (trailingIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// newMemberSource is invalid: {trailingIssue}"
            };
        }

        var insertedDescription = DescribeParsedMember(newMember);
        //newMember = newMember.WithAddedByComment("InsertMemberBefore");
        var membersList = typeDecl.Members.ToList();
        var idx = membersList.FindIndex(m => SyntaxTargetResolver.GetMemberName(m) == beforeMemberName);
        var insertIndex = idx < 0 ? membersList.Count : idx;

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Added {insertedDescription}.",
            UpdatedText = await RoslynFormattingHelper.InsertMemberFormattedAsync(document, root!, typeDecl, insertIndex, newMember, cancellationToken)
        };
    }

    private static string DescribeParsedMember(MemberDeclarationSyntax member)
    {
        var kind = member.Kind().ToString().Replace("Declaration", string.Empty);
        var names = member switch
        {
            BaseTypeDeclarationSyntax type => new[] { type.Identifier.Text },
            MethodDeclarationSyntax method => new[] { method.Identifier.Text },
            ConstructorDeclarationSyntax ctor => new[] { ctor.Identifier.Text },
            PropertyDeclarationSyntax property => new[] { property.Identifier.Text },
            EventDeclarationSyntax evt => new[] { evt.Identifier.Text },
            FieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Select(v => v.Identifier.Text).ToArray(),
            _ => Array.Empty<string>()
        };

        return names.Length == 0
            ? kind
            : $"{kind} '{string.Join("', '", names)}'";
    }

    /// <summary>
    /// Detects a newMemberSource that holds 2+ complete member declarations and returns a specific,
    /// recovery-oriented message (count, each member's kind+name, and the per-operation fix), or null
    /// when the source is not a multi-member block. Needed because the single-member parse
    /// (SyntaxFactory.ParseMemberDeclaration) only sees the first declaration: for replace, the
    /// trailing declarations surfaced as a generic "not a valid member declaration" error that sent
    /// agents off rewriting a perfectly valid member instead of splitting the call. The source is
    /// parsed as the body of a throwaway class so every declaration is counted; any
    /// IncompleteMemberSyntax (a statement or body fragment, not a member) makes this return null so
    /// those inputs keep their existing "not a valid member" diagnosis.
    /// </summary>
    /// <param name="source">The caller-supplied newMemberSource.</param>
    /// <param name="operation">"replace" or "addMember" - selects the recovery advice.</param>
    /// <param name="targetMemberName">replace only: the member being replaced.</param>
    internal static string? DetectMultipleMemberDeclarations(string source, string operation, string? targetMemberName = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var probe = SyntaxFactory.ParseCompilationUnit("class __SentinelMemberCountProbe__\n{\n" + source + "\n}\n");
        if (probe.Members.Count != 1 || probe.Members[0] is not ClassDeclarationSyntax probeClass)
        {
            return null;
        }

        var members = probeClass.Members;
        if (members.Count < 2 || members.Any(m => m is IncompleteMemberSyntax))
        {
            return null;
        }

        var described = string.Join(", ", members.Select(DescribeParsedMember));
        if (operation == "replace")
        {
            var target = string.IsNullOrEmpty(targetMemberName) ? "the target member" : $"'{targetMemberName}'";
            return $"newMemberSource contains {members.Count} member declarations ({described}), but replace takes exactly one member - the single declaration that replaces {target}. " +
                "Recovery: call Member(operation: replace) once per existing member you want to replace, passing just that member's full declaration, " +
                "and call Member(operation: addMember, containerName: ..., position: \"after:<MemberName>\") once for each member that is new.";
        }

        return $"newMemberSource contains {members.Count} member declarations ({described}), but {operation} takes exactly one member per call. " +
            $"Recovery: call Member(operation: {operation}) once per member, passing one full declaration each time; to keep them in order, give each call after the first position: \"after:<previous member's name>\".";
    }

    public async Task<DocumentEditResult> AddFieldAsync(FilePathWrapper filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var parts = new System.Text.StringBuilder();
        parts.Append(accessibility);
        if (isStatic)
        {
            parts.Append(" static");
        }

        if (isReadonly)
        {
            parts.Append(" readonly");
        }

        parts.Append($" {fieldType} {fieldName}");
        if (initializer != null)
        {
            parts.Append($" = {initializer}");
        }

        parts.Append(';');
        return await AddMemberAsync(filePath, containerName, parts.ToString(), contextSnippet, lineBefore, lineAfter, cancellationToken);
    }

    public async Task<DocumentEditResult> SortMembersAsync(FilePathWrapper filePath, string containerName, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: container not found."
            };
        }

        static int CategoryOf(MemberDeclarationSyntax m) => m switch
        {
            FieldDeclarationSyntax => 0,
            ConstructorDeclarationSyntax => 1,
            DestructorDeclarationSyntax => 2,
            PropertyDeclarationSyntax => 3,
            IndexerDeclarationSyntax => 4,
            EventDeclarationSyntax or EventFieldDeclarationSyntax => 5,
            MethodDeclarationSyntax => 6,
            OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => 7,
            ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax or InterfaceDeclarationSyntax or EnumDeclarationSyntax => 8,
            _ => 9
        };
        static bool IsStatic(MemberDeclarationSyntax m) => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword));
        var sorted = container.Members.OrderBy(CategoryOf).ThenBy(m => IsStatic(m) ? 0 : 1).ThenBy(m => SyntaxTargetResolver.GetMemberName(m) ?? "").ToList();
        var newContainer = container.WithMembers(SyntaxFactory.List(sorted));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, container, newContainer, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> AddPropertyAsync(FilePathWrapper filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var setter = hasSetter ? (isInit ? " init;" : " set;") : "";
        var source = $"{accessibility} {propertyType} {propertyName} {{ get;{setter} }}";
        return await AddMemberAsync(filePath, containerName, source, contextSnippet, lineBefore, lineAfter, cancellationToken);
    }

    public async Task<DocumentEditResult> AddRemoveParamsAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        if (!(_config ?? new SentinelConfiguration()).IsFeatureEnabled("AddRemoveParams"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature is disabled."
            };
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || !method.ParameterList.Parameters.Any())
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Method not found or has no parameters."
            };
        }

        var lastParam = method.ParameterList.Parameters.Last();
        var hasParams = lastParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));
        var newModifiers = hasParams ? lastParam.Modifiers.Remove(lastParam.Modifiers.First(m => m.IsKind(SyntaxKind.ParamsKeyword))) : lastParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ParamsKeyword));
        var newParam = lastParam.WithModifiers(newModifiers);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Params keyword toggled.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, lastParam, newParam, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ReplaceMemberAsync(FilePathWrapper filePath, string memberName, string newSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containerName = null, CancellationToken cancellationToken = default)
    {
        // excludeInterfaceMembers: false -- replace must be able to target an interface's own
        // member declaration (e.g. ISolutionProvider.CurrentSolution), not just implementers.
        // See docs/current/blockers/blocking_error_member_replace_interface_notfound.md.
        // PreferNonInterfaceMember still runs unconditionally below: the old resolver's
        // excludeInterfaceMembers only gated whether interface members entered the candidate pool
        // at all, not whether the implementer-preference narrowing ran once both were present.
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        SyntaxNode? member;
        try
        {
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(SyntaxTargetResolver.ResolveCandidates(root, sourceText, memberName, cancellationToken)
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember))
                .ToList());
            memberCandidates = SyntaxTargetResolver.FilterByContainingType(memberCandidates, containerName);
            member = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (member == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Member not found."
            };
        }

        // Must run before the ContainsDiagnostics check below: a multi-member source always has
        // parse diagnostics (the 2nd declaration is skipped-token trivia on the 1st), so checking
        // diagnostics first misreported it as "not a valid member declaration".
        var multipleMembersIssue = DetectMultipleMemberDeclarations(newSource, "replace", memberName);
        if (multipleMembersIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// {multipleMembersIssue}"
            };
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newSource);
        if (newMember == null || newMember.ContainsDiagnostics)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// newMemberSource is not a valid member declaration. Provide the full member (signature + body, e.g. 'private decimal Foo() { ... }'), " +
                    "not just a statement or method body fragment."
            };
        }

        var trailingIssue = DetectTrailingUnparsedDeclaration(newSource, newMember);
        if (trailingIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// newMemberSource is invalid: {trailingIssue}"
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Replaced with {DescribeParsedMember(newMember)}.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, member, newMember, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> AddMemberAsync(FilePathWrapper filePath, string containerName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Cannot parse file."
            };
        }

        BaseTypeDeclarationSyntax? container = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, containerName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = SyntaxTargetResolver.BuildContainerNotFoundMessage(root, containerName)
            };
        }

        if (container is EnumDeclarationSyntax)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot add a member to enum \"{containerName}\" via AddMember: enum members " +
                          "are not MemberDeclarationSyntax and use comma-separated syntax, not this method's " +
                          "member grammar. Use ModifyEnumAsync instead."
            };
        }

        if (container is not TypeDeclarationSyntax typeContainer)
        {
            throw new NotSupportedException(
                $"AddMemberAsync: unhandled container type {container.GetType().Name} for \"{containerName}\". " +
                "This is a bug - every BaseTypeDeclarationSyntax subtype must _symbolNavigationEngine. Resolve to a TypeDeclarationSyntax here; " +
                "silently returning the container unchanged would falsely report success.");
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Failed to parse new member."
            };
        }

        var trailingIssue = DetectMultipleMemberDeclarations(newMemberSource, "addMember")
            ?? DetectTrailingUnparsedDeclaration(newMemberSource, newMember);
        if (trailingIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// newMemberSource is invalid: {trailingIssue}"
            };
        }

        var addedDescription = DescribeParsedMember(newMember);
        //newMember = newMember.WithAddedByComment("AddMember");
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Added {addedDescription}.",
            UpdatedText = await RoslynFormattingHelper.InsertMemberFormattedAsync(document, root!, typeContainer, typeContainer.Members.Count, newMember, cancellationToken)
        };
    }

    /// <summary>
    /// Adds a brand-new top-level type declaration (enum/class/record/struct/interface) to a file,
    /// for the case AddMemberAsync can't handle: there is no existing BaseTypeDeclarationSyntax to
    /// target because the type being added doesn't exist yet. _symbolNavigationEngine. Resolves to the file's namespace
    /// (NamespaceDeclarationSyntax or FileScopedNamespaceDeclarationSyntax) when namespaceName is
    /// null and exactly one namespace is present, or to the CompilationUnitSyntax itself for a file
    /// with no namespace (global namespace). If the file has multiple namespaces and namespaceName
    /// wasn't given, that's ambiguous and reported as such rather than guessed.
    /// </summary>
    public async Task<DocumentEditResult> AddTopLevelTypeAsync(FilePathWrapper filePath, string newTypeSource, string? namespaceName = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Cannot parse file."
            };
        }

        var newType = SyntaxFactory.ParseMemberDeclaration(newTypeSource);
        if (newType is not BaseTypeDeclarationSyntax)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = newType == null ? "// Failed to parse new type." : "// newTypeSource did not parse as a type declaration (enum/class/record/struct/interface)."
            };
        }

        var trailingIssue = DetectTrailingUnparsedDeclaration(newTypeSource, newType);
        if (trailingIssue != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// newTypeSource is invalid: {trailingIssue}"
            };
        }

        //newType = newType.WithAddedByComment("AddTopLevelType");

        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList();
        BaseNamespaceDeclarationSyntax? targetNamespace;
        if (namespaceName != null)
        {
            targetNamespace = namespaces.FirstOrDefault(n => n.Name.ToString() == namespaceName);
            if (targetNamespace == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Namespace '{namespaceName}' not found. Available: {string.Join(", ", namespaces.Select(n => n.Name.ToString()))}."
                };
            }
        }
        else if (namespaces.Count <= 1)
        {
            targetNamespace = namespaces.FirstOrDefault();
        }
        else
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// File has {namespaces.Count} namespaces ({string.Join(", ", namespaces.Select(n => n.Name.ToString()))}); pass namespaceName to disambiguate."
            };
        }

        if (targetNamespace != null)
        {
            var newNamespace = targetNamespace.AddMembers(newType);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = $"// Added {DescribeParsedMember(newType)}.",
                UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, targetNamespace, newNamespace, cancellationToken)
            };
        }

        // No namespace in the file at all -> append directly to the compilation unit.
        // ReplaceNodeFormattedAsync needs oldNode to be a strict descendant of the tracked root,
        // which the root itself never is, so format the freshly-built root directly instead.
        var newRoot = root.AddMembers(newType);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), cancellationToken: cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = $"// Added {DescribeParsedMember(newType)}.",
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<DocumentEditResult> RemoveMemberAsync(FilePathWrapper filePath, string memberName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? containerName = null, CancellationToken cancellationToken = default)
    {
        // excludeInterfaceMembers: false -- remove must be able to target an interface's own
        // member declaration, not just implementers. Same rationale as ReplaceMemberAsync above.
        // PreferNonInterfaceMember still runs unconditionally below: the old resolver's
        // excludeInterfaceMembers only gated whether interface members entered the candidate pool
        // at all, not whether the implementer-preference narrowing ran once both were present.
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        SyntaxNode? member;
        List<SyntaxNodeCandidate>? typeKindCandidatesForHint = null;
        try
        {
            var allNameCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, memberName, cancellationToken);
            var memberCandidates = SyntaxTargetResolver.PreferNonInterfaceMember(allNameCandidates
                .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember))
                .ToList());
            memberCandidates = SyntaxTargetResolver.FilterByContainingType(memberCandidates, containerName);
            member = SyntaxTargetResolver.ResolveBySnippetOrThrow(memberCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node;

            // Member(remove) only ever operates on class-level members (methods, properties,
            // fields, etc.) -- Class/Interface/Struct/Record/Enum/EnumMember are excluded from
            // memberCandidates above by design (type-level removal has no supported operation
            // yet; see docs/current/blockers/blocking_error_member_remove_skipprecheck_targetnotfound_ambiguous_symbol.md).
            // When the exclusion is *why* nothing resolved, surface that explicitly instead of
            // the generic "Member not found" -- the name did resolve, just to a kind this
            // operation cannot touch, and this holds regardless of skipPrecheck since both the
            // precheck (FindCallersAsync, which does resolve type declarations) and this removal
            // path see the same name; only skipPrecheck determines whether the caller ever sees
            // this more specific message or the earlier caller-list refusal instead.
            if (member == null)
            {
                typeKindCandidatesForHint = allNameCandidates
                    .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember)
                    .ToList();
            }
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (member == null)
        {
            if (typeKindCandidatesForHint is { Count: > 0 })
            {
                var kindNames = string.Join(", ", typeKindCandidatesForHint.Select(c => c.Kind).Distinct());
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// '{memberName}' is a type-level declaration ({kindNames}), not a class member. " +
                        "Member(remove) only removes class-level members (methods, properties, fields, constructors, " +
                        "events, indexers) -- it does not support removing type declarations (classes, interfaces, " +
                        "structs, records, enums, or enum members), and skipPrecheck does not change this. " +
                        "There is currently no tool for removing a type declaration that still has callers; " +
                        "use ReplaceSnippet or ApplyDiff to edit the file's raw text instead."
                };
            }

            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Member not found."
            };
        }

        // Check for usages using SymbolFinder before removing
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel != null)
        {
            var symbol = semanticModel.GetDeclaredSymbol(member, cancellationToken);
            if (symbol != null)
            {
                var references = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                var usageCount = references.Sum(r => r.Locations.Count());
                if (usageCount > 0)
                {
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotRemove,
                        FilePath = filePath,
                        Message = $"// ERROR: Cannot remove member '{memberName}' - it has {usageCount} usages in the solution.\n{root!.ToFullString()}"
                    };
                }
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Member removed.",
            UpdatedText = await RoslynFormattingHelper.RemoveNodeFormattedAsync(document, root, member, cancellationToken)
        };
    }

    /// <summary>
    /// Moves named members from a class into a target class atomically -> one combined change set,
    /// so it validates and writes as a single unit instead of the remove-then-add sequence that
    /// otherwise fails the write-path chokepoint's per-write compiler check (source ends up with
    /// dangling references before the add lands).
    ///
    /// Three destination modes, chosen automatically from targetClassName:
    ///  - Existing base type of the source class: reuses PullUpMember's modifier adjustment
    ///    (removes override, adds virtual) and skips call-site rewriting -> virtual dispatch means
    ///    existing call sites keep working unchanged. Instance OR static members are both fine here.
    ///  - Existing unrelated class: moves the declaration as-is and rewrites call sites solution-wide
    ///    (ClassA.Foo() -> ClassB.Foo()). STATIC MEMBERS ONLY -> an instance member has no such
    ///    unambiguous rewrite (a call site may use the same source-class variable for other members
    ///    that stay behind, so there's no single correct receiver substitution); those are rejected
    ///    up front with a ToolNotFoundException rather than attempting a partial/guessed rewrite.
    ///  - No existing class named targetClassName: synthesizes a new class (same behavior as the
    ///    former ExtractMembers as=class). STATIC MEMBERS ONLY, same reasoning as above.
    /// </summary>
    public async Task<MoveMemberResult> MoveMemberAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default, bool autoResolveCallSites = true, Dictionary<string, string>? callSiteFixups = null)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for '{filePath}'.");
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' not found in '{filePath}'.");
        }

        var membersToMove = classNode.Members.Where(m =>
        {
            if (m is MethodDeclarationSyntax meth)
            {
                return memberNames.Contains(meth.Identifier.Text);
            }

            if (m is PropertyDeclarationSyntax prop)
            {
                return memberNames.Contains(prop.Identifier.Text);
            }

            if (m is FieldDeclarationSyntax field)
            {
                return field.Declaration.Variables.Any(v => memberNames.Contains(v.Identifier.Text));
            }

            if (m is BaseTypeDeclarationSyntax nestedType)
            {
                return memberNames.Contains(nestedType.Identifier.Text);
            }

            return false;
        }).ToList();
        if (membersToMove.Count == 0)
        {
            throw new ToolNotFoundException($"None of the requested member(s) [{string.Join(", ", memberNames)}] were found in class '{className}'.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var classSymbol = semanticModel?.GetDeclaredSymbol(classNode, cancellationToken) as INamedTypeSymbol;
        var baseType = classSymbol?.BaseType;
        bool targetIsBaseType = baseType != null && baseType.SpecialType != SpecialType.System_Object && baseType.Name == targetClassName;
        // Resolved BEFORE RequireNoUnmovedDependencies so that guardrail can tell "must move with
        // this member" apart from "already satisfied by an existing same-name/compatible-type field
        // on the target" -> see RequireNoUnmovedDependencies's own doc comment for why this ordering
        // matters (moving it later reproduces the duplicate-field collision this exists to prevent).
        INamedTypeSymbol? targetClassSymbol = null;
        if (!targetIsBaseType)
        {
            var targetSearchDocs = targetFilePath.HasValue ? solution.GetDocumentIdsWithFilePath(targetFilePath.Value).Select(solution.GetDocument).Where(d => d != null) : solution.Projects.SelectMany(p => p.Documents);
            foreach (var doc in targetSearchDocs)
            {
                if (doc?.FilePath == null)
                {
                    continue;
                }

                var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                var candidateNode = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
                if (candidateNode == null)
                {
                    continue;
                }

                var candidateModel = await doc.GetSemanticModelAsync(cancellationToken);
                targetClassSymbol = candidateModel?.GetDeclaredSymbol(candidateNode, cancellationToken) as INamedTypeSymbol;
                break;
            }
        }

        List<MemberDeclarationSyntax> instanceMembersToConvert = new();
        if (!targetIsBaseType && targetClassSymbol is { IsStatic: true } && semanticModel != null)
        {
            // A static class can only hold static members. Refuse here, naming the instance state, unless every moved
            // instance method/property can be made static (that conversion happens in the existing-class path below).
            instanceMembersToConvert = RequireInstanceMembersConvertibleToStatic(semanticModel, membersToMove, targetClassName, cancellationToken);
        }

        HashSet<string> alreadySatisfiedFields = new(StringComparer.Ordinal);
        if (classSymbol != null && semanticModel != null)
        {
            alreadySatisfiedFields = RequireNoUnmovedDependencies(classSymbol, semanticModel, membersToMove, memberNames, className, targetClassName, targetClassSymbol);
        }

        if (alreadySatisfiedFields.Count > 0)
        {
            // The dependency is satisfied by the target's own existing field of the same name/
            // compatible type -> exclude its declaration from the move so it is never duplicated
            // onto the target. The moved bodies already reference it by bare name, which resolves
            // correctly post-move against the target's pre-existing field - no rewrite needed.
            membersToMove = membersToMove.Where(m => !(m is FieldDeclarationSyntax fd && fd.Declaration.Variables.Any(v => alreadySatisfiedFields.Contains(v.Identifier.Text)))).ToList();
        }

        if (targetIsBaseType)
        {
            return await MoveMembersToBaseTypeAsync(solution, filePath, root, classNode, membersToMove, baseType!, cancellationToken);
        }

        // Moving an INSTANCE member to anywhere other than an existing base class requires rewriting
        // every call site's receiver expression. autoResolveCallSites (default true) drives that
        // rewrite through MoveInstanceMembersAsync: unambiguous sites are fixed automatically, and
        // anything left over must be named in callSiteFixups or the whole call is rejected -> see
        // Decision 4 of docs/current/plans/plan_scoped_operation_ledger.md. STATIC members have no
        // such ambiguity (ClassA.Foo() -> ClassB.Foo() is unambiguous everywhere) and always use the
        // direct rewrite path below.
        var nonStaticMembers = membersToMove.Where(m =>
        {
            if (m is BaseTypeDeclarationSyntax)
            {
                return false;
            }

            var modifiers = m switch
            {
                MethodDeclarationSyntax meth => meth.Modifiers,
                PropertyDeclarationSyntax prop => prop.Modifiers,
                FieldDeclarationSyntax field => field.Modifiers,
                _ => default
            };
            return !modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword));
        }).ToList();
        // Look for an existing (non-base) class named targetClassName, optionally narrowed by targetFilePath.
        var candidateDocs = targetFilePath.HasValue ? solution.GetDocumentIdsWithFilePath(targetFilePath.Value).Select(solution.GetDocument).Where(d => d != null) : solution.Projects.SelectMany(p => p.Documents);
        Document? targetDoc = null;
        ClassDeclarationSyntax? targetClassNode = null;
        foreach (var doc in candidateDocs)
        {
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            var candidate = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            if (candidate != null)
            {
                targetDoc = doc;
                targetClassNode = candidate;
                break;
            }
        }

        if (nonStaticMembers.Count > 0 && targetClassSymbol is { IsStatic: true } && targetDoc?.FilePath != null && targetClassNode != null)
        {
            // Static target class: the instance methods/properties were verified stateless above, so they move as STATIC
            // members (a static modifier is added to each) and every instance-qualified caller is rewritten to the target type.
            var staticTargetMemberSymbols = membersToMove.Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
            var conversionNotes = new List<string>();
            var moved = await MoveMembersToExistingClassAsync(solution, filePath, root, classNode, membersToMove, targetDoc.FilePath, targetClassNode, targetClassName, staticTargetMemberSymbols, instanceMembersToConvert, conversionNotes, cancellationToken);
            return moved with { Notes = conversionNotes };
        }

        if (nonStaticMembers.Count > 0)
        {
            if (!autoResolveCallSites)
            {
                var names = string.Join(", ", nonStaticMembers.Select(m => m switch
                {
                    MethodDeclarationSyntax meth => meth.Identifier.Text,
                    PropertyDeclarationSyntax prop => prop.Identifier.Text,
                    FieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text)),
                    _ => "?"
                }));
                throw new ToolNotFoundException($"Cannot move instance member(s) [{names}] to '{targetClassName}': it is not an existing base class of '{className}'. " + "Moving an instance member elsewhere requires rewriting every call site's receiver expression. Pass " + "autoResolveCallSites:true (the default) to have this resolved automatically where unambiguous, or " + "make the member(s) static first, or move to an existing base class of the source type.");
            }

            return await MoveInstanceMembersAsync(solution, filePath, className, membersToMove, targetClassName, memberNames, targetFilePath, targetDoc, targetClassNode, callSiteFixups, cancellationToken);
        }

        if (targetDoc?.FilePath != null && targetClassNode != null)
        {
            var existingClassMemberSymbols = membersToMove.Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
            return await MoveMembersToExistingClassAsync(solution, filePath, root, classNode, membersToMove, targetDoc.FilePath, targetClassNode, targetClassName, existingClassMemberSymbols, null, null, cancellationToken);
        }

        var newClassMemberSymbols = membersToMove.Select(m => semanticModel?.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
        return await MoveMembersToNewClassAsync(solution, filePath, root, classNode, membersToMove, targetClassName, newClassMemberSymbols, cancellationToken);
    }

    /// <summary>
    /// MoveMember into a STATIC target class: every moved instance method/property must be convertible to static
    /// (see <see cref="StaticConversionEdits"/>) and must use no instance state. Runs before
    /// <see cref="RequireNoUnmovedDependencies"/> so the refusal names the real cause (the instance state) instead of
    /// the CS0708 the unconverted copy would produce in the static class, or a dependency hint that points at a field
    /// a static class cannot hold anyway. Returns the instance members that will be made static.
    /// </summary>
    private static List<MemberDeclarationSyntax> RequireInstanceMembersConvertibleToStatic(SemanticModel semanticModel, List<MemberDeclarationSyntax> membersToMove, string targetClassName, CancellationToken cancellationToken)
    {
        var instanceMembers = membersToMove.Where(m => (m is MethodDeclarationSyntax || m is PropertyDeclarationSyntax || m is FieldDeclarationSyntax) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword) || mod.IsKind(SyntaxKind.ConstKeyword))).ToList();
        var fieldNames = instanceMembers.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)).ToList();
        if (fieldNames.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot move instance field(s) [{string.Join(", ", fieldNames)}] into static class '{targetClassName}': a static class cannot hold instance state. Make the field(s) static first, or choose a non-static target class.");
        }

        var candidates = instanceMembers.Select(m => (Declaration: m, Symbol: semanticModel.GetDeclaredSymbol(m, cancellationToken))).Where(t => t.Symbol != null).ToList();
        var refusals = new List<string>();
        foreach (var (declaration, symbol) in candidates)
        {
            var reason = StaticConversionEdits.GetIneligibilityReason(declaration, symbol!);
            if (reason != null)
            {
                refusals.Add($"'{symbol!.ContainingType?.Name}.{symbol.Name}': {reason}");
            }
        }

        if (refusals.Count == 0)
        {
            var converted = new HashSet<ISymbol>(candidates.Select(t => t.Symbol!.OriginalDefinition), SymbolEqualityComparer.Default);
            foreach (var (declaration, symbol) in candidates)
            {
                var uses = StaticConversionEdits.FindInstanceStateUses(declaration, semanticModel, symbol!.ContainingType, converted, cancellationToken);
                if (uses.Count > 0)
                {
                    refusals.Add($"'{symbol.ContainingType.Name}.{symbol.Name}' uses instance state: {string.Join(", ", uses.Select(u => u.ToString()))}");
                }
            }
        }

        if (refusals.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot move instance member(s) into static class '{targetClassName}', which can only hold static members - {string.Join("; ", refusals)}. Members that use no instance state are made static and their callers rewritten automatically; for the others, pass the state in as parameters or move the members they use along with them.");
        }

        return instanceMembers;
    }

    /// <summary>
    /// Refuses up front when a moved member's body references a private or internal sibling member
    /// of the source class (including nested types) that is not itself part of this move -&gt; without
    /// this check, MoveMemberAsync pastes the body into the target file verbatim and the reference
    /// only fails to compile afterward, as an unexplained CS0246/CS0103 pointing at the target file
    /// rather than at the real cause (an un-moved dependency back in the source class).
    /// </summary>
    private static HashSet<string> RequireNoUnmovedDependencies(INamedTypeSymbol classSymbol, SemanticModel semanticModel, List<MemberDeclarationSyntax> membersToMove, string[] memberNames, string className, string targetClassName, INamedTypeSymbol? targetClassSymbol)
    {
        var alreadySatisfiedFields = new HashSet<string>(StringComparer.Ordinal);
        var siblingsByName = classSymbol.GetMembers().Where(s => s.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.NotApplicable).Where(s => !memberNames.Contains(s.Name)).ToLookup(s => s.Name, StringComparer.Ordinal);
        if (siblingsByName.Count == 0)
        {
            return alreadySatisfiedFields;
        }

        var missingDependencies = new SortedSet<string>(StringComparer.Ordinal);
        string? incompatibleFieldName = null;
        ITypeSymbol? incompatibleSourceType = null;
        ITypeSymbol? incompatibleTargetType = null;
        foreach (var member in membersToMove)
        {
            var identifiers = member.DescendantNodes().OfType<SimpleNameSyntax>();
            foreach (var identifier in identifiers)
            {
                if (!siblingsByName.Contains(identifier.Identifier.Text))
                {
                    continue;
                }

                var symbolInfo = semanticModel.GetSymbolInfo(identifier);
                var resolved = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (resolved == null || !SymbolEqualityComparer.Default.Equals(resolved.ContainingType, classSymbol))
                {
                    continue;
                }

                if (resolved is IFieldSymbol sourceField && targetClassSymbol != null)
                {
                    var targetField = targetClassSymbol.GetMembers(sourceField.Name).OfType<IFieldSymbol>().FirstOrDefault();
                    if (targetField != null)
                    {
                        bool sameType = SymbolEqualityComparer.Default.Equals(sourceField.Type, targetField.Type);
                        bool implicitlyConvertible = !sameType && semanticModel.Compilation.ClassifyConversion(sourceField.Type, targetField.Type).IsImplicit;
                        if (sameType || implicitlyConvertible)
                        {
                            alreadySatisfiedFields.Add(sourceField.Name);
                        }
                        else
                        {
                            incompatibleFieldName = sourceField.Name;
                            incompatibleSourceType = sourceField.Type;
                            incompatibleTargetType = targetField.Type;
                        }

                        continue;
                    }
                }

                missingDependencies.Add(identifier.Identifier.Text);
            }
        }

        if (incompatibleFieldName != null)
        {
            throw new ToolInvalidArgumentException($"Cannot move the requested member(s) out of '{className}': their bodies reference field " + $"'{incompatibleFieldName}' of '{className}' (type '{incompatibleSourceType}'), but '{targetClassName}' " + $"already declares its own field named '{incompatibleFieldName}' of an incompatible type " + $"('{incompatibleTargetType}'). Rename one of the two fields first so they no longer collide, then retry.");
        }

        if (missingDependencies.Count > 0)
        {
            throw new ToolInvalidArgumentException($"Cannot move the requested member(s) out of '{className}': their bodies reference private/internal " + $"member(s) [{string.Join(", ", missingDependencies)}] of '{className}' that are not included in this move " + $"and would no longer be reachable from '{targetClassName}'. Add the missing name(s) to memberNames so they " + "move together, or leave the referencing member(s) behind.");
        }

        return alreadySatisfiedFields;
    }

    private static async Task<MoveMemberResult> MoveMembersToBaseTypeAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, INamedTypeSymbol baseType, CancellationToken cancellationToken)
    {
        if (baseType.DeclaringSyntaxReferences.Length == 0)
        {
            throw new ToolNotFoundException("Base class is in an external assembly and cannot be modified.");
        }

        var baseFile = baseType.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        if (baseFile == null)
        {
            throw new ToolNotFoundException("Base class source file not found.");
        }

        var baseDoc = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.FilePath == baseFile);
        if (baseDoc == null)
        {
            throw new ToolNotFoundException($"Base class source document not found at '{baseFile}'.");
        }

        var baseRoot = await baseDoc.GetSyntaxRootAsync(cancellationToken);
        if (baseRoot == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for base class file '{baseFile}'.");
        }

        var baseClassNode = baseRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == baseType.Name);
        if (baseClassNode == null)
        {
            throw new ToolNotFoundException($"Base class '{baseType.Name}' not found in '{baseFile}'.");
        }

        // Base and derived class routinely live in the same file (a small hierarchy kept together
        // rather than split one-type-per-file); then both edits target the same document's text.
        var sourceDocument = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).First()!;
        bool sameFile = sourceDocument.Id == baseDoc.Id;
        var sourceText = await sourceDocument.GetTextAsync(cancellationToken);
        var baseText = sameFile ? sourceText : await baseDoc.GetTextAsync(cancellationToken);

        var sourceEdits = MoveMemberTextEdits.BuildSourceEdits(sourceText, membersToMove, Array.Empty<TextChange>(), out _);

        var baseEol = EolUtilities.DetectDominantEol(baseText);
        var baseIndent = MoveMemberTextEdits.GetTargetMemberIndentation(baseText, baseClassNode);
        var blocks = membersToMove.Select(m => MoveMemberTextEdits.BuildMovedMemberText(sourceText, m, MoveMemberTextEdits.BuildPullUpModifierEdits(m), MoveMemberTextEdits.GetMemberIndentation(sourceText, m), baseIndent, baseEol)).ToList();
        var insertion = MoveMemberTextEdits.BuildInsertion(baseText, baseClassNode, MoveMemberTextEdits.JoinMovedMembers(membersToMove, blocks, baseEol), baseEol);
        if (sameFile)
        {
            sourceEdits.Add(insertion);
            return new MoveMemberResult(new Dictionary<FilePathWrapper, string> { { filePath, MoveMemberTextEdits.ApplyChanges(sourceText, sourceEdits) } }, new List<SkippedCallSite>());
        }

        return new MoveMemberResult(new Dictionary<FilePathWrapper, string> { { filePath, MoveMemberTextEdits.ApplyChanges(sourceText, sourceEdits) }, { baseFile, MoveMemberTextEdits.ApplyChanges(baseText, new[] { insertion }) } }, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Collects one minimal reference edit (qualifier replaced or inserted, see MoveMemberTextEdits.BuildReferenceEdit)
    /// per SymbolFinder reference location of the moved static members, grouped by document, with spans in each
    /// document's ORIGINAL text. Bare-name insertions inside the moved members themselves are skipped: those members
    /// move together, so their bare names still resolve at the destination.
    /// </summary>
    private static async Task<Dictionary<DocumentId, List<TextChange>>> CollectStaticMoveReferenceEditsAsync(Solution solution, DocumentId sourceDocumentId, IReadOnlyList<MemberDeclarationSyntax> membersToMove, IEnumerable<ISymbol> memberSymbols, string qualifierName, CancellationToken cancellationToken)
    {
        var movedSpans = membersToMove.Select(m => m.FullSpan).ToList();
        var editsByDocument = new Dictionary<DocumentId, List<TextChange>>();
        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            foreach (var location in references.SelectMany(r => r.Locations).Where(l => !l.IsCandidateLocation && l.Document.FilePath != null))
            {
                var document = location.Document;
                var documentRoot = await document.GetSyntaxRootAsync(cancellationToken);
                if (documentRoot == null)
                {
                    continue;
                }

                var span = location.Location.SourceSpan;
                var edit = MoveMemberTextEdits.BuildReferenceEdit(documentRoot, span, qualifierName);
                if (edit == null)
                {
                    continue;
                }

                var insideMovedMember = document.Id == sourceDocumentId && movedSpans.Any(s => s.Contains(span));
                if (insideMovedMember && edit.Value.Span.Length == 0)
                {
                    continue;
                }

                if (!editsByDocument.TryGetValue(document.Id, out var documentEdits))
                {
                    documentEdits = new List<TextChange>();
                    editsByDocument[document.Id] = documentEdits;
                }

                if (!documentEdits.Any(e => e.Span == edit.Value.Span))
                {
                    documentEdits.Add(edit.Value);
                }
            }
        }

        return editsByDocument;
    }

    /// <summary>
    /// The edits for moving methods/properties into a STATIC target class when some of them are instance members:
    /// every reference to a moved member gets its receiver replaced by the target type (a bare reference outside the
    /// moved members gets the type name inserted, one inside them is left alone because the members travel together),
    /// and each instance member gets a <c>static</c> modifier inside the text that moves. Spans are in each document's
    /// ORIGINAL text. Throws when a call site cannot be rewritten safely (for example a null-conditional chain).
    /// </summary>
    private static async Task<(Dictionary<DocumentId, List<TextChange>> Edits, List<string> Notes)> CollectConversionMoveEditsAsync(Solution solution, Document sourceDocument, Document targetDocument, ClassDeclarationSyntax targetClassNode, IReadOnlyList<MemberDeclarationSyntax> membersToMove, IReadOnlyList<MemberDeclarationSyntax> convertToStatic, IEnumerable<ISymbol> memberSymbols, CancellationToken cancellationToken)
    {
        var targetModel = await targetDocument.GetSemanticModelAsync(cancellationToken);
        var targetSymbol = targetModel?.GetDeclaredSymbol(targetClassNode, cancellationToken);
        if (targetSymbol == null)
        {
            throw new ToolNotFoundException($"Could not resolve target class '{targetClassNode.Identifier.Text}' to rewrite call sites against it.");
        }

        var movedSpans = membersToMove.Select(m => m.FullSpan).ToList();
        var collector = new StaticConversionReferenceCollector(solution);
        foreach (var symbol in memberSymbols)
        {
            await collector.AddReferencesAsync(symbol, targetSymbol, skipAllBareReferences: false, isInsideMovedMember: (documentId, span) => documentId == sourceDocument.Id && movedSpans.Any(s => s.Contains(span)), cancellationToken);
        }

        if (collector.Problems.Count > 0)
        {
            throw new ToolTargetIneligibleException($"Cannot rewrite every caller of the member(s) being moved into static class '{targetSymbol.Name}' - {string.Join("; ", collector.Problems)}.");
        }

        await collector.FinalizeAsync(cancellationToken);
        foreach (var member in convertToStatic)
        {
            collector.AddEdit(sourceDocument.Id, StaticConversionEdits.BuildStaticModifierEdit(member));
        }

        return (collector.EditsByDocument, collector.Notes);
    }

    /// <summary>
    /// Moves STATIC members into an existing, unrelated class. Static-only because the call-site
    /// rewrite is then unambiguous everywhere (ClassA.Foo() -> TargetClassName.Foo(), no receiver
    /// instance involved) -> MoveMemberAsync's caller already guarantees every member here is static.
    /// </summary>
    private static async Task<MoveMemberResult> MoveMembersToExistingClassAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, FilePathWrapper targetFilePath, ClassDeclarationSyntax targetClassNode, string targetClassName, List<ISymbol> memberSymbols, List<MemberDeclarationSyntax>? convertToStatic, List<string>? notesSink, CancellationToken cancellationToken)
    {
        var sourceDocument = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).First()!;
        var targetDocument = solution.GetDocumentIdsWithFilePath(targetFilePath).Select(solution.GetDocument).First()!;
        bool sameFile = sourceDocument.Id == targetDocument.Id;
        var sourceText = await sourceDocument.GetTextAsync(cancellationToken);
        var targetText = sameFile ? sourceText : await targetDocument.GetTextAsync(cancellationToken);

        Dictionary<DocumentId, List<TextChange>> editsByDocument;
        if (convertToStatic is { Count: > 0 })
        {
            // Instance members moving into a static class: they become static and their callers are rewritten too.
            var conversion = await CollectConversionMoveEditsAsync(solution, sourceDocument, targetDocument, targetClassNode, membersToMove, convertToStatic, memberSymbols, cancellationToken);
            editsByDocument = conversion.Edits;
            notesSink?.AddRange(conversion.Notes);
        }
        else
        {
            editsByDocument = await CollectStaticMoveReferenceEditsAsync(solution, sourceDocument.Id, membersToMove, memberSymbols, targetClassName, cancellationToken);
        }

        // Source document: cut the moved members out; reference edits that fall inside them travel with them.
        var sourceEdits = MoveMemberTextEdits.BuildSourceEdits(sourceText, membersToMove, editsByDocument.TryGetValue(sourceDocument.Id, out var existingSourceEdits) ? existingSourceEdits : new List<TextChange>(), out var insideMovedMemberEdits);

        // Target document: append the moved members, re-indented to the target class and in its EOL.
        var targetEol = EolUtilities.DetectDominantEol(targetText);
        var targetIndent = MoveMemberTextEdits.GetTargetMemberIndentation(targetText, targetClassNode);
        var blocks = membersToMove.Select(m => MoveMemberTextEdits.BuildMovedMemberText(sourceText, m, insideMovedMemberEdits.Where(e => m.FullSpan.Contains(e.Span)).ToList(), MoveMemberTextEdits.GetMemberIndentation(sourceText, m), targetIndent, targetEol)).ToList();
        var insertion = MoveMemberTextEdits.BuildInsertion(targetText, targetClassNode, MoveMemberTextEdits.JoinMovedMembers(membersToMove, blocks, targetEol), targetEol);
        if (sameFile)
        {
            sourceEdits.Add(insertion);
        }
        else
        {
            if (!editsByDocument.TryGetValue(targetDocument.Id, out var targetEdits))
            {
                targetEdits = new List<TextChange>();
                editsByDocument[targetDocument.Id] = targetEdits;
            }

            targetEdits.Add(insertion);
        }

        editsByDocument[sourceDocument.Id] = sourceEdits;

        // Materialize each touched document's new text from its original text.
        var result = new Dictionary<FilePathWrapper, string>();
        foreach (var (documentId, edits) in editsByDocument)
        {
            var document = solution.GetDocument(documentId);
            if (document?.FilePath == null || edits.Count == 0)
            {
                continue;
            }

            var originalText = document.Id == sourceDocument.Id ? sourceText : document.Id == targetDocument.Id ? targetText : await document.GetTextAsync(cancellationToken);
            result[document.FilePath] = MoveMemberTextEdits.ApplyChanges(originalText, edits);
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    private static async Task<MoveMemberResult> MoveMembersToNewClassAsync(Solution solution, FilePathWrapper filePath, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, string newClassName, List<ISymbol> memberSymbols, CancellationToken cancellationToken)
    {
        // Static members only (guaranteed by MoveMemberAsync's caller) -> no `this.Member()` case to
        // rewrite, and no accessor property needed; bare Member() becomes NewClassName.Member() directly.
        var sourceDocument = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).First()!;
        var sourceText = await sourceDocument.GetTextAsync(cancellationToken);
        var eol = EolUtilities.DetectDominantEol(sourceText);

        var editsByDocument = await CollectStaticMoveReferenceEditsAsync(solution, sourceDocument.Id, membersToMove, memberSymbols, newClassName, cancellationToken);
        editsByDocument[sourceDocument.Id] = MoveMemberTextEdits.BuildSourceEdits(sourceText, membersToMove, editsByDocument.TryGetValue(sourceDocument.Id, out var existingSourceEdits) ? existingSourceEdits : new List<TextChange>(), out var insideMovedMemberEdits);

        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var newFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{newClassName}.cs");
        var result = new Dictionary<FilePathWrapper, string>
        {
            {
                newFilePath,
                MoveMemberTextEdits.BuildNewClassFileText(root.Usings.Select(u => u.ToString()).ToList(), ns?.Name.ToString(), ns is FileScopedNamespaceDeclarationSyntax, newClassName, sourceText, membersToMove, insideMovedMemberEdits, eol)
            }
        };
        foreach (var (documentId, edits) in editsByDocument)
        {
            var document = solution.GetDocument(documentId);
            if (document?.FilePath == null || edits.Count == 0)
            {
                continue;
            }

            var originalText = document.Id == sourceDocument.Id ? sourceText : await document.GetTextAsync(cancellationToken);
            result[document.FilePath] = MoveMemberTextEdits.ApplyChanges(originalText, edits);
        }

        return new MoveMemberResult(result, new List<SkippedCallSite>());
    }

    /// <summary>
    /// Adds the structural half of an instance move to <paramref name = "editsByDocument"/> (which may already hold
    /// call-site receiver edits): the moved members are cut out of the source class and appended to the existing
    /// target class (same file or not), or - only when <paramref name = "createNewClassFile"/> is set and there is no
    /// existing target - written into a brand-new class file returned in NewFiles. Everything is a TextChange against
    /// the original text, so apply and preview share one definition of "what a move edits".
    /// Texts returns the original text of every document that has edits.
    /// </summary>
    public static async Task<(Dictionary<DocumentId, SourceText> Texts, Dictionary<FilePathWrapper, string> NewFiles)> AddInstanceMoveStructuralEditsAsync(Dictionary<DocumentId, List<TextChange>> editsByDocument, Document sourceDocument, CompilationUnitSyntax root, ClassDeclarationSyntax classNode, List<MemberDeclarationSyntax> membersToMove, string targetClassName, Document? targetDocument, ClassDeclarationSyntax? targetClassNode, bool createNewClassFile, CancellationToken cancellationToken)
    {
        var sourceText = await sourceDocument.GetTextAsync(cancellationToken);
        var texts = new Dictionary<DocumentId, SourceText> { [sourceDocument.Id] = sourceText };
        var newFiles = new Dictionary<FilePathWrapper, string>();
        var sourceEdits = MoveMemberTextEdits.BuildSourceEdits(sourceText, membersToMove, editsByDocument.TryGetValue(sourceDocument.Id, out var existingSourceEdits) ? existingSourceEdits : new List<TextChange>(), out var insideMovedMemberEdits);
        editsByDocument[sourceDocument.Id] = sourceEdits;
        if (targetDocument?.FilePath != null && targetClassNode != null)
        {
            bool sameFile = targetDocument.Id == sourceDocument.Id;
            var targetText = sameFile ? sourceText : await targetDocument.GetTextAsync(cancellationToken);
            texts[targetDocument.Id] = targetText;
            var targetEol = EolUtilities.DetectDominantEol(targetText);
            var targetIndent = MoveMemberTextEdits.GetTargetMemberIndentation(targetText, targetClassNode);
            var blocks = membersToMove.Select(m => MoveMemberTextEdits.BuildMovedMemberText(sourceText, m, insideMovedMemberEdits.Where(e => m.FullSpan.Contains(e.Span)).ToList(), MoveMemberTextEdits.GetMemberIndentation(sourceText, m), targetIndent, targetEol)).ToList();
            var insertion = MoveMemberTextEdits.BuildInsertion(targetText, targetClassNode, MoveMemberTextEdits.JoinMovedMembers(membersToMove, blocks, targetEol), targetEol);
            if (sameFile)
            {
                sourceEdits.Add(insertion);
            }
            else
            {
                if (!editsByDocument.TryGetValue(targetDocument.Id, out var targetEdits))
                {
                    targetEdits = new List<TextChange>();
                    editsByDocument[targetDocument.Id] = targetEdits;
                }

                targetEdits.Add(insertion);
            }
        }
        else if (createNewClassFile)
        {
            var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var newFilePath = Path.Combine(Path.GetDirectoryName(sourceDocument.FilePath)!, $"{targetClassName}.cs");
            newFiles[newFilePath] = MoveMemberTextEdits.BuildNewClassFileText(root.Usings.Select(u => u.ToString()).ToList(), ns?.Name.ToString(), ns is FileScopedNamespaceDeclarationSyntax, targetClassName, sourceText, membersToMove, insideMovedMemberEdits, EolUtilities.DetectDominantEol(sourceText));
        }

        return (texts, newFiles);
    }

    /// <summary>
    /// Applies each document's edits to that document's ORIGINAL text (taken from <paramref name = "knownTexts"/> when
    /// already loaded) and returns the new full text keyed by file path. Documents with no edits are omitted.
    /// </summary>
    public static async Task<Dictionary<FilePathWrapper, string>> MaterializeDocumentEditsAsync(Solution solution, Dictionary<DocumentId, List<TextChange>> editsByDocument, Dictionary<DocumentId, SourceText> knownTexts, CancellationToken cancellationToken)
    {
        var result = new Dictionary<FilePathWrapper, string>();
        foreach (var (documentId, edits) in editsByDocument)
        {
            var document = solution.GetDocument(documentId);
            if (document?.FilePath == null || edits.Count == 0)
            {
                continue;
            }

            var originalText = knownTexts.TryGetValue(documentId, out var known) ? known : await document.GetTextAsync(cancellationToken);
            result[document.FilePath] = MoveMemberTextEdits.ApplyChanges(originalText, edits);
        }

        return result;
    }

    private async Task<MoveMemberResult> MoveInstanceMembersAsync(Solution solution, FilePathWrapper filePath, string className, List<MemberDeclarationSyntax> membersToMove, string targetClassName, string[] memberNames, FilePathWrapper? targetFilePath, Document? existingTargetDoc, ClassDeclarationSyntax? existingTargetClassNode, Dictionary<string, string>? callSiteFixups, CancellationToken cancellationToken)
    {
        var rows = await PreviewInstanceMoveCallSitesAsync(filePath, className, memberNames, targetClassName, targetFilePath, cancellationToken);
        var fixups = CallSiteFixupMap.Parse(callSiteFixups, _workspaceManager.GetSolutionRoot());
        // Caller-supplied fixups actually applied, keyed by CallerFixupSiteKey(file, original line).
        // Auto-resolved (Valid) rows are deliberately absent: only a caller's own value can be blamed
        // for errors on the line it rewrote.
        var callerFixupSources = new Dictionary<string, (string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
        var appliedFixups = new List<AppliedCallSiteFixup>();
        var resolvedReceivers = new Dictionary<(string FilePath, int Line), string>();
        foreach (var row in rows)
        {
            if (row.Status == CallSiteStatus.Valid)
            {
                if (row.SuggestedFix != null)
                {
                    resolvedReceivers[(row.FilePath, row.Line)] = row.SuggestedFix;
                }

                continue;
            }

            // Only apply a fixup if this line doesn't already have a receiver (from a Valid row).
            // This prevents wildcard fixups from overwriting valid receivers on lines with multiple moved-member calls.
            if (!resolvedReceivers.ContainsKey((row.FilePath, row.Line)) && fixups.Match(row.FilePath, row.Line) is { } fixup)
            {
                resolvedReceivers[(row.FilePath, row.Line)] = fixup.Value;
                callerFixupSources[CallerFixupSiteKey(row.FilePath, row.Line)] = fixup;
            }
        }

        // Refuse BEFORE building or validating any change set - a bad "new" would otherwise surface
        // only as one undifferentiated CS7036 per fixed-up site from the compile gate.
        await EnsureNewFixupsAreConstructibleAsync(callerFixupSources.Values, existingTargetDoc, existingTargetClassNode, cancellationToken);
        var unresolvedRows = rows.Where(r => r.Status != CallSiteStatus.Valid && !resolvedReceivers.ContainsKey((r.FilePath, r.Line))).ToList();
        var skippedCallSites = new List<SkippedCallSite>();
        List<CallSiteLedgerEntry>? pendingLedgerEntries = null;
        string? pendingLedgerOperationName = null;
        if (unresolvedRows.Count > 0)
        {
            // Deliberately NOT opened here via TryOpen: this method only returns a proposed change
            // set, it does not write anything. The caller (MoveMember MCP tool) applies Changes via
            // ValidateAndApplyAsync first, and only opens the ledger once that atomic write has
            // actually succeeded -> opening it here would make IsBlocked refuse the move's own
            // source/target file write in the same apply, since neither necessarily has an
            // unresolved ledger entry for itself (see Decision 5 plan notes).
            pendingLedgerEntries = unresolvedRows.Select(r => new CallSiteLedgerEntry { EntryId = Guid.NewGuid().ToString("n")[..8], FilePath = r.FilePath, Line = r.Line, BrokenExpression = r.CallExpression, OldStaticType = className, Status = r.Status, BlockReason = r.BlockReason, SuggestedFix = r.SuggestedFix, CandidatesInScope = r.Candidates, }).ToList();
            pendingLedgerOperationName = $"MoveMember [{string.Join(", ", memberNames)}] '{className}' -> '{targetClassName}'";
            skippedCallSites = pendingLedgerEntries.Select(e => new SkippedCallSite(e.FilePath, e.Line, $"{e.Status}: {e.BlockReason} (ledger entry {e.EntryId})")).ToList();
        }

        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).First()!;
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken))!;
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == className);

        // Receiver rewrites (`oldReceiver.Member` -> `resolvedReceiver.Member`) are minimal edits: only the receiver
        // expression's own span is replaced, against each document's ORIGINAL text, so the rest of every caller file
        // (and the source/target files) stays byte-identical.
        var editsByDocument = new Dictionary<DocumentId, List<TextChange>>();
        var fixupSites = new List<(DocumentId DocumentId, string DocumentPath, string ReportPath, int Position, string SiteKey)>();
        foreach (var group in resolvedReceivers.GroupBy(kv => kv.Key.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var docFilePath = group.Key;
            var doc = solution.GetDocumentIdsWithFilePath(docFilePath).Select(solution.GetDocument).FirstOrDefault();
            if (doc?.FilePath == null)
            {
                continue;
            }

            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            if (docRoot == null)
            {
                continue;
            }

            var linesToFix = group.ToDictionary(kv => kv.Key.Line, kv => kv.Value);
            var memberAccesses = docRoot.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(ma => memberNames.Contains(ma.Name.Identifier.Text) && linesToFix.ContainsKey(ma.GetLocation().GetLineSpan().StartLinePosition.Line + 1)).ToList();
            foreach (var memberAccess in memberAccesses)
            {
                var line = memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var receiverExpr = linesToFix[line];
                var newReceiver = receiverExpr == CallSiteFixupMap.NewKeyword ? $"new {targetClassName}()" : receiverExpr;
                var receiverSpan = memberAccess.Expression.Span;
                if (!editsByDocument.TryGetValue(doc.Id, out var docEdits))
                {
                    docEdits = new List<TextChange>();
                    editsByDocument[doc.Id] = docEdits;
                }

                if (docEdits.Any(e => e.Span.OverlapsWith(receiverSpan)))
                {
                    continue;
                }

                docEdits.Add(new TextChange(receiverSpan, newReceiver));
                var siteKey = CallerFixupSiteKey(docFilePath, line);
                if (callerFixupSources.ContainsKey(siteKey))
                {
                    fixupSites.Add((doc.Id, doc.FilePath, docFilePath, receiverSpan.Start, siteKey));
                }
            }
        }

        var (texts, newFiles) = await AddInstanceMoveStructuralEditsAsync(editsByDocument, document, root, classNode, membersToMove, targetClassName, existingTargetDoc, existingTargetClassNode, true, cancellationToken);
        var result = await MaterializeDocumentEditsAsync(solution, editsByDocument, texts, cancellationToken);
        foreach (var (newFilePath, newFileText) in newFiles)
        {
            result[newFilePath] = newFileText;
        }

        // AppliedCallSiteFixup.Line is the 1-based line in the FINAL text (what the compile gate reports against), so each
        // fixed-up site's original position is mapped through that document's edits. A site inside a moved member lands in
        // the target document instead, so it is not reported here.
        foreach (var site in fixupSites)
        {
            if (site.DocumentId == document.Id && membersToMove.Any(m => m.FullSpan.Contains(site.Position)))
            {
                continue;
            }

            var finalText = SourceText.From(result[site.DocumentPath]);
            var finalPosition = MoveMemberTextEdits.MapPositionThroughEdits(editsByDocument[site.DocumentId], site.Position);
            var finalLine = finalText.Lines.GetLinePosition(finalPosition).Line + 1;
            var source = callerFixupSources[site.SiteKey];
            appliedFixups.Add(new AppliedCallSiteFixup(site.ReportPath, finalLine, source.Key, source.Value));
        }

        return new MoveMemberResult(result, skippedCallSites, pendingLedgerEntries, pendingLedgerOperationName, appliedFixups);
    }

    /// <summary>
    /// Renders a type for use in a freshly synthesized parameter/return signature, ignoring any
    /// nullable annotation that came from flow-state analysis rather than the variable's actual
    /// declared nullability. A type obtained from a data-flow-analysis symbol (e.g. via
    /// <c>SemanticModel.AnalyzeDataFlow</c>) can carry <see cref = "NullableAnnotation.Annotated"/>
    /// purely because the compiler's flow analysis is conservative at the region boundary -> not
    /// because the variable was ever actually assignable to null. Blindly copying that annotation
    /// into a generated signature produces a spurious <c>?</c> and a live CS8602 warning on every
    /// unguarded use inside the generated body, for a variable that's really always non-null.
    /// </summary>
    private static string DisplayTypeForExtractedSignature(ITypeSymbol type)
    {
        var normalized = type.NullableAnnotation == NullableAnnotation.Annotated ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : type;
        return normalized.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    /// <summary>
    /// Converts a method with no parameters to a property.
    /// </summary>
    public async Task<DocumentEditResult> ConvertMethodToPropertyAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode != null && !methodNode.ParameterList.Parameters.Any())
        {
            ArrowExpressionClauseSyntax? arrow = null;
            if (methodNode.ExpressionBody != null)
            {
                arrow = methodNode.ExpressionBody;
            }
            else if (methodNode.Body?.Statements.Count == 1 && methodNode.Body.Statements[0] is ReturnStatementSyntax ret)
            {
                arrow = SyntaxFactory.ArrowExpressionClause(ret.Expression!);
            }

            if (arrow != null)
            {
                var propertyNode = SyntaxFactory.PropertyDeclaration(methodNode.ReturnType, methodNode.Identifier)
                    .WithModifiers(methodNode.Modifiers)
                    .WithExpressionBody(arrow)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, methodNode, propertyNode, cancellationToken)
                };
            }
        }

        if (methodNode != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Could not convert method to property: methods with parameters cannot become properties.",
                UpdatedText = root!.ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Could not convert method to property."
        };
    }

    /// <summary>
    /// Makes a method static if it doesn't access any instance members.
    /// </summary>
    public async Task<DocumentEditResult> MakeMethodStaticAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodNode == null || methodNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Method not found or already static."
            };
        }

        // Check for instance access
        var hasInstanceAccess = methodNode.DescendantNodes().Any(node =>
        {
            if (node is ThisExpressionSyntax || node is BaseExpressionSyntax)
            {
                return true;
            }

            var symbol = semanticModel?.GetSymbolInfo(node, cancellationToken).Symbol;
            return symbol != null && !symbol.IsStatic && (symbol.Kind == SymbolKind.Field || symbol.Kind == SymbolKind.Property || symbol.Kind == SymbolKind.Method);
        });

        if (!hasInstanceAccess)
        {
            var newMethodNode = methodNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, methodNode, newMethodNode, cancellationToken)
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Method accesses instance members and cannot be made static."
        };
    }

    public async Task<ExtractMethodResult> ExtractMethodAsync(FilePathWrapper filePath, int startLine, string startLineText, int endLine, string endLineText, string newMethodName, CancellationToken cancellationToken = default)
    {
        if (!(_config ?? new SentinelConfiguration()).IsFeatureEnabled("ExtractMethod"))
        {
            return new ExtractMethodResult(true, "ExtractMethod feature is disabled.", null, null, null, null);
        }

        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ExtractMethodResult(true, $"File '{filePath}' not found in solution.", null, null, null, null);
        }

        var text = await document.GetTextAsync(cancellationToken);
        if (startLine < 1 || startLine > text.Lines.Count)
        {
            return new ExtractMethodResult(true, $"startLine {startLine} out of range (file has {text.Lines.Count} lines).", null, null, null, null);
        }

        if (endLine < startLine || endLine > text.Lines.Count)
        {
            return new ExtractMethodResult(true, $"endLine {endLine} is out of range.", null, null, null, null);
        }

        // Stale-file validation: physical line text must match what the caller observed
        var actualStart = text.Lines[startLine - 1].ToString().Trim();
        var actualEnd = text.Lines[endLine - 1].ToString().Trim();
        if (actualStart != startLineText.Trim())
        {
            return new ExtractMethodResult(true, $"startLine mismatch: expected '{startLineText.Trim()}' but found '{actualStart}'. File may have changed.", null, null, null, null);
        }

        if (actualEnd != endLineText.Trim())
        {
            return new ExtractMethodResult(true, $"endLine mismatch: expected '{endLineText.Trim()}' but found '{actualEnd}'. File may have changed.", null, null, null, null);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new ExtractMethodResult(true, "Could not obtain syntax root or semantic model.", null, null, null, null);
        }

        var startPos = text.Lines[startLine - 1].Start;
        var endPos = text.Lines[endLine - 1].End;
        var span = new TextSpan(startPos, endPos - startPos);
        // Find the method body that fully contains the selection
        var containingMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Body != null).FirstOrDefault(m => m.Body!.Span.Contains(span));
        if (containingMethod?.Body == null)
        {
            return new ExtractMethodResult(true, "Selected range must be inside a block-body method (expression-bodied methods are not supported).", null, null, null, null);
        }

        // Collect direct body statements that overlap the selection
        var selectedStatements = containingMethod.Body.Statements.Where(s => s.Span.IntersectsWith(span)).ToList();
        if (selectedStatements.Count == 0)
        {
            return new ExtractMethodResult(true, "No complete statements found in the selected line range.", null, null, null, null);
        }

        // SuccessData flow analysis to infer parameters and return type
        DataFlowAnalysis dataFlow;
        try
        {
            dataFlow = semanticModel.AnalyzeDataFlow(selectedStatements[0], selectedStatements[^1])!;
        }
        catch (Exception ex)
        {
            return new ExtractMethodResult(true, $"SuccessData flow analysis failed: {ex.Message}", null, null, null, null);
        }

        // Parameters: symbols flowing in -> local vars and non-this method parameters only
        var parameters = dataFlow.DataFlowsIn.Where(s => s.Kind == SymbolKind.Local || (s.Kind == SymbolKind.Parameter && s is IParameterSymbol p && !p.IsThis)).OrderBy(s => s.Name).ToList();
        // Fail early if any ref/out parameter flows out -> we can't safely return it
        var refOutFlowOut = dataFlow.DataFlowsOut.OfType<IParameterSymbol>().Where(p => p.RefKind != RefKind.None && !p.IsThis).ToList();
        if (refOutFlowOut.Count > 0)
        {
            return new ExtractMethodResult(true, $"Cannot extract: ref/out parameter(s) '{string.Join(", ", refOutFlowOut.Select(p => p.Name))}' are " + "written inside the selection and read after it. This case cannot be auto-extracted - refactor manually.", null, null, null, null);
        }

        // Return value: local variables assigned inside that are used after the region
        var flowsOut = dataFlow.DataFlowsOut.Where(s => s.Kind == SymbolKind.Local).ToList();
        if (flowsOut.Count > 1)
        {
            return new ExtractMethodResult(true, $"Multiple variables flow out ({string.Join(", ", flowsOut.Select(s => s.Name))}). " + "Cannot auto-determine return type - narrow the selection or handle manually.", null, null, null, null);
        }

        ILocalSymbol? returnVar = flowsOut.Count == 1 ? (ILocalSymbol)flowsOut[0] : null;
        bool isAsync = selectedStatements.Any(s => s.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AwaitKeyword)));
        bool parentStatic = containingMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        // Build return type syntax
        TypeSyntax returnType = (returnVar, isAsync) switch
        {
            ({ } rv, true) => SyntaxFactory.ParseTypeName($"Task<{DisplayTypeForExtractedSignature(rv.Type)}>"),
            ({ } rv, false) => SyntaxFactory.ParseTypeName(DisplayTypeForExtractedSignature(rv.Type)),
            (null, true) => SyntaxFactory.ParseTypeName("Task"),
            _ => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
        };
        // Build parameter list -> include ref/out/in modifiers for parameter symbols
        var paramSyntax = parameters.Select(sym =>
        {
            string typeName;
            RefKind refKind = RefKind.None;
            if (sym is ILocalSymbol loc)
            {
                typeName = DisplayTypeForExtractedSignature(loc.Type);
            }
            else
            {
                var p = (IParameterSymbol)sym;
                typeName = DisplayTypeForExtractedSignature(p.Type);
                refKind = p.RefKind;
            }

            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(sym.Name)).WithType(SyntaxFactory.ParseTypeName(typeName).WithTrailingTrivia(SyntaxFactory.Space));
            if (refKind != RefKind.None)
            {
                var kw = refKind switch
                {
                    RefKind.Out => SyntaxKind.OutKeyword,
                    RefKind.In => SyntaxKind.InKeyword,
                    _ => SyntaxKind.RefKeyword
                };
                param = param.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(kw)));
            }

            return param;
        }).ToArray();
        // Build extracted method body
        var bodyStmts = selectedStatements.Select(s => s.WithoutLeadingTrivia().WithoutTrailingTrivia()).Cast<StatementSyntax>().ToList();
        if (returnVar != null)
        {
            bodyStmts.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(returnVar.Name)));
        }

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
        };
        if (parentStatic)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        if (isAsync)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        var extractedMethod = (MethodDeclarationSyntax)RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(SyntaxFactory.MethodDeclaration(returnType, newMethodName).WithModifiers(SyntaxFactory.TokenList(modifiers)).WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(paramSyntax))).WithBody(SyntaxFactory.Block(bodyStmts)));
        // Build call site -> include ref/out/in keywords for parameter symbols
        var argList = parameters.Select(sym =>
        {
            var arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(sym.Name));
            if (sym is IParameterSymbol p && p.RefKind != RefKind.None)
            {
                var kw = p.RefKind switch
                {
                    RefKind.Out => SyntaxFactory.Token(SyntaxKind.OutKeyword),
                    RefKind.In => SyntaxFactory.Token(SyntaxKind.InKeyword),
                    _ => SyntaxFactory.Token(SyntaxKind.RefKeyword)
                };
                arg = arg.WithRefKindKeyword(kw);
            }

            return arg;
        });
        ExpressionSyntax callExpr = SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(newMethodName), SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argList)));
        StatementSyntax callStatement;
        if (returnVar != null)
        {
            var initExpr = isAsync ? (ExpressionSyntax)SyntaxFactory.AwaitExpression(callExpr) : callExpr;
            // If returnVar was declared INSIDE the selection, emit `var x = Method()`.
            // If it was declared BEFORE the selection (flows out but not declared here),
            // emit plain assignment `x = Method()` to avoid CS0128.
            bool declaredInSelection = dataFlow.VariablesDeclared.Contains(returnVar);
            if (declaredInSelection)
            {
                callStatement = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"), SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(returnVar.Name)).WithInitializer(SyntaxFactory.EqualsValueClause(initExpr)))));
            }
            else
            {
                callStatement = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(returnVar.Name), initExpr));
            }
        }
        else if (isAsync)
        {
            callStatement = SyntaxFactory.ExpressionStatement(SyntaxFactory.AwaitExpression(callExpr));
        }
        else
        {
            callStatement = SyntaxFactory.ExpressionStatement(callExpr);
        }

        callStatement = callStatement.WithLeadingTrivia(selectedStatements[0].GetLeadingTrivia());
        // Rewrite method body: replace selected statements with the call site
        var origStmts = containingMethod.Body.Statements.ToList();
        int insertAt = origStmts.IndexOf(selectedStatements[0]);
        var newStmts = origStmts.ToList();
        newStmts.RemoveRange(insertAt, selectedStatements.Count);
        newStmts.Insert(insertAt, callStatement);
        var updatedMethod = containingMethod.WithBody(containingMethod.Body.WithStatements(SyntaxFactory.List(newStmts)));
        if (containingMethod.Parent is not TypeDeclarationSyntax parentType)
        {
            return new ExtractMethodResult(true, "Could not find the containing type declaration.", null, null, null, null);
        }

        // Append extracted method after the type's existing members
        var newParent = parentType.ReplaceNode(containingMethod, updatedMethod).AddMembers(extractedMethod);
        var newRoot = root.ReplaceNode(parentType, newParent);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), null, cancellationToken);
        var updatedContent = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        var beforeSnippet = string.Concat(selectedStatements.Select(s => s.ToFullString())).Trim();
        var callSiteText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(callStatement).ToFullString().Trim();
        var extractedMethodText = extractedMethod.ToFullString().Trim();
        return new ExtractMethodResult(false, null, beforeSnippet, callSiteText, extractedMethodText, updatedContent);
    }

    /// <summary>
    /// Reporting-only preview for moving instance member(s) to a destination that is not an existing
    /// base type (the one case MoveMemberAsync itself still rejects outright). Does not write anything
    /// and does not lift MoveMemberAsync's static-only guard -> see
    /// docs/current/proposal_movemember_instance_callsite_resolution.md sections 1, 5, 6.
    ///
    /// For each call site of the member(s) being moved: builds an in-memory trial change set with the
    /// member relocated but that call site NOT rewritten, runs it through ValidationEngine so the
    /// compiler's own diagnostics decide whether the site breaks (rather than a hand-rolled prediction),
    /// then classifies broken sites by scanning for in-scope fields/properties/parameters of the
    /// destination type via SemanticModel.LookupSymbols (which already respects C# shadowing).
    /// </summary>
    public async Task<List<PreviewCallSite>> PreviewInstanceMoveCallSitesAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default)
    {
        if (_validationEngine == null)
        {
            throw new InvalidOperationException("PreviewInstanceMoveCallSitesAsync requires a ValidationEngine - this StructuralRefactoringEngine instance was constructed without one.");
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' not found.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            throw new ToolNotFoundException($"Failed to get syntax root for '{filePath}'.");
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new ToolNotFoundException($"Class '{className}' not found in '{filePath}'.");
        }

        var membersToMove = classNode.Members.Where(m =>
        {
            if (m is MethodDeclarationSyntax meth)
            {
                return memberNames.Contains(meth.Identifier.Text);
            }

            if (m is PropertyDeclarationSyntax prop)
            {
                return memberNames.Contains(prop.Identifier.Text);
            }

            if (m is FieldDeclarationSyntax field)
            {
                return field.Declaration.Variables.Any(v => memberNames.Contains(v.Identifier.Text));
            }

            return false;
        }).ToList();
        if (membersToMove.Count == 0)
        {
            throw new ToolNotFoundException($"None of the requested member(s) [{string.Join(", ", memberNames)}] were found in class '{className}'.");
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            throw new ToolNotFoundException($"Failed to get semantic model for '{filePath}'.");
        }

        var memberSymbols = membersToMove.Select(m => semanticModel.GetDeclaredSymbol(m is FieldDeclarationSyntax f ? f.Declaration.Variables.First() : m)).Where(s => s != null).Cast<ISymbol>().ToList();
        INamedTypeSymbol? destinationType = null;
        Document? destinationDoc = null;
        ClassDeclarationSyntax? destinationClassNode = null;
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
            var candidate = docRoot?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == targetClassName);
            if (candidate == null)
            {
                continue;
            }

            var candidateModel = await doc.GetSemanticModelAsync(cancellationToken);
            destinationType = candidateModel?.GetDeclaredSymbol(candidate, cancellationToken) as INamedTypeSymbol;
            if (destinationType != null)
            {
                destinationDoc = doc;
                destinationClassNode = candidate;
                break;
            }
        }

        // previewChanges must reflect the COMPLETE post-move state (member removed from the source
        // class AND added to the destination class), not just the removal -> otherwise the trial
        // compile does not reproduce the same diagnostics the real two-file apply would produce, and
        // every call site gets misclassified as already-Valid. See
        // docs/current/blockers/blocking_error_movemember_instance_callsite_not_rewritten.md.
        // Built from the same text-edit definition the apply path uses (no new class file is previewed), so the
        // trial compile sees exactly the structural edits a real move would write.
        var previewEdits = new Dictionary<DocumentId, List<TextChange>>();
        var (previewTexts, _) = await AddInstanceMoveStructuralEditsAsync(previewEdits, document, root, classNode, membersToMove, targetClassName, destinationDoc, destinationClassNode, false, cancellationToken);
        var previewChanges = await MaterializeDocumentEditsAsync(solution, previewEdits, previewTexts, cancellationToken);
        var validation = await _validationEngine.ValidateChangesAsync(previewChanges, cancellationToken);
        var results = new List<PreviewCallSite>();
        var movingMemberDeclarationSpans = new HashSet<TextSpan>(membersToMove.Select(m => m.Span));
        foreach (var symbol in memberSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            var locations = references.SelectMany(r => r.Locations).Where(l => l.Document.FilePath != null).ToList();
            foreach (var refLocation in locations)
            {
                var refDoc = refLocation.Document;
                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                var refNode = refRoot?.FindNode(refLocation.Location.SourceSpan);
                var memberAccess = refNode?.Ancestors().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(ma => ma.Name.Span.Contains(refLocation.Location.SourceSpan)) ?? refNode?.Parent as MemberAccessExpressionSyntax;
                var lineSpan = refLocation.Location.GetLineSpan();
                var callExpression = memberAccess?.ToString() ?? refNode?.ToString() ?? symbol.Name;
                // TextSpan carries no document identity, so this must not compare spans across
                // documents: a call site in an unrelated (often short) file can coincidentally
                // fall inside the same byte-offset range as the moved method's declaration span
                // in the SOURCE file, producing a false MoveOrderDependent classification (seen
                // for BatteryFifteenTests.cs/BatteryThirtyTests.cs call sites during MoveMember
                // testing). Only the source document's own span comparisons are meaningful.
                bool isSameDocAsSource = string.Equals(refDoc.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase);
                var isMoveOrderDependent = isSameDocAsSource && refNode != null && movingMemberDeclarationSpans.Any(span => span.Contains(refNode.Span));
                var brokenHere = validation.Diagnostics.Any(d => string.Equals(d.FilePath, refDoc.FilePath, StringComparison.OrdinalIgnoreCase) && d.StartLine == lineSpan.StartLinePosition.Line + 1);
                if (!brokenHere)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null, null, []));
                    continue;
                }

                if (isMoveOrderDependent)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.MoveOrderDependent, "This call site is inside a method that is itself being moved in the same batch - whether it can be resolved depends on move order.", null, []));
                    continue;
                }

                if (destinationType == null)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked, $"Destination type '{targetClassName}' could not be resolved.", null, []));
                    continue;
                }

                var refSemanticModel = await refDoc.GetSemanticModelAsync(cancellationToken);
                var refCompilation = refSemanticModel?.Compilation;
                bool sameAssembly = refCompilation != null && SymbolEqualityComparer.Default.Equals(refCompilation.Assembly, destinationType.ContainingAssembly);
                bool accessible = destinationType.DeclaredAccessibility == Accessibility.Public || sameAssembly;
                if (!accessible)
                {
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateBlocked, $"Destination type '{targetClassName}' is not accessible from this call site's compilation context.", null, []));
                    continue;
                }

                // destinationType was resolved from the semantic model of whichever document first
                // matched targetClassName - almost always a DIFFERENT Project/Compilation than
                // refCompilation whenever the destination class lives in a different project from the
                // call site (e.g. AntiPatternEngine in RoslynSentinel.Engines.Advanced vs. a call site in
                // RoslynSentinel.Tests.Advanced). INamedTypeSymbol instances are compilation-scoped:
                // SymbolEqualityComparer.Default.Equals(typeFromCompilationA, typeFromCompilationB)
                // is false for "the same" type seen through two different compilations, even when one
                // references the other's assembly directly, because they are genuinely distinct symbol
                // objects. Every sibling field/property/parameter/local of the destination type at the
                // call site therefore compared unequal and the site was misclassified as
                // NoCandidateIntroducible despite an in-scope, correctly-typed, correctly-initialized
                // candidate existing. Resolving destinationType's counterpart symbol IN refCompilation
                // (via its fully-qualified metadata name) before comparing fixes this: same-compilation
                // moves are unaffected (the round-trip is a no-op), and cross-project moves now compare
                // two symbols that both belong to refCompilation. See
                // docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md.
                var destinationTypeMetadataName = destinationType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                var destinationTypeInRefCompilation = (ITypeSymbol?)refCompilation?.GetTypeByMetadataName(destinationTypeMetadataName) ?? destinationType;
                // LookupSymbols must be called at a position that reflects general lexical/member
                // scope, not refNode.SpanStart itself: refNode is the referenced member's own
                // identifier token (e.g. "AnalyzeSemaphoreUsageAsync" in "_engine.AnalyzeSemaphoreUsageAsync(...)"),
                // which sits inside a MemberAccessExpressionSyntax's right-hand .Name. Roslyn's
                // LookupSymbols treats a position there as a member lookup scoped to the receiver's
                // OWN type (here, the source class being moved FROM), not the enclosing lexical
                // scope - so sibling fields/locals of the destination type declared in the
                // surrounding class/method were structurally invisible from that position, even
                // when correctly typed and in scope everywhere else. Using the receiver expression's
                // own SpanStart (falling back to refNode's when there is no member-access receiver,
                // e.g. a bare identifier call) evaluates lexical scope correctly.
                var lookupPosition = memberAccess?.Expression.SpanStart ?? refNode?.SpanStart;
                // The type-identity comparison below must not rely solely on SymbolEqualityComparer
                // across a compilation boundary: destinationType was bound in the destination class's
                // OWN document/project compilation, while every field/property/parameter/local found
                // by LookupSymbols here is bound in refSemanticModel's compilation (a DIFFERENT
                // Project/Compilation whenever the destination class lives in another project, e.g.
                // AntiPatternEngine in RoslynSentinel.Engines.Advanced vs. a call site in
                // RoslynSentinel.Tests.Advanced). destinationTypeInRefCompilation above re-resolves it
                // by metadata name so the SymbolEqualityComparer path can succeed, but as a
                // compilation-independent guarantee this also falls back to comparing fully-qualified
                // display names directly - a check that cannot be defeated by any cross-compilation
                // symbol-identity subtlety, since it never compares ITypeSymbol instances against each
                // other at all. See
                // docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md.
                bool IsDestinationType(ISymbol s)
                {
                    var candidateType = GetSymbolType(s);
                    if (candidateType == null)
                    {
                        return false;
                    }

                    if (SymbolEqualityComparer.Default.Equals(candidateType, destinationTypeInRefCompilation))
                    {
                        return true;
                    }

                    var candidateTypeName = candidateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                    return string.Equals(candidateTypeName, destinationTypeMetadataName, StringComparison.Ordinal);
                }

                // LookupSymbols surfaces a field/property by NAME as soon as it is lexically visible,
                // which for a nested class includes every instance member declared on its enclosing
                // type(s) - C# nested classes see enclosing-type member names in scope for lookup
                // purposes, but (unlike VB.NET/Java inner classes) hold no implicit outer-instance
                // reference, so referencing an outer instance field/property unqualified is always
                // CS0120 ("An object reference is required..."), never a valid rewrite target. Without
                // this check, a same-named/same-typed field declared on an outer or unrelated type
                // (reachable only by name, not by instance) was picked as the "resolved" receiver for
                // an already-correctly-compiling call site, corrupting it. See
                // docs/current/blockers/blocking_error_movemember_batch_false_cs0120_cross_nested_class.md.
                // A static candidate member needs no instance and is always fine; an instance candidate
                // is only usable unqualified if it is declared on the call site's own enclosing type or
                // one of that type's base types.
                var enclosingTypeAtCallSite = lookupPosition == null ? null : refSemanticModel?.GetEnclosingSymbol(lookupPosition.Value, cancellationToken)?.ContainingType;
                bool IsReachableUnqualified(ISymbol s)
                {
                    if (s.IsStatic)
                    {
                        return true;
                    }

                    if (enclosingTypeAtCallSite == null)
                    {
                        return false;
                    }

                    for (var t = enclosingTypeAtCallSite; t != null; t = t.BaseType)
                    {
                        if (SymbolEqualityComparer.Default.Equals(t, s.ContainingType))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var candidates = refSemanticModel == null || lookupPosition == null ? new List<string>() : refSemanticModel.LookupSymbols(lookupPosition.Value).Where(s => (s is IFieldSymbol || s is IPropertySymbol || s is IParameterSymbol || s is ILocalSymbol) && IsDestinationType(s) && IsReachableUnqualified(s)).Select(s => s.Name).Distinct().ToList();
                if (candidates.Count == 0)
                {
                    // SuggestedFix on a non-Valid row is human-facing prep-step prose, never a receiver
                    // expression: MoveInstanceMembersAsync only reads SuggestedFix as a receiver for
                    // Valid rows, and ledger entries merely echo it back to the caller.
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.NoCandidateIntroducible, $"No in-scope reference of type '{targetClassName}' found at this call site. Add a 'using' directive or introduce a field/parameter of that type.", BuildIntroduceFieldSuggestion(targetClassName, refNode, memberAccess), []));
                    continue;
                }

                if (candidates.Count == 1)
                {
                    // SuggestedFix (like callSiteFixups) is a receiver-only expression: the rewrite in
                    // MoveInstanceMembersAsync calls original.WithExpression(...) on the existing
                    // member-access node, which keeps its own .Name segment. A value that already
                    // includes ".{symbol.Name}" here produced a doubled method name on apply (e.g.
                    // "_apiGenerationEngine.AddValidationToPocoAsync.AddValidationToPocoAsync").
                    results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Valid, null, candidates[0], candidates));
                    continue;
                }

                results.Add(new PreviewCallSite(refDoc.FilePath!, lineSpan.StartLinePosition.Line + 1, callExpression, CallSiteStatus.Ambiguous, $"Multiple in-scope references of type '{targetClassName}' found; specify which one via callSiteFixups.", null, candidates));
            }
        }

        return results;
    }

    private const string CallerFixupAnnotationKind = "RoslynSentinel.MoveMember.CallerFixup";
    private static string CallerFixupSiteKey(string filePath, int line) => $"{filePath}|{line}";
    /// <summary>
    /// Prep-step prose for a NoCandidateIntroducible call site: names the destination type, the type
    /// that needs the new field, and the receiver whose construction it should mirror. It points the
    /// caller at the current receiver's constructor arguments on purpose - a bare "new" or an inferred
    /// argument list can compile while silently dropping configuration that receiver was built with.
    /// </summary>
    private static string BuildIntroduceFieldSuggestion(string targetClassName, SyntaxNode? refNode, MemberAccessExpressionSyntax? memberAccess)
    {
        var containingType = refNode?.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "the calling type";
        var receiver = memberAccess?.Expression;
        var initialization = receiver == null || receiver is ThisExpressionSyntax ? "initialize it with the same constructor arguments the current instance was created with" : $"initialize it where the current receiver '{receiver}' is initialized, with the same constructor arguments";
        return $"Add a field of type {targetClassName} to {containingType} ({initialization}), then retry MoveMember - the call site will then auto-resolve.";
    }

    /// <summary>
    /// Enforces the documented "new" contract (docs/current/proposal_movemember_instance_callsite_resolution.md
    /// section 2): "new" emits exactly 'new Target()' and nothing more. If Target has no accessible
    /// instance constructor callable with zero arguments, refuse up front naming the offending keys and
    /// the real constructor signature(s), instead of letting every fixed-up site fail the compile gate
    /// with an undifferentiated CS7036. Deliberately does NOT infer constructor arguments from scope: a
    /// guessed argument list can compile while silently dropping configuration (e.g. a per-test
    /// SentinelConfiguration), which no compile gate can catch. A synthesized destination class (no
    /// existing target) always has an implicit public parameterless constructor, so it needs no check.
    /// </summary>
    private static async Task EnsureNewFixupsAreConstructibleAsync(IEnumerable<(string Key, string Value)> callerFixups, Document? existingTargetDoc, ClassDeclarationSyntax? existingTargetClassNode, CancellationToken cancellationToken)
    {
        var newKeys = callerFixups.Where(f => f.Value == CallSiteFixupMap.NewKeyword).Select(f => f.Key).Distinct(StringComparer.Ordinal).ToList();
        if (newKeys.Count == 0 || existingTargetDoc == null || existingTargetClassNode == null)
        {
            return;
        }

        var model = await existingTargetDoc.GetSemanticModelAsync(cancellationToken);
        if (model?.GetDeclaredSymbol(existingTargetClassNode, cancellationToken) is not INamedTypeSymbol targetType)
        {
            // Symbol unresolvable - leave it to the compile gate rather than refusing on a guess.
            return;
        }

        bool zeroArgCallable = !targetType.IsAbstract && !targetType.IsStatic && targetType.InstanceConstructors.Any(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal && c.Parameters.All(p => p.IsOptional || p.IsParams));
        if (zeroArgCallable)
        {
            return;
        }

        const int maxKeysShown = 3;
        var name = targetType.Name;
        var keyList = string.Join(", ", newKeys.Take(maxKeysShown).Select(k => $"\"{k}\"")) + (newKeys.Count > maxKeysShown ? $" (+{newKeys.Count - maxKeysShown} more)" : "");
        var signatures = targetType.InstanceConstructors.Length == 0 ? "(none)" : string.Join("; ", targetType.InstanceConstructors.Select(c => FormatConstructorSignature(c, name)));
        var widest = targetType.InstanceConstructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        var example = widest is { Parameters.Length: > 0 } ? $"new {name}({string.Join(", ", widest.Parameters.Select(p => $"<{p.Name}>"))})" : $"new {name}(<args>)";
        var kindNote = targetType.IsStatic ? " (it is static)" : targetType.IsAbstract ? " (it is abstract)" : "";
        throw new ToolInvalidArgumentException($"{newKeys.Count} callSiteFixups key(s) use the value \"new\": {keyList}. \"new\" means a zero-argument constructor call only ('new {name}()'), " + $"but '{name}' has no accessible constructor callable with zero arguments{kindNote}. Its constructor(s): {signatures}. " + $"Pass a full receiver expression instead, e.g. \"{example}\", or the name of an existing in-scope field/property of type '{name}'. " + "Supply ALL constructor arguments explicitly, including configuration/options arguments: omitting an optional one can compile but silently change behavior. " + "No changes were made.");
    }

    private static ITypeSymbol? GetSymbolType(ISymbol symbol) => symbol switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        IParameterSymbol pa => pa.Type,
        ILocalSymbol l => l.Type,
        _ => null
    };

    private static string FormatConstructorSignature(IMethodSymbol constructor, string typeName)
    {
        var parameters = constructor.Parameters.Select(p =>
        {
            var text = (p.IsParams ? "params " : "") + p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name;
            if (p.HasExplicitDefaultValue)
            {
                text += " = " + (p.ExplicitDefaultValue switch
                {
                    null => "null",
                    string s => $"\"{s}\"",
                    bool b => b ? "true" : "false",
                    var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            return text;
        });
        return $"{SyntaxFacts.GetText(constructor.DeclaredAccessibility)} {typeName}({string.Join(", ", parameters)})".TrimStart();
    }

    public async Task<DocumentEditResult> AddBaseTypeAsync(FilePathWrapper filePath, string typeName, string baseTypeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        BaseTypeDeclarationSyntax? container = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, typeName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: type not found."
            };
        }

        // Idempotency check
        if (container.BaseList?.Types.Any(t => t.ToString().Contains(baseTypeName)) == true)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: base type already exists.",
                UpdatedText = root!.ToFullString()
            };
        }

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseTypeName));
        var newContainer = container.AddBaseListTypes(baseType);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, container, newContainer, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> RemoveBaseTypeAsync(FilePathWrapper filePath, string typeName, string baseTypeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        BaseTypeDeclarationSyntax? container = null;
        try
        {
            var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, typeName, cancellationToken)
                .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                .ToList();
            container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, contextSnippet, lineBefore, lineAfter,
                (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (container == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: type not found."
            };
        }

        if (container.BaseList == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: base type not found."
            };
        }

        var remaining = container.BaseList.Types.Where(t => !t.ToString().Contains(baseTypeName)).ToList();
        var newContainer = remaining.Count == 0 ? container.WithBaseList(null) : container.WithBaseList(container.BaseList.WithTypes(SyntaxFactory.SeparatedList(remaining)));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, container, newContainer, cancellationToken)
        };
    }

    /// <summary>
    /// Batch form of <see cref="AddBaseTypeAsync"/>/<see cref="RemoveBaseTypeAsync"/>: same execution
    /// model as <see cref="ApplyModifierBatchAsync"/>. Resolve every edit's type target via <see cref="SyntaxTargetResolver"/>
    /// against ONE original root, reject same-node collisions, then apply all edits as text spans
    /// against the original text via <see cref="AttributeTextEditBuilder.TryApply"/> (so a nested type and its containing
    /// type compose); overlapping spans are rejected with a message naming both edits. AppliedEditIndexes lists the edits applied.
    /// </summary>
    public async Task<DocumentEditResult> ApplyBaseTypeBatchAsync(FilePathWrapper filePath, IReadOnlyList<(int Index, string TypeName, string BaseTypeName, AddRemoveAction Action, string? ContextSnippet, string? LineBefore, string? LineAfter)> edits, CancellationToken cancellationToken = default)
    {
        // READCHOKEPOINT-CAST: see FormatDocumentAsync above for rationale (40-site constructor cascade avoided).
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, FilePath = filePath, Message = "// Document not found." };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = "// Cannot edit: syntax root not found." };
        }

        var errors = new List<string>();
        var resolvedTargets = new Dictionary<int, BaseTypeDeclarationSyntax>();
        foreach (var edit in edits)
        {
            try
            {
                var typeCandidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, edit.TypeName, cancellationToken)
                    .Where(c => c.Kind is CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum)
                    .ToList();
                var container = SyntaxTargetResolver.ResolveBySnippetOrThrow(typeCandidates, sourceText, edit.ContextSnippet, edit.LineBefore, edit.LineAfter,
                    (candidates, matches, failureMode) => SyntaxTargetResolver.BuildHintForCandidates(candidates, matches, failureMode))?.Node as BaseTypeDeclarationSyntax;
                if (container == null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TypeName}): type not found.");
                    continue;
                }
                resolvedTargets[edit.Index] = container;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"edits[{edit.Index}] ({edit.TypeName}): {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var seen = new Dictionary<BaseTypeDeclarationSyntax, int>();
        foreach (var kvp in resolvedTargets)
        {
            if (seen.TryGetValue(kvp.Value, out var firstIndex))
            {
                errors.Add($"edits[{firstIndex}] and edits[{kvp.Key}] both _symbolNavigationEngine. Resolve to the same target in '{filePath}'. Split these into separate calls.");
            }
            else
            {
                seen[kvp.Value] = kvp.Key;
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        // Text-span edits against the ORIGINAL text (same approach as ApplyAttributeBatchAsync). Folding ReplaceNode over
        // type nodes silently dropped a nested type's edit when its containing type was also edited, because the
        // replacement of an ancestor discards the descendant's pending replacement.
        var textEdits = new List<AttributeTextEditBuilder.TextEdit>();
        var appliedIndexes = new List<int>();
        foreach (var edit in edits)
        {
            var container = resolvedTargets[edit.Index];
            if (edit.Action == AddRemoveAction.add)
            {
                if (container.BaseList?.Types.Any(t => t.ToString().Contains(edit.BaseTypeName)) == true)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TypeName}): base type already exists.");
                    continue;
                }
                textEdits.Add(BaseTypeTextEditBuilder.BuildAddEdit(edit.Index, container, edit.BaseTypeName));
                appliedIndexes.Add(edit.Index);
            }
            else
            {
                var removeEdit = BaseTypeTextEditBuilder.BuildRemoveEdit(edit.Index, container, edit.BaseTypeName);
                if (removeEdit is null)
                {
                    errors.Add($"edits[{edit.Index}] ({edit.TypeName}): base type '{edit.BaseTypeName}' not found.");
                    continue;
                }
                textEdits.Add(removeEdit.Value);
                appliedIndexes.Add(edit.Index);
            }
        }

        if (errors.Count > 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = string.Join("\n", errors) };
        }

        var updatedText = AttributeTextEditBuilder.TryApply(sourceText, textEdits, out var overlapError);
        if (updatedText is null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = overlapError ?? "Edits change overlapping source text. Split these into separate calls." };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText,
            AppliedEditIndexes = appliedIndexes
        };
    }
}

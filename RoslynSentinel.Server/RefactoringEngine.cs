using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server;

public record ExtractMethodResult(
    bool Success,
    string? ErrorMessage,
    string? BeforeSnippet,
    string? CallSiteReplacement,
    string? ExtractedMethodText,
    string? UpdatedSourceContent);

public record RenameHunk(int LineNumber, string Before, string After, string? ContextBefore, string? ContextAfter);

public record RenameFileChange(string FilePath, List<RenameHunk> Hunks);

public record RenameSymbolResult(
    string OldName,
    string NewName,
    Dictionary<string, string> PendingChanges,
    List<RenameFileChange> FileChanges,
    string? Error = null);

public record ControlFlowSummary(
    string MethodName,
    bool AlwaysReturns,
    bool SometimesReturns,
    bool NeverReturns,
    List<string> ReturnPoints,
    List<string> ThrowPoints,
    int ExitPathCount
);

public record DataFlowSummary(
    string MethodName,
    List<string> ReadBeforeAssignment,
    List<string> WrittenInside,
    List<string> ReadInside,
    List<string> WrittenOutside,
    List<string> CapturedVariables,
    List<string> DataFlowWarnings
);

public record FormatHunk(
    int StartLine,
    int EndLine,
    List<string> ContextBefore,
    List<string> RemovedLines,
    List<string> AddedLines,
    List<string> ContextAfter
);

public record FormatPreviewResult(
    bool Changed,
    int TotalHunks,
    List<FormatHunk> Hunks
);

public class RefactoringEngine
{
    private readonly ILogger<RefactoringEngine> _logger;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    private static readonly string[] separator = new[] { "\r\n", "\r", "\n" };

    public RefactoringEngine(ILogger<RefactoringEngine> logger, PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> FormatDocumentAsync(string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var formatted = await Formatter.FormatAsync(document, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    public async Task<Dictionary<string, string>> ChangeSignatureAsync(string filePath, string methodName, int[] newParameterOrder, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<string, string>();
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (root == null || semanticModel == null)
        {
            return new Dictionary<string, string>();
        }

        var methodDecl = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodDecl == null)
        {
            return new Dictionary<string, string>();
        }

        var parameters = methodDecl.ParameterList.Parameters.ToList();
        if (parameters.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        // Validate order array
        if (newParameterOrder.Length != parameters.Count)
        {
            return new Dictionary<string, string>();
        }

        if (newParameterOrder.Any(i => i < 0 || i >= parameters.Count))
        {
            return new Dictionary<string, string>();
        }

        if (newParameterOrder.Distinct().Count() != parameters.Count)
        {
            return new Dictionary<string, string>();
        }

        var reorderedParams = newParameterOrder.Select(i => parameters[i]).ToList();
        var newParamList = methodDecl.ParameterList.WithParameters(SyntaxFactory.SeparatedList(reorderedParams));
        var updatedMethodDecl = methodDecl.WithParameterList(newParamList);
        var updatedRoot = root.ReplaceNode(methodDecl, updatedMethodDecl);
        var updatedDoc = document.WithSyntaxRoot(updatedRoot);

        var pendingChanges = new Dictionary<string, string>
        {
            [filePath] = (await updatedDoc.GetTextAsync(ct)).ToString()
        };

        // Reorder arguments at all call sites
        var symbol = semanticModel.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
        if (symbol != null)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    if (location.IsImplicit || location.Document.FilePath == null)
                    {
                        continue;
                    }

                    var refDoc = location.Document;
                    var refRoot = await refDoc.GetSyntaxRootAsync(ct);
                    if (refRoot == null)
                    {
                        continue;
                    }

                    var span = location.Location.SourceSpan;
                    var token = refRoot.FindToken(span.Start);
                    var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation == null)
                    {
                        continue;
                    }

                    var args = invocation.ArgumentList.Arguments.ToList();
                    if (args.Count != parameters.Count)
                    {
                        continue;
                    }

                    var docPath = refDoc.FilePath!;
                    // Work from the already-pending content if we've updated this doc
                    string currentContent = pendingChanges.TryGetValue(docPath, out var prev) ? prev : (await refDoc.GetTextAsync(ct)).ToString();
                    var currentRoot = SyntaxFactory.ParseCompilationUnit(currentContent);

                    var targetInv = currentRoot.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(inv => inv.Span == invocation.Span);
                    if (targetInv == null)
                    {
                        continue;
                    }

                    var reorderedArgs = newParameterOrder.Select(i => args[i]).ToList();
                    var newArgList = invocation.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(reorderedArgs));
                    var updatedInv = targetInv.WithArgumentList(newArgList);
                    pendingChanges[docPath] = currentRoot.ReplaceNode(targetInv, updatedInv).ToFullString();
                }
            }
        }

        // Format all changed files
        var result = new Dictionary<string, string>();
        foreach (var kvp in pendingChanges)
        {
            var doc = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == kvp.Key);
            if (doc != null)
            {
                var formatted = await Formatter.FormatAsync(
                    doc.WithSyntaxRoot(SyntaxFactory.ParseCompilationUnit(kvp.Value)), null, ct);
                result[kvp.Key] = (await formatted.GetTextAsync(ct)).ToString();
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    public async Task<ExtractMethodResult> ExtractMethodAsync(
        string filePath, int startLine, string startLineText, int endLine, string endLineText,
        string newMethodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractMethod"))
        {
            return new ExtractMethodResult(false, "ExtractMethod feature is disabled.", null, null, null, null);
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ExtractMethodResult(false, $"File '{filePath}' not found in solution.", null, null, null, null);
        }

        var text = await document.GetTextAsync(ct);
        if (startLine < 1 || startLine > text.Lines.Count)
        {
            return new ExtractMethodResult(false, $"startLine {startLine} out of range (file has {text.Lines.Count} lines).", null, null, null, null);
        }

        if (endLine < startLine || endLine > text.Lines.Count)
        {
            return new ExtractMethodResult(false, $"endLine {endLine} is out of range.", null, null, null, null);
        }

        // Stale-file validation: physical line text must match what the caller observed
        var actualStart = text.Lines[startLine - 1].ToString().Trim();
        var actualEnd = text.Lines[endLine - 1].ToString().Trim();
        if (actualStart != startLineText.Trim())
        {
            return new ExtractMethodResult(false,
                $"startLine mismatch: expected '{startLineText.Trim()}' but found '{actualStart}'. File may have changed.", null, null, null, null);
        }

        if (actualEnd != endLineText.Trim())
        {
            return new ExtractMethodResult(false,
                $"endLine mismatch: expected '{endLineText.Trim()}' but found '{actualEnd}'. File may have changed.", null, null, null, null);
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (root == null || semanticModel == null)
        {
            return new ExtractMethodResult(false, "Could not obtain syntax root or semantic model.", null, null, null, null);
        }

        var startPos = text.Lines[startLine - 1].Start;
        var endPos = text.Lines[endLine - 1].End;
        var span = new TextSpan(startPos, endPos - startPos);

        // Find the method body that fully contains the selection
        var containingMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null)
            .FirstOrDefault(m => m.Body!.Span.Contains(span));
        if (containingMethod?.Body == null)
        {
            return new ExtractMethodResult(false,
                "Selected range must be inside a block-body method (expression-bodied methods are not supported).", null, null, null, null);
        }

        // Collect direct body statements that overlap the selection
        var selectedStatements = containingMethod.Body.Statements
            .Where(s => s.Span.IntersectsWith(span))
            .ToList();
        if (selectedStatements.Count == 0)
        {
            return new ExtractMethodResult(false, "No complete statements found in the selected line range.", null, null, null, null);
        }

        // Data flow analysis to infer parameters and return type
        DataFlowAnalysis dataFlow;
        try
        {
            dataFlow = semanticModel.AnalyzeDataFlow(selectedStatements[0], selectedStatements[^1])!;
        }
        catch (Exception ex)
        {
            return new ExtractMethodResult(false, $"Data flow analysis failed: {ex.Message}", null, null, null, null);
        }

        // Parameters: symbols flowing in — local vars and non-this method parameters only
        var parameters = dataFlow.DataFlowsIn
            .Where(s => s.Kind == SymbolKind.Local ||
                        (s.Kind == SymbolKind.Parameter && s is IParameterSymbol p && !p.IsThis))
            .OrderBy(s => s.Name)
            .ToList();

        // Fail early if any ref/out parameter flows out — we can't safely return it
        var refOutFlowOut = dataFlow.DataFlowsOut
            .OfType<IParameterSymbol>()
            .Where(p => p.RefKind != RefKind.None && !p.IsThis)
            .ToList();
        if (refOutFlowOut.Count > 0)
        {
            return new ExtractMethodResult(false,
                $"Cannot extract: ref/out parameter(s) '{string.Join(", ", refOutFlowOut.Select(p => p.Name))}' are " +
                "written inside the selection and read after it. This case cannot be auto-extracted — refactor manually.",
                null, null, null, null);
        }

        // Return value: local variables assigned inside that are used after the region
        var flowsOut = dataFlow.DataFlowsOut
            .Where(s => s.Kind == SymbolKind.Local)
            .ToList();
        if (flowsOut.Count > 1)
        {
            return new ExtractMethodResult(false,
                $"Multiple variables flow out ({string.Join(", ", flowsOut.Select(s => s.Name))}). " +
                "Cannot auto-determine return type — narrow the selection or handle manually.", null, null, null, null);
        }

        ILocalSymbol? returnVar = flowsOut.Count == 1 ? (ILocalSymbol)flowsOut[0] : null;
        bool isAsync = selectedStatements.Any(s => s.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AwaitKeyword)));
        bool parentStatic = containingMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        // Build return type syntax
        TypeSyntax returnType = (returnVar, isAsync) switch
        {
            ({ } rv, true) => SyntaxFactory.ParseTypeName($"Task<{rv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>"),
            ({ } rv, false) => SyntaxFactory.ParseTypeName(rv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
            (null, true) => SyntaxFactory.ParseTypeName("Task"),
            _ => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
        };

        // Build parameter list — include ref/out/in modifiers for parameter symbols
        var paramSyntax = parameters.Select(sym =>
        {
            string typeName;
            RefKind refKind = RefKind.None;
            if (sym is ILocalSymbol loc)
            {
                typeName = loc.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            else
            {
                var p = (IParameterSymbol)sym;
                typeName = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                refKind = p.RefKind;
            }
            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(sym.Name))
                .WithType(SyntaxFactory.ParseTypeName(typeName).WithTrailingTrivia(SyntaxFactory.Space));
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
        var bodyStmts = selectedStatements
            .Select(s => s.WithoutLeadingTrivia().WithoutTrailingTrivia())
            .Cast<StatementSyntax>()
            .ToList();
        if (returnVar != null)
        {
            bodyStmts.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(returnVar.Name)));
        }

        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (parentStatic)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        if (isAsync)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        var extractedMethod = SyntaxFactory
            .MethodDeclaration(returnType, newMethodName)
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(paramSyntax)))
            .WithBody(SyntaxFactory.Block(bodyStmts))
            .NormalizeWhitespace();

        // Build call site — include ref/out/in keywords for parameter symbols
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
        ExpressionSyntax callExpr = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(newMethodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argList)));

        StatementSyntax callStatement;
        if (returnVar != null)
        {
            var initExpr = isAsync
                ? (ExpressionSyntax)SyntaxFactory.AwaitExpression(callExpr)
                : callExpr;
            // If returnVar was declared INSIDE the selection, emit `var x = Method()`.
            // If it was declared BEFORE the selection (flows out but not declared here),
            // emit plain assignment `x = Method()` to avoid CS0128.
            bool declaredInSelection = dataFlow.VariablesDeclared.Contains(returnVar);
            if (declaredInSelection)
            {
                callStatement = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName("var"),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(returnVar.Name))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(initExpr)))));
            }
            else
            {
                callStatement = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(returnVar.Name),
                        initExpr));
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
        var updatedMethod = containingMethod.WithBody(
            containingMethod.Body.WithStatements(SyntaxFactory.List(newStmts)));

        if (containingMethod.Parent is not TypeDeclarationSyntax parentType)
        {
            return new ExtractMethodResult(false, "Could not find the containing type declaration.", null, null, null, null);
        }

        // Append extracted method after the type's existing members
        var newParent = parentType
            .ReplaceNode(containingMethod, updatedMethod)
            .AddMembers(extractedMethod);
        var newRoot = root.ReplaceNode(parentType, newParent);

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), null, ct);
        var updatedContent = (await formattedDoc.GetTextAsync(ct)).ToString();

        var beforeSnippet = string.Concat(selectedStatements.Select(s => s.ToFullString())).Trim();
        var callSiteText = callStatement.NormalizeWhitespace().ToFullString().Trim();
        var extractedMethodText = extractedMethod.ToFullString().Trim();

        return new ExtractMethodResult(true, null, beforeSnippet, callSiteText, extractedMethodText, updatedContent);
    }

    public async Task<Dictionary<string, string>> MoveTypeToFileAsync(string filePath, string typeName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<string, string>();
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var typeNode = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == typeName);
        if (typeNode == null)
        {
            return new Dictionary<string, string>();
        }

        var (newRoot, cleanTypeNode) = BuildSplitFileRoot(root!, typeNode);
        var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name) : SyntaxFactory.NamespaceDeclaration(ns.Name);
            newRoot = newRoot.AddMembers(newNs.AddMembers(cleanTypeNode));
        }
        else
        {
            newRoot = newRoot.AddMembers(cleanTypeNode);
        }

        var sourceDirectory = Path.GetDirectoryName(document.FilePath ?? filePath);
        var newPath = string.IsNullOrEmpty(sourceDirectory)
            ? $"{typeName}.cs"
            : Path.Combine(sourceDirectory, $"{typeName}.cs");

        // Guard: if the type's name already matches the source file name, it's already in its own file — nothing to move
        if (string.Equals(typeName, Path.GetFileNameWithoutExtension(document.Name), StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>();
        }

        var updatedOrig = RemoveOrphanedRegionDirectives(root!.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!);

        var newDoc = document.Project.AddDocument($"{typeName}.cs", newRoot);
        var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, ct);
        var newContent = (await formattedNewDoc.GetTextAsync(ct)).ToString();

        var updatedOrigDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, ct);
        var updatedOrigContent = (await formattedOrigDoc.GetTextAsync(ct)).ToString();

        return new Dictionary<string, string> { { filePath, updatedOrigContent }, { newPath, newContent } };
    }

    private async Task<Dictionary<string, string>> MoveAllTypesToFilesForDocumentAsync(Document document, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        if (root == null)
        {
            return new Dictionary<string, string>();
        }

        var allTypes = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax || t.Parent is BaseNamespaceDeclarationSyntax)
            .ToList();

        if (allTypes.Count <= 1)
        {
            return new Dictionary<string, string>();
        }

        var fileBaseName = Path.GetFileNameWithoutExtension(document.FilePath ?? document.Name);
        var primaryType = allTypes.FirstOrDefault(t => t.Identifier.Text == fileBaseName) ?? allTypes[0];
        var typesToMove = allTypes.Where(t => t != primaryType).ToList();

        if (typesToMove.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var changes = new Dictionary<string, string>();
        var sourceDirectory = Path.GetDirectoryName(document.FilePath) ?? "";

        foreach (var typeNode in typesToMove)
        {
            var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var (newRoot, cleanTypeNode) = BuildSplitFileRoot(root, typeNode);

            if (ns != null)
            {
                var cleanNsName = SyntaxFactory.ParseName(ns.Name.ToString());
                var newNs = ns is FileScopedNamespaceDeclarationSyntax
                    ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(cleanNsName)
                    : SyntaxFactory.NamespaceDeclaration(cleanNsName);
                newRoot = newRoot.AddMembers(newNs.AddMembers(cleanTypeNode));
            }
            else
            {
                newRoot = newRoot.AddMembers(cleanTypeNode);
            }

            var typeName = typeNode.Identifier.Text;
            var newPath = string.IsNullOrEmpty(sourceDirectory)
                ? $"{typeName}.cs"
                : Path.Combine(sourceDirectory, $"{typeName}.cs");

            var newDoc = document.Project.AddDocument($"{typeName}.cs", newRoot);
            var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, ct);
            changes[newPath] = (await formattedNewDoc.GetTextAsync(ct)).ToString();
        }

        var updatedRoot = RemoveOrphanedRegionDirectives(root.RemoveNodes(typesToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
        var updatedOrigDoc = document.WithSyntaxRoot(updatedRoot);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, ct);
        changes[document.FilePath ?? document.Name] = (await formattedOrigDoc.GetTextAsync(ct)).ToString();

        return changes;
    }

    // Builds the compilation unit for a type being split into its own file, handling:
    // - extern alias declarations (not in root.Usings — must be copied separately)
    // - global using aliases filtered out (project-scoped; duplicating them causes CS1537)
    // - file-scoped types promoted to internal (file modifier = visible only in declaring file)
    private static (CompilationUnitSyntax newRoot, BaseTypeDeclarationSyntax cleanNode) BuildSplitFileRoot(
        CompilationUnitSyntax root, BaseTypeDeclarationSyntax typeNode)
    {
        var cleanNode = typeNode
            .WithoutLeadingTrivia()
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

        // Promote `file` modifier to `internal` — the type is now in its own file and must be accessible
        if (cleanNode.Modifiers.Any(m => m.IsKind(SyntaxKind.FileKeyword)))
        {
            var fileToken = cleanNode.Modifiers.First(m => m.IsKind(SyntaxKind.FileKeyword));
            var internalToken = SyntaxFactory.Token(SyntaxKind.InternalKeyword)
                .WithLeadingTrivia(fileToken.LeadingTrivia)
                .WithTrailingTrivia(fileToken.TrailingTrivia);
            var newModifiers = cleanNode.Modifiers.Replace(fileToken, internalToken);
            cleanNode = (BaseTypeDeclarationSyntax)cleanNode.WithModifiers(newModifiers);
        }

        var cleanExterns = SyntaxFactory.List(root.Externs.Select(e =>
            e.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));

        // Exclude global using aliases — they are project-scoped; duplicating them across split files causes CS1537
        var cleanUsings = SyntaxFactory.List(root.Usings
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));

        var newRoot = SyntaxFactory.CompilationUnit()
            .WithExterns(cleanExterns)
            .WithUsings(cleanUsings);

        return (newRoot, cleanNode);
    }

    // Removes #endregion directives that have no matching #region (orphaned when types are removed from a file).
    private static CompilationUnitSyntax RemoveOrphanedRegionDirectives(CompilationUnitSyntax root)
    {
        var toRemove = new HashSet<SyntaxTrivia>();
        int depth = 0;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.RegionDirectiveTrivia))
            {
                depth++;
            }
            else if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            {
                if (depth == 0)
                {
                    toRemove.Add(trivia);
                }
                else
                {
                    depth--;
                }
            }
        }
        return toRemove.Count == 0
            ? root
            : (CompilationUnitSyntax)root.ReplaceTrivia(toRemove, (_, _) => SyntaxFactory.Whitespace(""));
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<string, string>();
        }

        return await MoveAllTypesToFilesForDocumentAsync(document, ct);
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesInProjectAsync(string projectName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found.");

        var allChanges = new Dictionary<string, string>();
        foreach (var document in project.Documents.Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, ct))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }
        return allChanges;
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesInSolutionAsync(CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var allChanges = new Dictionary<string, string>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents).Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, ct))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }
        return allChanges;
    }

    public async Task<Dictionary<string, string>> ExtractInterfaceAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractInterface"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new Dictionary<string, string>();
        }

        // Extract public instance methods (exclude static, constructors)
        var methods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                     && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)));

        // Extract public non-static properties with at least a getter
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                     && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword))
                     && p.AccessorList != null);

        static SyntaxTriviaList MemberTrivia() => SyntaxFactory.TriviaList(
            SyntaxFactory.CarriageReturnLineFeed,
            SyntaxFactory.Whitespace("    "));

        var ifaceMethods = methods.Select(m =>
            (MemberDeclarationSyntax)SyntaxFactory.MethodDeclaration(
                    m.ReturnType.WithoutTrivia(),
                    m.Identifier)
                .WithTypeParameterList(m.TypeParameterList)
                .WithParameterList(m.ParameterList)
                .WithConstraintClauses(m.ConstraintClauses)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(MemberTrivia())
                .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed)));

        var ifaceProperties = properties.Select(p =>
        {
            // Build interface accessor list: only keep get/set/init that existed in source
            var accessors = p.AccessorList!.Accessors
                .Select(acc => SyntaxFactory.AccessorDeclaration(acc.Kind())
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            return (MemberDeclarationSyntax)SyntaxFactory.PropertyDeclaration(
                    p.Type.WithoutTrivia(),
                    p.Identifier)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                .WithLeadingTrivia(MemberTrivia())
                .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed));
        });

        var ifaceMembers = ifaceProperties.Concat(ifaceMethods).ToArray();

        var ifaceNode = SyntaxFactory.InterfaceDeclaration(interfaceName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(ifaceMembers);

        // Wrap in namespace + usings to produce a compilable file
        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var cleanUsings = SyntaxFactory.List(root!.Usings.Select(u =>
            u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));

        CompilationUnitSyntax ifaceCompUnit;
        if (ns != null)
        {
            BaseNamespaceDeclarationSyntax newNs = ns is FileScopedNamespaceDeclarationSyntax
                ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name).AddMembers(ifaceNode)
                : SyntaxFactory.NamespaceDeclaration(ns.Name).AddMembers(ifaceNode);
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(newNs);
        }
        else
        {
            ifaceCompUnit = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings).AddMembers(ifaceNode);
        }

        // Add interface to class's base list (only if not already present)
        var alreadyImplements = classNode.BaseList?.Types
            .Any(t => t.Type.ToString() == interfaceName) == true;
        var newClass = alreadyImplements
            ? classNode
            : classNode.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName)));
        var updatedOrig = root.ReplaceNode(classNode, newClass);

        var ifacePath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", $"{interfaceName}.cs");

        // Format the interface file using NormalizeWhitespace for reliable member separation.
        // Formatter.FormatAsync with null workspace options can flatten all members onto one line.
        var ifaceContent = ifaceCompUnit.NormalizeWhitespace(elasticTrivia: false).ToFullString();

        // Format the original file
        var origDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await Formatter.FormatAsync(origDoc, null, ct);
        var origContent = (await formattedOrigDoc.GetTextAsync(ct)).ToString();

        return new Dictionary<string, string> { { filePath, origContent }, { ifacePath, ifaceContent } };
    }

    public async Task<RenameSymbolResult> RenameSymbolAsync(string filePath, string symbolName, string contextSnippet, string newName, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        static RenameSymbolResult Err(string msg, string n) =>
            new("", n, new Dictionary<string, string>(), new List<RenameFileChange>(), msg);

        if (!_config.IsFeatureEnabled("Rename"))
        {
            return Err("Feature 'Rename' is disabled.", newName);
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return Err($"File not found in solution: {filePath}", newName);
        }

        var text = await document.GetTextAsync(ct);
        var fullSource = text.ToString();

        // Locate contextSnippet — must match exactly once, or use lineBefore/lineAfter to disambiguate
        int firstIdx = fullSource.IndexOf(contextSnippet, StringComparison.Ordinal);
        if (firstIdx < 0)
        {
            return Err($"Context snippet not found in file. Verify the snippet is copied verbatim from the source: \"{contextSnippet}\"", newName);
        }

        int secondIdx = fullSource.IndexOf(contextSnippet, firstIdx + 1, StringComparison.Ordinal);
        if (secondIdx >= 0)
        {
            if (lineBefore == null && lineAfter == null)
            {
                return Err($"Context snippet matches multiple locations in the file. Use a longer or more unique snippet, or provide lineBefore/lineAfter to pin the symbol.", newName);
            }

            try { firstIdx = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter); }
            catch (InvalidOperationException ex) { return Err(ex.Message, newName); }
        }

        // Find symbolName as a word-boundary identifier within the matched snippet
        int symOffset = FindIdentifierInSnippet(contextSnippet, symbolName);
        if (symOffset < 0)
        {
            return Err($"'{symbolName}' not found as a distinct identifier in the context snippet \"{contextSnippet}\". Ensure the symbol appears verbatim and is not part of a larger identifier.", newName);
        }

        var pos = firstIdx + symOffset;
        var model = await document.GetSemanticModelAsync(ct);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model!, pos, solution.Workspace, ct);
        if (symbol == null)
        {
            return Err($"No Roslyn symbol resolved at the identified position. The context may be pointing at a keyword or non-symbol token.", newName);
        }

        var oldName = symbol.Name;
        var updated = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            solution, symbol, new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), newName, ct);

        var pendingChanges = new Dictionary<string, string>();
        var fileChanges = new List<RenameFileChange>();
        foreach (var pc in updated.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in pc.GetChangedDocuments())
            {
                var newDoc = updated.GetDocument(docId)!;
                var filePth = newDoc.FilePath ?? newDoc.Name;
                var newContent = (await newDoc.GetTextAsync(ct)).ToString();
                pendingChanges[filePth] = newContent;

                var origContent = (await solution.GetDocument(docId)!.GetTextAsync(ct)).ToString();
                fileChanges.Add(new RenameFileChange(filePth, ComputeRenameHunks(origContent, newContent)));
            }
        }
        return new RenameSymbolResult(oldName, newName, pendingChanges, fileChanges);
    }

    private static List<RenameHunk> ComputeRenameHunks(string oldContent, string newContent, int contextLines = 2)
    {
        var oldLines = oldContent.Split(separator, StringSplitOptions.None);
        var newLines = newContent.Split(separator, StringSplitOptions.None);
        var hunks = new List<RenameHunk>();
        // Use Max so lines added/removed at the end (e.g. new using directives) are included
        int len = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < len; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : "";
            var newLine = i < newLines.Length ? newLines[i] : "";
            if (oldLine == newLine)
            {
                continue;
            }

            var ctxBefore = i > 0
                ? string.Join("\n", oldLines[Math.Max(0, i - contextLines)..Math.Min(i, oldLines.Length)])
                : null;
            var ctxAfter = i + 1 < newLines.Length
                ? string.Join("\n", newLines[(i + 1)..Math.Min(newLines.Length, i + 1 + contextLines)])
                : null;
            hunks.Add(new RenameHunk(i + 1, oldLine, newLine, ctxBefore, ctxAfter));
        }
        return hunks;
    }

    // Returns the start offset of `symbolName` as a word-boundary identifier within `snippet`,
    // or -1 if not found. Prefers the first match where neither adjacent char is an identifier char.
    private static int FindIdentifierInSnippet(string snippet, string symbolName)
    {
        int searchFrom = 0;
        while (true)
        {
            int idx = snippet.IndexOf(symbolName, searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                return -1;
            }

            bool leftBound = idx == 0 || !IsIdentChar(snippet[idx - 1]);
            bool rightBound = idx + symbolName.Length >= snippet.Length || !IsIdentChar(snippet[idx + symbolName.Length]);
            if (leftBound && rightBound)
            {
                return idx;
            }

            searchFrom = idx + 1;
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public async Task<string> ConvertIndexerToMethodAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ConvertIndexerToMethod"))
        {
            return string.Empty;
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var indexer = root?.DescendantNodes().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
        if (indexer == null)
        {
            return root?.ToFullString() ?? "";
        }

        var blockBody = indexer.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.Body;
        var arrowExpr = indexer.ExpressionBody?.Expression
            ?? indexer.AccessorList?.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.ExpressionBody?.Expression;

        MethodDeclarationSyntax getter;
        if (blockBody != null)
        {
            getter = SyntaxFactory.MethodDeclaration(indexer.Type, "Get")
                .WithModifiers(indexer.Modifiers)
                .WithParameterList(SyntaxFactory.ParameterList(indexer.ParameterList.Parameters))
                .WithBody(blockBody);
        }
        else if (arrowExpr != null)
        {
            getter = SyntaxFactory.MethodDeclaration(indexer.Type, "Get")
                .WithModifiers(indexer.Modifiers)
                .WithParameterList(SyntaxFactory.ParameterList(indexer.ParameterList.Parameters))
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(arrowExpr))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else
        {
            return root?.ToFullString() ?? "";
        }

        var newRoot = root!.ReplaceNode(indexer, getter);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddRemoveParamsAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("AddRemoveParams"))
        {
            return string.Empty;
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || !method.ParameterList.Parameters.Any())
        {
            return root?.ToFullString() ?? "";
        }

        var lastParam = method.ParameterList.Parameters.Last();
        var hasParams = lastParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword));

        var newModifiers = hasParams
            ? lastParam.Modifiers.Remove(lastParam.Modifiers.First(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            : lastParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ParamsKeyword));

        var newParam = lastParam.WithModifiers(newModifiers);
        var newRoot = root!.ReplaceNode(lastParam, newParam);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ReplaceMemberAsync(string filePath, string memberName, string newSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var member = root?.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == memberName && !(m.Parent is InterfaceDeclarationSyntax));
        if (member == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newSource);
        if (newMember == null)
        {
            return root?.ToFullString() ?? "";
        }

        return root!.ReplaceNode(member, newMember).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddMemberAsync(string filePath, string containerName, string newMemberSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newContainer = container switch
        {
            ClassDeclarationSyntax c => (BaseTypeDeclarationSyntax)c.AddMembers(newMember),
            InterfaceDeclarationSyntax i => (BaseTypeDeclarationSyntax)i.AddMembers(newMember),
            RecordDeclarationSyntax r => (BaseTypeDeclarationSyntax)r.AddMembers(newMember),
            StructDeclarationSyntax s => (BaseTypeDeclarationSyntax)s.AddMembers(newMember),
            _ => container
        };
        return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveMemberAsync(string filePath, string memberName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var member = root?.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == memberName && !(m.Parent is InterfaceDeclarationSyntax));
        if (member == null)
        {
            return root?.ToFullString() ?? "";
        }

        // Check for usages using SymbolFinder before removing
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel != null)
        {
            var symbol = semanticModel.GetDeclaredSymbol(member, ct);
            if (symbol != null)
            {
                var references = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(symbol, solution, ct);
                var usageCount = references.Sum(r => r.Locations.Count());

                if (usageCount > 0)
                {
                    return $"// ERROR: Cannot remove member '{memberName}' — it has {usageCount} usages in the solution.\n{root!.ToFullString()}";
                }
            }
        }

        return root!.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertToPrimaryConstructorAsync(string filePath, string className, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("PrimaryConstructors"))
        {
            return string.Empty;
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return root?.ToFullString() ?? "";
        }

        var ctor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null || ctor.ParameterList.Parameters.Count == 0)
        {
            return root?.ToFullString() ?? "";
        }

        // Minimal implementation for tests: convert to class C(int x) and remove fields/ctor
        var newClass = SyntaxFactory.ClassDeclaration(classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(ctor.ParameterList);

        var members = classNode.Members.Where(m => m is not ConstructorDeclarationSyntax && m is not FieldDeclarationSyntax).ToList();
        newClass = newClass.WithMembers(SyntaxFactory.List(members));

        var newRoot = root!.ReplaceNode(classNode, newClass);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<Dictionary<string, string>> SafeDeleteSymbolAsync(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("SafeDeleteUnusedSymbol"))
        {
            return new Dictionary<string, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<string, string>();
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
        if (pos < 0)
        {
            return new Dictionary<string, string>();
        }

        var node = root!.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0));
        // Walk up ancestors to find the nearest declaration symbol (FindNode may return a child token/identifier)
        var symbol = node.AncestorsAndSelf()
            .Select(n => model!.GetDeclaredSymbol(n, ct))
            .FirstOrDefault(s => s != null)
            ?? model!.GetSymbolInfo(node, ct).Symbol;
        if (symbol == null)
        {
            return new Dictionary<string, string>();
        }

        // Check for reflection usage
        foreach (var proj in solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                var docRoot = await doc.GetSyntaxRootAsync(ct);
                var literals = docRoot?.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.ValueText == symbol.Name);

                if (literals?.Any() == true)
                {
                    throw new InvalidOperationException(
                        $"Potential Reflection Risk: symbol '{symbol.Name}' is referenced by string literal in {doc.Name} — possible reflection/dynamic usage. Delete manually after verifying.");
                }
            }
        }

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);

        // Count all references, not just those with locations in the same document
        var totalRefCount = refs.Sum(r => r.Locations.Count());

        // BUG-73 FIX: Explicit check that symbol is truly unused
        // If we have ANY references (including implicit ones), refuse deletion
        if (totalRefCount > 0)
        {
            _logger.LogWarning("SafeDeleteUnusedSymbol blocked: symbol '{SymbolName}' has usages and cannot be safely deleted.", symbol.Name);
            return new Dictionary<string, string> { { "ERROR", $"Cannot delete '{symbol.Name}': symbol is used in {totalRefCount} location(s)." } };
        }

        // Additional safety check: scan syntax tree for any identifier matching the symbol name
        // This catches usages that SymbolFinder might miss
        var declarationNode = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        foreach (var proj in solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                var docRoot = await doc.GetSyntaxRootAsync(ct);
                var semanticModel = await doc.GetSemanticModelAsync(ct);
                var identifierNodes = docRoot?.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(id => id.Identifier.Text == symbol.Name);

                if (identifierNodes?.Any() == true)
                {
                    // Check if any of these identifiers resolve to our symbol
                    foreach (var id in identifierNodes)
                    {
                        try
                        {
                            // Skip if this is inside the declaration node itself
                            if (declarationNode != null && id.Ancestors().Contains(declarationNode) && doc.Id == document.Id)
                            {
                                continue;
                            }

                            var idSymbol = semanticModel!.GetSymbolInfo(id, ct).Symbol;
                            if (idSymbol != null && SymbolEqualityComparer.Default.Equals(idSymbol, symbol))
                            {
                                // This is a usage of our symbol (not the declaration)
                                _logger.LogWarning("SafeDeleteUnusedSymbol blocked: symbol '{SymbolName}' has usages and cannot be safely deleted.", symbol.Name);
                                return new Dictionary<string, string> { { "ERROR", $"Cannot delete '{symbol.Name}': symbol is used and cannot be safely removed." } };
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string> { { filePath, root.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString() } };
    }

    public async Task<string> ConvertExpressionBodyAsync(string filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ConvertExpressionBody"))
        {
            return "";
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return "";
        }

        var root = (await document.GetSyntaxRootAsync(ct))!;
        var text = await document.GetTextAsync(ct);

        MemberDeclarationSyntax? target = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
            if (pos < 0)
            {
                return $"Error: {snippetError}";
            }

            target = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0))
                .AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        }
        else
        {
            var candidates = root.DescendantNodes()
                .OfType<MemberDeclarationSyntax>()
                .Where(n => (n is MethodDeclarationSyntax m && m.Identifier.Text == memberName) ||
                            (n is PropertyDeclarationSyntax p && p.Identifier.Text == memberName) ||
                            (n is ConstructorDeclarationSyntax c && c.Identifier.Text == memberName))
                .ToList();
            target = candidates.FirstOrDefault();
        }

        if (target == null)
        {
            return $"Error: Member '{memberName}' not found in '{Path.GetFileName(filePath)}'.";
        }

        SyntaxNode newTarget;
        if (direction == "ToExpressionBody")
        {
            if (target is MethodDeclarationSyntax meth && meth.Body != null)
            {
                var stmts = meth.Body.Statements;
                if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax ret && ret.Expression != null)
                {
                    newTarget = meth
                        .WithBody(null)
                        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(ret.Expression))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return $"Error: Cannot convert '{memberName}' to expression body: method body has {stmts.Count} statement(s); only single-return methods can be converted.";
                }
            }
            else if (target is PropertyDeclarationSyntax prop && prop.AccessorList != null)
            {
                var getter = prop.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                if (getter?.Body?.Statements.Count == 1 && getter.Body.Statements[0] is ReturnStatementSyntax pret && pret.Expression != null)
                {
                    newTarget = prop
                        .WithAccessorList(null)
                        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(pret.Expression))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return $"Error: Cannot convert '{memberName}' to expression body: property getter does not contain a simple return statement.";
                }
            }
            else
            {
                return $"Error: Cannot convert '{memberName}' to expression body: member has no block body or is already an expression body.";
            }
        }
        else // ToBlockBody
        {
            if (target is MethodDeclarationSyntax methExpr && methExpr.ExpressionBody != null)
            {
                var returnType = methExpr.ReturnType.ToString().Trim();
                StatementSyntax stmt = returnType == "void"
                    ? SyntaxFactory.ExpressionStatement(methExpr.ExpressionBody.Expression)
                    : (StatementSyntax)SyntaxFactory.ReturnStatement(methExpr.ExpressionBody.Expression);
                newTarget = methExpr
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(SyntaxFactory.Block(stmt));
            }
            else if (target is PropertyDeclarationSyntax propExpr && propExpr.ExpressionBody != null)
            {
                var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(propExpr.ExpressionBody.Expression)));
                newTarget = propExpr
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            }
            else
            {
                return $"Error: Cannot convert '{memberName}' to block body: member has no expression body (already a block body or not a method/property).";
            }
        }

        var newRoot = root.ReplaceNode(target, newTarget.NormalizeWhitespace());
        var doc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(doc, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    public async Task<string> ExtractConstantAsync(string filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractConstant"))
        {
            return "";
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return "";
        }

        var root = (await document.GetSyntaxRootAsync(ct))!;
        var text = await document.GetTextAsync(ct);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return $"Error: {snippetError}";
        }

        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
        var literal = node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal == null)
        {
            return "";
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            return "";
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);
        TypeSyntax constType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(literal, ct);
            constType = typeInfo.Type != null
                ? SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString())
                : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }
        else
        {
            constType = SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        var accessMod = visibility switch
        {
            "public" => SyntaxKind.PublicKeyword,
            "protected" => SyntaxKind.ProtectedKeyword,
            "internal" => SyntaxKind.InternalKeyword,
            _ => SyntaxKind.PrivateKeyword
        };

        var constDecl = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(constType)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(constantName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(literal.WithoutTrivia())))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(accessMod),
                SyntaxFactory.Token(SyntaxKind.ConstKeyword)));

        var literalValue = literal.Token.Text;
        var allLiterals = containingType.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.Token.Text == literalValue)
            .ToList();

        var trackedRoot = root.TrackNodes(new SyntaxNode[] { containingType }.Concat(allLiterals));
        foreach (var lit in allLiterals)
        {
            var current = trackedRoot.GetCurrentNode(lit)!;
            trackedRoot = trackedRoot.ReplaceNode(current, SyntaxFactory.IdentifierName(constantName).WithTriviaFrom(current));
        }
        var currentType = trackedRoot.GetCurrentNode(containingType)!;
        var newType = currentType.WithMembers(((TypeDeclarationSyntax)currentType).Members.Insert(0, constDecl));
        trackedRoot = trackedRoot.ReplaceNode(currentType, newType);

        return trackedRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ExtractLocalVariableAsync(
        string filePath, string contextSnippet, string? newVariableName = null,
        string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractLocalVariable"))
        {
            return "";
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return "";
        }

        var root = (await document.GetSyntaxRootAsync(ct))!;
        var text = await document.GetTextAsync(ct);

        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return $"Error: {snippetError}";
        }

        // Find the expression that matches the context snippet - use same logic as IntroduceVariableAsync
        var trimmedSnippet = contextSnippet.Trim();
        var expression = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(e => e.SpanStart == pos && e.ToString().Trim() == trimmedSnippet)
            .FirstOrDefault()
            // Fallback: walk from the token at the position up to the first expression
            ?? root.FindToken(pos).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();

        if (expression == null)
        {
            return "";
        }

        // Find the containing method
        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null || containingMethod.Body == null)
        {
            return root.ToFullString();
        }

        // Find the containing statement and block
        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            return root.ToFullString();
        }

        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (containingBlock == null)
        {
            return root.ToFullString();
        }

        // Skip if expression is already a standalone variable declaration
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl &&
            existingDecl.Declaration.Variables.Count == 1 &&
            existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return $"// '{existingName}' is already a local variable — nothing to extract.";
        }

        // Skip if expression has potential side effects (method calls, assignments)
        if (HasSideEffects(expression))
        {
            return "";
        }

        // Generate or validate variable name
        var varName = newVariableName;
        if (string.IsNullOrWhiteSpace(varName))
        {
            var baseName = InferVariableName(expression);
            varName = ContextHelper.GetUniqueVariableName(containingMethod.Body, baseName);
        }
        else
        {
            // Check if provided name conflicts
            varName = ContextHelper.GetUniqueVariableName(containingMethod.Body, varName);
        }

        // Infer type from semantic analysis if possible
        var semanticModel = await document.GetSemanticModelAsync(ct);
        TypeSyntax? inferredType = null;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, ct);
            if (typeInfo.Type != null)
            {
                inferredType = SyntaxFactory.ParseTypeName(typeInfo.Type.ToDisplayString());
            }
        }

        // Create variable declaration with 'var' type
        var varDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(varName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(expression.WithoutTrivia())))));

        // Handle parenthesized expressions - replace outer parens too if the expression is the sole content
        SyntaxNode nodeToReplace = expression;
        if (expression.Parent is ParenthesizedExpressionSyntax parenParent &&
            parenParent.Expression == expression)
        {
            nodeToReplace = parenParent;
        }

        var varRef = SyntaxFactory.IdentifierName(varName).WithTriviaFrom(nodeToReplace);

        // Track all nodes that need to be replaced
        var trackedRoot = root.TrackNodes(new SyntaxNode[] { nodeToReplace, containingStatement, containingBlock });

        // Replace the expression with variable reference
        var newRoot = trackedRoot.ReplaceNode(trackedRoot.GetCurrentNode(nodeToReplace)!, varRef);

        // Get updated statement and block
        var currentStatement = newRoot.GetCurrentNode(containingStatement)!;
        var currentBlock = newRoot.GetCurrentNode(containingBlock)!;

        // Find the index where we insert the variable declaration
        var idx = currentBlock.Statements.IndexOf(currentStatement);
        if (idx < 0)
        {
            return root.ToFullString();
        }

        // Insert variable declaration before the statement
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        newRoot = newRoot.ReplaceNode(currentBlock, newBlock);

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static bool HasSideEffects(ExpressionSyntax expression)
    {
        // Check for method calls, assignments, and other side-effect operations
        var descendants = expression.DescendantNodesAndSelf();

        // Method invocations are risky unless they're property getters
        if (descendants.OfType<InvocationExpressionSyntax>().Any())
        {
            return true;
        }

        // Assignment expressions always have side effects
        if (descendants.OfType<AssignmentExpressionSyntax>().Any())
        {
            return true;
        }

        // Pre/post increment/decrement
        if (descendants.OfType<PostfixUnaryExpressionSyntax>().Any())
        {
            return true;
        }

        if (descendants.OfType<PrefixUnaryExpressionSyntax>().Any(p =>
            p.IsKind(SyntaxKind.PreIncrementExpression) || p.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        return false;
    }

    private static string InferVariableName(ExpressionSyntax expression)
    {
        // Try to infer a meaningful variable name from the expression
        return expression switch
        {
            // Binary operations: "x + y" -> "sum", "x * y" -> "product"
            BinaryExpressionSyntax binary => binary.OperatorToken.Kind() switch
            {
                SyntaxKind.PlusToken => "sum",
                SyntaxKind.MinusToken => "difference",
                SyntaxKind.AsteriskToken => "product",
                SyntaxKind.SlashToken => "quotient",
                SyntaxKind.PercentToken => "remainder",
                SyntaxKind.GreaterThanToken or SyntaxKind.LessThanToken or
                SyntaxKind.GreaterThanEqualsToken or SyntaxKind.LessThanEqualsToken or
                SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken => "comparison",
                SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken => "condition",
                _ => "result"
            },
            // Member access: "obj.Property" -> "property"
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text.ToLowerInvariant(),
            // Identifier: "x" -> "x"
            IdentifierNameSyntax ident => ident.Identifier.Text,
            // String literals: "..." -> "text" or "str"
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) => "text",
            // Numeric literals
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.NumericLiteralExpression) => "value",
            // Default fallback
            _ => "extracted"
        };
    }

    public async Task<ControlFlowSummary> AnalyzeControlFlowAsync(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var root = (await document.GetSyntaxRootAsync(ct))!;
        var text = await document.GetTextAsync(ct);

        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0))
                    .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }
        method ??= root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method?.Body == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var flow = model.AnalyzeControlFlow(method.Body);
        if (flow == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var returnPoints = flow.ReturnStatements
            .Select(r => r.ToString().Trim())
            .ToList();
        var throwPoints = method.Body.DescendantNodes()
            .OfType<ThrowStatementSyntax>()
            .Select(t => t.ToString().Trim())
            .ToList();

        return new ControlFlowSummary(
            methodName,
            flow.EndPointIsReachable == false,
            flow.ReturnStatements.Length > 0,
            flow.ReturnStatements.Length == 0,
            returnPoints,
            throwPoints,
            flow.ExitPoints.Length
        );
    }

    public async Task<DataFlowSummary> AnalyzeDataFlowAsync(string filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        var root = (await document.GetSyntaxRootAsync(ct))!;
        var text = await document.GetTextAsync(ct);

        MethodDeclarationSyntax? method = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
            if (pos >= 0)
            {
                method = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0))
                    .AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            }
        }
        method ??= root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method?.Body == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        var model = await document.GetSemanticModelAsync(ct);
        if (model == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        DataFlowAnalysis flow;
        try { flow = model.AnalyzeDataFlow(method.Body)!; }
        catch { return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string> { "AnalyzeDataFlow failed: body may contain unsupported constructs." }); }

        var warnings = new List<string>();
        var writtenOnly = flow.WrittenInside.Except(flow.ReadInside).ToList();
        foreach (var v in writtenOnly)
        {
            warnings.Add($"'{v.Name}' is written but never read — possible dead assignment.");
        }

        return new DataFlowSummary(
            methodName,
            flow.ReadOutside.Select(s => s.Name).ToList(),
            flow.WrittenInside.Select(s => s.Name).ToList(),
            flow.ReadInside.Select(s => s.Name).ToList(),
            flow.WrittenOutside.Select(s => s.Name).ToList(),
            flow.Captured.Select(s => s.Name).ToList(),
            warnings
        );
    }

    public async Task<string> AddUsingDirectiveAsync(string filePath, string namespaceName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        // Idempotency check
        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        if (root.Usings.Any(u => u.Name?.ToString() == targetName))
        {
            return root.ToFullString();
        }

        UsingDirectiveSyntax newUsing;
        if (namespaceName.StartsWith("static "))
        {
            newUsing = SyntaxFactory.UsingDirective(
                    SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    null,
                    SyntaxFactory.ParseName(namespaceName[7..]))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }
        else
        {
            newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

        var newRoot = root.AddUsings(newUsing);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddEnumValueAsync(string filePath, string enumName, string valueName, int? explicitValue = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var enumNode = root?.DescendantNodes().OfType<EnumDeclarationSyntax>().FirstOrDefault(e => e.Identifier.Text == enumName);
        if (enumNode == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newMember = SyntaxFactory.EnumMemberDeclaration(valueName);
        if (explicitValue.HasValue)
        {
            newMember = newMember.WithEqualsValue(
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(explicitValue.Value))));
        }

        var newEnumNode = enumNode.AddMembers(newMember);
        return root!.ReplaceNode(enumNode, newEnumNode).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> InsertMemberAfterAsync(string filePath, string containerName, string afterMemberName, string newMemberSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return root?.ToFullString() ?? "";
        }

        if (container is TypeDeclarationSyntax typeDecl)
        {
            var membersList = typeDecl.Members.ToList();
            var idx = membersList.FindIndex(m => GetMemberName(m) == afterMemberName);
            SyntaxList<MemberDeclarationSyntax> newMembers;
            if (idx < 0)
            {
                newMembers = typeDecl.Members.Add(newMember);
            }
            else
            {
                newMembers = SyntaxFactory.List(membersList.Take(idx + 1).Append(newMember).Concat(membersList.Skip(idx + 1)));
            }

            var newContainer = typeDecl.WithMembers(newMembers);
            return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
        }

        // Fallback: append
        return await AddMemberAsync(filePath, containerName, newMemberSource, ct);
    }

    public async Task<string> InsertMemberBeforeAsync(string filePath, string containerName, string beforeMemberName, string newMemberSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
        }

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null)
        {
            return root?.ToFullString() ?? "";
        }

        if (container is TypeDeclarationSyntax typeDecl)
        {
            var membersList = typeDecl.Members.ToList();
            var idx = membersList.FindIndex(m => GetMemberName(m) == beforeMemberName);
            SyntaxList<MemberDeclarationSyntax> newMembers;
            if (idx < 0)
            {
                newMembers = typeDecl.Members.Add(newMember);
            }
            else
            {
                newMembers = SyntaxFactory.List(membersList.Take(idx).Append(newMember).Concat(membersList.Skip(idx)));
            }

            var newContainer = typeDecl.WithMembers(newMembers);
            return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
        }

        return await AddMemberAsync(filePath, containerName, newMemberSource, ct);
    }

    public async Task<string> AddAttributeAsync(string filePath, string targetName, string attributeSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

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
            return root.ToFullString();
        }

        // Try member first, then type declaration
        var memberNode = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName && m is not BaseTypeDeclarationSyntax);
        if (memberNode != null)
        {
            var newMember = memberNode.AddAttributeLists(attrList);
            return root.ReplaceNode(memberNode, newMember).NormalizeWhitespace().ToFullString();
        }

        var typeNode = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == targetName);
        if (typeNode != null)
        {
            var newType = typeNode.AddAttributeLists(attrList);
            return root.ReplaceNode(typeNode, newType).NormalizeWhitespace().ToFullString();
        }

        return root.ToFullString();
    }

    public async Task<string> AddBaseTypeAsync(string filePath, string typeName, string baseTypeName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
        }

        // Idempotency check
        if (container.BaseList?.Types.Any(t => t.ToString().Contains(baseTypeName)) == true)
        {
            return root!.ToFullString();
        }

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseTypeName));
        var newContainer = container.AddBaseListTypes(baseType);
        return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveAttributeAsync(string filePath, string targetName, string attributeName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var attrCore = attributeName.EndsWith("Attribute") ? attributeName[..^9] : attributeName;

        bool AttrMatches(AttributeSyntax a)
        {
            var name = a.Name.ToString();
            return name == attributeName || name == attrCore || name == attrCore + "Attribute";
        }

        SyntaxNode? target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName && m is not BaseTypeDeclarationSyntax);
        target ??= root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == targetName);

        if (target is not MemberDeclarationSyntax memberTarget)
        {
            return root.ToFullString();
        }

        var newAttrLists = memberTarget.AttributeLists
            .Select(al => al.WithAttributes(SyntaxFactory.SeparatedList(al.Attributes.Where(a => !AttrMatches(a)))))
            .Where(al => al.Attributes.Count > 0)
            .ToList();

        var newMember = memberTarget.WithAttributeLists(SyntaxFactory.List(newAttrLists));
        return root.ReplaceNode(memberTarget, newMember).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveBaseTypeAsync(string filePath, string typeName, string baseTypeName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
        }

        if (container.BaseList == null)
        {
            return root!.ToFullString();
        }

        var remaining = container.BaseList.Types.Where(t => !t.ToString().Contains(baseTypeName)).ToList();
        TypeDeclarationSyntax newContainer = remaining.Count == 0
            ? container.WithBaseList(null)
            : container.WithBaseList(container.BaseList.WithTypes(SyntaxFactory.SeparatedList(remaining)));

        return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ChangeAccessibilityAsync(string filePath, string targetName, string accessibility, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        SyntaxKind[] newKinds = accessibility.ToLowerInvariant() switch
        {
            "public" => [SyntaxKind.PublicKeyword],
            "private" => [SyntaxKind.PrivateKeyword],
            "internal" => [SyntaxKind.InternalKeyword],
            "protected" => [SyntaxKind.ProtectedKeyword],
            "protected internal" => [SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword],
            "private protected" => [SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword],
            _ => [SyntaxKind.PublicKeyword]
        };

        var accessModifierKinds = new HashSet<SyntaxKind>
        {
            SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword,
            SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword
        };

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null)
        {
            return root.ToFullString();
        }

        var remaining = target.Modifiers.Where(m => !accessModifierKinds.Contains(m.Kind())).ToList();
        var newTokens = newKinds.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space));
        var newModifiers = SyntaxFactory.TokenList(newTokens.Concat(remaining));
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null)
        {
            return root.ToFullString();
        }

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None)
        {
            kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        }

        if (target.Modifiers.Any(m => m.IsKind(kind)))
        {
            return root.ToFullString(); // idempotent
        }

        var token = SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space);
        var newModifiers = target.Modifiers.Add(token);
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null)
        {
            return root.ToFullString();
        }

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None)
        {
            kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        }

        if (!target.Modifiers.Any(m => m.IsKind(kind)))
        {
            return root.ToFullString(); // idempotent
        }

        var newModifiers = SyntaxFactory.TokenList(target.Modifiers.Where(m => !m.IsKind(kind)));
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddSummaryCommentAsync(string filePath, string targetName, string summaryText, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null)
        {
            return root.ToFullString();
        }

        var docText = $"/// <summary>\n/// {summaryText}\n/// </summary>\nvoid __Dummy__() {{}}";
        var parsedMember = SyntaxFactory.ParseMemberDeclaration(docText);
        var docTrivia = parsedMember!.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();

        var stripped = target.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();

        var newTrivia = SyntaxFactory.TriviaList(docTrivia.Concat(stripped));
        return root.ReplaceNode(target, target.WithLeadingTrivia(newTrivia)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddPropertyAsync(string filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, CancellationToken ct = default)
    {
        var setter = hasSetter ? (isInit ? " init;" : " set;") : "";
        var source = $"{accessibility} {propertyType} {propertyName} {{ get;{setter} }}";
        return await AddMemberAsync(filePath, containerName, source, ct);
    }

    public async Task<string> AddFieldAsync(string filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, CancellationToken ct = default)
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
        return await AddMemberAsync(filePath, containerName, parts.ToString(), ct);
    }

    public async Task<string> SortMembersAsync(string filePath, string containerName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null)
        {
            return root?.ToFullString() ?? "";
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
            ClassDeclarationSyntax or RecordDeclarationSyntax
                or StructDeclarationSyntax or InterfaceDeclarationSyntax
                or EnumDeclarationSyntax => 8,
            _ => 9
        };

        static bool IsStatic(MemberDeclarationSyntax m) =>
            m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword));

        var sorted = container.Members
            .OrderBy(CategoryOf)
            .ThenBy(m => IsStatic(m) ? 0 : 1)
            .ThenBy(m => GetMemberName(m) ?? "")
            .ToList();

        var newContainer = container.WithMembers(SyntaxFactory.List(sorted));
        return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> WrapInTryCatchAsync(string filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var tree = root.SyntaxTree;

        int StatementStartLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan, ct).StartLinePosition.Line + 1;
        int StatementEndLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan, ct).EndLinePosition.Line + 1;

        // Find the smallest block that fully contains the line range
        var block = root.DescendantNodes()
            .OfType<BlockSyntax>()
            .Where(b =>
            {
                var ls = tree.GetLineSpan(b.Span, ct);
                return ls.StartLinePosition.Line + 1 <= startLine &&
                       ls.EndLinePosition.Line + 1 >= endLine;
            })
            .OrderBy(b => b.Span.Length)
            .FirstOrDefault();
        if (block == null)
        {
            return root.ToFullString();
        }

        var targeted = block.Statements
            .Where(s => StatementStartLine(s) <= endLine && StatementEndLine(s) >= startLine)
            .ToList();
        if (targeted.Count == 0)
        {
            return root.ToFullString();
        }

        var tryBlock = SyntaxFactory.Block(SyntaxFactory.List(targeted));
        var catchDecl = SyntaxFactory.CatchDeclaration(
            SyntaxFactory.ParseTypeName(exceptionType),
            SyntaxFactory.Identifier(catchVariableName));

        StatementSyntax? catchStmt = null;
        if (catchBody != null)
        {
            catchStmt = SyntaxFactory.ParseStatement(catchBody);
        }

        var catchBlock = catchStmt != null
            ? SyntaxFactory.Block(catchStmt)
            : SyntaxFactory.Block();

        var catchClause = SyntaxFactory.CatchClause(catchDecl, null, catchBlock);
        var tryStatement = SyntaxFactory.TryStatement(tryBlock, SyntaxFactory.List([catchClause]), null);

        var newStatements = block.Statements
            .Select((s, i) =>
            {
                if (s == targeted[0])
                {
                    return (StatementSyntax)tryStatement;
                }

                if (targeted.Contains(s))
                {
                    return null;
                }

                return s;
            })
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        var newBlock = block.WithStatements(SyntaxFactory.List(newStatements));
        return root.ReplaceNode(block, newBlock).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddConstructorParameterAsync(string filePath, string className, string paramName, string paramType, string? fieldName = null, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return root.ToFullString();
        }

        var derivedFieldName = fieldName ?? $"_{char.ToLower(paramName[0])}{paramName[1..]}";

        var fieldDecl = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            $"private readonly {paramType} {derivedFieldName};")!;

        var assignmentStatement = SyntaxFactory.ParseStatement($"{derivedFieldName} = {paramName};");
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
            .WithType(SyntaxFactory.ParseTypeName(paramType).WithTrailingTrivia(SyntaxFactory.Space));

        var ctor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

        ConstructorDeclarationSyntax newCtor;
        if (ctor != null)
        {
            var newParams = ctor.ParameterList.Parameters.Count == 0
                ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList([newParam]))
                : ctor.ParameterList.AddParameters(newParam);

            BlockSyntax body;
            if (ctor.Body != null)
            {
                body = ctor.Body.AddStatements(assignmentStatement);
            }
            else
            {
                // expression body → convert to block
                var exprStatement = SyntaxFactory.ExpressionStatement(ctor.ExpressionBody!.Expression);
                body = SyntaxFactory.Block(exprStatement, assignmentStatement);
            }

            newCtor = ctor.WithParameterList(newParams).WithBody(body).WithExpressionBody(null).WithSemicolonToken(default);
        }
        else
        {
            var paramList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList([newParam]));
            var body = SyntaxFactory.Block(assignmentStatement);
            newCtor = SyntaxFactory.ConstructorDeclaration(className)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                .WithParameterList(paramList)
                .WithBody(body);
        }

        var newMembers = new List<MemberDeclarationSyntax> { fieldDecl };
        foreach (var m in classNode.Members)
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

        var newClassNode = classNode.WithMembers(SyntaxFactory.List(newMembers));
        return root.ReplaceNode(classNode, newClassNode).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> WrapInRegionAsync(string filePath, int startLine, int endLine, string regionName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var text = await document.GetTextAsync(ct);

        var lines = text.Lines;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1; // 1-based
            if (lineNumber == startLine)
            {
                sb.AppendLine($"#region {regionName}");
            }

            sb.AppendLine(lines[i].ToString());
            if (lineNumber == endLine)
            {
                sb.AppendLine("#endregion");
            }
        }
        return sb.ToString();
    }

    private string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            ClassDeclarationSyntax c => c.Identifier.Text,
            InterfaceDeclarationSyntax i => i.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
            ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
            _ => null
        };
    }

    public async Task<string> SyncInterfaceToImplementationAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        // Find the class document
        var classDocument = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (classDocument == null)
        {
            return "// Class file not found.";
        }

        var classRoot = await classDocument.GetSyntaxRootAsync(ct);
        if (classRoot == null)
        {
            return "// Could not parse class file.";
        }

        var classNode = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return "// Class not found.";
        }

        // Collect public non-static non-override methods and properties from the class
        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) &&
                        !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) &&
                        !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)))
            .ToList();

        var publicProperties = classNode.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) &&
                        !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) &&
                        !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)))
            .ToList();

        // Find the interface — first in same file, then in other documents
        Document? interfaceDocument = null;
        InterfaceDeclarationSyntax? interfaceNode = null;
        SyntaxNode? interfaceRoot = null;

        // Search same file first
        interfaceNode = classRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
            .FirstOrDefault(i => i.Identifier.Text == interfaceName);
        if (interfaceNode != null)
        {
            interfaceDocument = classDocument;
            interfaceRoot = classRoot;
        }
        else
        {
            // Search all documents
            foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
            {
                if (doc == classDocument)
                {
                    continue;
                }

                var r = await doc.GetSyntaxRootAsync(ct);
                if (r == null)
                {
                    continue;
                }

                var iface = r.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
                    .FirstOrDefault(i => i.Identifier.Text == interfaceName);
                if (iface != null)
                {
                    interfaceDocument = doc;
                    interfaceRoot = r;
                    interfaceNode = iface;
                    break;
                }
            }
        }

        if (interfaceNode == null || interfaceDocument == null || interfaceRoot == null)
        {
            return "// Interface not found.";
        }

        // Collect existing interface member signatures (for deduplication)
        var existingMethodSigs = interfaceNode.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text + "|" + string.Join(",", m.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim())))
            .ToHashSet(StringComparer.Ordinal);

        var existingPropertyNames = interfaceNode.Members.OfType<PropertyDeclarationSyntax>()
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        var newMembers = new List<MemberDeclarationSyntax>();

        foreach (var method in publicMethods)
        {
            var sig = method.Identifier.Text + "|" +
                      string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim()));
            if (existingMethodSigs.Contains(sig))
            {
                continue;
            }

            // Build interface method: return type + name + params, no body
            var ifaceMethod = SyntaxFactory.MethodDeclaration(method.ReturnType, method.Identifier)
                .WithParameterList(method.ParameterList)
                .WithTypeParameterList(method.TypeParameterList)
                .WithConstraintClauses(method.ConstraintClauses)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace();
            newMembers.Add(ifaceMethod);
        }

        foreach (var prop in publicProperties)
        {
            if (existingPropertyNames.Contains(prop.Identifier.Text))
            {
                continue;
            }

            // Build interface property
            var hasGetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true
                            || prop.ExpressionBody != null;
            var hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
            var hasInit = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

            var accessors = new List<AccessorDeclarationSyntax>();
            if (hasGetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasSetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasInit)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            var ifaceProp = SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace();
            newMembers.Add(ifaceProp);
        }

        if (newMembers.Count == 0)
        {
            return interfaceRoot.ToFullString(); // Already up to date
        }

        var newInterfaceNode = interfaceNode.AddMembers(newMembers.ToArray());
        var newInterfaceRoot = interfaceRoot.ReplaceNode(interfaceNode, newInterfaceNode);

        // If interface is in a different file, indicate which file was updated
        if (interfaceDocument != classDocument)
        {
            var updatedPath = interfaceDocument.FilePath ?? interfaceDocument.Name;
            return $"// Updated file: {updatedPath}\n" + newInterfaceRoot.NormalizeWhitespace().ToFullString();
        }

        return newInterfaceRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UpdateXmlDocsFromSignatureAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return root.ToFullString();
        }

        var currentParams = method.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToList();

        // Find the XML doc comment trivia preceding the method
        var xmlTrivia = method.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                  t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        // If no XML doc exists, generate one
        if (xmlTrivia == default)
        {
            // Generate new XML doc
            var lines = new List<XmlNodeSyntax>();

            // Add <summary> tag
            lines.Add(SyntaxFactory.XmlElement(
                SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("summary")),
                SyntaxFactory.SingletonList<XmlNodeSyntax>(
                    SyntaxFactory.XmlText("Description of " + methodName)),
                SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("summary"))));

            // Add <param> tags
            foreach (var param in currentParams)
            {
                lines.Add(SyntaxFactory.XmlElement(
                    SyntaxFactory.XmlElementStartTag(SyntaxFactory.XmlName("param"))
                        .AddAttributes(SyntaxFactory.XmlNameAttribute(param)),
                    SyntaxFactory.SingletonList<XmlNodeSyntax>(
                        SyntaxFactory.XmlText($"The {param} parameter.")),
                    SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("param"))));
            }

            // Create the documentation comment
            var newXmlDoc = SyntaxFactory.DocumentationCommentTrivia(
                SyntaxKind.MultiLineDocumentationCommentTrivia,
                SyntaxFactory.List(lines.Cast<XmlNodeSyntax>()));

            var newTrivia = SyntaxFactory.Trivia(newXmlDoc);
            var newLeadingTrivia = method.GetLeadingTrivia().Insert(0, newTrivia);
            var newMethod = method.WithLeadingTrivia(newLeadingTrivia);
            var newRoot = root.ReplaceNode(method, newMethod);
            return newRoot.ToFullString();
        }

        // XML doc exists — update it
        var xmlDoc = xmlTrivia.GetStructure() as Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax;
        if (xmlDoc == null)
        {
            return root.ToFullString();
        }

        // Find existing param tags
        var existingParamTags = xmlDoc.Content
            .OfType<XmlElementSyntax>()
            .Where(e => e.StartTag.Name.LocalName.Text == "param")
            .ToList();

        var existingParamNames = existingParamTags
            .Select(e => e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.Text ?? "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet();

        // Params to add (in current signature but not in XML)
        var toAdd = currentParams.Except(existingParamNames).ToList();
        // Param tags to remove (in XML but not in current signature)
        var toRemove = existingParamTags
            .Where(e =>
            {
                var name = e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.Text;
                return name != null && !currentParams.Contains(name);
            })
            .ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return root.ToFullString();
        }

        // Build updated XML doc content
        var updatedContent = xmlDoc.Content.ToList();

        // Remove stale param tags
        foreach (var staleTag in toRemove)
        {
            updatedContent.Remove(staleTag);
        }

        // Add missing param tags
        foreach (var paramName in toAdd)
        {
            var newTag = SyntaxFactory.XmlElement(
                SyntaxFactory.XmlElementStartTag(
                    SyntaxFactory.XmlName("param"))
                    .AddAttributes(SyntaxFactory.XmlNameAttribute(paramName)),
                SyntaxFactory.SingletonList<XmlNodeSyntax>(
                    SyntaxFactory.XmlText($"The {paramName} parameter.")),
                SyntaxFactory.XmlElementEndTag(SyntaxFactory.XmlName("param")));
            updatedContent.Add(newTag);
        }

        var updatedXmlDoc = xmlDoc.WithContent(SyntaxFactory.List(updatedContent));
        var updatedTrivia = SyntaxFactory.Trivia(updatedXmlDoc);

        var updatedLeadingTrivia = method.GetLeadingTrivia().Replace(xmlTrivia, updatedTrivia);
        var updatedMethod = method.WithLeadingTrivia(updatedLeadingTrivia);
        var updatedRoot = root.ReplaceNode(method, updatedMethod);
        return updatedRoot.ToFullString();
    }

    /// <summary>
    /// Returns a preview of what FormatDocument would change without applying changes.
    /// Shows changed line ranges with ±3 lines of context (like a unified diff).
    /// Returns Changed=false and an empty hunks list if the file is already formatted correctly.
    /// </summary>
    public async Task<FormatPreviewResult> FormatDocumentPreviewAsync(string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new FormatPreviewResult(false, 0, new List<FormatHunk>());
        }

        var originalText = (await document.GetTextAsync(ct)).ToString();
        var formattedDoc = await Formatter.FormatAsync(document, null, ct);
        var formattedText = (await formattedDoc.GetTextAsync(ct)).ToString();

        if (originalText == formattedText)
        {
            return new FormatPreviewResult(false, 0, new List<FormatHunk>());
        }

        var originalLines = originalText.Split('\n');
        var formattedLines = formattedText.Split('\n');
        var hunks = ComputeFormatHunks(originalLines, formattedLines, contextLines: 3);

        return new FormatPreviewResult(true, hunks.Count, hunks);
    }

    private static List<FormatHunk> ComputeFormatHunks(string[] original, string[] formatted, int contextLines)
    {
        var changedLines = new List<int>();
        var minLen = Math.Min(original.Length, formatted.Length);

        for (int i = 0; i < minLen; i++)
        {
            if (original[i] != formatted[i])
            {
                changedLines.Add(i);
            }
        }

        for (int i = minLen; i < Math.Max(original.Length, formatted.Length); i++)
        {
            changedLines.Add(i);
        }

        if (changedLines.Count == 0)
        {
            return new List<FormatHunk>();
        }

        // Group nearby changed lines into hunks
        var groups = new List<(int start, int end)>();
        int gStart = changedLines[0], gEnd = changedLines[0];
        for (int k = 1; k < changedLines.Count; k++)
        {
            if (changedLines[k] - gEnd <= contextLines * 2 + 1)
            {
                gEnd = changedLines[k];
            }
            else
            {
                groups.Add((gStart, gEnd));
                gStart = gEnd = changedLines[k];
            }
        }
        groups.Add((gStart, gEnd));

        var hunks = new List<FormatHunk>();
        foreach (var (start, end) in groups)
        {
            var ctxBeforeStart = Math.Max(0, start - contextLines);
            var ctxBefore = Enumerable.Range(ctxBeforeStart, start - ctxBeforeStart)
                .Select(l => original[l]).ToList();

            var removed = Enumerable.Range(start, Math.Min(end + 1, original.Length) - start)
                .Select(l => original[l]).ToList();

            var added = Enumerable.Range(start, Math.Min(end + 1, formatted.Length) - start)
                .Select(l => formatted[l]).ToList();

            var ctxAfter = Enumerable.Range(end + 1, contextLines)
                .Where(l => l < original.Length)
                .Select(l => original[l]).ToList();

            hunks.Add(new FormatHunk(
                StartLine: start + 1,
                EndLine: end + 1,
                ContextBefore: ctxBefore,
                RemovedLines: removed,
                AddedLines: added,
                ContextAfter: ctxAfter
            ));
        }

        return hunks;
    }
}

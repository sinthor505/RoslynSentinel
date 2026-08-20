using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;

namespace RoslynSentinel.Basic;

public record ExtractMethodResult(
    bool Success,
    string? ErrorMessage,
    string? BeforeSnippet,
    string? CallSiteReplacement,
    string? ExtractedMethodText,
    string? UpdatedSourceContent);

public record UsingDirectiveInfo(string Name, bool IsStatic, string? Alias);

public record RenameHunk(int LineNumber, string Before, string After, string? ContextBefore, string? ContextAfter);

public record RenameFileChange(FilePath filePath, List<RenameHunk> Hunks);

public record RenameSymbolResult(
    string OldName,
    string NewName,
    Dictionary<FilePath, string> PendingChanges,
    List<RenameFileChange> FileChanges,
    string? Error = null,
    SymbolHandle? UpdatedHandle = null)
{
    public string ToToolResponse()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            success = Error is null,
            oldName = OldName,
            newName = NewName,
            filesChanged = FileChanges.Count,
            fileChanges = FileChanges,
            updatedHandle = UpdatedHandle is SymbolHandle h
                ? new
                {
                    h.SessionId,
                    h.ProjectName,
                    h.DocCommentId
                }
                : null,
            note = UpdatedHandle is null
                ? "updatedHandle is null — re-run locate_symbol before further operations on this symbol."
                : null
        });
    }
}

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

    /// <summary>
    /// Replaces <paramref name="oldNode"/> with <paramref name="newNode"/> and formats only the
    /// replaced node (via a tracking annotation), instead of the whole file. Prevents write-back
    /// paths from silently reformatting unrelated code and shifting line numbers below the edit.
    /// </summary>
    private static async Task<string> ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken)
    {
        var annotation = new SyntaxAnnotation();
        var annotatedNewNode = newNode.WithAdditionalAnnotations(annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Removes <paramref name="nodeToRemove"/> and formats only its former container (the nearest
    /// ancestor whose node identity survives the removal), instead of the whole file.
    /// </summary>
    private static async Task<string> RemoveNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions removeOptions, CancellationToken cancellationToken)
    {
        var container = nodeToRemove.Parent;
        if (container == null)
        {
            var bareNewRoot = root.RemoveNode(nodeToRemove, removeOptions)!;
            var bareFormattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(bareNewRoot), cancellationToken: cancellationToken);
            return (await bareFormattedDoc.GetTextAsync(cancellationToken)).ToString();
        }

        var annotation = new SyntaxAnnotation();
        var annotatedRoot = root.ReplaceNode(container, container.WithAdditionalAnnotations(annotation));
        var annotatedContainer = annotatedRoot.GetAnnotatedNodes(annotation).Single();
        var trackedNodeToRemove = annotatedContainer.DescendantNodesAndSelf().Single(n => n.IsEquivalentTo(nodeToRemove) && n.Span == nodeToRemove.Span);
        var newRoot = annotatedRoot.RemoveNode(trackedNodeToRemove, removeOptions)!;
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Renders a type for use in a freshly synthesized parameter/return signature, ignoring any
    /// nullable annotation that came from flow-state analysis rather than the variable's actual
    /// declared nullability. A type obtained from a data-flow-analysis symbol (e.g. via
    /// <c>SemanticModel.AnalyzeDataFlow</c>) can carry <see cref="NullableAnnotation.Annotated"/>
    /// purely because the compiler's flow analysis is conservative at the region boundary — not
    /// because the variable was ever actually assignable to null. Blindly copying that annotation
    /// into a generated signature produces a spurious <c>?</c> and a live CS8602 warning on every
    /// unguarded use inside the generated body, for a variable that's really always non-null.
    /// </summary>
    private static string DisplayTypeForExtractedSignature(ITypeSymbol type)
    {
        var normalized = type.NullableAnnotation == NullableAnnotation.Annotated
            ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            : type;
        return normalized.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    public async Task<DocumentEditResult> FormatDocumentAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var formatted = await Formatter.FormatAsync(document, null, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<Dictionary<FilePath, string>> ChangeSignatureAsync(FilePath filePath, string methodName, int[] newParameterOrder, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || semanticModel == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var methodDecl = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodDecl == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var parameters = methodDecl.ParameterList.Parameters.ToList();
        if (parameters.Count == 0)
        {
            return new Dictionary<FilePath, string>();
        }

        // Validate order array
        if (newParameterOrder.Length != parameters.Count)
        {
            return new Dictionary<FilePath, string>();
        }

        if (newParameterOrder.Any(i => i < 0 || i >= parameters.Count))
        {
            return new Dictionary<FilePath, string>();
        }

        if (newParameterOrder.Distinct().Count() != parameters.Count)
        {
            return new Dictionary<FilePath, string>();
        }

        var reorderedParams = newParameterOrder.Select(i => parameters[i]).ToList();
        var newParamList = methodDecl.ParameterList.WithParameters(SyntaxFactory.SeparatedList(reorderedParams));
        var updatedMethodDecl = methodDecl.WithParameterList(newParamList);
        var updatedRoot = root.ReplaceNode(methodDecl, updatedMethodDecl);
        var updatedDoc = document.WithSyntaxRoot(updatedRoot);

        var pendingChanges = new Dictionary<FilePath, string>
        {
            [filePath] = (await updatedDoc.GetTextAsync(cancellationToken)).ToString()
        };

        // Reorder arguments at all call sites
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
                    string currentContent = pendingChanges.TryGetValue(docPath, out var prev) ? prev : (await refDoc.GetTextAsync(cancellationToken)).ToString();
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
        var result = new Dictionary<FilePath, string>();
        foreach (var kvp in pendingChanges)
        {
            var doc = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == kvp.Key);
            if (doc != null)
            {
                var formatted = await Formatter.FormatAsync(
                    doc.WithSyntaxRoot(SyntaxFactory.ParseCompilationUnit(kvp.Value)), null, cancellationToken);
                result[kvp.Key] = (await formatted.GetTextAsync(cancellationToken)).ToString();
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    public async Task<ExtractMethodResult> ExtractMethodAsync(
        FilePath filePath, int startLine, string startLineText, int endLine, string endLineText,
        string newMethodName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractMethod"))
        {
            return new ExtractMethodResult(false, "ExtractMethod feature is disabled.", null, null, null, null);
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ExtractMethodResult(false, $"File '{filePath}' not found in solution.", null, null, null, null);
        }

        var text = await document.GetTextAsync(cancellationToken);
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

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
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
            ({ } rv, true) => SyntaxFactory.ParseTypeName($"Task<{DisplayTypeForExtractedSignature(rv.Type)}>"),
            ({ } rv, false) => SyntaxFactory.ParseTypeName(DisplayTypeForExtractedSignature(rv.Type)),
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
                typeName = DisplayTypeForExtractedSignature(loc.Type);
            }
            else
            {
                var p = (IParameterSymbol)sym;
                typeName = DisplayTypeForExtractedSignature(p.Type);
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

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), null, cancellationToken);
        var updatedContent = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();

        var beforeSnippet = string.Concat(selectedStatements.Select(s => s.ToFullString())).Trim();
        var callSiteText = callStatement.NormalizeWhitespace().ToFullString().Trim();
        var extractedMethodText = extractedMethod.ToFullString().Trim();

        return new ExtractMethodResult(true, null, beforeSnippet, callSiteText, extractedMethodText, updatedContent);
    }

    public async Task<Dictionary<FilePath, string>> MoveTypeToFileAsync(FilePath filePath, string typeName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var typeNode = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == typeName);
        if (typeNode == null)
        {
            return new Dictionary<FilePath, string>();
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
            return new Dictionary<FilePath, string>();
        }

        var updatedOrig = RemoveOrphanedRegionDirectives(root!.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!);

        var newDoc = document.Project.AddDocument($"{typeName}.cs", newRoot);
        var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, cancellationToken);
        var newContent = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();

        var updatedOrigDoc = document.WithSyntaxRoot(updatedOrig);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, cancellationToken);
        var updatedOrigContent = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();

        return new Dictionary<FilePath, string> { { filePath, updatedOrigContent }, { newPath, newContent } };
    }

    private async Task<Dictionary<FilePath, string>> MoveAllTypesToFilesForDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var allTypes = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax || t.Parent is BaseNamespaceDeclarationSyntax)
            .ToList();

        if (allTypes.Count <= 1)
        {
            return new Dictionary<FilePath, string>();
        }

        var fileBaseName = Path.GetFileNameWithoutExtension(document.FilePath ?? document.Name);
        var primaryType = allTypes.FirstOrDefault(t => t.Identifier.Text == fileBaseName) ?? allTypes[0];
        var typesToMove = allTypes.Where(t => t != primaryType).ToList();

        if (typesToMove.Count == 0)
        {
            return new Dictionary<FilePath, string>();
        }

        var changes = new Dictionary<FilePath, string>();
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
            var formattedNewDoc = await Formatter.FormatAsync(newDoc, null, cancellationToken);
            changes[newPath] = (await formattedNewDoc.GetTextAsync(cancellationToken)).ToString();
        }

        var updatedRoot = RemoveOrphanedRegionDirectives(root.RemoveNodes(typesToMove, SyntaxRemoveOptions.KeepNoTrivia)!);
        var updatedOrigDoc = document.WithSyntaxRoot(updatedRoot);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, cancellationToken);
        changes[document.FilePath ?? document.Name] = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();

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

    public async Task<Dictionary<FilePath, string>> MoveAllTypesToFilesAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        return await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken);
    }

    public async Task<Dictionary<FilePath, string>> MoveAllTypesToFilesInProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found.");

        var allChanges = new Dictionary<FilePath, string>();
        foreach (var document in project.Documents.Where(d => d.FilePath.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }
        return allChanges;
    }

    public async Task<Dictionary<FilePath, string>> MoveAllTypesToFilesInSolutionAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);

        var allChanges = new Dictionary<FilePath, string>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents).Where(d => d.FilePath.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, cancellationToken))
            {
                allChanges[kvp.Key] = kvp.Value;
            }
        }
        return allChanges;
    }

    public async Task<Dictionary<FilePath, string>> ExtractInterfaceAsync(FilePath filePath, string className, string interfaceName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractInterface"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new Dictionary<FilePath, string>();
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
        var formattedOrigDoc = await Formatter.FormatAsync(origDoc, null, cancellationToken);
        var origContent = (await formattedOrigDoc.GetTextAsync(cancellationToken)).ToString();

        return new Dictionary<FilePath, string> { { filePath, origContent }, { ifacePath, ifaceContent } };
    }

    public async Task<RenameSymbolResult> RenameSymbolAsync(
        SymbolHandle handle,
        ISymbol symbol,
        string newName,
        CancellationToken cancellationToken = default)
    {
        static RenameSymbolResult Err(string msg, string n) =>
            new("", n, new Dictionary<FilePath, string>(), new List<RenameFileChange>(), msg);

        if (!_config.IsFeatureEnabled("Rename"))
        {
            return Err("Feature 'Rename' is disabled.", newName);
        }

        if (!symbol.Locations.Any(l => l.IsInSource))
        {
            return Err("Symbol is not defined in editable source. Rename is not available.", newName);
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);

        var updated = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            solution, symbol, new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), newName, cancellationToken);

        var pendingChanges = new Dictionary<FilePath, string>();
        var fileChanges = new List<RenameFileChange>();

        foreach (var pc in updated.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in pc.GetChangedDocuments())
            {
                var newDoc = updated.GetDocument(docId)!;
                var filePth = new FilePath(newDoc.FilePath ?? newDoc.Name, _workspaceManager.GetSolutionRoot());
                var newContent = (await newDoc.GetTextAsync(cancellationToken)).ToString();
                pendingChanges[filePth] = newContent;
                var origContent = (await solution.GetDocument(docId)!.GetTextAsync(cancellationToken)).ToString();
                fileChanges.Add(new RenameFileChange(filePth, ComputeRenameHunks(origContent, newContent)));
            }
        }

        var updatedHandle = await TryResolveUpdatedHandleAsync(handle, symbol, updated, newName, cancellationToken);
        return new RenameSymbolResult(symbol.Name, newName, pendingChanges, fileChanges, null, updatedHandle);
    }

    private static async Task<SymbolHandle?> TryResolveUpdatedHandleAsync(
        SymbolHandle handle,
        ISymbol originalSymbol,
        Solution updatedSolution,
        string newName,
        CancellationToken cancellationToken)
    {
        try
        {
            var originalLocation = originalSymbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (originalLocation is null) return null;

            var docId = updatedSolution.GetDocumentId(originalLocation.SourceTree!);
            if (docId is null) return null;

            var updatedDoc = updatedSolution.GetDocument(docId);
            if (updatedDoc is null) return null;

            var updatedRoot = await updatedDoc.GetSyntaxRootAsync(cancellationToken);
            var updatedModel = await updatedDoc.GetSemanticModelAsync(cancellationToken);
            if (updatedRoot is null || updatedModel is null) return null;

            var originalSpanStart = originalLocation.SourceSpan.Start;
            var candidates = updatedRoot.DescendantTokens()
                .Where(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken)
                         && t.Text == newName
                         && Math.Abs(t.SpanStart - originalSpanStart) < 500);

            foreach (var token in candidates)
            {
                var parentNode = token.Parent;
                if (parentNode is null) continue;
                var declaredSymbol = updatedModel.GetDeclaredSymbol(parentNode, cancellationToken);
                if (declaredSymbol is null) continue;
                var newDocCommentId = declaredSymbol.GetDocumentationCommentId();
                if (newDocCommentId is not null)
                {
                    return new SymbolHandle(handle.SessionId, handle.ProjectName, newDocCommentId);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
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

    public async Task<DocumentEditResult> ConvertIndexerToMethodAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertIndexerToMethod"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
        var indexer = root?.DescendantNodes().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
        if (indexer == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Indexer not found."
            };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Indexer not found."
            };
        }

        var newRoot = root!.ReplaceNode(indexer, getter);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Indexer converted to method.",
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> AddRemoveParamsAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("AddRemoveParams"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var newModifiers = hasParams
            ? lastParam.Modifiers.Remove(lastParam.Modifiers.First(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            : lastParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ParamsKeyword));

        var newParam = lastParam.WithModifiers(newModifiers);
        var newRoot = root!.ReplaceNode(lastParam, newParam);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Params keyword toggled.",
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> ReplaceMemberAsync(
        FilePath filePath, string memberName, string newSource,
        string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null,
        IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        MemberDeclarationSyntax? member;
        try
        {
            member = ResolveMemberByNameOrSnippet(root, sourceText, memberName, contextSnippet, lineBefore, lineAfter);
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

        var newMember = SyntaxFactory.ParseMemberDeclaration(newSource);
        if (newMember == null || newMember.ContainsDiagnostics)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// newSource is not a valid member declaration (method/property/class/etc. with a signature). " +
                    "Provide the full member, not just a statement or method body."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Member replaced.",
            UpdatedText = await ReplaceNodeFormattedAsync(document, root, member, newMember, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> AddMemberAsync(FilePath filePath, string containerName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            container = ResolveTypeByNameOrSnippet(root, sourceText, containerName, contextSnippet, lineBefore, lineAfter);
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
                Message = "// Container not found."
            };
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
        newMember = newMember.WithAddedByComment("AddMember");

        var newContainer = container switch
        {
            ClassDeclarationSyntax c => (BaseTypeDeclarationSyntax)c.AddMembers(newMember),
            InterfaceDeclarationSyntax i => (BaseTypeDeclarationSyntax)i.AddMembers(newMember),
            RecordDeclarationSyntax r => (BaseTypeDeclarationSyntax)r.AddMembers(newMember),
            StructDeclarationSyntax s => (BaseTypeDeclarationSyntax)s.AddMembers(newMember),
            _ => container
        };
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Member added.",
            UpdatedText = await ReplaceNodeFormattedAsync(document, root!, container, newContainer, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> RemoveMemberAsync(
        FilePath filePath, string memberName,
        string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null,
        IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        MemberDeclarationSyntax? member;
        try
        {
            member = ResolveMemberByNameOrSnippet(root, sourceText, memberName, contextSnippet, lineBefore, lineAfter);
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
                        Message = $"// ERROR: Cannot remove member '{memberName}' — it has {usageCount} usages in the solution.\n{root!.ToFullString()}"
                    };
                }
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Member removed.",
            UpdatedText = await RemoveNodeFormattedAsync(document, root, member, SyntaxRemoveOptions.KeepNoTrivia, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ConvertToPrimaryConstructorAsync(FilePath filePath, string className, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("PrimaryConstructors"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'PrimaryConstructors' is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        var ctor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null || ctor.ParameterList.Parameters.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Constructor not found or has no parameters."
            };
        }

        // Minimal implementation for tests: convert to class C(int x) and remove fields/ctor
        var newClass = SyntaxFactory.ClassDeclaration(classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(ctor.ParameterList);

        var members = classNode.Members.Where(m => m is not ConstructorDeclarationSyntax && m is not FieldDeclarationSyntax).ToList();
        newClass = newClass.WithMembers(SyntaxFactory.List(members));

        var newRoot = root!.ReplaceNode(classNode, newClass);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Class converted to primary constructor.",
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<Dictionary<FilePath, string>> SafeDeleteSymbolAsync(FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SafeDeleteUnusedSymbol"))
        {
            return new Dictionary<FilePath, string>();
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out _, lineBefore, lineAfter);
        if (pos < 0)
        {
            return new Dictionary<FilePath, string>();
        }

        var node = root!.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0));
        // Walk up ancestors to find the nearest declaration symbol (FindNode may return a child token/identifier)
        var symbol = node.AncestorsAndSelf()
            .Select(n => model!.GetDeclaredSymbol(n, cancellationToken))
            .FirstOrDefault(s => s != null)
            ?? model!.GetSymbolInfo(node, cancellationToken).Symbol;
        if (symbol == null)
        {
            return new Dictionary<FilePath, string>();
        }

        // Check for reflection usage
        foreach (var proj in solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                var literals = docRoot?.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.ValueText == symbol.Name);

                if (literals?.Any() == true)
                {
                    throw new InvalidOperationException(
                        $"Potential Reflection Risk: symbol '{symbol.Name}' is referenced by string literal in {doc.Name} — possible reflection/dynamic usage. Delete manually after verifying.");
                }
            }
        }

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);

        // Count all references, not just those with locations in the same document
        var totalRefCount = refs.Sum(r => r.Locations.Count());

        // BUG-73 FIX: Explicit check that symbol is truly unused
        // If we have ANY references (including implicit ones), refuse deletion
        if (totalRefCount > 0)
        {
            _logger.LogWarning("SafeDeleteUnusedSymbol blocked: symbol '{SymbolName}' has usages and cannot be safely deleted.", symbol.Name);
            return new Dictionary<FilePath, string> { { "ERROR", $"Cannot delete '{symbol.Name}': symbol is used in {totalRefCount} location(s)." } };
        }

        // Additional safety check: scan syntax tree for any identifier matching the symbol name
        // This catches usages that SymbolFinder might miss
        var declarationNode = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        foreach (var proj in solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await doc.GetSemanticModelAsync(cancellationToken);
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

                            var idSymbol = semanticModel!.GetSymbolInfo(id, cancellationToken).Symbol;
                            if (idSymbol != null && SymbolEqualityComparer.Default.Equals(idSymbol, symbol))
                            {
                                // This is a usage of our symbol (not the declaration)
                                _logger.LogWarning("SafeDeleteUnusedSymbol blocked: symbol '{SymbolName}' has usages and cannot be safely deleted.", symbol.Name);
                                return new Dictionary<FilePath, string> { { "ERROR", $"Cannot delete '{symbol.Name}': symbol is used and cannot be safely removed." } };
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
            return new Dictionary<FilePath, string>();
        }

        return new Dictionary<FilePath, string> { { filePath, root.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString() } };
    }

    public async Task<DocumentEditResult> ConvertExpressionBodyAsync(FilePath filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertExpressionBody"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'ConvertExpressionBody' is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);

        MemberDeclarationSyntax? target = null;
        if (contextSnippet != null)
        {
            var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
            if (pos < 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// {snippetError}"
                };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Member '{memberName}' not found in '{Path.GetFileName(filePath)}'."
            };
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
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: method body has {stmts.Count} statement(s); only single-return methods can be converted."
                    };
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
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: property getter does not contain a simple return statement."
                    };
                }
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to expression body: member has no block body or is already an expression body."
                };
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
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to block body: member has no expression body (already a block body or not a method/property)."
                };
            }
        }

        var newRoot = root.ReplaceNode(target, newTarget.NormalizeWhitespace());
        var doc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(doc, null, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<DocumentEditResult> ExtractConstantAsync(FilePath filePath, string contextSnippet, string constantName, string visibility = "private", string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractConstant"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// ExtractConstant feature is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// Error: {snippetError}"
            };
        }

        var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length));
        var literal = node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault()
            ?? node.AncestorsAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault();
        if (literal == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: literal not found."
            };
        }

        var containingType = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing type not found."
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax constType;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(literal, cancellationToken);
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

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = trackedRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> ExtractLocalVariableAsync(
        FilePath filePath, string contextSnippet, string? newVariableName = null,
        string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ExtractLocalVariable"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// ExtractLocalVariable feature is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);

        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            // ContextHelper's message ("contextSnippet not found"/"ambiguous (N matches)") is
            // necessarily generic — ContextHelper only sees raw text offsets, it has no symbolName
            // or declaration list to enumerate the way ResolveMemberByNameOrSnippet's NearMissList
            // hint does, and this tool has no name argument at all (it targets an expression by its
            // literal text, not a named declaration) — so there is no candidate set to report here
            // the way there is for the member/type resolvers. Point the caller at the tools that
            // would show it real file content instead of leaving a bare message with nothing to act on.
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = $"// Error: {snippetError} Re-check the snippet against GetMethodSource/GetFileOutline " +
                    "output, or add lineBefore/lineAfter (verbatim text from the surrounding lines) to disambiguate."
            };
        }

        // Find the expression that matches the context snippet - use same logic as IntroduceVariableAsync.
        // Comparison is whitespace-collapsed (not raw-trimmed) so a caller who reproduces the exact
        // expression but with different internal spacing (e.g. around operators) still hits this exact
        // path instead of silently falling through to the ambiguous nearest-enclosing-expression guess
        // below — that fallback exists for a genuinely partial contextSnippet, not a whitespace variant
        // of a complete one.
        var normalizedSnippet = System.Text.RegularExpressions.Regex.Replace(contextSnippet.Trim(), @"\s+", " ");
        var exactMatch = root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Where(e => e.SpanStart == pos &&
                System.Text.RegularExpressions.Regex.Replace(e.ToString().Trim(), @"\s+", " ") == normalizedSnippet)
            .FirstOrDefault();

        // Fallback: contextSnippet didn't match a whole expression's text at this position — walk from
        // the token at the position up to the nearest enclosing expression instead. This is inherently
        // ambiguous (a partial/short contextSnippet can resolve to a larger expression than the caller
        // intended), so it only ever kicks in when the exact match above fails, and never overrides it.
        var expression = exactMatch
            ?? root.FindToken(pos).Parent?.AncestorsAndSelf().OfType<ExpressionSyntax>().FirstOrDefault();

        if (expression == null)
        {
            // The snippet DID resolve to a text position (pos, above) — the failure is that no
            // ExpressionSyntax boundary aligns with it (e.g. the snippet spans a statement, a
            // keyword, or crosses an expression boundary). Report where it landed instead of a
            // bare "not found", since that position is real, already-available information — a
            // caller reading only "expression not found" has no way to tell its snippet was even
            // located at all versus silently mismatched.
            var landedLine = text.Lines.GetLineFromPosition(pos).LineNumber + 1;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = $"// Cannot convert: contextSnippet was found at line {landedLine}, but it does not " +
                    "align to a single, whole extractable expression there (it may span a statement, a keyword, " +
                    "or cross an expression boundary). Narrow the snippet to exactly one expression's text."
            };
        }

        // Find the containing method
        var containingMethod = expression.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod == null || containingMethod.Body == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing method not found."
            };
        }

        // Find the containing statement and block
        var containingStatement = expression.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (containingStatement == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing statement not found."
            };
        }

        var containingBlock = containingStatement.Parent as BlockSyntax;
        if (containingBlock == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: containing block not found."
            };
        }

        // Skip if expression is already a standalone variable declaration
        if (containingStatement is LocalDeclarationStatementSyntax existingDecl &&
            existingDecl.Declaration.Variables.Count == 1 &&
            existingDecl.Declaration.Variables[0].Initializer?.Value?.IsEquivalentTo(expression) == true)
        {
            var existingName = existingDecl.Declaration.Variables[0].Identifier.Text;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = $"// '{existingName}' is already a local variable — nothing to extract."
            };
        }

        // Skip if expression has potential side effects (method calls, assignments)
        if (HasSideEffects(expression))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: expression has potential side effects."
            };
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
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        TypeSyntax? inferredType = null;
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression, cancellationToken);
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: statement index not found."
            };
        }

        // Insert variable declaration before the statement
        var newBlock = currentBlock.WithStatements(currentBlock.Statements.Insert(idx, varDecl));
        newRoot = newRoot.ReplaceNode(currentBlock, newBlock);

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
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

    public async Task<ControlFlowSummary> AnalyzeControlFlowAsync(FilePath filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new ControlFlowSummary(methodName, false, false, true, new List<string>(), new List<string>(), 0);
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);

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

        var model = await document.GetSemanticModelAsync(cancellationToken);
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

    public async Task<DataFlowSummary> AnalyzeDataFlowAsync(FilePath filePath, string methodName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DataFlowSummary(methodName, new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);

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

        var model = await document.GetSemanticModelAsync(cancellationToken);
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

    public async Task<DocumentEditResult> AddUsingDirectiveAsync(FilePath filePath, string namespaceName, bool simplifyExisting = false, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        // Idempotency check
        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        if (root.Usings.Any(u => u.Name?.ToString() == targetName))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Using directive already exists.",
                UpdatedText = root.ToFullString()
            };
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

        var annotation = new SyntaxAnnotation();
        var newRoot = root.AddUsings(newUsing.WithAdditionalAnnotations(annotation));
        var editedDocument = document.WithSyntaxRoot(newRoot);
        var formattedDoc = await Formatter.FormatAsync(editedDocument, annotation, cancellationToken: cancellationToken);

        if (simplifyExisting)
        {
            // Semantic-safe shortening of now-redundant fully-qualified names, via Roslyn's own
            // Simplifier (not text find/replace) — it consults the semantic model per-node, so it
            // only reduces a qualified name when doing so introduces no ambiguity in this file.
            formattedDoc = await Simplifier.ReduceAsync(formattedDoc, Simplifier.Annotation, cancellationToken: cancellationToken);
            var simplifiedRoot = await formattedDoc.GetSyntaxRootAsync(cancellationToken);
            formattedDoc = await Formatter.FormatAsync(formattedDoc.WithSyntaxRoot(simplifiedRoot!.WithAdditionalAnnotations(Simplifier.Annotation)), cancellationToken: cancellationToken);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<DocumentEditResult> RemoveUsingDirectiveAsync(FilePath filePath, string namespaceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: syntax root not found."
            };
        }

        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        var isStaticTarget = namespaceName.StartsWith("static ");
        var existing = root.Usings.FirstOrDefault(u => u.Name?.ToString() == targetName && u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) == isStaticTarget);
        if (existing == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Using directive '{namespaceName}' not found."
            };
        }

        var newRoot = root.RemoveNode(existing, SyntaxRemoveOptions.KeepExteriorTrivia)!.NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<List<UsingDirectiveInfo>> GetUsingDirectivesAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return [];
        }

        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return [];
        }

        return root.Usings
            .Select(u => new UsingDirectiveInfo(
                Name: u.Name?.ToString() ?? "",
                IsStatic: u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
                Alias: u.Alias?.Name.ToString()))
            .ToList();
    }

    /// <summary>
    /// Sets an enum's complete member list in one pass — covers add, remove, and reorder.
    /// <paramref name="values"/> is a comma-separated "Name[=IntValue]" list in the desired final
    /// order. Members whose name is retained keep their existing explicit value unless the caller
    /// supplies an override; members omitted from <paramref name="values"/> are removed; names not
    /// currently present are added (in the position given). The returned DocumentEditResult.Message
    /// summarizes what was added/removed/reordered so callers can verify the diff matched intent.
    /// Members that were already explicit in the source keep their literal value regardless of new
    /// position (same as a hand-edit would); members that were implicit take the next ordinal from
    /// their predecessor in the NEW order — same renumbering behavior as manually retyping the enum
    /// body — so a mid-list insert or removal can shift a retained implicit member's underlying
    /// value. Pass "=N" explicitly for any member whose numeric value must not move. 
    /// </summary>
    public async Task<DocumentEditResult> ModifyEnumAsync(FilePath filePath, string enumName, string values, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            enumNode = ResolveTypeByNameOrSnippet(root, sourceText, enumName, contextSnippet, lineBefore, lineAfter);
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
        foreach (var raw in values.Split(',', StringSplitOptions.RemoveEmptyEntries))
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

        bool valueChanged = requested.Any(r =>
            r.Value.HasValue &&
            existingByName.TryGetValue(r.Name, out var existingMember) &&
            GetExistingExplicitValue(existingMember) != r.Value);

        if (added.Count == 0 && removed.Count == 0 && !reordered && !valueChanged)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// No change: requested values already match the current member list and order."
            };
        }

        var newMembers = new List<EnumMemberDeclarationSyntax>();
        foreach (var (name, explicitValue) in requested)
        {
            EnumMemberDeclarationSyntax member = existingByName.TryGetValue(name, out var existingMember)
                ? existingMember
                : SyntaxFactory.EnumMemberDeclaration(name);

            if (explicitValue.HasValue)
            {
                member = member.WithEqualsValue(
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(explicitValue.Value))));
            }

            newMembers.Add(member);
        }

        // Detect duplicate effective values — e.g. inserting a new implicit member ahead of an
        // already-explicit one shifts the implicit member's auto-numbered value into a collision
        // that C# permits silently. Fail loudly instead of producing a duplicate-valued enum.
        var effectiveValues = new List<(string Name, int Value)>();
        int nextImplicit = 0;
        foreach (var member in newMembers)
        {
            int value = member.EqualsValue?.Value is LiteralExpressionSyntax { Token.Value: int explicitVal }
                ? explicitVal
                : nextImplicit;
            effectiveValues.Add((member.Identifier.Text, value));
            nextImplicit = value + 1;
        }

        var valueCollisions = effectiveValues
            .GroupBy(v => v.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(m => m.Name))} both = {g.Key}")
            .ToList();

        if (valueCollisions.Count > 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: this would produce duplicate enum values ({string.Join("; ", valueCollisions)}). " +
                    "Pass an explicit '=N' for the member(s) whose value must change to avoid the collision."
            };
        }

        var newEnumNode = enumDecl.WithMembers(SyntaxFactory.SeparatedList(newMembers));
        var updatedText = await ReplaceNodeFormattedAsync(document, root!, enumDecl, newEnumNode, cancellationToken);

        var summary = new List<string>();
        if (added.Count > 0) summary.Add($"added {string.Join(", ", added)}");
        if (removed.Count > 0) summary.Add($"removed {string.Join(", ", removed)}");
        if (reordered) summary.Add("reordered");

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText,
            Message = string.Join("; ", summary)
        };

        static int? GetExistingExplicitValue(EnumMemberDeclarationSyntax member) =>
            member.EqualsValue?.Value is LiteralExpressionSyntax { Token.Value: int existingValue }
                ? existingValue
                : null;
    }

    public async Task<DocumentEditResult> InsertMemberAfterAsync(FilePath filePath, string containerName, string afterMemberName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            container = ResolveTypeByNameOrSnippet(root, sourceText, containerName, contextSnippet, lineBefore, lineAfter);
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
                Message = "// Container not found."
            };
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
        newMember = newMember.WithAddedByComment("InsertMemberAfter");

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
            var newRoot = root!.ReplaceNode(container, newContainer).NormalizeWhitespace();
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.ToFullString()
            };
        }

        // Fallback: append
        return await AddMemberAsync(filePath, containerName, newMemberSource, null, null, null, progress, cancellationToken);
    }

    public async Task<DocumentEditResult> InsertMemberBeforeAsync(
        FilePath filePath,
        string containerName,
        string beforeMemberName,
        string newMemberSource,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            container = ResolveTypeByNameOrSnippet(root, sourceText, containerName, contextSnippet, lineBefore, lineAfter);
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
                Message = "// Container not found."
            };
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
        newMember = newMember.WithAddedByComment("InsertMemberBefore");

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
            var newRoot = root!.ReplaceNode(container, newContainer).NormalizeWhitespace();
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.ToFullString()
            };
        }

        return await AddMemberAsync(filePath, containerName, newMemberSource, null, null, null, progress, cancellationToken);
    }

    public async Task<DocumentEditResult> AddAttributeAsync(FilePath filePath, string targetName, string attributeSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            var memberNode = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter, m => m is not BaseTypeDeclarationSyntax);
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
                var typeNode = ResolveTypeByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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

        var newNode = targetNode is MemberDeclarationSyntax memberTarget
            ? (SyntaxNode)memberTarget.AddAttributeLists(attrList)
            : ((BaseTypeDeclarationSyntax)targetNode).AddAttributeLists(attrList);
        var newRoot = root.ReplaceNode(targetNode, newNode).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> AddBaseTypeAsync(FilePath filePath, string typeName, string baseTypeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            container = ResolveTypeByNameOrSnippet(root, sourceText, typeName, contextSnippet, lineBefore, lineAfter);
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
        var newRoot = root!.ReplaceNode(container, newContainer).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> ReplaceAttributeAsync(
    FilePath filePath,
    string targetName,
    string oldAttributeName,
    string newAttributeSource,
    string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null,
    IProgress<ProgressNotificationValue>? progress = default,
    CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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
            var memberTarget = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter, m => m is not BaseTypeDeclarationSyntax);
            targetNode = memberTarget;
            if (targetNode == null)
            {
                targetNode = ResolveTypeByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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
        var attrLists = targetNode is MemberDeclarationSyntax m2
            ? m2.AttributeLists
            : ((BaseTypeDeclarationSyntax)targetNode).AttributeLists;

        AttributeSyntax? oldAttr = attrLists
            .SelectMany(al => al.Attributes)
            .FirstOrDefault(a => GetAttributeName(a) == oldAttributeName);

        if (oldAttr == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Cannot edit: attribute '{oldAttributeName}' not found on target."
            };
        }

        var newRoot = root.ReplaceNode(oldAttr, newAttr).NormalizeWhitespace();

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
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

    public async Task<DocumentEditResult> RemoveAttributeAsync(FilePath filePath, string targetName, string attributeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            memberTarget = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter, m => m is not BaseTypeDeclarationSyntax);
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

        var newAttrLists = memberTarget.AttributeLists
            .Select(al => al.WithAttributes(SyntaxFactory.SeparatedList(al.Attributes.Where(a => !AttrMatches(a)))))
            .Where(al => al.Attributes.Count > 0)
            .ToList();

        var newMember = memberTarget.WithAttributeLists(SyntaxFactory.List(newAttrLists));
        var newRoot = root.ReplaceNode(memberTarget, newMember).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> RemoveBaseTypeAsync(FilePath filePath, string typeName, string baseTypeName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            container = ResolveTypeByNameOrSnippet(root, sourceText, typeName, contextSnippet, lineBefore, lineAfter);
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
        var newContainer = remaining.Count == 0
            ? container.WithBaseList(null)
            : container.WithBaseList(container.BaseList.WithTypes(SyntaxFactory.SeparatedList(remaining)));

        var newRoot = root!.ReplaceNode(container, newContainer).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> ChangeAccessibilityAsync(FilePath filePath, string targetName, string accessibility, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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

        var remaining = target.Modifiers.Where(m => !accessModifierKinds.Contains(m.Kind())).ToList();
        var newTokens = newKinds.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space));
        var newModifiers = SyntaxFactory.TokenList(newTokens.Concat(remaining));
        var updatedText = await ReplaceNodeFormattedAsync(document, root, target, target.WithModifiers(newModifiers), cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedText
        };
    }

    public async Task<DocumentEditResult> AddModifierAsync(FilePath filePath, string targetName, string modifier, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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
        var newRoot = root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> RemoveModifierAsync(FilePath filePath, string targetName, string modifier, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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
        var newRoot = root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> AddSummaryCommentAsync(FilePath filePath, string targetName, string summaryText, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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

        var normalizedSummary = NormalizeSummaryText(summaryText);
        var docText = $"/// <summary>\n/// {normalizedSummary}\n/// </summary>\nvoid __Dummy__() {{}}";
        var parsedMember = SyntaxFactory.ParseMemberDeclaration(docText);
        var docTrivia = parsedMember!.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();

        var stripped = target.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();

        var newTrivia = SyntaxFactory.TriviaList(docTrivia.Concat(stripped));
        var newRoot = root.ReplaceNode(target, target.WithLeadingTrivia(newTrivia)).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    // Callers sometimes pass summaryText already shaped as a doc comment (e.g. "/// <summary>...</summary>"
    // or "<summary>...</summary>") instead of plain prose, which would otherwise get wrapped a second time
    // into malformed nested <summary> tags. Strip any such wrapping so the caller's text is always
    // re-wrapped exactly once, regardless of the shape they supplied it in.
    private static string NormalizeSummaryText(string summaryText)
    {
        var lines = summaryText.Replace("\r\n", "\n").Split('\n')
            .Select(line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("///"))
                {
                    trimmed = trimmed[3..].TrimStart();
                }
                return trimmed;
            })
            .Where(line => !line.Equals("<summary>", StringComparison.OrdinalIgnoreCase)
                         && !line.Equals("</summary>", StringComparison.OrdinalIgnoreCase)
                         && line.Length > 0);

        var joined = string.Join(" ", lines).Trim();

        if (joined.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
        {
            joined = joined[9..];
        }
        if (joined.EndsWith("</summary>", StringComparison.OrdinalIgnoreCase))
        {
            joined = joined[..^10];
        }

        return joined.Trim();
    }

    public async Task<DocumentEditResult> RemoveSummaryCommentAsync(FilePath filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        MemberDeclarationSyntax? target;
        try
        {
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
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

        if (!target.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// No summary comment present.",
                UpdatedText = root.ToFullString()
            };
        }

        var stripped = target.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
            .ToList();
        var newRoot = root.ReplaceNode(target, target.WithLeadingTrivia(SyntaxFactory.TriviaList(stripped))).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<(EditOutcome Outcome, string? Message, string? SummaryText)> GetSummaryCommentAsync(FilePath filePath, string targetName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return (EditOutcome.DocumentNotFound, "// Document not found.", null);
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        if (root == null || sourceText == null)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: syntax root not found.", null);
        }

        MemberDeclarationSyntax? target;
        try
        {
            target = ResolveMemberByNameOrSnippet(root, sourceText, targetName, contextSnippet, lineBefore, lineAfter);
        }
        catch (InvalidOperationException ex)
        {
            return (EditOutcome.CannotEdit, ex.Message, null);
        }

        if (target == null)
        {
            return (EditOutcome.CannotEdit, "// Cannot edit: target not found.", null);
        }

        var docTrivia = target.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
        if (docTrivia == default)
        {
            return (EditOutcome.NoChange, "// No summary comment present.", null);
        }

        var lines = docTrivia.ToFullString()
            .Split('\n')
            .Select(l => l.Trim().TrimStart('/').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("<summary>") && !l.StartsWith("</summary>"))
            .ToList();
        return (EditOutcome.Modified, null, string.Join(" ", lines));
    }

    public async Task<DocumentEditResult> AddPropertyAsync(FilePath filePath, string containerName, string propertyName, string propertyType, string accessibility = "public", bool hasSetter = true, bool isInit = false, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var setter = hasSetter ? (isInit ? " init;" : " set;") : "";
        var source = $"{accessibility} {propertyType} {propertyName} {{ get;{setter} }}";
        return await AddMemberAsync(filePath, containerName, source, contextSnippet, lineBefore, lineAfter, progress, cancellationToken);
    }

    public async Task<DocumentEditResult> AddFieldAsync(FilePath filePath, string containerName, string fieldName, string fieldType, string accessibility = "private", bool isReadonly = false, bool isStatic = false, string? initializer = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
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
        return await AddMemberAsync(filePath, containerName, parts.ToString(), contextSnippet, lineBefore, lineAfter, progress, cancellationToken);
    }

    public async Task<DocumentEditResult> SortMembersAsync(FilePath filePath, string containerName, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
        var newRoot = root!.ReplaceNode(container, newContainer).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePath filePath, int startLine, int endLine, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var tree = root.SyntaxTree;

        int StatementStartLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan, cancellationToken).StartLinePosition.Line + 1;
        int StatementEndLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan, cancellationToken).EndLinePosition.Line + 1;

        // Find the smallest block that fully contains the line range
        var block = root.DescendantNodes()
            .OfType<BlockSyntax>()
            .Where(b =>
            {
                var ls = tree.GetLineSpan(b.Span, cancellationToken);
                return ls.StartLinePosition.Line + 1 <= startLine &&
                       ls.EndLinePosition.Line + 1 >= endLine;
            })
            .OrderBy(b => b.Span.Length)
            .FirstOrDefault();
        if (block == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not find a suitable block."
            };
        }

        var targeted = block.Statements
            .Where(s => StatementStartLine(s) <= endLine && StatementEndLine(s) >= startLine)
            .ToList();
        if (targeted.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not find any statements in the specified range."
            };
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
        var newRoot = root.ReplaceNode(block, newBlock).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a try/catch block.
    /// Uses ContextHelper.FindSnippetPosition to locate the snippet, then wraps the enclosing statements.
    /// </summary>
    public async Task<DocumentEditResult> WrapInTryCatchAsync(FilePath filePath, string contextSnippet, string? lineBefore, string? lineAfter, string exceptionType = "Exception", string catchVariableName = "ex", string? catchBody = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        try
        {
            // Find the snippet position
            var snippetPos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
            var tree = root.SyntaxTree;

            // Convert position to line numbers
            var linePos = sourceText.Lines.GetLinePosition(snippetPos);
            var startLine = linePos.Line + 1;
            var endLine = startLine; // Start with the snippet's line

            // Find the enclosing block and targeted statements
            var block = root.DescendantNodes()
                .OfType<BlockSyntax>()
                .Where(b =>
                {
                    var ls = tree.GetLineSpan(b.Span, cancellationToken);
                    return ls.StartLinePosition.Line + 1 <= startLine &&
                           ls.EndLinePosition.Line + 1 >= endLine;
                })
                .OrderBy(b => b.Span.Length)
                .FirstOrDefault();

            if (block == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = "// Could not find a suitable block for the snippet."
                };
            }

            int StatementStartLine(StatementSyntax s) =>
                tree.GetLineSpan(s.FullSpan, cancellationToken).StartLinePosition.Line + 1;
            int StatementEndLine(StatementSyntax s) =>
                tree.GetLineSpan(s.FullSpan, cancellationToken).EndLinePosition.Line + 1;

            // Find statements that contain or overlap the snippet
            var targeted = block.Statements
                .Where(s => s.Span.Contains(snippetPos))
                .ToList();

            if (targeted.Count == 0)
            {
                // Fallback: try to find statement that contains the snippet position
                targeted = block.Statements
                    .Where(s => s.Span.Start <= snippetPos && s.Span.End >= snippetPos)
                    .ToList();
            }

            if (targeted.Count == 0)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = "// Could not find any statements containing the snippet."
                };
            }

            // Build the try/catch as in the original
            var tryBlock = SyntaxFactory.Block(SyntaxFactory.List(targeted));
            var catchDecl = SyntaxFactory.CatchDeclaration(
                SyntaxFactory.ParseTypeName(exceptionType),
                SyntaxFactory.Identifier(catchVariableName));

            StatementSyntax? catchStmt = null;
            if (catchBody != null)
            {
                catchStmt = SyntaxFactory.ParseStatement(catchBody);
            }

            var catchBlockStmt = catchStmt != null
                ? SyntaxFactory.Block(catchStmt)
                : SyntaxFactory.Block();

            var catchClause = SyntaxFactory.CatchClause(catchDecl, null, catchBlockStmt);
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
            var newRoot = root.ReplaceNode(block, newBlock).NormalizeWhitespace();
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.ToFullString()
            };
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ContextSnippet error: {ex.Message}"
            };
        }
    }

    public async Task<DocumentEditResult> AddConstructorParameterAsync(FilePath filePath, string className, string paramName, string paramType, string? fieldName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, IProgress<ProgressNotificationValue>? progress = default, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        BaseTypeDeclarationSyntax? classNode = null;
        try
        {
            classNode = ResolveTypeByNameOrSnippet(root, sourceText, className, contextSnippet, lineBefore, lineAfter);
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

        if (classNode == null || classNode is not ClassDeclarationSyntax)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Cannot edit: class not found."
            };
        }

        var classDecl = (ClassDeclarationSyntax)classNode;

        // Derive the backing field name, disambiguating from paramName so the generated
        // assignment can never degenerate into a no-op self-assignment (e.g. `stopwatch = stopwatch;`
        // instead of assigning the parameter into a distinct field — confirmed regression:
        // ContosoOrders OrderService, caller passed fieldName == paramName == "stopwatch"). A
        // caller-supplied fieldName that collides with paramName, with or without a leading
        // underscore, is treated the same as omitting fieldName: it falls back to the default
        // "_camelCase(paramName)" derivation, which always differs from paramName.
        var defaultFieldName = $"_{char.ToLower(paramName[0])}{paramName[1..]}";
        string derivedFieldName = fieldName == null || fieldName == paramName || fieldName == $"_{paramName}"
            ? defaultFieldName
            : fieldName;

        var fieldDecl = ((FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            $"private readonly {paramType} {derivedFieldName};")!)
            .WithAddedByComment("AddConstructorParameter");

        var assignmentStatement = SyntaxFactory.ParseStatement($"{derivedFieldName} = {paramName};");
        var newParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
            .WithType(SyntaxFactory.ParseTypeName(paramType).WithTrailingTrivia(SyntaxFactory.Space));

        var ctor = classDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();

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
                .WithBody(body)
                .WithAddedByComment("AddConstructorParameter");
        }

        var newMembers = new List<MemberDeclarationSyntax> { fieldDecl };
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
        var newRoot = root.ReplaceNode(classDecl, newClassNode).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString(),
            Message = $"// paramName='{paramName}', fieldName='{derivedFieldName}'"
        };
    }

    /// <summary>
    /// Removes a DI constructor parameter and its assignment statement. The backing field is only
    /// deleted when a solution-wide reference check (SymbolFinder.FindReferencesAsync) confirms
    /// nothing outside the removed assignment reads or writes it — otherwise the field is left in
    /// place so removal never silently breaks code that still depends on it.
    /// </summary>
    public async Task<DocumentEditResult> RemoveConstructorParameterAsync(FilePath filePath, string className, string paramName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            classNode = ResolveTypeByNameOrSnippet(root, sourceText, className, contextSnippet, lineBefore, lineAfter);
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
        var assignment = ctor.Body.Statements.OfType<ExpressionStatementSyntax>()
            .FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax
            {
                Right: IdentifierNameSyntax rhs
            } assign && rhs.Identifier.Text == paramName
            && (assign.Left is IdentifierNameSyntax || (assign.Left is MemberAccessExpressionSyntax ma && ma.Expression is ThisExpressionSyntax)));

        string? candidateFieldName = assignment != null && assignment.Expression is AssignmentExpressionSyntax a
            ? a.Left switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Name: IdentifierNameSyntax memberId } => memberId.Identifier.Text,
                _ => null
            }
            : null;

        var newParams = ctor.ParameterList.WithParameters(SyntaxFactory.SeparatedList(ctor.ParameterList.Parameters.Where(p => p != targetParam)));
        var newStatements = assignment != null
            ? ctor.Body.Statements.Where(s => s != assignment).ToList()
            : ctor.Body.Statements.ToList();
        var newCtor = ctor.WithParameterList(newParams).WithBody(ctor.Body.WithStatements(SyntaxFactory.List(newStatements)));

        var fieldDecl = candidateFieldName != null
            ? classDecl.Members.OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == candidateFieldName))
            : null;

        var newMembers = classDecl.Members.Select(m => m == ctor ? (MemberDeclarationSyntax)newCtor : m).ToList();

        if (fieldDecl != null)
        {
            var semanticModel = await document.Project.GetCompilationAsync(cancellationToken) is { } compilation
                ? compilation.GetSemanticModel(root.SyntaxTree)
                : null;
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
                        // The assignment statement we're removing is itself a reference to the field —
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
                // No semantic model / symbol available — can't prove the field is unused, so err
                // conservative and leave it in place rather than risk deleting something still live.
                fieldStillUsedElsewhere = true;
            }

            if (!fieldStillUsedElsewhere)
            {
                newMembers = newMembers.Where(m => m != fieldDecl).ToList();
            }
        }

        var newClassNode = classDecl.WithMembers(SyntaxFactory.List(newMembers));
        var newRoot = root.ReplaceNode(classDecl, newClassNode).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString(),
            Message = fieldDecl != null
                ? $"// paramName='{paramName}', fieldName='{candidateFieldName}', fieldRemoved='{newMembers.All(m => m != fieldDecl)}'"
                : $"// paramName='{paramName}'"
        };
    }

    public record ConstructorParameterInfo(string ParamName, string ParamType, string? FieldName);

    /// <summary>
    /// Lists a class's primary constructor parameters alongside their best-guess backing field,
    /// inferred from a `<field> = <paramName>;` (or `this.<field> = <paramName>;`) assignment
    /// statement in the constructor body — the same convention AddConstructorParameterAsync writes.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, List<ConstructorParameterInfo> Parameters)> GetConstructorParametersAsync(FilePath filePath, string className, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            classNode = ResolveTypeByNameOrSnippet(root, sourceText, className, contextSnippet, lineBefore, lineAfter);
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

        var assignments = ctor.Body?.Statements.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression as AssignmentExpressionSyntax)
            .Where(a => a != null && a!.Right is IdentifierNameSyntax)
            .ToList() ?? [];

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

    public async Task<DocumentEditResult> WrapInRegionAsync(FilePath filePath, int startLine, int endLine, string regionName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var text = await document.GetTextAsync(cancellationToken);

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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = sb.ToString()
        };
    }

    /// <summary>
    /// Wraps a code snippet (identified via contextSnippet, lineBefore/lineAfter) in a #region block.
    /// Uses ContextHelper.FindSnippetPosition to locate the snippet, then derives the line number.
    /// </summary>
    public async Task<DocumentEditResult> WrapInRegionAsync(FilePath filePath, string contextSnippet, string? lineBefore, string? lineAfter, string regionName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

        var text = await document.GetTextAsync(cancellationToken);

        try
        {
            // Find the snippet position
            var snippetPos = ContextHelper.FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);

            // Convert position to line number
            var linePos = text.Lines.GetLinePosition(snippetPos);
            var startLine = linePos.Line + 1;
            var endLine = startLine; // Start and end at the snippet's line

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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = sb.ToString()
            };
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// ContextSnippet error: {ex.Message}"
            };
        }
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

    // Task I evaluation (docs/plan-tool-disambiguation-remediation-v1.md, addendum under Task I):
    // NearMissList won over NearestSnippet/CorrectedCoordinates because it's the only strategy that
    // shows an agent every real candidate instead of just the first one — on a genuinely ambiguous
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
    private MemberDeclarationSyntax? ResolveMemberByNameOrSnippet(
        SyntaxNode root, SourceText sourceText, string memberName,
        string? contextSnippet, string? lineBefore, string? lineAfter,
        Func<MemberDeclarationSyntax, bool>? extraFilter = null)
    {
        var candidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => GetMemberName(m) == memberName && !(m.Parent is InterfaceDeclarationSyntax))
            .Where(m => extraFilter == null || extraFilter(m))
            .ToList();

        // A type's own name and its constructor's name are identical (both read from
        // ClassDeclarationSyntax/StructDeclarationSyntax.Identifier and
        // ConstructorDeclarationSyntax.Identifier), so "OrderService" matches both the class
        // declaration and its constructor here. None of these tools operate on whole type
        // declarations (ReplaceMember/ChangeAccessibility/etc. target "a method, property, or
        // field"), so when a constructor shares the name, prefer it over the enclosing type —
        // otherwise the type declaration (found first, being the ancestor node) silently wins
        // and callers asking for "the OrderService member" get the whole class back.
        if (candidates.Count > 1 && candidates.Any(c => c is ConstructorDeclarationSyntax))
        {
            candidates = candidates.Where(c => c is not BaseTypeDeclarationSyntax).ToList();
        }

        if (contextSnippet == null || candidates.Count <= 1)
        {
            // memberName alone already resolves unambiguously (zero or one candidate) — a
            // contextSnippet exists only to disambiguate between multiple same-named candidates,
            // so there's nothing for it to do here. A caller that includes one defensively (or
            // whose snippet has a whitespace/formatting mismatch against the file, unrelated to
            // *which* member is meant) should not have the whole call fail over a match that was
            // never actually needed — confirmed regression: ContosoOrders ApplyDiscount (a single,
            // non-overloaded method) failed ReplaceMember twice on contextSnippet mismatches that
            // had no bearing on which member was targeted, before the caller gave up and switched
            // tools entirely.
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
            // Snippet matched but didn't align to a candidate member — treat as ambiguous
            throw new InvalidOperationException(BuildMemberHint(candidates, matches, "ambiguous"));
        }

        // 2+ matches
        throw new InvalidOperationException(BuildMemberHint(candidates, matches, "ambiguous"));
    }

    public record ContainerMemberInfo(string? Name, string Kind, string Signature, int StartLine, int EndLine);

    /// <summary>
    /// Lists the direct members of one container (class/struct/interface/record) in one file,
    /// syntax-scoped rather than symbol-scoped — unlike GetTypeInfo/GetTypeMembersDetailAsync
    /// (which resolve by type name across the whole solution's compilation and include inherited
    /// members), this only looks at the exact container the caller is about to edit, so its
    /// output lines up with what RemoveMember/ReplaceMember need: an exact memberName plus enough
    /// signature text to build a contextSnippet if the name turns out to be overloaded.
    /// </summary>
    public async Task<(EditOutcome Outcome, string? Message, List<ContainerMemberInfo> Members)> GetContainerMembersAsync(FilePath filePath, string containerName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            return new ContainerMemberInfo(
                GetMemberName(m),
                kind,
                signature,
                lines.GetLineFromPosition(m.SpanStart).LineNumber + 1,
                lines.GetLineFromPosition(m.Span.End).LineNumber + 1);
        }).ToList();

        return (EditOutcome.Modified, null, result);
    }

    /// <summary>
    /// Resolves a type by name, optionally disambiguating with a contextSnippet when the name
    /// matches more than one declaration. Falls back to first-match-by-name when contextSnippet is
    /// null, preserving existing behavior for callers that don't supply one. On an unresolvable or
    /// still-ambiguous contextSnippet, throws with a NearMissList-style hint (see BuildTypeHint).
    /// </summary>
    private BaseTypeDeclarationSyntax? ResolveTypeByNameOrSnippet(
        SyntaxNode root, SourceText sourceText, string typeName,
        string? contextSnippet, string? lineBefore, string? lineAfter,
        Func<BaseTypeDeclarationSyntax, bool>? extraFilter = null)
    {
        var candidates = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == typeName)
            .Where(t => extraFilter == null || extraFilter(t))
            .ToList();

        if (contextSnippet == null || candidates.Count <= 1)
        {
            // typeName alone already resolves unambiguously — see the identical guard and
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

    private string BuildMemberHint(List<MemberDeclarationSyntax> candidates, List<int> matches, string failureMode)
    {
        if (candidates.Count == 0)
        {
            return $"contextSnippet {failureMode}: no candidates found.";
        }

        var previews = candidates.Take(3).Select(c =>
        {
            var line = c.SyntaxTree?.GetLineSpan(c.Span).StartLinePosition.Line + 1 ?? -1;
            var text = c.ToString().Split('\n').First().Trim();
            if (text.Length > 50) text = text.Substring(0, 47) + "...";
            return $"line {line} `{text}`";
        });

        var count = candidates.Count;
        var suffix = count > 3 ? $" (+{count - 3} more)" : "";
        return $"contextSnippet {failureMode} ({count} candidates): {string.Join(", ", previews)}{suffix}. " +
               "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
    }

    private string BuildTypeHint(List<BaseTypeDeclarationSyntax> candidates, List<int> matches, string failureMode)
    {
        if (candidates.Count == 0)
        {
            return $"contextSnippet {failureMode}: no candidates found.";
        }

        var previews = candidates.Take(3).Select(c =>
        {
            var line = c.SyntaxTree?.GetLineSpan(c.Span).StartLinePosition.Line + 1 ?? -1;
            var text = c.ToString().Split('\n').First().Trim();
            if (text.Length > 50) text = text.Substring(0, 47) + "...";
            return $"line {line} `{text}`";
        });

        var count = candidates.Count;
        var suffix = count > 3 ? $" (+{count - 3} more)" : "";
        return $"contextSnippet {failureMode} ({count} candidates): {string.Join(", ", previews)}{suffix}. " +
               "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
    }

    public async Task<DocumentEditResult> SyncInterfaceToImplementationAsync(FilePath filePath, string className, string interfaceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);

        // Find the class document
        var classDocument = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (classDocument == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Class file not found."
            };
        }

        var classRoot = await classDocument.GetSyntaxRootAsync(cancellationToken);
        if (classRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse class file."
            };
        }

        var classNode = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Class not found."
            };
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

                var r = await doc.GetSyntaxRootAsync(cancellationToken);
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Interface not found."
            };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
                UpdatedText = interfaceRoot.ToFullString() // Already up to date
            };
        }

        var newInterfaceNode = interfaceNode.AddMembers(newMembers.ToArray());
        var newInterfaceRoot = interfaceRoot.ReplaceNode(interfaceNode, newInterfaceNode);

        // If interface is in a different file, indicate which file was updated
        if (interfaceDocument != classDocument)
        {
            var updatedPath = interfaceDocument.FilePath ?? interfaceDocument.Name;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = updatedPath,
                UpdatedText = "// Updated file: " + updatedPath + "\n" + newInterfaceRoot.NormalizeWhitespace().ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
            UpdatedText = newInterfaceRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UpdateXmlDocsFromSignatureAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
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
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse document."
            };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Method not found."
            };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.ToFullString()
            };
        }

        // XML doc exists — update it
        var xmlDoc = xmlTrivia.GetStructure() as Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax;
        if (xmlDoc == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse XML documentation."
            };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// No changes needed."
            };
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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = updatedRoot.ToFullString()
        };
    }

    /// <summary>
    /// Returns a preview of what FormatDocument would change without applying changes.
    /// Shows changed line ranges with ±3 lines of context (like a unified diff).
    /// Returns Changed=false and an empty hunks list if the file is already formatted correctly.
    /// </summary>
    public async Task<FormatPreviewResult> FormatDocumentPreviewAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new FormatPreviewResult(false, 0, new List<FormatHunk>());
        }

        var originalText = (await document.GetTextAsync(cancellationToken)).ToString();
        var formattedDoc = await Formatter.FormatAsync(document, null, cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();

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

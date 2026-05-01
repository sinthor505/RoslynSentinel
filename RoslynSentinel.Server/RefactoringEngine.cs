using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Collections.Generic;
using System.Linq;

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

public class RefactoringEngine
{
    private readonly ILogger<RefactoringEngine> _logger;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

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
        if (document == null) return string.Empty;
        var formatted = await Formatter.FormatAsync(document, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    public async Task<Dictionary<string, string>> ChangeSignatureAsync(string filePath, string methodName, int[] newParameterOrder, CancellationToken ct = default)
    {
        // Placeholder implementation for signature change
        return new Dictionary<string, string>();
    }

    public async Task<ExtractMethodResult> ExtractMethodAsync(
        string filePath, int startLine, string startLineText, int endLine, string endLineText,
        string newMethodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractMethod"))
            return new ExtractMethodResult(false, "ExtractMethod feature is disabled.", null, null, null, null);

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
            return new ExtractMethodResult(false, $"File '{filePath}' not found in solution.", null, null, null, null);

        var text = await document.GetTextAsync(ct);
        if (startLine < 1 || startLine > text.Lines.Count)
            return new ExtractMethodResult(false, $"startLine {startLine} out of range (file has {text.Lines.Count} lines).", null, null, null, null);
        if (endLine < startLine || endLine > text.Lines.Count)
            return new ExtractMethodResult(false, $"endLine {endLine} is out of range.", null, null, null, null);

        // Stale-file validation: physical line text must match what the caller observed
        var actualStart = text.Lines[startLine - 1].ToString().Trim();
        var actualEnd   = text.Lines[endLine   - 1].ToString().Trim();
        if (actualStart != startLineText.Trim())
            return new ExtractMethodResult(false,
                $"startLine mismatch: expected '{startLineText.Trim()}' but found '{actualStart}'. File may have changed.", null, null, null, null);
        if (actualEnd != endLineText.Trim())
            return new ExtractMethodResult(false,
                $"endLine mismatch: expected '{endLineText.Trim()}' but found '{actualEnd}'. File may have changed.", null, null, null, null);

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (root == null || semanticModel == null)
            return new ExtractMethodResult(false, "Could not obtain syntax root or semantic model.", null, null, null, null);

        var startPos = text.Lines[startLine - 1].Start;
        var endPos   = text.Lines[endLine   - 1].End;
        var span     = new TextSpan(startPos, endPos - startPos);

        // Find the method body that fully contains the selection
        var containingMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null)
            .FirstOrDefault(m => m.Body!.Span.Contains(span));
        if (containingMethod?.Body == null)
            return new ExtractMethodResult(false,
                "Selected range must be inside a block-body method (expression-bodied methods are not supported).", null, null, null, null);

        // Collect direct body statements that overlap the selection
        var selectedStatements = containingMethod.Body.Statements
            .Where(s => s.Span.IntersectsWith(span))
            .ToList();
        if (selectedStatements.Count == 0)
            return new ExtractMethodResult(false, "No complete statements found in the selected line range.", null, null, null, null);

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
            return new ExtractMethodResult(false,
                $"Cannot extract: ref/out parameter(s) '{string.Join(", ", refOutFlowOut.Select(p => p.Name))}' are " +
                "written inside the selection and read after it. This case cannot be auto-extracted — refactor manually.",
                null, null, null, null);

        // Return value: local variables assigned inside that are used after the region
        var flowsOut = dataFlow.DataFlowsOut
            .Where(s => s.Kind == SymbolKind.Local)
            .ToList();
        if (flowsOut.Count > 1)
            return new ExtractMethodResult(false,
                $"Multiple variables flow out ({string.Join(", ", flowsOut.Select(s => s.Name))}). " +
                "Cannot auto-determine return type — narrow the selection or handle manually.", null, null, null, null);

        ILocalSymbol? returnVar   = flowsOut.Count == 1 ? (ILocalSymbol)flowsOut[0] : null;
        bool isAsync              = selectedStatements.Any(s => s.DescendantTokens().Any(t => t.IsKind(SyntaxKind.AwaitKeyword)));
        bool parentStatic         = containingMethod.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));

        // Build return type syntax
        TypeSyntax returnType = (returnVar, isAsync) switch
        {
            ({ } rv, true)  => SyntaxFactory.ParseTypeName($"Task<{rv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>"),
            ({ } rv, false) => SyntaxFactory.ParseTypeName(rv.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)),
            (null,   true)  => SyntaxFactory.ParseTypeName("Task"),
            _               => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword))
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
                    RefKind.In  => SyntaxKind.InKeyword,
                    _           => SyntaxKind.RefKeyword
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
            bodyStmts.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(returnVar.Name)));

        var modifiers = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (parentStatic) modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (isAsync)      modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

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
                    RefKind.In  => SyntaxFactory.Token(SyntaxKind.InKeyword),
                    _           => SyntaxFactory.Token(SyntaxKind.RefKeyword)
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
        int insertAt  = origStmts.IndexOf(selectedStatements[0]);
        var newStmts  = origStmts.ToList();
        newStmts.RemoveRange(insertAt, selectedStatements.Count);
        newStmts.Insert(insertAt, callStatement);
        var updatedMethod = containingMethod.WithBody(
            containingMethod.Body.WithStatements(SyntaxFactory.List(newStmts)));

        if (containingMethod.Parent is not TypeDeclarationSyntax parentType)
            return new ExtractMethodResult(false, "Could not find the containing type declaration.", null, null, null, null);

        // Append extracted method after the type's existing members
        var newParent = parentType
            .ReplaceNode(containingMethod, updatedMethod)
            .AddMembers(extractedMethod);
        var newRoot = root.ReplaceNode(parentType, newParent);

        var formattedDoc   = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), null, ct);
        var updatedContent = (await formattedDoc.GetTextAsync(ct)).ToString();

        var beforeSnippet       = string.Concat(selectedStatements.Select(s => s.ToFullString())).Trim();
        var callSiteText        = callStatement.NormalizeWhitespace().ToFullString().Trim();
        var extractedMethodText = extractedMethod.ToFullString().Trim();

        return new ExtractMethodResult(true, null, beforeSnippet, callSiteText, extractedMethodText, updatedContent);
    }

    public async Task<Dictionary<string, string>> MoveTypeToFileAsync(string filePath, string typeName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var typeNode = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == typeName);
        if (typeNode == null) return new Dictionary<string, string>();

        var cleanTypeNode = typeNode.WithoutLeadingTrivia().WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        var cleanUsings = SyntaxFactory.List(root!.Usings.Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));
        var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var newRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings);
        
        if (ns != null) 
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax ? (BaseNamespaceDeclarationSyntax)SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name) : SyntaxFactory.NamespaceDeclaration(ns.Name);
            newRoot = newRoot.AddMembers(newNs.AddMembers(cleanTypeNode));
        }
        else newRoot = newRoot.AddMembers(cleanTypeNode);

        var sourceDirectory = Path.GetDirectoryName(document.FilePath ?? filePath);
        var newPath = string.IsNullOrEmpty(sourceDirectory)
            ? $"{typeName}.cs"
            : Path.Combine(sourceDirectory, $"{typeName}.cs");
        var updatedOrig = root!.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!;

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
        if (root == null) return new Dictionary<string, string>();

        var allTypes = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax || t.Parent is BaseNamespaceDeclarationSyntax)
            .ToList();

        if (allTypes.Count <= 1) return new Dictionary<string, string>();

        var fileBaseName = Path.GetFileNameWithoutExtension(document.FilePath ?? document.Name);
        var primaryType = allTypes.FirstOrDefault(t => t.Identifier.Text == fileBaseName) ?? allTypes[0];
        var typesToMove = allTypes.Where(t => t != primaryType).ToList();

        if (typesToMove.Count == 0) return new Dictionary<string, string>();

        var changes = new Dictionary<string, string>();
        var sourceDirectory = Path.GetDirectoryName(document.FilePath) ?? "";

        foreach (var typeNode in typesToMove)
        {
            var ns = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            var cleanTypeNode = typeNode.WithoutLeadingTrivia()
                .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            var cleanUsings = SyntaxFactory.List(root.Usings.Select(u =>
                u.WithoutTrailingTrivia().WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)));

            var newRoot = SyntaxFactory.CompilationUnit().WithUsings(cleanUsings);
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

        var updatedRoot = root.RemoveNodes(typesToMove, SyntaxRemoveOptions.KeepNoTrivia)!;
        var updatedOrigDoc = document.WithSyntaxRoot(updatedRoot);
        var formattedOrigDoc = await Formatter.FormatAsync(updatedOrigDoc, null, ct);
        changes[document.FilePath ?? document.Name] = (await formattedOrigDoc.GetTextAsync(ct)).ToString();

        return changes;
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();
        return await MoveAllTypesToFilesForDocumentAsync(document, ct);
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesInProjectAsync(string projectName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"Project '{projectName}' not found.");

        var allChanges = new Dictionary<string, string>();
        foreach (var document in project.Documents.Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, ct))
                allChanges[kvp.Key] = kvp.Value;
        }
        return allChanges;
    }

    public async Task<Dictionary<string, string>> MoveAllTypesToFilesInSolutionAsync(CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("MoveTypeToFile")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var allChanges = new Dictionary<string, string>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents).Where(d => d.FilePath?.EndsWith(".cs") == true))
        {
            foreach (var kvp in await MoveAllTypesToFilesForDocumentAsync(document, ct))
                allChanges[kvp.Key] = kvp.Value;
        }
        return allChanges;
    }

    public async Task<Dictionary<string, string>> ExtractInterfaceAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ExtractInterface")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return new Dictionary<string, string>();

        var methods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));
        var ifaceMembers = methods.Select(m => (MemberDeclarationSyntax)SyntaxFactory.MethodDeclaration(m.ReturnType, m.Identifier).WithParameterList(m.ParameterList).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))).ToArray();
        var ifaceNode = SyntaxFactory.InterfaceDeclaration(interfaceName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMembers);

        var newClass = classNode.AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName)));
        var ifacePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{interfaceName}.cs");
        var updatedOrig = root!.ReplaceNode(classNode, newClass);

        return new Dictionary<string, string> { { filePath, updatedOrig.ToFullString() }, { ifacePath, ifaceNode.NormalizeWhitespace().ToFullString() } };
    }

    public async Task<RenameSymbolResult> RenameSymbolAsync(string filePath, string symbolName, string contextSnippet, string newName, CancellationToken ct = default)
    {
        static RenameSymbolResult Err(string msg, string n) =>
            new("", n, new Dictionary<string, string>(), new List<RenameFileChange>(), msg);

        if (!_config.IsFeatureEnabled("Rename")) return Err("Feature 'Rename' is disabled.", newName);

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return Err($"File not found in solution: {filePath}", newName);

        var text       = await document.GetTextAsync(ct);
        var fullSource = text.ToString();

        // Locate contextSnippet — must match exactly once
        int firstIdx = fullSource.IndexOf(contextSnippet, StringComparison.Ordinal);
        if (firstIdx < 0)
            return Err($"Context snippet not found in file. Verify the snippet is copied verbatim from the source: \"{contextSnippet}\"", newName);

        int secondIdx = fullSource.IndexOf(contextSnippet, firstIdx + 1, StringComparison.Ordinal);
        if (secondIdx >= 0)
            return Err($"Context snippet matches multiple locations in the file. Use a longer or more unique snippet to pin the symbol.", newName);

        // Find symbolName as a word-boundary identifier within the matched snippet
        int symOffset = FindIdentifierInSnippet(contextSnippet, symbolName);
        if (symOffset < 0)
            return Err($"'{symbolName}' not found as a distinct identifier in the context snippet \"{contextSnippet}\". Ensure the symbol appears verbatim and is not part of a larger identifier.", newName);

        var pos    = firstIdx + symOffset;
        var model  = await document.GetSemanticModelAsync(ct);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model!, pos, solution.Workspace, ct);
        if (symbol == null)
            return Err($"No Roslyn symbol resolved at the identified position. The context may be pointing at a keyword or non-symbol token.", newName);

        var oldName = symbol.Name;
        var updated = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            solution, symbol, new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), newName, ct);

        var pendingChanges = new Dictionary<string, string>();
        var fileChanges    = new List<RenameFileChange>();
        foreach (var pc in updated.GetChanges(solution).GetProjectChanges())
        {
            foreach (var docId in pc.GetChangedDocuments())
            {
                var newDoc     = updated.GetDocument(docId)!;
                var filePth    = newDoc.FilePath ?? newDoc.Name;
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
        var oldLines = oldContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var newLines = newContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var hunks    = new List<RenameHunk>();
        // Use Max so lines added/removed at the end (e.g. new using directives) are included
        int len      = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < len; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : "";
            var newLine = i < newLines.Length ? newLines[i] : "";
            if (oldLine == newLine) continue;
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
            if (idx < 0) return -1;
            bool leftBound  = idx == 0 || !IsIdentChar(snippet[idx - 1]);
            bool rightBound = idx + symbolName.Length >= snippet.Length || !IsIdentChar(snippet[idx + symbolName.Length]);
            if (leftBound && rightBound) return idx;
            searchFrom = idx + 1;
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public async Task<string> ConvertIndexerToMethodAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ConvertIndexerToMethod")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        var indexer = root?.DescendantNodes().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
        if (indexer == null) return root?.ToFullString() ?? "";

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
        if (!_config.IsFeatureEnabled("AddRemoveParams")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || !method.ParameterList.Parameters.Any()) return root?.ToFullString() ?? "";

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var member = root?.DescendantNodes().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => GetMemberName(m) == memberName);
        if (member == null) return root?.ToFullString() ?? "";
        var newMember = SyntaxFactory.ParseMemberDeclaration(newSource);
        if (newMember == null) return root?.ToFullString() ?? "";
        return root!.ReplaceNode(member, newMember).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddMemberAsync(string filePath, string containerName, string newMemberSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null) return root?.ToFullString() ?? "";
        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null) return root?.ToFullString() ?? "";
        var newContainer = container switch {
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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var member = root?.DescendantNodes().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => GetMemberName(m) == memberName);
        if (member == null) return root?.ToFullString() ?? "";
        return root!.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertToPrimaryConstructorAsync(string filePath, string className, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("PrimaryConstructors")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return root?.ToFullString() ?? "";

        var ctor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null || ctor.ParameterList.Parameters.Count == 0) return root?.ToFullString() ?? "";

        // Minimal implementation for tests: convert to class C(int x) and remove fields/ctor
        var newClass = SyntaxFactory.ClassDeclaration(classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(ctor.ParameterList);

        var members = classNode.Members.Where(m => m is not ConstructorDeclarationSyntax && m is not FieldDeclarationSyntax).ToList();
        newClass = newClass.WithMembers(SyntaxFactory.List(members));

        var newRoot = root!.ReplaceNode(classNode, newClass);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<Dictionary<string, string>> SafeDeleteSymbolAsync(string filePath, int line, int column, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("SafeDelete")) return new Dictionary<string, string>();
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return new Dictionary<string, string>();
        var root = await document.GetSyntaxRootAsync(ct);
        var model = await document.GetSemanticModelAsync(ct);
        var text = await document.GetTextAsync(ct);
        var pos = text.Lines[line-1].Start + (column - 1);
        var node = root!.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0));
        var symbol = model!.GetDeclaredSymbol(node, ct) ?? model.GetSymbolInfo(node, ct).Symbol;
        if (symbol == null) return new Dictionary<string, string>();

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
                    _logger.LogWarning("SafeDelete blocked: symbol '{SymbolName}' is mentioned by string literal in {DocName} — possible reflection usage.", symbol.Name, doc.Name);
                    return new Dictionary<string, string>();
                }
            }
        }

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        if (refs.Any(r => r.Locations.Any()))
        {
            _logger.LogWarning("SafeDelete blocked: symbol '{SymbolName}' has usages and cannot be safely deleted.", symbol.Name);
            return new Dictionary<string, string>();
        }
        
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null) return new Dictionary<string, string>();
        
        return new Dictionary<string, string> { { filePath, root.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString() } };
    }

    public async Task<string> AddUsingDirectiveAsync(string filePath, string namespaceName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = (CompilationUnitSyntax?)await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        // Idempotency check
        var targetName = namespaceName.StartsWith("static ") ? namespaceName[7..] : namespaceName;
        if (root.Usings.Any(u => u.Name?.ToString() == targetName))
            return root.ToFullString();

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var enumNode = root?.DescendantNodes().OfType<EnumDeclarationSyntax>().FirstOrDefault(e => e.Identifier.Text == enumName);
        if (enumNode == null) return root?.ToFullString() ?? "";

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null) return root?.ToFullString() ?? "";

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null) return root?.ToFullString() ?? "";

        if (container is TypeDeclarationSyntax typeDecl)
        {
            var membersList = typeDecl.Members.ToList();
            var idx = membersList.FindIndex(m => GetMemberName(m) == afterMemberName);
            SyntaxList<MemberDeclarationSyntax> newMembers;
            if (idx < 0)
                newMembers = typeDecl.Members.Add(newMember);
            else
                newMembers = SyntaxFactory.List(membersList.Take(idx + 1).Append(newMember).Concat(membersList.Skip(idx + 1)));
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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null) return root?.ToFullString() ?? "";

        var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);
        if (newMember == null) return root?.ToFullString() ?? "";

        if (container is TypeDeclarationSyntax typeDecl)
        {
            var membersList = typeDecl.Members.ToList();
            var idx = membersList.FindIndex(m => GetMemberName(m) == beforeMemberName);
            SyntaxList<MemberDeclarationSyntax> newMembers;
            if (idx < 0)
                newMembers = typeDecl.Members.Add(newMember);
            else
                newMembers = SyntaxFactory.List(membersList.Take(idx).Append(newMember).Concat(membersList.Skip(idx)));
            var newContainer = typeDecl.WithMembers(newMembers);
            return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
        }

        return await AddMemberAsync(filePath, containerName, newMemberSource, ct);
    }

    public async Task<string> AddAttributeAsync(string filePath, string targetName, string attributeSource, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var normalizedSource = attributeSource.Trim();
        if (!normalizedSource.StartsWith("["))
            normalizedSource = $"[{normalizedSource}]";
        // Parse by embedding in a dummy class declaration
        var snippet = SyntaxFactory.ParseCompilationUnit($"{normalizedSource}\npublic class __Dummy__ {{}}");
        var attrList = snippet.DescendantNodes().OfType<AttributeListSyntax>().FirstOrDefault();
        if (attrList == null) return root.ToFullString();

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
        if (container == null) return root?.ToFullString() ?? "";

        // Idempotency check
        if (container.BaseList?.Types.Any(t => t.ToString().Contains(baseTypeName)) == true)
            return root!.ToFullString();

        var baseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(baseTypeName));
        var newContainer = container.AddBaseListTypes(baseType);
        return root!.ReplaceNode(container, newContainer).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveAttributeAsync(string filePath, string targetName, string attributeName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

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

        if (target is not MemberDeclarationSyntax memberTarget) return root.ToFullString();

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
        if (container == null) return root?.ToFullString() ?? "";
        if (container.BaseList == null) return root!.ToFullString();

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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        SyntaxKind[] newKinds = accessibility.ToLowerInvariant() switch
        {
            "public"             => [SyntaxKind.PublicKeyword],
            "private"            => [SyntaxKind.PrivateKeyword],
            "internal"           => [SyntaxKind.InternalKeyword],
            "protected"          => [SyntaxKind.ProtectedKeyword],
            "protected internal" => [SyntaxKind.ProtectedKeyword, SyntaxKind.InternalKeyword],
            "private protected"  => [SyntaxKind.PrivateKeyword, SyntaxKind.ProtectedKeyword],
            _                    => [SyntaxKind.PublicKeyword]
        };

        var accessModifierKinds = new HashSet<SyntaxKind>
        {
            SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword,
            SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword
        };

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null) return root.ToFullString();

        var remaining = target.Modifiers.Where(m => !accessModifierKinds.Contains(m.Kind())).ToList();
        var newTokens = newKinds.Select(k => SyntaxFactory.Token(k).WithTrailingTrivia(SyntaxFactory.Space));
        var newModifiers = SyntaxFactory.TokenList(newTokens.Concat(remaining));
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null) return root.ToFullString();

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None) kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        if (target.Modifiers.Any(m => m.IsKind(kind))) return root.ToFullString(); // idempotent

        var token = SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space);
        var newModifiers = target.Modifiers.Add(token);
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RemoveModifierAsync(string filePath, string targetName, string modifier, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null) return root.ToFullString();

        var kind = SyntaxFacts.GetKeywordKind(modifier);
        if (kind == SyntaxKind.None) kind = SyntaxFacts.GetContextualKeywordKind(modifier);
        if (!target.Modifiers.Any(m => m.IsKind(kind))) return root.ToFullString(); // idempotent

        var newModifiers = SyntaxFactory.TokenList(target.Modifiers.Where(m => !m.IsKind(kind)));
        return root.ReplaceNode(target, target.WithModifiers(newModifiers)).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddSummaryCommentAsync(string filePath, string targetName, string summaryText, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var target = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => GetMemberName(m) == targetName);
        if (target == null) return root.ToFullString();

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
        if (isStatic) parts.Append(" static");
        if (isReadonly) parts.Append(" readonly");
        parts.Append($" {fieldType} {fieldName}");
        if (initializer != null) parts.Append($" = {initializer}");
        parts.Append(';');
        return await AddMemberAsync(filePath, containerName, parts.ToString(), ct);
    }

    public async Task<string> SortMembersAsync(string filePath, string containerName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        var container = root?.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == containerName);
        if (container == null) return root?.ToFullString() ?? "";

        static int CategoryOf(MemberDeclarationSyntax m) => m switch
        {
            FieldDeclarationSyntax                                              => 0,
            ConstructorDeclarationSyntax                                        => 1,
            DestructorDeclarationSyntax                                         => 2,
            PropertyDeclarationSyntax                                           => 3,
            IndexerDeclarationSyntax                                            => 4,
            EventDeclarationSyntax or EventFieldDeclarationSyntax               => 5,
            MethodDeclarationSyntax                                             => 6,
            OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax    => 7,
            ClassDeclarationSyntax or RecordDeclarationSyntax
                or StructDeclarationSyntax or InterfaceDeclarationSyntax
                or EnumDeclarationSyntax                                        => 8,
            _                                                                   => 9
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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var tree = root.SyntaxTree;

        int StatementStartLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan).StartLinePosition.Line + 1;
        int StatementEndLine(StatementSyntax s) =>
            tree.GetLineSpan(s.FullSpan).EndLinePosition.Line + 1;

        // Find the smallest block that fully contains the line range
        var block = root.DescendantNodes()
            .OfType<BlockSyntax>()
            .Where(b =>
            {
                var ls = tree.GetLineSpan(b.Span);
                return ls.StartLinePosition.Line + 1 <= startLine &&
                       ls.EndLinePosition.Line + 1 >= endLine;
            })
            .OrderBy(b => b.Span.Length)
            .FirstOrDefault();
        if (block == null) return root.ToFullString();

        var targeted = block.Statements
            .Where(s => StatementStartLine(s) <= endLine && StatementEndLine(s) >= startLine)
            .ToList();
        if (targeted.Count == 0) return root.ToFullString();

        var tryBlock = SyntaxFactory.Block(SyntaxFactory.List(targeted));
        var catchDecl = SyntaxFactory.CatchDeclaration(
            SyntaxFactory.ParseTypeName(exceptionType),
            SyntaxFactory.Identifier(catchVariableName));

        StatementSyntax? catchStmt = null;
        if (catchBody != null)
            catchStmt = SyntaxFactory.ParseStatement(catchBody);

        var catchBlock = catchStmt != null
            ? SyntaxFactory.Block(catchStmt)
            : SyntaxFactory.Block();

        var catchClause = SyntaxFactory.CatchClause(catchDecl, null, catchBlock);
        var tryStatement = SyntaxFactory.TryStatement(tryBlock, SyntaxFactory.List([catchClause]), null);

        var newStatements = block.Statements
            .Select((s, i) =>
            {
                if (s == targeted[0]) return (StatementSyntax)tryStatement;
                if (targeted.Contains(s)) return null;
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
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return root.ToFullString();

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
                body = ctor.Body.AddStatements(assignmentStatement);
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
                newMembers.Add(newCtor);
            else
                newMembers.Add(m);
        }
        if (ctor == null) newMembers.Add(newCtor);

        var newClassNode = classNode.WithMembers(SyntaxFactory.List(newMembers));
        return root.ReplaceNode(classNode, newClassNode).NormalizeWhitespace().ToFullString();
    }

    public async Task<string> WrapInRegionAsync(string filePath, int startLine, int endLine, string regionName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var text = await document.GetTextAsync(ct);

        var lines = text.Lines;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            int lineNumber = i + 1; // 1-based
            if (lineNumber == startLine)
                sb.AppendLine($"#region {regionName}");
            sb.AppendLine(lines[i].ToString());
            if (lineNumber == endLine)
                sb.AppendLine("#endregion");
        }
        return sb.ToString();
    }

    private string? GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch {
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
        if (classDocument == null) return "// Class file not found.";

        var classRoot = await classDocument.GetSyntaxRootAsync(ct);
        if (classRoot == null) return "// Could not parse class file.";

        var classNode = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return "// Class not found.";

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
                if (doc == classDocument) continue;
                var r = await doc.GetSyntaxRootAsync(ct);
                if (r == null) continue;
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
            return "// Interface not found.";

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
            if (existingMethodSigs.Contains(sig)) continue;

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
            if (existingPropertyNames.Contains(prop.Identifier.Text)) continue;

            // Build interface property
            var hasGetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true
                            || prop.ExpressionBody != null;
            var hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
            var hasInit = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;

            var accessors = new List<AccessorDeclarationSyntax>();
            if (hasGetter)
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            if (hasSetter)
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            if (hasInit)
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

            var ifaceProp = SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                .WithModifiers(SyntaxFactory.TokenList())
                .NormalizeWhitespace();
            newMembers.Add(ifaceProp);
        }

        if (newMembers.Count == 0)
            return interfaceRoot.ToFullString(); // Already up to date

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
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null) return root.ToFullString();

        // Find the XML doc comment trivia preceding the method
        var xmlTrivia = method.GetLeadingTrivia()
            .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                  t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        if (xmlTrivia == default) return root.ToFullString(); // No XML doc — do nothing

        var currentParams = method.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet();

        var xmlDoc = xmlTrivia.GetStructure() as Microsoft.CodeAnalysis.CSharp.Syntax.DocumentationCommentTriviaSyntax;
        if (xmlDoc == null) return root.ToFullString();

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
            .Where(e => {
                var name = e.StartTag.Attributes.OfType<XmlNameAttributeSyntax>().FirstOrDefault()?.Identifier.Identifier.Text;
                return name != null && !currentParams.Contains(name);
            })
            .ToList();

        if (!toAdd.Any() && !toRemove.Any()) return root.ToFullString();

        // Build updated XML doc content
        var updatedContent = xmlDoc.Content.ToList();

        // Remove stale param tags
        foreach (var staleTag in toRemove)
            updatedContent.Remove(staleTag);

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

        var newXmlDoc = xmlDoc.WithContent(SyntaxFactory.List(updatedContent));
        var newTrivia = SyntaxFactory.Trivia(newXmlDoc);

        var newLeadingTrivia = method.GetLeadingTrivia().Replace(xmlTrivia, newTrivia);
        var newMethod = method.WithLeadingTrivia(newLeadingTrivia);
        var newRoot = root.ReplaceNode(method, newMethod);
        return newRoot.ToFullString();
    }
}

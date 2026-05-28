using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public record OutParamConversionResult(
    bool Success,
    string Message,
    string? OriginalSignature,
    string? NewSignature,
    int CallSitesRewritten,
    List<string> CallSiteWarnings
);

public class OutParamRefactoringEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public OutParamRefactoringEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<OutParamConversionResult> ConvertOutParamsToValueTupleAsync(
        string filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        Document? document = null;
        foreach (var project in solution.Projects)
        {
            document = project.Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (document != null)
            {
                break;
            }
        }

        if (document == null)
        {
            return new OutParamConversionResult(false, $"File not found: {filePath}", null, null, 0, []);
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel == null)
        {
            return new OutParamConversionResult(false, "Could not get semantic model.", null, null, 0, []);
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new OutParamConversionResult(false, "Could not get syntax root.", null, null, 0, []);
        }

        // Find the target method
        var methodDecl = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);

        if (methodDecl == null)
        {
            return new OutParamConversionResult(false, $"Method '{methodName}' not found in {filePath}.", null, null, 0, []);
        }

        var outParams = methodDecl.ParameterList.Parameters
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)))
            .ToList();

        if (outParams.Count < 2)
        {
            return new OutParamConversionResult(false,
                $"Method '{methodName}' has fewer than 2 out parameters ({outParams.Count}). No conversion needed.",
                null, null, 0, []);
        }

        if (semanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol methodSymbol)
        {
            return new OutParamConversionResult(false, "Could not resolve method symbol.", null, null, 0, []);
        }

        string originalReturn = methodDecl.ReturnType.ToString();
        bool isVoid = originalReturn is "void" or "Task";
        bool isAsync = methodDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

        // Build the tuple type string
        var outElements = outParams.Select(p =>
            $"{p.Type?.ToString().Replace("out ", "").Trim() ?? "object"} {p.Identifier.Text}");

        string tupleType;
        if (isVoid)
        {
            tupleType = $"({string.Join(", ", outElements)})";
        }
        else
        {
            tupleType = $"({originalReturn} result, {string.Join(", ", outElements)})";
        }

        if (isAsync && isVoid)
        {
            tupleType = $"Task<({string.Join(", ", outElements)})>";
        }
        else if (isAsync && !isVoid)
        {
            tupleType = $"Task<({originalReturn.Replace("Task<", "").TrimEnd('>')} result, {string.Join(", ", outElements)})>";
        }

        var originalSignature = $"{originalReturn} {methodName}({methodDecl.ParameterList})";
        var outParamNames = outParams.Select(p => p.Identifier.Text).ToList();

        // --- Rewrite the method declaration ---
        var workspace = solution.Workspace;
        var editor = await DocumentEditor.CreateAsync(document, ct);

        // 1. Change return type
        var newReturnType = SyntaxFactory.ParseTypeName(tupleType)
            .WithLeadingTrivia(methodDecl.ReturnType.GetLeadingTrivia())
            .WithTrailingTrivia(methodDecl.ReturnType.GetTrailingTrivia());
        editor.ReplaceNode(methodDecl.ReturnType, newReturnType);

        // 2. Remove out parameters from parameter list
        var newParams = methodDecl.ParameterList.Parameters
            .Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)));
        var newParamList = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(newParams));
        editor.ReplaceNode(methodDecl.ParameterList, newParamList);

        // 3. Rewrite method body: introduce locals for each out param, fix returns
        if (methodDecl.Body != null)
        {
            var newBody = RewriteMethodBody(methodDecl.Body, outParamNames, isVoid);
            editor.ReplaceNode(methodDecl.Body, newBody);
        }

        var modifiedDocument = editor.GetChangedDocument();
        var callSiteWarnings = new List<string>();
        int callSitesRewritten = 0;

        // --- Find and rewrite call sites ---
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, solution, ct);
        var callSitesByDocument = references
            .SelectMany(r => r.Locations)
            .GroupBy(l => l.Document.Id);

        // Apply method body changes first
        var updatedSolution = modifiedDocument.Project.Solution;

        foreach (var docGroup in callSitesByDocument)
        {
            var callDoc = updatedSolution.GetDocument(docGroup.Key);
            if (callDoc == null)
            {
                continue;
            }

            var callRoot = await callDoc.GetSyntaxRootAsync(ct);
            if (callRoot == null)
            {
                continue;
            }

            var callEditor = await DocumentEditor.CreateAsync(callDoc, ct);
            bool changed = false;

            foreach (var location in docGroup)
            {
                var span = location.Location.SourceSpan;
                var node = callRoot.FindNode(span, getInnermostNodeForTie: true);

                // Walk up to find the invocation expression
                var invocation = node.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault()
                    ?? (node as InvocationExpressionSyntax);
                if (invocation == null)
                {
                    continue;
                }

                // Collect out argument variable names used at call site
                var outArgs = invocation.ArgumentList.Arguments
                    .Where(a => a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    .ToList();

                // Build new argument list (remove out args)
                var newArgs = invocation.ArgumentList.Arguments
                    .Where(a => !a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword));
                var newArgList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
                var newInvocation = invocation.WithArgumentList(newArgList);

                // Determine statement context
                var statement = invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                if (statement == null)
                {
                    callSiteWarnings.Add($"Could not rewrite complex call site at {location.Location.GetLineSpan()}");
                    continue;
                }

                var outVarNames = outArgs.Select(a =>
                {
                    // Handle "out var x" and "out T x" declaration patterns
                    if (a.Expression is DeclarationExpressionSyntax decl)
                    {
                        return decl.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_";
                    }

                    return a.Expression.ToString().TrimStart('_').Trim();
                }).ToList();

                StatementSyntax? replacement = null;

                if (statement is ExpressionStatementSyntax exprStmt && exprStmt.Expression == invocation)
                {
                    // Standalone call: Method(arg, out x, out y)
                    // → var (x, y) = Method(arg);  or  var (result, x, y) = Method(arg);
                    string tupleVars = isVoid
                        ? $"({string.Join(", ", outVarNames)})"
                        : $"(_, {string.Join(", ", outVarNames)})";
                    replacement = SyntaxFactory.ParseStatement(
                        $"var {tupleVars} = {newInvocation.ToFullString()};")
                        .WithLeadingTrivia(statement.GetLeadingTrivia())
                        .WithTrailingTrivia(statement.GetTrailingTrivia());
                }
                else if (statement is LocalDeclarationStatementSyntax localDecl)
                {
                    // bool ok = Method(arg, out x, out y)
                    // → var (ok, x, y) = Method(arg);
                    var assignedVarName = localDecl.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "result";
                    string tupleVars = isVoid
                        ? $"({string.Join(", ", outVarNames)})"
                        : $"({assignedVarName}, {string.Join(", ", outVarNames)})";
                    replacement = SyntaxFactory.ParseStatement(
                        $"var {tupleVars} = {newInvocation.ToFullString()};")
                        .WithLeadingTrivia(statement.GetLeadingTrivia())
                        .WithTrailingTrivia(statement.GetTrailingTrivia());
                }
                else if (statement is ExpressionStatementSyntax exprStmt2 &&
                         exprStmt2.Expression is AssignmentExpressionSyntax assignExpr &&
                         assignExpr.Right == invocation)
                {
                    // existing = Method(arg, out x, out y)
                    var assignedText = assignExpr.Left.ToString();
                    string tupleVars = isVoid
                        ? $"({string.Join(", ", outVarNames)})"
                        : $"({assignedText}, {string.Join(", ", outVarNames)})";
                    replacement = SyntaxFactory.ParseStatement(
                        $"var {tupleVars} = {newInvocation.ToFullString()};")
                        .WithLeadingTrivia(statement.GetLeadingTrivia())
                        .WithTrailingTrivia(statement.GetTrailingTrivia());
                }
                else
                {
                    // Complex usage — add a TODO comment and do a best-effort argument rewrite
                    callSiteWarnings.Add(
                        $"TODO: manual rewrite needed at {location.Location.GetLineSpan()} — complex out-param usage");
                    callEditor.ReplaceNode(invocation, newInvocation
                        .WithLeadingTrivia(SyntaxFactory.Comment("// TODO: rewrite for ValueTuple return — "),
                         SyntaxFactory.ElasticMarker));
                    changed = true;
                    callSitesRewritten++;
                    continue;
                }

                if (replacement != null)
                {
                    callEditor.ReplaceNode(statement, replacement);
                    changed = true;
                    callSitesRewritten++;
                }
            }

            if (changed)
            {
                var changedCallDoc = callEditor.GetChangedDocument();
                updatedSolution = changedCallDoc.Project.Solution;
            }
        }

        // Apply the solution back to the workspace
        bool applied = workspace.TryApplyChanges(updatedSolution);

        if (!applied)
        {
            return new OutParamConversionResult(false,
                "TryApplyChanges failed — workspace may be read-only.",
                originalSignature, tupleType, 0, callSiteWarnings);
        }

        var newSig = $"{tupleType} {methodName}({string.Join(", ", methodDecl.ParameterList.Parameters.Where(p => !p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword))))})";
        return new OutParamConversionResult(
            true,
            $"Converted '{methodName}' to return {tupleType}. {callSitesRewritten} call site(s) rewritten.",
            originalSignature,
            newSig,
            callSitesRewritten,
            callSiteWarnings);
    }

    private static BlockSyntax RewriteMethodBody(
        BlockSyntax body, List<string> outParamNames, bool isVoid)
    {
        // Insert local variable declarations for each out param at method top
        var localDecls = outParamNames.Select(name =>
            SyntaxFactory.ParseStatement($"var {name} = default!;\n")
                .WithLeadingTrivia(body.Statements.FirstOrDefault()?.GetLeadingTrivia()
                    ?? SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    "))));

        // Build the tuple expression for returns
        string tupleArgs = isVoid
            ? $"({string.Join(", ", outParamNames)})"
            : $"(result, {string.Join(", ", outParamNames)})";

        // Rewrite return statements
        var rewriter = new ReturnRewriter(outParamNames, isVoid);
        var rewrittenBody = (BlockSyntax)rewriter.Visit(body);

        // Check if there's a trailing return; add one if the body doesn't already end with return
        var stmts = rewrittenBody.Statements.ToList();
        bool lastIsReturn = stmts.LastOrDefault() is ReturnStatementSyntax;

        var newStmts = localDecls.Concat(stmts);
        if (!lastIsReturn)
        {
            var finalReturn = SyntaxFactory.ParseStatement($"return {tupleArgs};\n")
                .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    ")));
            newStmts = newStmts.Append(finalReturn);
        }

        return rewrittenBody.WithStatements(
            SyntaxFactory.List(newStmts));
    }

    private sealed class ReturnRewriter : CSharpSyntaxRewriter
    {
        private readonly List<string> _outParamNames;
        private readonly bool _isVoid;

        public ReturnRewriter(List<string> outParamNames, bool isVoid)
        {
            _outParamNames = outParamNames;
            _isVoid = isVoid;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            string tupleArgs;
            if (node.Expression == null || _isVoid)
            {
                tupleArgs = $"({string.Join(", ", _outParamNames)})";
            }
            else
            {
                tupleArgs = $"({node.Expression}, {string.Join(", ", _outParamNames)})";
            }

            return SyntaxFactory.ParseStatement($"return {tupleArgs};\n")
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Server;

public class AdvancedLogicEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AdvancedLogicEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<Dictionary<FilePath, string>> InvertBooleanLogicAsync(string filepath, string boolName, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        var variable = root?.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.Text == boolName);
        ISymbol? symbol = null;
        if (variable != null)
        {
            symbol = semanticModel?.GetDeclaredSymbol(variable, cancellationToken);
        }
        else
        {
            var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == boolName);
            if (method != null)
            {
                symbol = semanticModel?.GetDeclaredSymbol(method, cancellationToken);
            }
        }

        if (symbol == null)
        {
            return new Dictionary<FilePath, string>();
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var updatedSolution = solution;

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDocument = updatedSolution.GetDocument(location.Document.Id)!;
                var refRoot = await refDocument.GetSyntaxRootAsync(cancellationToken);
                var node = refRoot?.FindNode(location.Location.SourceSpan) as ExpressionSyntax;

                if (node != null)
                {
                    var parent = node.Parent;
                    if (parent is PrefixUnaryExpressionSyntax p && p.IsKind(SyntaxKind.LogicalNotExpression))
                    {
                        refRoot = refRoot!.ReplaceNode(p, node);
                    }
                    else
                    {
                        var inverted = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, node);
                        refRoot = refRoot!.ReplaceNode(node, inverted);
                    }
                    updatedSolution = updatedSolution.WithDocumentSyntaxRoot(refDocument.Id, refRoot!);
                }
            }
        }

        var changes = new Dictionary<FilePath, string>();
        foreach (var projectChange in updatedSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var doc = updatedSolution.GetDocument(changedDocId)!;
                changes[doc.FilePath ?? doc.Name] = (await doc.GetTextAsync(cancellationToken)).ToString();
            }
        }
        return changes;
    }

    public async Task<DocumentEditResult> ConvertIfToSwitchExpressionAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var ifStmt = method.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStmt == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        if (!TryExtractIfChainBranches(ifStmt, out var condVar, out var branches, out var defaultResult))
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var arms = branches.Select(b =>
            SyntaxFactory.SwitchExpressionArm(SyntaxFactory.ConstantPattern(b.Pattern), b.Result)).ToList();
        if (defaultResult != null)
        {
            arms.Add(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), defaultResult));
        }

        var switchExpr = SyntaxFactory.SwitchExpression(
            SyntaxFactory.ParseExpression(condVar),
            SyntaxFactory.SeparatedList(arms));
        var newRoot = root.ReplaceNode(ifStmt, SyntaxFactory.ReturnStatement(switchExpr));
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    public async Task<DocumentEditResult> ConvertIfToSwitchStatementAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var ifStmt = method.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStmt == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        if (!TryExtractIfChainBranches(ifStmt, out var condVar, out var branches, out var defaultResult))
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = "", FilePath = filePath };
        }

        var sections = new List<SwitchSectionSyntax>();
        foreach (var branch in branches)
        {
            sections.Add(SyntaxFactory.SwitchSection(
                SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(branch.Pattern)),
                SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(branch.Result))));
        }
        if (defaultResult != null)
        {
            sections.Add(SyntaxFactory.SwitchSection(
                SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()),
                SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(defaultResult))));
        }

        var switchStmt = SyntaxFactory.SwitchStatement(SyntaxFactory.ParseExpression(condVar))
            .WithSections(SyntaxFactory.List(sections));
        var newRoot = root.ReplaceNode(ifStmt, switchStmt);
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    public async Task<DocumentEditResult> ExtensionToStaticAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Remove(firstParam.Modifiers.First(m => m.IsKind(SyntaxKind.ThisKeyword))));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = root!.ReplaceNode(methodNode, newMethod).NormalizeWhitespace().ToFullString(), FilePath = filePath };
            }
        }
        return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root?.ToFullString() ?? "", FilePath = filePath };
    }

    public async Task<DocumentEditResult> ConvertStaticToExtensionAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var methodNode = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (methodNode != null && methodNode.ParameterList.Parameters.Any())
        {
            var firstParam = methodNode.ParameterList.Parameters[0];
            if (!firstParam.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
            {
                var newParam = firstParam.WithModifiers(firstParam.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.ThisKeyword)));
                var newMethod = methodNode.WithParameterList(methodNode.ParameterList.WithParameters(methodNode.ParameterList.Parameters.Replace(firstParam, newParam)));
                var updatedRoot = root!.ReplaceNode(methodNode, newMethod);

                // Also ensure the containing class is marked as static
                // Find the class in the updated root
                var classNode = updatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == methodName));
                if (classNode != null && !classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    var newClass = classNode.WithModifiers(classNode.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
                    updatedRoot = updatedRoot.ReplaceNode(classNode, newClass);
                }

                return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = updatedRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
            }
        }
        return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root?.ToFullString() ?? "", FilePath = filePath };
    }

    public async Task<DocumentEditResult> ConvertForEachToForAsync(string filepath, int line, CancellationToken ct = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var forEach = root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);

        if (forEach == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var collection = forEach.Expression;
        var varName = forEach.Identifier.Text;

        // Determine whether to use .Length or .Count
        string lengthProp = "Count";
        var semanticModel = await document.GetSemanticModelAsync(ct);
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(collection, ct);
            if (typeInfo.Type?.TypeKind == TypeKind.Array)
            {
                lengthProp = "Length";
            }
        }
        else
        {
            // Fallback: if the type syntax has [] it's an array
            if (forEach.Type.ToString().Contains("[]"))
            {
                lengthProp = "Length";
            }
        }

        // Pick a safe index variable name
        var bodyText = forEach.Statement.ToFullString();
        string indexVar = "i";
        if (System.Text.RegularExpressions.Regex.IsMatch(bodyText, @"\bi\b"))
        {
            indexVar = "j";
        }

        if (indexVar == "j" && System.Text.RegularExpressions.Regex.IsMatch(bodyText, @"\bj\b"))
        {
            indexVar = "k";
        }

        var collectionStr = collection.ToString();

        // var varName = collection[indexVar];
        var elementAccessStmt = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(varName),
                        null,
                        SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ElementAccessExpression(
                                SyntaxFactory.ParseExpression(collectionStr),
                                SyntaxFactory.BracketedArgumentList(
                                    SyntaxFactory.SingletonSeparatedList(
                                        SyntaxFactory.Argument(
                                            SyntaxFactory.IdentifierName(indexVar))))))))));

        // Build the body block
        BlockSyntax newBody;
        if (forEach.Statement is BlockSyntax block)
        {
            newBody = block.WithStatements(block.Statements.Insert(0, elementAccessStmt));
        }
        else
        {
            newBody = SyntaxFactory.Block(elementAccessStmt, forEach.Statement);
        }

        // for (int i = 0; i < collection.LengthOrCount; i++)
        var initializer = SyntaxFactory.VariableDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(
                    SyntaxFactory.Identifier(indexVar),
                    null,
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(0))))));

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.LessThanExpression,
            SyntaxFactory.IdentifierName(indexVar),
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression(collectionStr),
                SyntaxFactory.IdentifierName(lengthProp)));

        var incrementors = SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
            SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.PostIncrementExpression,
                SyntaxFactory.IdentifierName(indexVar)));

        var forStatement = SyntaxFactory.ForStatement(
            initializer,
            SyntaxFactory.SeparatedList<ExpressionSyntax>(),
            condition,
            incrementors,
            newBody);

        var newRoot = root.ReplaceNode(forEach, forStatement);
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    public async Task<DocumentEditResult> ConvertForToForEachAsync(string filepath, int line, CancellationToken ct = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var forStmt = root.DescendantNodes().OfType<ForStatementSyntax>()
            .FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        if (forStmt == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        if (forStmt.Declaration == null || forStmt.Declaration.Variables.Count == 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var indexVar = forStmt.Declaration.Variables[0].Identifier.Text;

        // Extract collection from condition: i < arr.Length or i < arr.Count
        if (forStmt.Condition is not BinaryExpressionSyntax condBin ||
            condBin.Right is not MemberAccessExpressionSyntax memberAccess ||
            (memberAccess.Name.Identifier.Text != "Length" && memberAccess.Name.Identifier.Text != "Count"))
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var collectionStr = memberAccess.Expression.ToString();

        const string elementVar = "item";
        var rewriter = new IndexedAccessRewriter(collectionStr, indexVar, elementVar);
        var newBody = (StatementSyntax)(rewriter.Visit(forStmt.Statement) ?? forStmt.Statement);

        var forEach = SyntaxFactory.ForEachStatement(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(elementVar),
            SyntaxFactory.ParseExpression(collectionStr),
            newBody);

        var newRoot = root.ReplaceNode(forStmt, forEach);
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    public async Task<DocumentEditResult> ConvertWhileToForAsync(string filepath, int line, CancellationToken ct = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.DocumentNotFound, UpdatedText = "", FilePath = filePath };
        }

        var whileStmt = root.DescendantNodes().OfType<WhileStatementSyntax>()
            .FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        if (whileStmt == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        if (whileStmt.Condition is not BinaryExpressionSyntax condBin ||
            condBin.Left is not IdentifierNameSyntax counterIdent)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var counterName = counterIdent.Identifier.Text;

        if (whileStmt.Parent is not BlockSyntax parentBlock)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var whileIndex = parentBlock.Statements.IndexOf(whileStmt);
        if (whileIndex <= 0)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var prevStmt = parentBlock.Statements[whileIndex - 1];
        if (prevStmt is not LocalDeclarationStatementSyntax localDecl ||
            localDecl.Declaration.Variables.Count == 0 ||
            localDecl.Declaration.Variables[0].Identifier.Text != counterName)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        if (whileStmt.Statement is not BlockSyntax whileBody)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        ExpressionStatementSyntax? incrementStmt = null;
        foreach (var s in whileBody.Statements)
        {
            if (s is ExpressionStatementSyntax ess &&
                ess.Expression is PostfixUnaryExpressionSyntax post &&
                post.IsKind(SyntaxKind.PostIncrementExpression) &&
                post.Operand is IdentifierNameSyntax id &&
                id.Identifier.Text == counterName)
            {
                incrementStmt = ess;
                break;
            }
        }
        if (incrementStmt == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, UpdatedText = root.ToFullString(), FilePath = filePath };
        }

        var newBody = whileBody.WithStatements(whileBody.Statements.Remove(incrementStmt));
        var incrementors = SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
            SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                SyntaxFactory.IdentifierName(counterName)));
        var forStmt = SyntaxFactory.ForStatement(
            localDecl.Declaration,
            SyntaxFactory.SeparatedList<ExpressionSyntax>(),
            whileStmt.Condition,
            incrementors,
            newBody);

        var newStmtList = new List<StatementSyntax>();
        foreach (var s in parentBlock.Statements)
        {
            if (ReferenceEquals(s, localDecl))
            {
                continue;
            }

            newStmtList.Add(ReferenceEquals(s, whileStmt) ? (StatementSyntax)forStmt : s);
        }
        var newStatements = SyntaxFactory.List<StatementSyntax>(newStmtList);
        var newRoot = root.ReplaceNode(parentBlock, parentBlock.WithStatements(newStatements));
        return new DocumentEditResult { Outcome = EditOutcome.Modified, UpdatedText = newRoot.NormalizeWhitespace().ToFullString(), FilePath = filePath };
    }

    private record IfBranch(ExpressionSyntax Pattern, ExpressionSyntax Result);

    private static bool TryExtractIfChainBranches(IfStatementSyntax ifStmt, out string condVar, out List<IfBranch> branches, out ExpressionSyntax? defaultResult)
    {
        condVar = "";
        branches = new List<IfBranch>();
        defaultResult = null;

        IfStatementSyntax? current = ifStmt;
        while (current != null)
        {
            if (current.Condition is not BinaryExpressionSyntax bin || !bin.IsKind(SyntaxKind.EqualsExpression))
            {
                return false;
            }

            var leftStr = bin.Left.ToString();
            if (condVar == "")
            {
                condVar = leftStr;
            }
            else if (condVar != leftStr)
            {
                return false;
            }

            var result = GetSingleReturnExpression(current.Statement);
            if (result == null)
            {
                return false;
            }

            branches.Add(new IfBranch(bin.Right, result));

            if (current.Else == null)
            {
                break;
            }

            if (current.Else.Statement is IfStatementSyntax elseIf)
            {
                current = elseIf;
            }
            else
            {
                defaultResult = GetSingleReturnExpression(current.Else.Statement);
                break;
            }
        }

        return branches.Count >= 1;
    }

    private static ExpressionSyntax? GetSingleReturnExpression(StatementSyntax stmt)
    {
        if (stmt is ReturnStatementSyntax ret)
        {
            return ret.Expression;
        }

        if (stmt is BlockSyntax block && block.Statements.Count == 1 &&
            block.Statements[0] is ReturnStatementSyntax br)
        {
            return br.Expression;
        }

        return null;
    }

    private sealed class IndexedAccessRewriter : CSharpSyntaxRewriter
    {
        private readonly string _collection;
        private readonly string _indexVar;
        private readonly string _elementVar;

        public IndexedAccessRewriter(string collection, string indexVar, string elementVar)
        {
            _collection = collection;
            _indexVar = indexVar;
            _elementVar = elementVar;
        }

        public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            if (node.Expression.ToString() == _collection &&
                node.ArgumentList.Arguments.Count == 1 &&
                node.ArgumentList.Arguments[0].Expression.ToString() == _indexVar)
            {
                return SyntaxFactory.IdentifierName(_elementVar).WithTriviaFrom(node);
            }
            return base.VisitElementAccessExpression(node);
        }
    }
}

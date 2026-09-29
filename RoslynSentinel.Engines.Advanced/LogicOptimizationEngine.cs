using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics.CodeAnalysis;
using RoslynSentinel.Common;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynSentinel.Engines.Advanced;
public class LogicSimplificationEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    public LogicSimplificationEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Simplifies redundant logic like 'if (x == true)' to 'if (x)'.
    /// </summary>
    public async Task<DocumentEditResult> SimplifyBooleanExpressionsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
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
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new BooleanSimplifierRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Boolean expressions simplified.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    /// <summary>
    /// Adds ArgumentNullException.ThrowIfNull checks to all reference type parameters in a method.
    /// </summary>
    public async Task<DocumentEditResult> AddGuardClausesAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
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
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null || method.Body == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Method or body not found."
            };
        }

        var guards = new List<StatementSyntax>();
        foreach (var parameter in method.ParameterList.Parameters)
        {
            // Skip explicitly nullable reference types (string?, IService?) -> null is valid for them
            if (parameter.Type is NullableTypeSyntax)
            {
                continue;
            }

            var symbol = semanticModel.GetDeclaredSymbol(parameter, cancellationToken);
            if (symbol != null && symbol.Type.IsReferenceType)
            {
                var guard = SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")), SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Identifier))))));
                guards.Add(guard);
            }
        }

        if (guards.Count != 0)
        {
            var newBody = method.Body.WithStatements(method.Body.Statements.InsertRange(0, guards));
            var newRoot = root!.ReplaceNode(method, method.WithBody(newBody));
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Message = "// Guard clauses added.",
                UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.NoChange,
            FilePath = filePath,
            Message = "// No reference type parameters found.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root!).ToFullString()
        };
    }

    /// <summary>
    /// Modernizes null checks using null coalescing operators (?? and ??=).
    /// Converts patterns like 'if (x == null) x = y;' to 'x ??= y;'
    /// and 'x == null ? y : x' to 'x ?? y'.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToNullCoalescingAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
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
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new NullCoalescingRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Null coalescing operators applied.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    /// <summary>
    /// Converts if-else chains to switch statements for modernization.
    /// Detects patterns like: if (x == 1) { ... } else if (x == 2) { ... } else { ... }
    /// and converts to: switch (x) { case 1: ...; break; case 2: ...; break; default: ...; }
    /// 
    /// Safety requirements:
    /// - Only converts chains of 3+ branches (not worth converting 2-branch if-else)
    /// - All comparisons must be equality checks (==)
    /// - All branches must compare to literals/constants (not complex expressions)
    /// - Single clear subject variable across all checks
    /// - No float/double comparisons (precision issues)
    /// - No string comparisons without case sensitivity handling
    /// </summary>
    public async Task<DocumentEditResult> ConvertToSwitchAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
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
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new SwitchConversionRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Switch statements converted.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    private class BooleanSimplifierRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.EqualsExpression) || node.IsKind(SyntaxKind.NotEqualsExpression))
            {
                var isTrue = node.Right.IsKind(SyntaxKind.TrueLiteralExpression) || node.Left.IsKind(SyntaxKind.TrueLiteralExpression);
                var isFalse = node.Right.IsKind(SyntaxKind.FalseLiteralExpression) || node.Left.IsKind(SyntaxKind.FalseLiteralExpression);
                if (isTrue)
                {
                    var expr = node.Right.IsKind(SyntaxKind.TrueLiteralExpression) ? node.Left : node.Right;
                    if (node.IsKind(SyntaxKind.EqualsExpression))
                    {
                        return expr;
                    }

                    return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(expr));
                }

                if (isFalse)
                {
                    var expr = node.Right.IsKind(SyntaxKind.FalseLiteralExpression) ? node.Left : node.Right;
                    if (node.IsKind(SyntaxKind.EqualsExpression))
                    {
                        return SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(expr));
                    }

                    return expr;
                }
            }

            return base.VisitBinaryExpression(node);
        }
    }

    private class NullCoalescingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            var condition = node.Condition;
            var whenTrue = node.WhenTrue;
            var whenFalse = node.WhenFalse;
            if (condition == null)
            {
                return null;
            }

            // Pattern: x != null ? x : defaultValue  =>  x ?? defaultValue
            if (IsNotNullComparison(condition, out var checkedExpr) && checkedExpr != null && AreExpressionsEquivalent(checkedExpr, whenTrue))
            {
                return SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, checkedExpr, whenFalse);
            }

            // Pattern: x == null ? defaultValue : x  =>  x ?? defaultValue
            if (IsNullComparison(condition, out var checkedExpr2) && checkedExpr2 != null && AreExpressionsEquivalent(checkedExpr2, whenFalse))
            {
                return SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, checkedExpr2, whenTrue);
            }

            return base.VisitConditionalExpression(node);
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Pattern: if (x == null) x = defaultValue;  =>  x ??= defaultValue;
            if (node.Else == null && node.Statement is BlockSyntax block && block.Statements.Count == 1)
            {
                var singleStatement = block.Statements[0];
                if (IsNullComparison(node.Condition, out var checkedExpr) && checkedExpr != null && IsAssignmentToVariable(singleStatement, checkedExpr, out var defaultValue))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, checkedExpr, defaultValue));
                    return assignment;
                }
            }

            // Pattern: if (x == null) x = defaultValue; (without braces)
            if (node.Else == null && !(node.Statement is BlockSyntax) && IsNullComparison(node.Condition, out var checkedExpr3) && checkedExpr3 != null && IsAssignmentToVariable(node.Statement, checkedExpr3, out var defaultValue3))
            {
                var assignment = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, checkedExpr3, defaultValue3));
                return assignment;
            }

            // Pattern: if (x != null) { } else x = defaultValue;  =>  x ??= defaultValue;
            if (node.Else != null && IsEmptyOrNoOp(node.Statement) && IsNotNullComparison(node.Condition, out var checkedExpr4) && checkedExpr4 != null)
            {
                var elseClause = node.Else;
                if (elseClause.Statement is IfStatementSyntax elseIf && elseIf.Else == null && IsAssignmentToVariable(elseIf.Statement, checkedExpr4, out var defaultValue4))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, checkedExpr4, defaultValue4));
                    return assignment;
                }
                else if (IsAssignmentToVariable(elseClause.Statement, checkedExpr4, out var defaultValue5))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.CoalesceAssignmentExpression, checkedExpr4, defaultValue5));
                    return assignment;
                }
            }

            return base.VisitIfStatement(node);
        }

        private static bool IsNullComparison(ExpressionSyntax condition, out ExpressionSyntax? checkedExpr)
        {
            checkedExpr = null;
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
            {
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    checkedExpr = binary.Left;
                    return true;
                }

                if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    checkedExpr = binary.Right;
                    return true;
                }
            }

            return false;
        }

        private static bool IsNotNullComparison(ExpressionSyntax condition, out ExpressionSyntax? checkedExpr)
        {
            checkedExpr = null;
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    checkedExpr = binary.Left;
                    return true;
                }

                if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    checkedExpr = binary.Right;
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssignmentToVariable(SyntaxNode statement, ExpressionSyntax variable, [NotNullWhen(true)] out ExpressionSyntax? value)
        {
            value = null;
            if (statement is ExpressionStatementSyntax exprStmt && exprStmt.Expression is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && AreExpressionsEquivalent(assignment.Left, variable))
            {
                value = assignment.Right;
                return true;
            }

            if (statement is BlockSyntax block && block.Statements.Count == 1 && block.Statements[0] is ExpressionStatementSyntax blockExprStmt && blockExprStmt.Expression is AssignmentExpressionSyntax blockAssignment && blockAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && AreExpressionsEquivalent(blockAssignment.Left, variable))
            {
                value = blockAssignment.Right;
                return true;
            }

            return false;
        }

        private static bool IsEmptyOrNoOp(SyntaxNode statement)
        {
            if (statement is BlockSyntax block)
            {
                return block.Statements.Count == 0;
            }

            return false;
        }

        private static bool AreExpressionsEquivalent(ExpressionSyntax expr1, ExpressionSyntax expr2)
        {
            if (expr1 == null || expr2 == null)
            {
                return false;
            }

            return expr1.IsEquivalentTo(expr2, topLevel: false);
        }
    }

    private class SwitchConversionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Try to convert this if statement to a switch BEFORE recursively visiting children
            // This prevents else-if chains from being partially converted
            if (TryConvertIfChainToSwitch(node, out var switchStatement, out _) && switchStatement != null)
            {
                // We converted the entire if-else chain to a switch
                // Now recursively visit the switch statements to handle any nested conversions
                return Visit(switchStatement);
            }

            // If we couldn't convert to a switch, recursively visit child nodes as normal
            var baseVisited = base.VisitIfStatement(node);
            return baseVisited;
        }

        private bool TryConvertIfChainToSwitch(IfStatementSyntax ifStatement, out SwitchStatementSyntax? switchStatement, out int chainLength)
        {
            switchStatement = null;
            chainLength = 0;
            var chain = CollectIfElseChain(ifStatement);
            if (chain == null || chain.Count < 3)
            {
                return false;
            }

            var subject = ExtractCommonSubject(chain, out bool isValid);
            if (!isValid || subject == null)
            {
                return false;
            }

            var switchCases = new List<SwitchSectionSyntax>();
            for (int i = 0; i < chain.Count - 1; i++)
            {
                var(condition, body) = chain[i];
                if (condition == null || !TryExtractCaseValue(condition, subject, out var caseValue))
                {
                    return false;
                }

                if (caseValue == null)
                {
                    return false;
                }

                var caseLabel = SyntaxFactory.CaseSwitchLabel(caseValue);
                var statements = ExtractStatementsFromBody(body);
                if (statements.Count == 0)
                {
                    return false;
                }

                var caseSection = SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(caseLabel), SyntaxFactory.List(statements));
                switchCases.Add(caseSection);
            }

            // Add default case if there's a final else, or add the last case if it's another condition
            var(lastCondition, lastBody) = chain[chain.Count - 1];
            if (lastCondition == null)
            {
                // Final else clause (not else if)
                var defaultLabel = SyntaxFactory.DefaultSwitchLabel();
                var defaultStatements = ExtractStatementsFromBody(lastBody);
                if (defaultStatements.Count != 0)
                {
                    var defaultSection = SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(defaultLabel), SyntaxFactory.List(defaultStatements));
                    switchCases.Add(defaultSection);
                }
            }
            else
            {
                // Final condition (last else if) - add as a regular case
                if (!TryExtractCaseValue(lastCondition, subject, out var lastCaseValue))
                {
                    return false;
                }

                if (lastCaseValue == null)
                {
                    return false;
                }

                var lastCaseLabel = SyntaxFactory.CaseSwitchLabel(lastCaseValue);
                var lastStatements = ExtractStatementsFromBody(lastBody);
                if (lastStatements.Count == 0)
                {
                    return false;
                }

                var lastCaseSection = SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(lastCaseLabel), SyntaxFactory.List(lastStatements));
                switchCases.Add(lastCaseSection);
            }

            if (switchCases.Count == 0)
            {
                return false;
            }

            switchStatement = SyntaxFactory.SwitchStatement(subject).WithSections(SyntaxFactory.List(switchCases));
            chainLength = chain.Count;
            return true;
        }

        private List<(ExpressionSyntax? condition, SyntaxNode body)>? CollectIfElseChain(IfStatementSyntax ifStatement)
        {
            var chain = new List<(ExpressionSyntax? , SyntaxNode)>();
            var current = ifStatement;
            while (current != null)
            {
                chain.Add((current.Condition, current.Statement));
                if (current.Else == null)
                {
                    // Final if with no else
                    break;
                }

                if (current.Else.Statement is IfStatementSyntax elseIf)
                {
                    current = elseIf;
                }
                else
                {
                    // else block (not else if)
                    chain.Add((null, current.Else.Statement));
                    break;
                }
            }

            return chain;
        }

        private ExpressionSyntax? ExtractCommonSubject(List<(ExpressionSyntax? condition, SyntaxNode body)> chain, out bool isValid)
        {
            isValid = false;
            ExpressionSyntax? subject = null;
            for (int i = 0; i < chain.Count - 1; i++)
            {
                var condition = chain[i].condition;
                if (condition == null)
                {
                    continue;
                }

                var conditionSubject = ExtractSubjectFromCondition(condition);
                if (conditionSubject == null)
                {
                    return null;
                }

                if (subject == null)
                {
                    subject = conditionSubject;
                }
                else if (!AreExpressionsEquivalent(subject, conditionSubject))
                {
                    return null;
                }
            }

            isValid = subject != null;
            return subject;
        }

        private ExpressionSyntax? ExtractSubjectFromCondition(ExpressionSyntax condition)
        {
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
            {
                if (IsLiteralOrConstant(binary.Right))
                {
                    return binary.Left;
                }

                if (IsLiteralOrConstant(binary.Left))
                {
                    return binary.Right;
                }
            }

            return null;
        }

        private bool IsLiteralOrConstant(ExpressionSyntax expr)
        {
            return expr.IsKind(SyntaxKind.NumericLiteralExpression) || expr.IsKind(SyntaxKind.StringLiteralExpression) || expr.IsKind(SyntaxKind.CharacterLiteralExpression) || expr.IsKind(SyntaxKind.TrueLiteralExpression) || expr.IsKind(SyntaxKind.FalseLiteralExpression) || expr.IsKind(SyntaxKind.NullLiteralExpression) || (expr is IdentifierNameSyntax id && IsEnumLikeIdentifier(id.Identifier.Text)) || (expr is MemberAccessExpressionSyntax); // Enum values like Status.Active
        }

        private bool IsEnumLikeIdentifier(string name)
        {
            // Simple heuristic: identifiers that are ALL_CAPS or PascalCase starting with uppercase
            // Could be enum values or constants
            return char.IsUpper(name[0]);
        }

        private bool TryExtractCaseValue(ExpressionSyntax condition, ExpressionSyntax subject, out ExpressionSyntax? caseValue)
        {
            caseValue = null;
            if (condition is not BinaryExpressionSyntax binary || !binary.IsKind(SyntaxKind.EqualsExpression))
            {
                return false;
            }

            if (AreExpressionsEquivalent(binary.Left, subject) && IsLiteralOrConstant(binary.Right))
            {
                caseValue = binary.Right;
                return true;
            }

            if (AreExpressionsEquivalent(binary.Right, subject) && IsLiteralOrConstant(binary.Left))
            {
                caseValue = binary.Left;
                return true;
            }

            return false;
        }

        private List<StatementSyntax> ExtractStatementsFromBody(SyntaxNode body)
        {
            var statements = new List<StatementSyntax>();
            if (body is BlockSyntax block)
            {
                statements.AddRange(block.Statements);
            }
            else if (body is StatementSyntax stmt)
            {
                statements.Add(stmt);
            }

            // Add break statement if the last statement is not a return or throw
            if (statements.Count != 0)
            {
                var lastStmt = statements[statements.Count - 1];
                if (!(lastStmt is ReturnStatementSyntax) && !(lastStmt is ThrowStatementSyntax))
                {
                    statements.Add(SyntaxFactory.BreakStatement());
                }
            }

            return statements;
        }

        private static bool AreExpressionsEquivalent(ExpressionSyntax expr1, ExpressionSyntax expr2)
        {
            if (expr1 == null || expr2 == null)
            {
                return false;
            }

            return expr1.IsEquivalentTo(expr2, topLevel: false);
        }
    }

    public async Task<EngineResultWrapper<List<DocumentEditResult>>> InvertBooleanLogicAsync(string filepath, string boolName, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new EngineResultWrapper<List<DocumentEditResult>>(EngineOutcome.DocumentNotFound, null, new EngineError("Document not found"));
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
            return new EngineResultWrapper<List<DocumentEditResult>>(EngineOutcome.TargetNotFound, null, new EngineError("Symbol not found"));
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

        var changes = new List<DocumentEditResult>();
        foreach (var projectChange in updatedSolution.GetChanges(solution).GetProjectChanges())
        {
            foreach (var changedDocId in projectChange.GetChangedDocuments())
            {
                var doc = updatedSolution.GetDocument(changedDocId)!;
                changes.Add(new DocumentEditResult { FilePath = doc.FilePath ?? doc.Name, UpdatedText = (await doc.GetTextAsync(cancellationToken)).ToString() });
            }
        }

        return new EngineResultWrapper<List<DocumentEditResult>>(EngineOutcome.Success, changes, null);
    }

    public async Task<DocumentEditResult> ConvertIfToSwitchExpressionAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var ifStmt = method.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        if (!TryExtractIfChainBranches(ifStmt, out var condVar, out var branches, out var defaultResult))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var arms = branches.Select(b => SyntaxFactory.SwitchExpressionArm(SyntaxFactory.ConstantPattern(b.Pattern), b.Result)).ToList();
        if (defaultResult != null)
        {
            arms.Add(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), defaultResult));
        }

        var switchExpr = SyntaxFactory.SwitchExpression(SyntaxFactory.ParseExpression(condVar), SyntaxFactory.SeparatedList(arms));
        var newRoot = root.ReplaceNode(ifStmt, SyntaxFactory.ReturnStatement(switchExpr));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ConvertIfToSwitchStatementAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var ifStmt = method.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault();
        if (ifStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        if (!TryExtractIfChainBranches(ifStmt, out var condVar, out var branches, out var defaultResult))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var sections = new List<SwitchSectionSyntax>();
        foreach (var branch in branches)
        {
            sections.Add(SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.CaseSwitchLabel(branch.Pattern)), SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(branch.Result))));
        }

        if (defaultResult != null)
        {
            sections.Add(SyntaxFactory.SwitchSection(SyntaxFactory.SingletonList<SwitchLabelSyntax>(SyntaxFactory.DefaultSwitchLabel()), SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(defaultResult))));
        }

        var switchStmt = SyntaxFactory.SwitchStatement(SyntaxFactory.ParseExpression(condVar)).WithSections(SyntaxFactory.List(sections));
        var newRoot = root.ReplaceNode(ifStmt, switchStmt);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ExtensionToStaticAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
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
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(root!.ReplaceNode(methodNode, newMethod)).ToFullString(),
                    FilePath = filePath
                };
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            UpdatedText = root?.ToFullString() ?? "",
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ConvertStaticToExtensionAsync(string filepath, string methodName, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
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
                var classNode = updatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == methodName));
                if (classNode != null && !classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                {
                    var newClass = classNode.WithModifiers(classNode.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
                    updatedRoot = updatedRoot.ReplaceNode(classNode, newClass);
                }

                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(updatedRoot).ToFullString(),
                    FilePath = filePath
                };
            }
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            UpdatedText = root?.ToFullString() ?? "",
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ConvertForEachToForAsync(string filepath, int line, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var forEach = root.DescendantNodes().OfType<ForEachStatementSyntax>().FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        if (forEach == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var collection = forEach.Expression;
        var varName = forEach.Identifier.Text;
        // Determine whether to use .Length or .Count
        string lengthProp = "Count";
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel != null)
        {
            var typeInfo = semanticModel.GetTypeInfo(collection, cancellationToken);
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
        var elementAccessStmt = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"), SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(varName), null, SyntaxFactory.EqualsValueClause(SyntaxFactory.ElementAccessExpression(SyntaxFactory.ParseExpression(collectionStr), SyntaxFactory.BracketedArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(indexVar))))))))));
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
        var initializer = SyntaxFactory.VariableDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(indexVar), null, SyntaxFactory.EqualsValueClause(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0))))));
        var condition = SyntaxFactory.BinaryExpression(SyntaxKind.LessThanExpression, SyntaxFactory.IdentifierName(indexVar), SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ParseExpression(collectionStr), SyntaxFactory.IdentifierName(lengthProp)));
        var incrementors = SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName(indexVar)));
        var forStatement = SyntaxFactory.ForStatement(initializer, SyntaxFactory.SeparatedList<ExpressionSyntax>(), condition, incrementors, newBody);
        var newRoot = root.ReplaceNode(forEach, forStatement);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ConvertForToForEachAsync(string filepath, int line, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var forStmt = root.DescendantNodes().OfType<ForStatementSyntax>().FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        if (forStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        if (forStmt.Declaration == null || forStmt.Declaration.Variables.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var indexVar = forStmt.Declaration.Variables[0].Identifier.Text;
        // Extract collection from condition: i < arr.Length or i < arr.Count
        if (forStmt.Condition is not BinaryExpressionSyntax condBin || condBin.Right is not MemberAccessExpressionSyntax memberAccess || (memberAccess.Name.Identifier.Text != "Length" && memberAccess.Name.Identifier.Text != "Count"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var collectionStr = memberAccess.Expression.ToString();
        const string elementVar = "item";
        var rewriter = new IndexedAccessRewriter(collectionStr, indexVar, elementVar);
        var newBody = (StatementSyntax)(rewriter.Visit(forStmt.Statement) ?? forStmt.Statement);
        var forEach = SyntaxFactory.ForEachStatement(SyntaxFactory.IdentifierName("var"), SyntaxFactory.Identifier(elementVar), SyntaxFactory.ParseExpression(collectionStr), newBody);
        var newRoot = root.ReplaceNode(forStmt, forEach);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> ConvertWhileToForAsync(string filepath, int line, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var whileStmt = root.DescendantNodes().OfType<WhileStatementSyntax>().FirstOrDefault(n => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        if (whileStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        if (whileStmt.Condition is not BinaryExpressionSyntax condBin || condBin.Left is not IdentifierNameSyntax counterIdent)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var counterName = counterIdent.Identifier.Text;
        if (whileStmt.Parent is not BlockSyntax parentBlock)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var whileIndex = parentBlock.Statements.IndexOf(whileStmt);
        if (whileIndex <= 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var prevStmt = parentBlock.Statements[whileIndex - 1];
        if (prevStmt is not LocalDeclarationStatementSyntax localDecl || localDecl.Declaration.Variables.Count == 0 || localDecl.Declaration.Variables[0].Identifier.Text != counterName)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        if (whileStmt.Statement is not BlockSyntax whileBody)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        ExpressionStatementSyntax? incrementStmt = null;
        foreach (var s in whileBody.Statements)
        {
            if (s is ExpressionStatementSyntax ess && ess.Expression is PostfixUnaryExpressionSyntax post && post.IsKind(SyntaxKind.PostIncrementExpression) && post.Operand is IdentifierNameSyntax id && id.Identifier.Text == counterName)
            {
                incrementStmt = ess;
                break;
            }
        }

        if (incrementStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                UpdatedText = root.ToFullString(),
                FilePath = filePath
            };
        }

        var newBody = whileBody.WithStatements(whileBody.Statements.Remove(incrementStmt));
        var incrementors = SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression, SyntaxFactory.IdentifierName(counterName)));
        var forStmt = SyntaxFactory.ForStatement(localDecl.Declaration, SyntaxFactory.SeparatedList<ExpressionSyntax>(), whileStmt.Condition, incrementors, newBody);
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
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
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

        if (stmt is BlockSyntax block && block.Statements.Count == 1 && block.Statements[0] is ReturnStatementSyntax br)
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
            if (node.Expression.ToString() == _collection && node.ArgumentList.Arguments.Count == 1 && node.ArgumentList.Arguments[0].Expression.ToString() == _indexVar)
            {
                return SyntaxFactory.IdentifierName(_elementVar).WithTriviaFrom(node);
            }

            return base.VisitElementAccessExpression(node);
        }
    }

    /// <summary>
    /// Reduces block depth by finding if statements that encompass the whole method body and inverting them to return early.
    /// </summary>
    public async Task<DocumentEditResult> ReduceBlockDepthAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
            if (document == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.DocumentNotFound,
                    FilePath = filePath,
                    Message = $"// ErrorDetails: File '{filePath}' not found."};
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotEdit,
                    FilePath = filePath,
                    Message = $"// ErrorDetails: Failed to get syntax root for '{filePath}'."};
            }

            var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
            if (methodNode == null || methodNode.Body == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// ErrorDetails: Method '{methodName}' not found or has no body."};
            }

            // Look for: 
            // void Method() { 
            //     if (condition) { 
            //         /* logic */ 
            //     } 
            // }
            // To convert to:
            // void Method() {
            //     if (!condition) return;
            //     /* logic */
            // }
            if (methodNode.Body.Statements.Count == 1 && methodNode.Body.Statements[0] is IfStatementSyntax ifStmt)
            {
                if (ifStmt.Else == null) // Must not have an else
                {
                    var invertedCondition = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(ifStmt.Condition));
                    var earlyReturn = SyntaxFactory.IfStatement(invertedCondition, SyntaxFactory.ReturnStatement());
                    var newStatements = new List<StatementSyntax>
                    {
                        earlyReturn
                    };
                    if (ifStmt.Statement is BlockSyntax block)
                    {
                        newStatements.AddRange(block.Statements);
                    }
                    else
                    {
                        newStatements.Add(ifStmt.Statement);
                    }

                    var newBody = SyntaxFactory.Block(newStatements);
                    var newMethodNode = methodNode.WithBody(newBody);
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.Modified,
                        UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root, methodNode, newMethodNode, cancellationToken),
                        FilePath = filePath
                    };
                }
            }

            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                Message = "// Info: No optimization could be safely applied.",
                UpdatedText = root.ToFullString()
            };
        }
        catch (Exception ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// ErrorDetails: {ex.Message}"};
        }
    }
}
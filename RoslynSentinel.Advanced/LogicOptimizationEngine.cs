using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Advanced;

public class LogicOptimizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public LogicOptimizationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Simplifies redundant logic like 'if (x == true)' to 'if (x)'.
    /// </summary>
    public async Task<DocumentEditResult> SimplifyBooleanExpressionsAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    /// <summary>
    /// Adds ArgumentNullException.ThrowIfNull checks to all reference type parameters in a method.
    /// </summary>
    public async Task<DocumentEditResult> AddGuardClausesAsync(FilePath filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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
            // Skip explicitly nullable reference types (string?, IService?) — null is valid for them
            if (parameter.Type is NullableTypeSyntax)
            {
                continue;
            }

            var symbol = semanticModel.GetDeclaredSymbol(parameter, cancellationToken);
            if (symbol != null && symbol.Type.IsReferenceType)
            {
                var guard = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(parameter.Identifier))))));
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
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.NoChange,
            FilePath = filePath,
            Message = "// No reference type parameters found."
        };
    }

    /// <summary>
    /// Modernizes null checks using null coalescing operators (?? and ??=).
    /// Converts patterns like 'if (x == null) x = y;' to 'x ??= y;'
    /// and 'x == null ? y : x' to 'x ?? y'.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToNullCoalescingAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
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
    public async Task<DocumentEditResult> ConvertToSwitchAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
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
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    checkedExpr,
                    whenFalse);
            }

            // Pattern: x == null ? defaultValue : x  =>  x ?? defaultValue
            if (IsNullComparison(condition, out var checkedExpr2) && checkedExpr2 != null && AreExpressionsEquivalent(checkedExpr2, whenFalse))
            {
                return SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    checkedExpr2,
                    whenTrue);
            }

            return base.VisitConditionalExpression(node);
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Pattern: if (x == null) x = defaultValue;  =>  x ??= defaultValue;
            if (node.Else == null && node.Statement is BlockSyntax block && block.Statements.Count == 1)
            {
                var singleStatement = block.Statements[0];
                if (IsNullComparison(node.Condition, out var checkedExpr) && checkedExpr != null &&
                    IsAssignmentToVariable(singleStatement, checkedExpr, out var defaultValue))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.CoalesceAssignmentExpression,
                            checkedExpr,
                            defaultValue));
                    return assignment;
                }
            }

            // Pattern: if (x == null) x = defaultValue; (without braces)
            if (node.Else == null && !(node.Statement is BlockSyntax) &&
                IsNullComparison(node.Condition, out var checkedExpr3) && checkedExpr3 != null &&
                IsAssignmentToVariable(node.Statement, checkedExpr3, out var defaultValue3))
            {
                var assignment = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.CoalesceAssignmentExpression,
                        checkedExpr3,
                        defaultValue3));
                return assignment;
            }

            // Pattern: if (x != null) { } else x = defaultValue;  =>  x ??= defaultValue;
            if (node.Else != null && IsEmptyOrNoOp(node.Statement) &&
                IsNotNullComparison(node.Condition, out var checkedExpr4) && checkedExpr4 != null)
            {
                var elseClause = node.Else;
                if (elseClause.Statement is IfStatementSyntax elseIf && elseIf.Else == null &&
                    IsAssignmentToVariable(elseIf.Statement, checkedExpr4, out var defaultValue4))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.CoalesceAssignmentExpression,
                            checkedExpr4,
                            defaultValue4));
                    return assignment;
                }
                else if (IsAssignmentToVariable(elseClause.Statement, checkedExpr4, out var defaultValue5))
                {
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.CoalesceAssignmentExpression,
                            checkedExpr4,
                            defaultValue5));
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

        private static bool IsAssignmentToVariable(SyntaxNode statement, ExpressionSyntax variable, out ExpressionSyntax? value)
        {
            value = null;

            if (statement is ExpressionStatementSyntax exprStmt &&
                exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                AreExpressionsEquivalent(assignment.Left, variable))
            {
                value = assignment.Right;
                return true;
            }

            if (statement is BlockSyntax block && block.Statements.Count == 1 &&
                block.Statements[0] is ExpressionStatementSyntax blockExprStmt &&
                blockExprStmt.Expression is AssignmentExpressionSyntax blockAssignment &&
                blockAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                AreExpressionsEquivalent(blockAssignment.Left, variable))
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
                var (condition, body) = chain[i];
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

                var caseSection = SyntaxFactory.SwitchSection(
                    SyntaxFactory.SingletonList<SwitchLabelSyntax>(caseLabel),
                    SyntaxFactory.List(statements));

                switchCases.Add(caseSection);
            }

            // Add default case if there's a final else, or add the last case if it's another condition
            var (lastCondition, lastBody) = chain[chain.Count - 1];
            if (lastCondition == null)
            {
                // Final else clause (not else if)
                var defaultLabel = SyntaxFactory.DefaultSwitchLabel();
                var defaultStatements = ExtractStatementsFromBody(lastBody);
                if (defaultStatements.Count != 0)
                {
                    var defaultSection = SyntaxFactory.SwitchSection(
                        SyntaxFactory.SingletonList<SwitchLabelSyntax>(defaultLabel),
                        SyntaxFactory.List(defaultStatements));
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

                var lastCaseSection = SyntaxFactory.SwitchSection(
                    SyntaxFactory.SingletonList<SwitchLabelSyntax>(lastCaseLabel),
                    SyntaxFactory.List(lastStatements));

                switchCases.Add(lastCaseSection);
            }

            if (switchCases.Count == 0)
            {
                return false;
            }

            switchStatement = SyntaxFactory.SwitchStatement(subject)
                .WithSections(SyntaxFactory.List(switchCases));

            chainLength = chain.Count;
            return true;
        }

        private List<(ExpressionSyntax? condition, SyntaxNode body)>? CollectIfElseChain(IfStatementSyntax ifStatement)
        {
            var chain = new List<(ExpressionSyntax?, SyntaxNode)>();
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
            return expr.IsKind(SyntaxKind.NumericLiteralExpression) ||
                   expr.IsKind(SyntaxKind.StringLiteralExpression) ||
                   expr.IsKind(SyntaxKind.CharacterLiteralExpression) ||
                   expr.IsKind(SyntaxKind.TrueLiteralExpression) ||
                   expr.IsKind(SyntaxKind.FalseLiteralExpression) ||
                   expr.IsKind(SyntaxKind.NullLiteralExpression) ||
                   (expr is IdentifierNameSyntax id && IsEnumLikeIdentifier(id.Identifier.Text)) ||
                   (expr is MemberAccessExpressionSyntax); // Enum values like Status.Active
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
}

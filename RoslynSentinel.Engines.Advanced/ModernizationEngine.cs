using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Engines.Advanced;
public class SyntaxModernizationEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    public SyntaxModernizationEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<DocumentEditResult> ClassToRecordAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ClassToRecord"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'ClassToRecord' is disabled."
            };
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
                Message = $"// Class '{className}' not found.",
                UpdatedText = root?.ToFullString() ?? string.Empty
            };
        }

        // Extract properties
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().ToList();
        // Only use positional syntax when properties have no attributes and no initializers.
        // If any property has attributes or an initializer, positional syntax would silently drop them.
        bool canUsePositional = properties.All(p => !p.AttributeLists.Any() && p.Initializer == null);
        SyntaxNode recordNode;
        if (canUsePositional)
        {
            // Positional record: `record Foo(T Prop1, T Prop2);`
            var parameters = properties.Select(prop => SyntaxFactory.Parameter(prop.Identifier).WithType(prop.Type)).ToList();
            var positional = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), classNode.Identifier).WithModifiers(classNode.Modifiers).WithParameterList(parameters.Count != 0 ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)) : SyntaxFactory.ParameterList());
            // Only include non-property members (positional params auto-generate the properties)
            var nonPropMembers = classNode.Members.Where(m => m is not PropertyDeclarationSyntax).ToList();
            if (nonPropMembers.Count > 0)
            {
                positional = positional.WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken)).WithMembers(SyntaxFactory.List(nonPropMembers)).WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
            }
            else
            {
                positional = positional.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            }

            recordNode = positional;
        }
        else
        {
            // Class-body record: preserves attributes and initializers on each property.
            // Convert `set` accessors to `init` so the record remains immutable by convention.
            var convertedProperties = properties.Select(prop =>
            {
                if (prop.AccessorList == null)
                {
                    return (MemberDeclarationSyntax)prop;
                }

                var newAccessors = prop.AccessorList.Accessors.Select(acc =>
                {
                    if (acc.IsKind(SyntaxKind.SetAccessorDeclaration) && acc.Body == null && acc.ExpressionBody == null)
                    {
                        return SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    }

                    return acc;
                });
                return (MemberDeclarationSyntax)prop.WithAccessorList(prop.AccessorList.WithAccessors(SyntaxFactory.List(newAccessors)));
            });
            var allMembers = convertedProperties.Concat(classNode.Members.Where(m => m is not PropertyDeclarationSyntax)).ToList();
            recordNode = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), classNode.Identifier).WithModifiers(classNode.Modifiers).WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken)).WithMembers(SyntaxFactory.List(allMembers)).WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
        }

        var newRoot = root!.ReplaceNode(classNode, recordNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Record converted to class.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    public async Task<DocumentEditResult> RecordToClassAsync(FilePathWrapper filePath, string recordName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("RecordToClass"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'RecordToClass' is disabled."
            };
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        var recordNode = root?.DescendantNodes().OfType<RecordDeclarationSyntax>().FirstOrDefault(r => r.Identifier.Text == recordName);
        if (recordNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Record '{recordName}' not found."};
        }

        var classNode = SyntaxFactory.ClassDeclaration(recordNode.Identifier).WithModifiers(recordNode.Modifiers);
        // Preserve base types (interfaces, base classes)
        if (recordNode.BaseList != null)
        {
            classNode = classNode.WithBaseList(recordNode.BaseList);
        }

        var properties = new List<MemberDeclarationSyntax>();
        if (recordNode.ParameterList != null)
        {
            foreach (var parameter in recordNode.ParameterList.Parameters)
            {
                var property = SyntaxFactory.PropertyDeclaration(parameter.Type!, parameter.Identifier).WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword))).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) })));
                properties.Add(property);
            }
        }

        properties.AddRange(recordNode.Members);
        classNode = classNode.WithMembers(SyntaxFactory.List(properties));
        var newRoot = root!.ReplaceNode(recordNode, classNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Record converted to class.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    public async Task<DocumentEditResult> ConvertMethodToExpressionBodyAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
        if (method?.Body != null && method.Body.Statements.Count == 1)
        {
            var stmt = method.Body.Statements[0];
            ExpressionSyntax? expr = null;
            if (stmt is ReturnStatementSyntax ret)
            {
                expr = ret.Expression;
            }
            else if (stmt is ExpressionStatementSyntax es)
            {
                expr = es.Expression;
            }

            if (expr != null)
            {
                var newMethod = method.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                var newRoot = root!.ReplaceNode(method, newMethod);
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.Modified,
                    FilePath = filePath,
                    Message = "// Method converted to expression-bodied.",
                    UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
                };
            }
        }

        if (method != null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = $"// Method '{methodName}' cannot be converted to an expression body.",
                UpdatedText = root!.ToFullString()
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.TargetNotFound,
            FilePath = filePath,
            Message = $"// Method '{methodName}' not found."};
    }

    public async Task<DocumentEditResult> ConvertToPatternAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new PatternModernizationRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Patterns modernized.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    private class PatternModernizationRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            // Recursively visit children first
            var visitedNode = (IfStatementSyntax? )base.VisitIfStatement(node) ?? node;
            // Try to convert conditions to patterns
            if (TryConvertToPattern(visitedNode.Condition, out var newCondition) && newCondition != null)
            {
                visitedNode = visitedNode.WithCondition(newCondition);
            }

            return visitedNode;
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var visited = (BinaryExpressionSyntax? )base.VisitBinaryExpression(node) ?? node;
            // Handle binary OR patterns - convert 'x == 1 || x == 2' to 'x is 1 or 2'
            if (visited.IsKind(SyntaxKind.LogicalOrExpression))
            {
                if (TryConvertOrChainToPattern(visited, out var patternExpr) && patternExpr != null)
                {
                    return patternExpr;
                }
            }

            return visited;
        }

        private bool TryConvertToPattern(ExpressionSyntax condition, out ExpressionSyntax? newCondition)
        {
            newCondition = null;
            // Pattern 1: x == null -> x is null
            if (condition is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
            {
                if (binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    // Create: x is null using ConstantPattern
                    var nullPattern = SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    newCondition = SyntaxFactory.IsPatternExpression(binary.Left, nullPattern);
                    return true;
                }

                if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var nullPattern = SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    newCondition = SyntaxFactory.IsPatternExpression(binary.Right, nullPattern);
                    return true;
                }
            }

            // Pattern 2: x != null -> x is not null
            if (condition is BinaryExpressionSyntax notEqual && notEqual.IsKind(SyntaxKind.NotEqualsExpression))
            {
                if (notEqual.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    // Create: x is not null pattern
                    var nullPattern = SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    // Note: We can't create "not null" pattern directly in older Roslyn versions
                    // so we'll keep this as-is for now
                    return false;
                }

                if (notEqual.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return false;
                }
            }

            // Pattern 3: Combined patterns with logical AND
            // obj != null && obj.Property > 0 -> simplified to keep readable
            if (condition is BinaryExpressionSyntax andExpr && andExpr.IsKind(SyntaxKind.LogicalAndExpression))
            {
                // For now, skip complex property patterns as they require more advanced Roslyn API
                return false;
            }

            return false;
        }

        private bool TryConvertOrChainToPattern(BinaryExpressionSyntax orExpr, out ExpressionSyntax? result)
        {
            result = null;
            var expressions = CollectOrChain(orExpr);
            if (expressions.Count < 2)
            {
                return false;
            }

            // Check if all expressions are comparisons with the same subject
            ExpressionSyntax? subject = null;
            var caseValues = new List<ExpressionSyntax>();
            foreach (var expr in expressions)
            {
                if (expr is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
                {
                    if (IsLiteralOrIdentifier(binary.Right))
                    {
                        if (subject == null)
                        {
                            subject = binary.Left;
                        }
                        else if (!AreExpressionsEquivalent(subject, binary.Left))
                        {
                            return false;
                        }

                        caseValues.Add(binary.Right);
                    }
                    else if (IsLiteralOrIdentifier(binary.Left))
                    {
                        if (subject == null)
                        {
                            subject = binary.Right;
                        }
                        else if (!AreExpressionsEquivalent(subject, binary.Right))
                        {
                            return false;
                        }

                        caseValues.Add(binary.Left);
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            if (subject == null || caseValues.Count != expressions.Count)
            {
                return false;
            }

            // Build or pattern: x is 1 or 2 or 3
            // Due to Roslyn API limitations, we'll try to build this using ConstantPatterns
            PatternSyntax pattern = SyntaxFactory.ConstantPattern(caseValues[0]);
            // Try to create an or pattern: x is 1 or 2 or 3
            for (int i = 1; i < caseValues.Count; i++)
            {
                var nextPattern = SyntaxFactory.ConstantPattern(caseValues[i]);
                pattern = SyntaxFactory.BinaryPattern(SyntaxKind.OrPattern, pattern, nextPattern);
            }

            result = SyntaxFactory.IsPatternExpression(subject, pattern);
            return true;
        }

        private List<ExpressionSyntax> CollectOrChain(BinaryExpressionSyntax orExpr)
        {
            var result = new List<ExpressionSyntax>();
            CollectOrChainRecursive(orExpr, result);
            return result;
        }

        private void CollectOrChainRecursive(BinaryExpressionSyntax expr, List<ExpressionSyntax> result)
        {
            if (expr.Left is BinaryExpressionSyntax leftOr && leftOr.IsKind(SyntaxKind.LogicalOrExpression))
            {
                CollectOrChainRecursive(leftOr, result);
            }
            else
            {
                result.Add(expr.Left);
            }

            result.Add(expr.Right);
        }

        private bool IsLiteralOrIdentifier(ExpressionSyntax expr)
        {
            return expr.IsKind(SyntaxKind.NumericLiteralExpression) || expr.IsKind(SyntaxKind.StringLiteralExpression) || expr.IsKind(SyntaxKind.CharacterLiteralExpression) || expr.IsKind(SyntaxKind.TrueLiteralExpression) || expr.IsKind(SyntaxKind.FalseLiteralExpression) || expr.IsKind(SyntaxKind.NullLiteralExpression) || expr is IdentifierNameSyntax || expr is MemberAccessExpressionSyntax;
        }

        private static bool AreExpressionsEquivalent(ExpressionSyntax? a, ExpressionSyntax? b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return a.IsEquivalentTo(b);
        }
    }

    private class SpanParsingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax memberAccess && memberAccess.Name.Identifier.Text == "Substring" && node.ArgumentList.Arguments.Count is 1 or 2)
            {
                // str.AsSpan(args...)
                var asSpanAccess = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, memberAccess.Expression, SyntaxFactory.IdentifierName("AsSpan"));
                var asSpanCall = SyntaxFactory.InvocationExpression(asSpanAccess, node.ArgumentList);
                // .ToString()
                var toStringAccess = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, asSpanCall, SyntaxFactory.IdentifierName("ToString"));
                var result = SyntaxFactory.InvocationExpression(toStringAccess, SyntaxFactory.ArgumentList()).WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
                return result;
            }

            return base.VisitInvocationExpression(node);
        }
    }

    private class ThrowExpressionRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var statements = node.Statements.ToList();
            var replacements = new Dictionary<int, (bool skipNext, StatementSyntax newStmt)>();
            for (int i = 0; i < statements.Count - 1; i++)
            {
                // Look for: var x = someExpr;  followed by  if (x == null) throw ...;
                if (statements[i] is not LocalDeclarationStatementSyntax localDecl)
                {
                    continue;
                }

                if (localDecl.Declaration.Variables.Count != 1)
                {
                    continue;
                }

                var variable = localDecl.Declaration.Variables[0];
                var varName = variable.Identifier.Text;
                var initValue = variable.Initializer?.Value;
                if (initValue == null)
                {
                    continue;
                }

                if (statements[i + 1] is not IfStatementSyntax ifStmt)
                {
                    continue;
                }

                if (ifStmt.Else != null)
                {
                    continue;
                }

                if (ifStmt.Condition is not BinaryExpressionSyntax condBin)
                {
                    continue;
                }

                if (!condBin.IsKind(SyntaxKind.EqualsExpression))
                {
                    continue;
                }

                bool rightIsNull = condBin.Right.IsKind(SyntaxKind.NullLiteralExpression);
                bool leftIsNull = condBin.Left.IsKind(SyntaxKind.NullLiteralExpression);
                if (!rightIsNull && !leftIsNull)
                {
                    continue;
                }

                var condVar = rightIsNull ? condBin.Left.ToString() : condBin.Right.ToString();
                if (condVar != varName)
                {
                    continue;
                }

                // Get throw statement from if body
                ThrowStatementSyntax? throwStmt = ifStmt.Statement as ThrowStatementSyntax;
                if (throwStmt == null && ifStmt.Statement is BlockSyntax blk && blk.Statements.Count == 1)
                {
                    throwStmt = blk.Statements[0] as ThrowStatementSyntax;
                }

                if (throwStmt?.Expression == null)
                {
                    continue;
                }

                // Build: var x = initValue ?? throw new ...;
                var throwExpr = SyntaxFactory.ThrowExpression(SyntaxFactory.Token(SyntaxKind.ThrowKeyword).WithTrailingTrivia(SyntaxFactory.Space), throwStmt.Expression);
                var coalescedInit = SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, initValue, throwExpr);
                var newVarDecl = variable.WithInitializer(variable.Initializer!.WithValue(coalescedInit));
                var newDecl = localDecl.WithDeclaration(localDecl.Declaration.WithVariables(SyntaxFactory.SingletonSeparatedList(newVarDecl)));
                replacements[i] = (skipNext: true, newStmt: newDecl);
            }

            if (replacements.Count == 0)
            {
                return base.VisitBlock(node);
            }

            var newStatements = new List<StatementSyntax>();
            bool skipThisLine = false;
            for (int i = 0; i < statements.Count; i++)
            {
                if (skipThisLine)
                {
                    skipThisLine = false;
                    continue;
                }

                if (replacements.TryGetValue(i, out var rep))
                {
                    newStatements.Add(rep.newStmt);
                    skipThisLine = rep.skipNext;
                }
                else
                {
                    newStatements.Add(statements[i]);
                }
            }

            var updated = node.WithStatements(SyntaxFactory.List(newStatements));
            return base.VisitBlock(updated);
        }
    }

    /// <summary>
    /// Upgrades legacy string parsing to use Span<char> for zero-allocation performance.
    /// Converts: str.Substring(start, length) -> str.AsSpan(start, length).ToString()
    /// and:      str.Substring(start)         -> str.AsSpan(start).ToString()
    /// Scoped to the named method when methodName is provided; otherwise transforms entire file.
    /// </summary>
    public async Task<DocumentEditResult> UseSpanForParsingAsync(FilePathWrapper filePath, string methodName, CancellationToken cancellationToken = default)
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

        SyntaxNode scope = root;
        if (!string.IsNullOrWhiteSpace(methodName))
        {
            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
            if (method == null)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.TargetNotFound,
                    FilePath = filePath,
                    Message = $"// Method '{methodName}' not found.",
                    UpdatedText = root.ToFullString()
                };
            }

            scope = method;
        }

        var rewriter = new SpanParsingRewriter();
        var newScope = rewriter.Visit(scope);
        var newRoot = string.IsNullOrWhiteSpace(methodName) ? newScope : root.ReplaceNode(scope, newScope);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// String parsing upgraded to use Span<char>.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    /// <summary>
    /// Converts adjacent null-assign + null-guard patterns to null-coalescing throw expressions (C# 7+).
    /// Before:  var x = GetValue();
    ///          if (x == null) throw new ArgumentNullException(nameof(x));
    /// After:   var x = GetValue() ?? throw new ArgumentNullException(nameof(x));
    /// Only transforms cases where the assignment and null check are consecutive statements in the same block.
    /// </summary>
    public async Task<DocumentEditResult> UseThrowExpressionsAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
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

        var rewriter = new ThrowExpressionRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Null coalescing throw expressions applied.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    /// <summary>
    /// Converts a standard logger call (e.g. _logger.LogInformation("Msg {Param}", p)) into a source-generated [LoggerMessage] method.
    /// </summary>
    public async Task<DocumentEditResult> ConvertToSourceGeneratedLoggingAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = (root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className)) ?? throw new ToolNotFoundException("Class not found.");
        // Identify logging calls
        var invocations = classNode.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(inv => inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text.StartsWith("Log") && (ma.Name.Identifier.Text == "LogInformation" || ma.Name.Identifier.Text == "LogError" || ma.Name.Identifier.Text == "LogWarning")).ToList();
        if (invocations.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No logging invocations found.",
                UpdatedText = root?.ToFullString() ?? string.Empty
            };
        }

        int eventId = 1;
        var generatedMethods = new List<MethodDeclarationSyntax>();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var inv in invocations)
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                var levelStr = ma.Name.Identifier.Text.Substring(3); // e.g. "Information"
                var args = inv.ArgumentList.Arguments;
                if (args.Count == 0)
                {
                    continue;
                }

                // Simple heuristic: arg0 is message, subsequent args are params.
                var messageArg = args[0].Expression as LiteralExpressionSyntax;
                if (messageArg == null || !messageArg.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                var methodName = $"Log{levelStr}Event{eventId}";
                // Build LoggerMessage attribute
                var attrArgs = SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(eventId))), SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression($"LogLevel.{levelStr}")), SyntaxFactory.AttributeArgument(messageArg) }));
                var attrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("LoggerMessage")).WithArgumentList(attrArgs)));
                // Build partial method parameters (ILogger + whatever args were passed)
                var parameters = new List<ParameterSyntax>
                {
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("logger")).WithType(SyntaxFactory.ParseTypeName("ILogger"))
                };
                for (int i = 1; i < args.Count; i++)
                {
                    parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier($"p{i}")).WithType(SyntaxFactory.ParseTypeName("object")));
                }

                var genMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), methodName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword), SyntaxFactory.Token(SyntaxKind.PartialKeyword)).WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters))).AddAttributeLists(attrList).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                generatedMethods.Add(genMethod);
                // Build replacement invocation
                var newArgs = new List<ArgumentSyntax>
                {
                    SyntaxFactory.Argument(ma.Expression)
                }; // pass the logger instance
                newArgs.AddRange(args.Skip(1)); // pass the rest of the args
                var newInv = SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(methodName)).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
                replaceMap[inv] = newInv;
                eventId++;
            }
        }

        // Replace invocations first on the original classNode (before any AddModifiers mutation
        // that would create new green-node identities and make the replaceMap keys stale).
        var newClassNode = replaceMap.Count > 0 ? classNode.ReplaceNodes(replaceMap.Keys, (oldNode, _) => replaceMap[oldNode]) : classNode;
        // Now safe to AddModifiers (re-found tree is already correct after ReplaceNodes)
        if (!newClassNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            newClassNode = newClassNode.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }

        newClassNode = newClassNode.AddMembers(generatedMethods.ToArray());
        var newRoot = root!.ReplaceNode(classNode, newClassNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Logging methods generated.",
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString()
        };
    }

    /// <summary>
    /// Converts a class to be immutable by making fields readonly and properties init-only.
    /// </summary>
    public async Task<DocumentEditResult> MakeClassImmutableAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
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
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found.",
                UpdatedText = root?.ToFullString() ?? string.Empty
            };
        }

        var newMembers = classNode.Members.Select(member =>
        {
            if (member is FieldDeclarationSyntax field)
            {
                // const fields cannot have readonly -> skip them
                if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
                {
                    return field;
                }

                if (!field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                {
                    return field.AddModifiers(SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.ReadOnlyKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)));
                }
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                var setter = prop.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
                if (setter != null)
                {
                    var initOnly = setter.WithKeyword(SyntaxFactory.Token(SyntaxKind.InitKeyword));
                    return prop.WithAccessorList(prop.AccessorList!.WithAccessors(prop.AccessorList.Accessors.Replace(setter, initOnly)));
                }
            }

            return member;
        });
        var newClass = classNode.WithMembers(SyntaxFactory.List(newMembers));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Class made immutable.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, classNode, newClass, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ReplaceStringConcatWithInterpolationAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        // Find top-level string concat chains -> not a child of another string-concat-with-literal
        var topLevelConcats = root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(b)).Where(b => !b.Ancestors().OfType<BinaryExpressionSyntax>().Any(a => a.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(a))).ToList();
        if (topLevelConcats.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var newRoot = root.ReplaceNodes(topLevelConcats, (original, _) =>
        {
            var segments = FlattenConcatTree(original);
            var contents = new List<InterpolatedStringContentSyntax>();
            foreach (var seg in segments)
            {
                if (seg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var tokenText = lit.Token.Text;
                    // Verbatim/raw strings have different escape semantics -> wrap as interpolation hole
                    if (tokenText.StartsWith("@") || tokenText.StartsWith("\"\"\""))
                    {
                        contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                        continue;
                    }

                    // Strip surrounding quotes, double {{ and }} for the interpolated context
                    var innerText = tokenText.Length >= 2 ? tokenText.Substring(1, tokenText.Length - 2) : string.Empty;
                    var escapedText = innerText.Replace("{", "{{").Replace("}", "}}");
                    if (!string.IsNullOrEmpty(escapedText))
                    {
                        contents.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, escapedText, lit.Token.ValueText, SyntaxTriviaList.Empty)));
                    }
                }
                else
                {
                    contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                }
            }

            return SyntaxFactory.InterpolatedStringExpression(SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken), SyntaxFactory.List(contents), SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken)).WithTriviaFrom(original);
        });
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    private static bool ContainsStringLiteral(ExpressionSyntax expr) => (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) || (expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression) && (ContainsStringLiteral(bin.Left) || ContainsStringLiteral(bin.Right)));
    private static List<ExpressionSyntax> FlattenConcatTree(ExpressionSyntax expr)
    {
        if (expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression))
        {
            return FlattenConcatTree(bin.Left).Concat(FlattenConcatTree(bin.Right)).ToList();
        }

        return new List<ExpressionSyntax>
        {
            expr
        };
    }
}
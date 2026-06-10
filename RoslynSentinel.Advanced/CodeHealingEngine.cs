using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;

public class CodeHealingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public CodeHealingEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<DocumentEditResult> FixThreadSleepAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("EPC33"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature EPC33 is disabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = filePath,
                Message = "// Source invalid."
            };
        }

        var rewriter = new ThreadSleepRewriter();
        var newRoot = rewriter.Visit(root).NormalizeWhitespace();
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.ToFullString()
        };
    }

    private class ThreadSleepRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var call = node.Expression.ToString();
            if (call is "Thread.Sleep" or "System.Threading.Thread.Sleep")
            {
                var asyncMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)));
                if (asyncMethod != null)
                {
                    var delay = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("Delay")),
                        node.ArgumentList);
                    return SyntaxFactory.AwaitExpression(delay).WithTriviaFrom(node);
                }
            }
            return base.VisitInvocationExpression(node);
        }
    }

    public async Task<DocumentEditResult> AddRetryPolicyAsync(string f, int sl, int el, int rc)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == f || d.FilePath == f);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = f,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync();
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.SourceInvalid,
                FilePath = f,
                Message = "// Source invalid."
            };
        }

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        MethodDeclarationSyntax? method = null;
        if (sl > 0 && el > 0)
        {
            method = methods.FirstOrDefault(m =>
            {
                var span = m.GetLocation().GetLineSpan();
                return span.StartLinePosition.Line + 1 >= sl && span.EndLinePosition.Line + 1 <= el;
            });
        }
        method ??= methods.FirstOrDefault();
        if (method?.Body == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = f,
                Message = "// Method body not found."
            };
        }

        var retryDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier("_retryCount"), null,
                        SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)))))));

        var tryBody = SyntaxFactory.Block(
            method.Body.Statements.Add(SyntaxFactory.BreakStatement()));

        var filterExpr = SyntaxFactory.BinaryExpression(
            SyntaxKind.LessThanExpression,
            SyntaxFactory.IdentifierName("_retryCount"),
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(rc)));

        var catchBody = SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.PostfixUnaryExpression(SyntaxKind.PostIncrementExpression,
                    SyntaxFactory.IdentifierName("_retryCount"))));

        var catchClause = SyntaxFactory.CatchClause(
            SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName("Exception")),
            SyntaxFactory.CatchFilterClause(filterExpr),
            catchBody);

        var whileStmt = SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(SyntaxFactory.TryStatement(tryBody, SyntaxFactory.SingletonList(catchClause), null)));

        var newMethod = method.WithBody(SyntaxFactory.Block(retryDecl, whileStmt));
        var newRoot = root.ReplaceNode(method, newMethod);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = f,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<Dictionary<FilePath, string>> ModernizeExceptionsAsync(List<ExceptionTarget> targets, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var changes = new Dictionary<FilePath, string>();

        foreach (var target in targets)
        {
            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == target.FilePath || d.FilePath == target.FilePath);
            if (document == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(ct);
            var text = await document.GetTextAsync(ct);
            if (target.Line < 1 || target.Line > text.Lines.Count)
            {
                continue;
            }

            var lineSpan = text.Lines[target.Line - 1].Span;
            var node = root?.FindNode(lineSpan).DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().FirstOrDefault();

            if (node?.Expression is ObjectCreationExpressionSyntax oce)
            {
                var ns = root?.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var newExceptionName = target.NewExceptionName;
                var newOce = oce.WithType(SyntaxFactory.ParseTypeName(newExceptionName));
                var newRoot = root!.ReplaceNode(oce, newOce);
                changes[target.FilePath] = newRoot.NormalizeWhitespace().ToFullString();

                // Generate the new exception class
                var nsDeclaration = string.IsNullOrEmpty(ns) ? "" : $"namespace {ns};\n";
                var newExceptionSource = $@"using System;

{nsDeclaration}
public class {newExceptionName} : Exception
{{
    public {newExceptionName}() {{ }}
    public {newExceptionName}(string message) : base(message) {{ }}
    public {newExceptionName}(string message, Exception inner) : base(message, inner) {{ }}
}}";
                var newFilePath = Path.Combine(Path.GetDirectoryName(target.FilePath) ?? "", $"{newExceptionName}.cs");
                changes[newFilePath] = newExceptionSource;
            }
        }

        return changes;
    }

    public record ExceptionTarget(FilePath FilePath, int Line, string NewExceptionName);
}

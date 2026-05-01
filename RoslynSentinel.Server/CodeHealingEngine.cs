using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class CodeHealingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public CodeHealingEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> FixThreadSleepAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("EPC33")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;
        var rewriter = new ThreadSleepRewriter();
        return rewriter.Visit(root).NormalizeWhitespace().ToFullString();
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

    public async Task<string> AddRetryPolicyAsync(string f, int sl, int el, int rc) => "";
    
    public async Task<Dictionary<string, string>> ModernizeExceptionsAsync(List<ExceptionTarget> targets, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var changes = new Dictionary<string, string>();
        
        foreach (var target in targets)
        {
            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == target.FilePath || d.FilePath == target.FilePath);
            if (document == null) continue;

            var root = await document.GetSyntaxRootAsync(ct);
            var text = await document.GetTextAsync(ct);
            if (target.Line < 1 || target.Line > text.Lines.Count) continue;
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

    public record ExceptionTarget(string FilePath, int Line, string NewExceptionName);
}

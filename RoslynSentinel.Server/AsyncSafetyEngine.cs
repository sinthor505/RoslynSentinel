using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record AsyncSafetyReport(string FilePath, string MethodName, string Reason);

public class AsyncSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public AsyncSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<AsyncSafetyReport>> DetectAsyncVoidMethodsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath) 
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();

        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)) && m.ReturnType.ToString() == "void");

            foreach (var method in methods)
            {
                reports.Add(new AsyncSafetyReport(document.FilePath ?? document.Name, method.Identifier.Text, "Async void methods cannot be awaited and crash the process on exceptions."));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskYieldUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var yieldCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression.ToString() == "Task"
                    && ma.Name.Identifier.Text == "Yield");

            foreach (var call in yieldCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Yield()' forces an async context switch — verify it is intentional and not a workaround."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var delayCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression.ToString() == "Task"
                    && ma.Name.Identifier.Text == "Delay");

            foreach (var call in delayCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Delay(...)' found. Prefer CancellationToken overloads; avoid polling-with-delay patterns."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayZeroUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var delayZeroCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv =>
                {
                    if (inv.Expression is not MemberAccessExpressionSyntax ma
                        || ma.Expression.ToString() != "Task"
                        || ma.Name.Identifier.Text != "Delay")
                        return false;
                    var args = inv.ArgumentList.Arguments;
                    if (args.Count == 0) return false;
                    var first = args[0].Expression;
                    if (first is LiteralExpressionSyntax lit && lit.Token.Text == "0") return true;
                    if (first is MemberAccessExpressionSyntax mts
                        && mts.Expression.ToString() == "TimeSpan"
                        && mts.Name.Identifier.Text == "Zero") return true;
                    return false;
                });

            foreach (var call in delayZeroCalls)
            {
                var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var lineSpan = call.GetLocation().GetLineSpan();
                reports.Add(new AsyncSafetyReport(
                    document.FilePath ?? document.Name,
                    method?.Identifier.Text ?? "<unknown>",
                    $"Line {lineSpan.StartLinePosition.Line + 1}: 'Task.Delay(0)' found. Use 'await Task.Yield()' to yield the thread to the thread pool if that is the intent."
                ));
            }
        }
        return reports;
    }

    public async Task<List<AsyncSafetyReport>> FindTaskWhenAllUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var documents = string.IsNullOrEmpty(filePath)
            ? solution.Projects.SelectMany(p => p.Documents)
            : solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);

        var reports = new List<AsyncSafetyReport>();
        foreach (var document in documents)
        {
            if (document == null) continue;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Detect methods with 2+ sequential awaits that could be parallelized with Task.WhenAll
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                foreach (var block in method.DescendantNodes().OfType<BlockSyntax>())
                {
                    int awaitCount = block.Statements.Count(s =>
                        s is ExpressionStatementSyntax e && e.Expression is AwaitExpressionSyntax
                        || s is LocalDeclarationStatementSyntax ld
                            && ld.Declaration.Variables.Any(v => v.Initializer?.Value is AwaitExpressionSyntax));

                    if (awaitCount >= 2)
                    {
                        var lineSpan = method.GetLocation().GetLineSpan();
                        reports.Add(new AsyncSafetyReport(
                            document.FilePath ?? document.Name,
                            method.Identifier.Text,
                            $"Line {lineSpan.StartLinePosition.Line + 1}: Method has {awaitCount} sequential awaits. If tasks are independent, consider Task.WhenAll() for parallelism."
                        ));
                        break;
                    }
                }
            }
        }
        return reports;
    }
}

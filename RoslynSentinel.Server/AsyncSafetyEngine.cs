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
        return new List<AsyncSafetyReport>();
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return new List<AsyncSafetyReport>();
    }

    public async Task<List<AsyncSafetyReport>> FindTaskDelayZeroUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return new List<AsyncSafetyReport>();
    }

    public async Task<List<AsyncSafetyReport>> FindTaskWhenAllUsageAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return new List<AsyncSafetyReport>();
    }
}

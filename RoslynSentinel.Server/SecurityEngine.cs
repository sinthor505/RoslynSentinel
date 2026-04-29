using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SecurityIssueReport(string FilePath, int Line, int Column, string IssueType, string Description);

public class SecurityEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SecurityEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<SecurityIssueReport>> AnalyzeSecurityAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        var reports = new List<SecurityIssueReport>();
        // logic for hardcoded passwords, secrets, etc.
        return reports;
    }

    public async Task<List<SecurityIssueReport>> FindHardcodedPathsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        var reports = new List<SecurityIssueReport>();
        var strings = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        foreach (var str in strings)
        {
            var text = str.Token.ValueText;
            if (text.Contains(@":\") || text.Contains(@"/") || text.StartsWith(@"\\"))
            {
                 var loc = str.GetLocation().GetLineSpan().StartLinePosition;
                 reports.Add(new SecurityIssueReport(filePath, loc.Line + 1, loc.Character + 1, "HardcodedPath", "Avoid hardcoding file system paths. Use configuration or environment variables."));
            }
        }
        return reports;
    }

    public async Task<List<SecurityIssueReport>> CheckForSqlInjectionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<SecurityIssueReport>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SecurityIssueReport>();

        var reports = new List<SecurityIssueReport>();
        // logic to find interpolated strings used in ExecuteSql or similar...
        return reports;
    }
}

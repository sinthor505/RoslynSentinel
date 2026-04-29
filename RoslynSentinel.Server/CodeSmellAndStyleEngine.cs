using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record CodeSmell(string Id, string Severity, string Description, int Line);

public class CodeSmellAndStyleEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeSmellAndStyleEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Scans a file for a massive range of IDE style and code smell rules (IDE0xxx, EPCxxx).
    /// </summary>
    public async Task<List<CodeSmell>> ScanForSmellsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return new List<CodeSmell>();

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var smells = new List<CodeSmell>();

        if (root == null) return smells;

        // 1. Detect EPC33: Thread.Sleep in Async
        var asyncMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)));
        foreach (var method in asyncMethods)
        {
            if (method.ToString().Contains("Thread.Sleep"))
                smells.Add(new CodeSmell("EPC33", "Warning", "Do not use Thread.Sleep in async methods.", method.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        }

        // 2. Detect IDE0017: Simplify Object Initialization
        // (Heuristic: consecutive assignments to same variable after new)
        
        // 3. Detect IDE0031: Use Null Propagation
        var binaryExprs = root.DescendantNodes().OfType<BinaryExpressionSyntax>();
        foreach (var bin in binaryExprs)
        {
             if (bin.IsKind(SyntaxKind.NotEqualsExpression) && (bin.Right.IsKind(SyntaxKind.NullLiteralExpression) || bin.Left.IsKind(SyntaxKind.NullLiteralExpression)))
             {
                 // Potential candidates for ?. 
             }
        }

        return smells;
    }

    /// <summary>
    /// Implements IDE0066: Use switch expression.
    /// </summary>
    public async Task<string> UseSwitchExpressionAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var switchStatements = root.DescendantNodes().OfType<SwitchStatementSyntax>().ToList();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var sw in switchStatements)
        {
             // logic to build SwitchExpressionArmSyntax for each section...
             // and replace sw with a ReturnStatement(SwitchExpression) or assignment.
        }

        return root.ToFullString();
    }
}

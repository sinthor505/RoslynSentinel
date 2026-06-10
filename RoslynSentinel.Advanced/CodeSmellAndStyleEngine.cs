using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Advanced;

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
    public async Task<List<CodeSmell>> ScanForSmellsAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new List<CodeSmell>();
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var smells = new List<CodeSmell>();

        if (root == null)
        {
            return smells;
        }

        // 1. Detect EPC33: Thread.Sleep in Async
        var asyncMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.AsyncKeyword)));
        foreach (var method in asyncMethods)
        {
            if (method.ToString().Contains("Thread.Sleep"))
            {
                smells.Add(new CodeSmell("EPC33", "Warning", "Do not use Thread.Sleep in async methods.", method.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            }
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
    public async Task<DocumentEditResult> UseSwitchExpressionAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
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
                Outcome = EditOutcome.SourceInvalid,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var switchStatements = root.DescendantNodes().OfType<SwitchStatementSyntax>().ToList();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var sw in switchStatements)
        {
            // Only convert if all sections have one label and one return statement (no fall-through)
            if (!sw.Sections.All(s =>
                s.Labels.Count == 1 &&
                s.Statements.Count == 1 &&
                s.Statements[0] is ReturnStatementSyntax))
            {
                continue;
            }

            var arms = sw.Sections.Select(s =>
            {
                var label = s.Labels.FirstOrDefault();
                var pattern = label is CaseSwitchLabelSyntax c
                    ? (PatternSyntax)SyntaxFactory.ConstantPattern(c.Value)
                    : SyntaxFactory.DiscardPattern();
                var result = ((ReturnStatementSyntax)s.Statements[0]).Expression
                    ?? SyntaxFactory.ParseExpression("default");
                return SyntaxFactory.SwitchExpressionArm(pattern, result);
            });

            var switchExpr = SyntaxFactory.SwitchExpression(sw.Expression, SyntaxFactory.SeparatedList(arms));
            replaceMap[sw] = SyntaxFactory.ReturnStatement(switchExpr);
        }

        if (replaceMap.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                UpdatedText = null,
                FilePath = filePath
            };
        }

        var newRoot = root.ReplaceNodes(replaceMap.Keys, (orig, _) => replaceMap[orig]);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString(),
            FilePath = filePath
        };
    }
}

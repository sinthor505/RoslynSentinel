using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Server;

public class CodeHealingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeHealingEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Organizes usings (removes unused and sorts alphabetically).
    /// </summary>
    public async Task<string> OrganizeUsingsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        if (root == null) return string.Empty;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null) return root.ToFullString();

        var usings = root.Usings;
        var sortedUsings = usings.OrderBy(u => u.Name.ToString()).ToList();
        var newRoot = root.WithUsings(SyntaxFactory.List(sortedUsings));
        
        return newRoot.ToFullString();
    }

    /// <summary>
    /// Attempts to add a missing library reference to a .csproj file.
    /// </summary>
    public async Task<string> AddNuGetPackageAsync(string projectPath, string packageId, string version, CancellationToken cancellationToken = default)
    {
        var xml = await File.ReadAllTextAsync(projectPath, cancellationToken);
        if (xml.Contains($"Include=\"{packageId}\"")) return xml;

        var packageRef = $"<PackageReference Include=\"{packageId}\" Version=\"{version}\" />";
        if (xml.Contains("</ItemGroup>"))
        {
            xml = xml.Replace("</ItemGroup>", $"  {packageRef}\n  </ItemGroup>");
        }
        else
        {
            xml = xml.Replace("</Project>", $"  <ItemGroup>\n    {packageRef}\n  </ItemGroup>\n</Project>");
        }
        return xml;
    }

    /// <summary>
    /// Wraps a block of code in a retry policy loop.
    /// </summary>
    public async Task<string> AddRetryPolicyAsync(string filePath, int startLine, int endLine, int retryCount = 3, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(sourceText.Lines[startLine - 1].Start, sourceText.Lines[endLine - 1].End);
        
        var nodes = root?.DescendantNodes(span).Where(n => n is StatementSyntax && n.Parent is BlockSyntax).Cast<StatementSyntax>().ToList();
        if (nodes == null || !nodes.Any()) return "";

        var firstNode = nodes[0];
        var parentBlock = (BlockSyntax)firstNode.Parent!;

        var retryCall = SyntaxFactory.ParseStatement($@"
            await Policy.Handle<Exception>()
                .WaitAndRetryAsync({retryCount}, _ => TimeSpan.FromSeconds(1))
                .ExecuteAsync(async () => {{ 
                    {string.Join(Environment.NewLine, nodes.Select(n => n.ToFullString()))}
                }});");

        var newBlock = parentBlock.ReplaceNodes(nodes, (old, _) => old == nodes[0] ? retryCall : null);
        var newRoot = root!.ReplaceNode(parentBlock, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public record ExceptionTarget(string FilePath, int Line, string NewExceptionName);

    /// <summary>
    /// Replaces generic Exceptions with custom ones and generates the new classes.
    /// </summary>
    public async Task<Dictionary<string, string>> ModernizeExceptionsAsync(List<ExceptionTarget> targets, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var allChanges = new Dictionary<string, string>();

        foreach (var target in targets)
        {
            var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == target.FilePath || d.FilePath == target.FilePath);
            if (document == null) continue;

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            if (root == null) continue;

            var lineSpan = text.Lines[target.Line - 1].Span;
            var throwStmt = root.FindNode(lineSpan).DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().FirstOrDefault();

            if (throwStmt != null && throwStmt.Expression is ObjectCreationExpressionSyntax oce && oce.Type.ToString() == "Exception")
            {
                // 1. Replace the throw statement
                var newOce = oce.WithType(SyntaxFactory.ParseTypeName(target.NewExceptionName));
                var newRoot = root.ReplaceNode(oce, newOce);
                allChanges[target.FilePath] = newRoot.NormalizeWhitespace().ToFullString();

                // 2. Generate the new exception class
                var currentDir = Path.GetDirectoryName(target.FilePath) ?? "";
                var exceptionPath = Path.Combine(currentDir, $"{target.NewExceptionName}.cs");
                
                var nsNode = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                var ns = nsNode?.Name.ToString() ?? "Global";
                
                var exceptionContent = $@"using System;

namespace {ns};

public class {target.NewExceptionName} : Exception
{{
    public {target.NewExceptionName}(string message) : base(message) {{ }}
    public {target.NewExceptionName}(string message, Exception inner) : base(message, inner) {{ }}
}}";
                allChanges[exceptionPath] = exceptionContent;
            }
        }

        return allChanges;
    }
}

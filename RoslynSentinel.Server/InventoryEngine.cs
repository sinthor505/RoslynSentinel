using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record CodeInventoryReport(
    string FilePath,
    List<string> Namespaces,
    List<string> Classes,
    List<string> Interfaces,
    List<string> Methods,
    List<string> Properties
);

public class InventoryEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public InventoryEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<CodeInventoryReport> GetCodeInventoryAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Syntax root not found.");

        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text).ToList();
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Select(i => i.Identifier.Text).ToList();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToList();

        return new CodeInventoryReport(filePath, namespaces, classes, interfaces, methods, properties);
    }
}

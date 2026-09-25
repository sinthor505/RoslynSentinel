using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Basic;

public class InventoryEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<InventoryEngine> _logger;

    public InventoryEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = NullLogger<InventoryEngine>.Instance;
    }

    public InventoryEngine(IWorkspaceManager workspaceManager, ILogger<InventoryEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public async Task<CodeInventoryReport> GetCodeInventoryAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var normalizedPath = Path.GetFullPath(filePath);

        // Fallback: tolerate path/link differences by matching on file name + full-path equality (case-insensitive).
        var document = solution.GetDocumentIdsWithFilePath(normalizedPath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault() ?? solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                                     string.Equals(Path.GetFullPath(d.FilePath), normalizedPath,
                                                   StringComparison.OrdinalIgnoreCase));
        if (document == null)
        {
            var existsOnDisk = File.Exists(normalizedPath);
            var projectCount = solution.Projects.Count();
            throw new FileNotFoundException(
                $"File not found in solution: {normalizedPath} " +
                $"(existsOnDisk={existsOnDisk}, projectsLoaded={projectCount}). " +
                "The owning project may have failed to load - check workspace load errors.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken)
                   ?? throw new InvalidOperationException("Syntax root not found.");

        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text).ToList();
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Select(i => i.Identifier.Text).ToList();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToList();

        return new CodeInventoryReport(filePath, namespaces, classes, interfaces, methods, properties);
    }

    public async Task<CodeInventoryReport> GetCodeInventoryAsync2(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var normalizedPath = Path.GetFullPath(filePath);

        // Fallback: tolerate path/link differences by matching on file name + full-path equality (case-insensitive).
        var document = solution.GetDocumentIdsWithFilePath(normalizedPath)
                               .Select(solution.GetDocument)
                               .FirstOrDefault() ?? solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                                     string.Equals(Path.GetFullPath(d.FilePath), normalizedPath,
                                                   StringComparison.OrdinalIgnoreCase));
        if (document == null)
        {
            var existsOnDisk = File.Exists(normalizedPath);
            var projectCount = solution.Projects.Count();
            throw new FileNotFoundException(
                $"File not found in solution: {normalizedPath} " +
                $"(existsOnDisk={existsOnDisk}, projectsLoaded={projectCount}). " +
                "The owning project may have failed to load - check workspace load errors.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken)
                   ?? throw new InvalidOperationException("Syntax root not found.");

        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text).ToList();
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Select(i => i.Identifier.Text).ToList();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToList();

        return new CodeInventoryReport(filePath, namespaces, classes, interfaces, methods, properties);
    }
}

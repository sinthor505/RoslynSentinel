using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

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

        var lookup = DocumentLookup.TryGetDocument(solution, filePath);
        if (lookup.Status == DocumentLookupStatus.NotFound)
        {
            throw FileMiss(lookup, normalizedPath, solution);
        }

        // An ambiguous bare file name throws ToolAmbiguousMatchException here, as FindDocumentOrNull did.
        var document = lookup.GetDocumentOrThrow();

        var root = await document.GetSyntaxRootAsync(cancellationToken)
                   ?? throw new InvalidOperationException("Syntax root not found.");

        var namespaces = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToList();
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text).ToList();
        var interfaces = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>().Select(i => i.Identifier.Text).ToList();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToList();
        var properties = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToList();

        return new CodeInventoryReport(filePath, namespaces, classes, interfaces, methods, properties);
    }

    // The not-found error for a file that is not a document of the solution: the shared file-miss text (ranked candidates, or the
    // no-similar-name fact with an orientation option) followed by the disk and load facts this tool has always stated.
    private static ToolNotFoundException FileMiss(DocumentLookupResult lookup, string normalizedPath, Solution solution)
    {
        var existsOnDisk = File.Exists(normalizedPath);
        var projectCount = solution.Projects.Count();
        return new ToolNotFoundException(
            $"{lookup.Describe()} Disk and load facts: existsOnDisk={existsOnDisk}, projectsLoaded={projectCount}. " +
            "A project that failed to load leaves its files out of the solution.",
            "document", normalizedPath);
    }

    public async Task<CodeInventoryReport> GetCodeInventoryAsync2(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var normalizedPath = Path.GetFullPath(filePath);

        var lookup = DocumentLookup.TryGetDocument(solution, filePath);
        if (lookup.Status == DocumentLookupStatus.NotFound)
        {
            throw FileMiss(lookup, normalizedPath, solution);
        }

        // An ambiguous bare file name throws ToolAmbiguousMatchException here, as FindDocumentOrNull did.
        var document = lookup.GetDocumentOrThrow();

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

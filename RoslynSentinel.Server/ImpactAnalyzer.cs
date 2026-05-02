using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server;

public record ImpactReport(
    string SymbolName,
    string SymbolKind,
    List<ReferenceInfo> References,
    int TotalCallSites,
    int AffectedProjectsCount
);

public record ReferenceInfo(
    string FilePath,
    int Line,
    int Column,
    string Preview
);

public class ImpactAnalyzer
{
    private readonly ILogger<ImpactAnalyzer> _logger;
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ImpactAnalyzer(ILogger<ImpactAnalyzer> logger, PersistentWorkspaceManager workspaceManager)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
    }

    public async Task<ImpactReport> AnalyzeImpactAsync(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
            .Select(solution.GetDocument)
            .FirstOrDefault();

        if (document == null)
        {
            throw new Exception($"Document not found: {filePath}");
        }

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        if (syntaxRoot == null || semanticModel == null)
        {
            throw new Exception("Could not retrieve syntax root or semantic model.");
        }

        // Find the symbol at the given position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        var token = syntaxRoot.FindToken(position);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, cancellationToken);

        if (symbol == null)
        {
            // Fallback: try finding the node's symbol directly if FindSymbolAtPosition fails
            var node = token.Parent;
            while (node != null && symbol == null)
            {
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken) ?? semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                node = node.Parent;
            }
        }

        if (symbol == null)
        {
            throw new Exception("No symbol found at the specified position.");
        }

        _logger.LogInformation("Analyzing impact for symbol: {SymbolName} ({Kind})", symbol.Name, symbol.Kind);

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        var referenceInfos = new List<ReferenceInfo>();
        var affectedProjects = new HashSet<ProjectId>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var loc = location.Location;
                var lineSpan = loc.GetLineSpan();
                
                var refDocument = solution.GetDocument(location.Document.Id);
                var text = await refDocument!.GetTextAsync(cancellationToken);
                var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                referenceInfos.Add(new ReferenceInfo(
                    lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    lineText
                ));

                affectedProjects.Add(location.Document.Project.Id);
            }
        }

        return new ImpactReport(
            symbol.ToDisplayString(),
            symbol.Kind.ToString(),
            referenceInfos,
            referenceInfos.Count,
            affectedProjects.Count
        );
    }

    public async Task<ImpactReport> FindDerivedTypesAsync(string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        return await FindSymbolRelationsAsync(filePath, line, column, async (symbol, sol, ct) => 
        {
            if (symbol is INamedTypeSymbol namedType)
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(namedType, sol, cancellationToken: ct);
                return derived.Cast<ISymbol>();
            }
            return Enumerable.Empty<ISymbol>();
        }, cancellationToken);
    }

    public async Task<ImpactReport> FindImplementationsAsync(string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        return await FindSymbolRelationsAsync(filePath, line, column, async (symbol, sol, ct) => 
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(symbol, sol, cancellationToken: ct);
            return implementations;
        }, cancellationToken);
    }

    public async Task<List<string>> GetDataFlowAsync(string filePath, int startLine, int startColumn, int endLine, int endColumn, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception($"Document not found: {filePath}");

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (syntaxRoot == null || semanticModel == null) throw new Exception("Could not retrieve syntax root or semantic model.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = sourceText.Lines[startLine - 1].Start + (startColumn - 1);
        var endPosition = sourceText.Lines[endLine - 1].Start + (endColumn - 1);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var firstToken = syntaxRoot.FindToken(startPosition);
        var lastToken = syntaxRoot.FindToken(endPosition);

        var firstStatement = firstToken.Parent?.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>().FirstOrDefault();
        var lastStatement = lastToken.Parent?.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>().FirstOrDefault();

        if (firstStatement == null || lastStatement == null)
            throw new Exception("Could not resolve statements for data flow analysis.");

        var dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        
        var report = new List<string>();
        if (dataFlow.Succeeded)
        {
            report.Add($"Variables Declared: {string.Join(", ", dataFlow.VariablesDeclared.Select(v => v.Name))}");
            report.Add($"Read Inside: {string.Join(", ", dataFlow.ReadInside.Select(v => v.Name))}");
            report.Add($"Written Inside: {string.Join(", ", dataFlow.WrittenInside.Select(v => v.Name))}");
            report.Add($"Data Flows In: {string.Join(", ", dataFlow.DataFlowsIn.Select(v => v.Name))}");
            report.Add($"Data Flows Out: {string.Join(", ", dataFlow.DataFlowsOut.Select(v => v.Name))}");
            report.Add($"Always Assigned: {string.Join(", ", dataFlow.AlwaysAssigned.Select(v => v.Name))}");
        }
        else
        {
            report.Add("Data flow analysis failed or could not be determined for the selected range.");
        }

        return report;
    }

    private async Task<ImpactReport> FindSymbolRelationsAsync(string filePath, int line, int column, Func<ISymbol, Solution, CancellationToken, Task<IEnumerable<ISymbol>>> relationFinder, CancellationToken cancellationToken)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath)
            .Select(solution.GetDocument)
            .FirstOrDefault();

        if (document == null) throw new Exception($"Document not found: {filePath}");

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (syntaxRoot == null || semanticModel == null) throw new Exception("Could not retrieve syntax root or semantic model.");

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start + (column - 1);
        var token = syntaxRoot.FindToken(position);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, solution.Workspace, cancellationToken);

        if (symbol == null)
        {
            var node = token.Parent;
            while (node != null && symbol == null)
            {
                symbol = semanticModel.GetDeclaredSymbol(node, cancellationToken) ?? semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                node = node.Parent;
            }
        }

        if (symbol == null) throw new Exception("No symbol found at the specified position.");

        var relatedSymbols = await relationFinder(symbol, solution, cancellationToken);
        var referenceInfos = new List<ReferenceInfo>();
        var affectedProjects = new HashSet<ProjectId>();

        foreach (var relSymbol in relatedSymbols)
        {
            foreach (var location in relSymbol.Locations)
            {
                if (!location.IsInSource || location.SourceTree == null) continue;

                var loc = location;
                var lineSpan = loc.GetLineSpan();
                
                var refDocument = solution.GetDocument(location.SourceTree);
                if (refDocument == null) continue;

                var text = await refDocument.GetTextAsync(cancellationToken);
                var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                referenceInfos.Add(new ReferenceInfo(
                    lineSpan.Path,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    lineText
                ));

                affectedProjects.Add(refDocument.Project.Id);
            }
        }

        return new ImpactReport(
            symbol.ToDisplayString(),
            symbol.Kind.ToString(),
            referenceInfos,
            referenceInfos.Count,
            affectedProjects.Count
        );
    }
}

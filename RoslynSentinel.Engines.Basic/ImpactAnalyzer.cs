using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

public class ImpactAnalyzer
{
    private readonly ILogger<ImpactAnalyzer> _logger;
    private readonly IWorkspaceManager _workspaceManager;

    public ImpactAnalyzer(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<ImpactAnalyzer>();
    }

    public ImpactAnalyzer(IWorkspaceManager workspaceManager, ILogger<ImpactAnalyzer> logger)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
    }

    public async Task<ImpactReport> AnalyzeImpactAsync(FilePathWrapper filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var documentLookup = DocumentLookup.TryGetDocument(solution, filePath);
            if (!documentLookup.TryGetDocument(out var document))
            {
                // TODO this returned lookup miss carries no ErrorCode (it reads as Exception); pass documentLookup.Code so it is NotFound or Ambiguous
                return new ImpactReport("", "", [], 0, 0, Error: documentLookup.Describe());
            }

            var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            if (syntaxRoot == null || semanticModel == null)
            {
                return new ImpactReport("", "", [], 0, 0, Error: "Could not retrieve syntax root or semantic model.");
            }

            // Find the symbol at the given position
            var sourceText = await document.GetTextAsync(cancellationToken);
            // Resolve at the snippet start first (mirrors InspectSymbol(info)); the last-identifier path below is the fallback
            var symbol = await SnippetSymbolLocator.FindSymbolAtSnippetAsync(document, contextSnippet, lineBefore, lineAfter, cancellationToken);
            var snippetMatch = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter);
            // Advance to the last identifier token in the snippet span so FindSymbolAtPositionAsync works
            var position = ContextHelper.AdvanceToLastIdentifier(syntaxRoot, snippetMatch.Start, snippetMatch.Length);
            var token = syntaxRoot.FindToken(position);
            symbol ??= await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken);

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
                return new ImpactReport("", "", [], 0, 0, Error: $"No symbol was found for the snippet matched at line {sourceText.Lines.GetLinePosition(snippetMatch.Start).Line + 1} of '{filePath}': the snippet matched there, but its innermost node ({token.Parent?.GetType().Name}) has no declared or referenced symbol. To see what the file declares, call {ToolCallHint.Call("GetFileOutline", ("filePath", filePath.ToString()))}. A snippet on a declaration, or one narrowed with lineBefore/lineAfter, resolves to a symbol.", ErrorCode: ToolErrorCode.NotFound);
            }

            // Metadata (BCL/referenced) symbols have no source location; reporting their references would be a silent mis-resolution
            if (!symbol.Locations.Any(l => l.IsInSource))
            {
                // TODO offer ranked candidates (SymbolNotFoundDetail.FindCandidatesAsync over the solution's source symbols) next to this fact
                return new ImpactReport("", "", [], 0, 0, Error: $"'{symbol.ToDisplayString()}' is not declared in this solution: the snippet resolved to a referenced (metadata) symbol such as a library type or constructor, which has no source references to report. A snippet on the declared name, or one narrowed with lineBefore/lineAfter, resolves to a source symbol.", ErrorCode: ToolErrorCode.NotFound);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Analyzing impact for symbol: {SymbolName} ({Kind})", symbol.Name, symbol.Kind);
            }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ImpactReport("", "", [], 0, 0, Error: ex.Message);
        }
    }

    public async Task<ImpactReport> FindDerivedTypesAsync(FilePathWrapper filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        return await FindSymbolRelationsAsync(filePath, line, column, async (symbol, sol, cancellationToken) =>
        {
            if (symbol is INamedTypeSymbol namedType)
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(namedType, sol, cancellationToken: cancellationToken);
                return derived.Cast<ISymbol>();
            }
            return Enumerable.Empty<ISymbol>();
        }, cancellationToken);
    }

    public async Task<ImpactReport> FindImplementationsAsync(FilePathWrapper filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        return await FindSymbolRelationsAsync(filePath, line, column, async (symbol, sol, cancellationToken) =>
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(symbol, sol, cancellationToken: cancellationToken);
            return implementations;
        }, cancellationToken);
    }

    public async Task<List<string>> GetDataFlowAsync(FilePathWrapper filePath, int startLine, int startColumn, int endLine, int endColumn, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (syntaxRoot == null || semanticModel == null)
        {
            throw new InvalidOperationException("Could not retrieve syntax root or semantic model.");
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var startPosition = sourceText.Lines[startLine - 1].Start + (startColumn - 1);
        var endPosition = sourceText.Lines[endLine - 1].Start + (endColumn - 1);
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var firstToken = syntaxRoot.FindToken(startPosition);
        var lastToken = syntaxRoot.FindToken(endPosition);

        var firstStatement = firstToken.Parent?.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>().FirstOrDefault();
        var lastStatement = lastToken.Parent?.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>().FirstOrDefault();

        if (firstStatement == null || lastStatement == null)
        {
            throw new InvalidOperationException("Could not resolve statements for data flow analysis.");
        }

        var dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);

        var report = new List<string>();
        if (dataFlow.Succeeded)
        {
            report.Add($"Variables Declared: {string.Join(", ", dataFlow.VariablesDeclared.Select(v => v.Name))}");
            report.Add($"Read Inside: {string.Join(", ", dataFlow.ReadInside.Select(v => v.Name))}");
            report.Add($"Written Inside: {string.Join(", ", dataFlow.WrittenInside.Select(v => v.Name))}");
            report.Add($"SuccessData Flows In: {string.Join(", ", dataFlow.DataFlowsIn.Select(v => v.Name))}");
            report.Add($"SuccessData Flows Out: {string.Join(", ", dataFlow.DataFlowsOut.Select(v => v.Name))}");
            report.Add($"Always Assigned: {string.Join(", ", dataFlow.AlwaysAssigned.Select(v => v.Name))}");
        }
        else
        {
            report.Add("SuccessData flow analysis failed or could not be determined for the selected range.");
        }

        return report;
    }

    private async Task<ImpactReport> FindSymbolRelationsAsync(FilePathWrapper filePath, int line, int column, Func<ISymbol, Solution, CancellationToken, Task<IEnumerable<ISymbol>>> relationFinder, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = DocumentLookup.RequireDocument(solution, filePath);
        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (syntaxRoot == null || semanticModel == null)
        {
            throw new InvalidOperationException("Could not retrieve syntax root or semantic model.");
        }

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

        if (symbol == null)
        {
            throw MemberRefactoringNotFound.Unresolved("symbol", $"{line}:{column}",
                $"No symbol was found at line {line}, column {column} of '{filePath}': the node there ({token.Parent?.GetType().Name}) has no declared or referenced symbol.", filePath.ToString());
        }

        var relatedSymbols = await relationFinder(symbol, solution, cancellationToken);
        var referenceInfos = new List<ReferenceInfo>();
        var affectedProjects = new HashSet<ProjectId>();

        foreach (var relSymbol in relatedSymbols)
        {
            foreach (var location in relSymbol.Locations)
            {
                if (!location.IsInSource || location.SourceTree == null)
                {
                    continue;
                }

                var loc = location;
                var lineSpan = loc.GetLineSpan();

                var refDocument = solution.GetDocument(location.SourceTree);
                if (refDocument == null)
                {
                    continue;
                }

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

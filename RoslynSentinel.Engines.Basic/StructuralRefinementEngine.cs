using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Engines.Basic;

public class StructuralRefinementEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public StructuralRefinementEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _config = new SentinelConfiguration();
    }

    public StructuralRefinementEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    /// <summary>
    /// Synchronizes the filename to match a type declared in the file. When <paramref name="targetTypeName"/>
    /// is omitted, defaults to the first non-nested type declared in the file -> a real default for the
    /// common single-type-per-file case, not an accidental fallback, but not necessarily the file's
    /// conceptual "primary" type when several top-level types share a file. Pass targetTypeName to name
    /// the type explicitly instead of relying on declaration order.
    /// Uses staging mechanism (returns change ID) instead of direct file writes.
    /// </summary>
    public async Task<DocumentEditResult> SyncTypeAndFilenameAsync(FilePathWrapper filePath, string? targetTypeName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        var topLevelTypes = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is not BaseTypeDeclarationSyntax) // Not nested
            .ToList() ?? [];

        BaseTypeDeclarationSyntax? primaryType;
        if (targetTypeName != null)
        {
            primaryType = RoslynSentinel.Common.NameTargetLookup.RequireSingle(topLevelTypes.Where(t => t.Identifier.Text == targetTypeName).ToList(), "Type", targetTypeName);
            if (primaryType == null)
            {
                var available = topLevelTypes.Count == 0 ? "none" : string.Join(", ", topLevelTypes.Select(t => t.Identifier.Text));
                throw new ToolNotFoundException($"No top-level type named '{targetTypeName}' found in '{filePath}'. Top-level types present: {available}. Next: {ToolCallHint.Call("Search", ("mode", "class"), ("query", targetTypeName))}", "type", targetTypeName);
            }
        }
        else
        {
            primaryType = topLevelTypes.FirstOrDefault();
        }

        if (primaryType == null)
        {
            throw new ToolNotFoundException($"No type declaration found in '{filePath}'. Next: {ToolCallHint.Call("ReadFile")} the file to confirm it declares a type.", "type");
        }

        var expectedName = primaryType.Identifier.Text + ".cs";
        var currentName = Path.GetFileName(filePath);
        var directory = Path.GetDirectoryName(filePath);

        if (expectedName != currentName && directory != null)
        {
            var newPath = Path.Combine(directory, expectedName);
            var sourceText = await document.GetTextAsync(cancellationToken);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                Changes = new Dictionary<FilePathWrapper, string> { [newPath] = sourceText.ToString() },
                UpdatedText = sourceText.ToString(),
                Message = $"Renaming '{currentName}' to '{expectedName}' to match primary type '{primaryType.Identifier.Text}'."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.AlreadyInTargetState,
            FilePath = filePath,
            Message = "// Filename matches primary type.",
            UpdatedText = root?.ToFullString() ?? ""
        };
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (legacy: line/column-based).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePathWrapper filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("DeleteUnusedSymbol"))
        {
            throw new ToolFeatureDisabledException("DeleteUnusedSymbol");
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' is not in the loaded solution. Next: {ToolCallHint.Call("ListAll")} to confirm the owning project.", "file");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start + (column - 1);
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));

        var symbol = semanticModel?.GetDeclaredSymbol(node!, cancellationToken) ?? semanticModel?.GetSymbolInfo(node!, cancellationToken).Symbol;
        if (symbol == null)
        {
            throw new ToolNotFoundException($"No symbol found at line {line}, column {column} of '{filePath}'. Next: {ToolCallHint.Call("ReadFile")} the line to confirm the position.", "symbol");
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        if (references.Any(r => r.Locations.Any()))
        {
            var firstLocations = references.SelectMany(r => r.Locations.Select(l => l.Location)).Take(3)
                .Select(l => $"{Path.GetFileName(l.SourceTree?.FilePath ?? string.Empty)}:{l.GetLineSpan().StartLinePosition.Line + 1}");
            throw new ToolTargetIneligibleException($"Symbol '{symbol.Name}' has {references.Sum(r => r.Locations.Count())} usages and cannot be safely deleted. First locations: {string.Join(", ", firstLocations)}. Next: {ToolCallHint.Call("Search", ("mode", "references"))} to review them.", ToolCallHint.Call("Search", ("mode", "references")));
        }

        var reflectionRisk = await CheckReflectionRiskAsync(solution, filePath, symbol, cancellationToken);
        if (reflectionRisk != null)
        {
            return reflectionRisk;
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync(document, root!, node!, SyntaxRemoveOptions.KeepUnbalancedDirectives, cancellationToken)
        };
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (contextSnippet-based
    /// resolution -> an agent-friendly alternative to the line/column overload above, which requires
    /// a column that a caller who only knows a line number has no cheap way to obtain).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePathWrapper filePath, string symbolName, string? contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' is not in the loaded solution. Next: {ToolCallHint.Call("ListAll")} to confirm the owning project.", "file");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || sourceText == null || semanticModel == null)
        {
            throw new ToolInvalidArgumentException($"Could not parse '{filePath}': syntax root, source text or semantic model is unavailable.");
        }

        var candidates = root.DescendantNodes()
            .Where(n => GetDeclaredNodeName(n) == symbolName)
            .ToList();

        SyntaxNode? target;
        if (contextSnippet == null || candidates.Count <= 1)
        {
            // symbolName alone already resolves unambiguously -> see the identical guard and
            // rationale in SyntaxTargetResolver.ResolveBySnippetOrThrow.
            target = candidates.FirstOrDefault();
        }
        else
        {
            SnippetMatch snippetMatch = SnippetLookup.ResolveOrThrow(sourceText, contextSnippet, lineBefore, lineAfter);

            target = candidates.FirstOrDefault(c => c.Span.Contains(snippetMatch.Start))
                ?? candidates.FirstOrDefault(c => new TextSpan(snippetMatch.Start, snippetMatch.Length).OverlapsWith(c.Span));
        }

        if (target == null)
        {
            throw new ToolNotFoundException(candidates.Count == 0
                ? $"No symbol named '{symbolName}' found in '{filePath}'. Next: {ToolCallHint.Call("Search", ("mode", "symbol"), ("query", symbolName))}"
                : $"'{symbolName}' has {candidates.Count} declaration(s) in '{filePath}', and contextSnippet did not match any of them. Provide a contextSnippet that is a verbatim substring of the intended declaration.", "symbol", symbolName);
        }

        var symbol = semanticModel.GetDeclaredSymbol(target, cancellationToken);
        if (symbol == null)
        {
            throw new ToolNotFoundException($"No symbol could be resolved for '{symbolName}' at the matched location of '{filePath}'. Next: {ToolCallHint.Call("ReadFile")} the snippet location to confirm it is a declaration.", "symbol", symbolName);
        }

        return await SafeDeleteSymbolAsync(filePath, symbol, cancellationToken);
    }

    private static string? GetDeclaredNodeName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        FieldDeclarationSyntax f => f.Declaration.Variables.Count == 1 ? f.Declaration.Variables[0].Identifier.Text : null,
        VariableDeclaratorSyntax v => v.Identifier.Text,
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        _ => null
    };

    /// <summary>
    /// Scans every document in the solution for a string literal matching <paramref name="symbol"/>'s
    /// name -> a likely sign of reflection/<c>nameof</c>-adjacent dynamic usage that <see cref="SymbolFinder"/>
    /// would not catch (ported from the dead <c>BasicRefactoringEngine.SafeDeleteSymbolAsync</c> copy -> see
    /// docs/TODO.md's "Duplicate/dead SafeDeleteSymbolAsync" entry). Returns a blocking
    /// <see cref="DocumentEditResult"/> if a match is found anywhere, otherwise null.
    /// </summary>
    private static async Task<DocumentEditResult?> CheckReflectionRiskAsync(Solution solution, FilePathWrapper filePath, ISymbol symbol, CancellationToken cancellationToken = default)
    {
        foreach (var proj in solution.Projects)
        {
            foreach (var doc in proj.Documents)
            {
                var docRoot = await doc.GetSyntaxRootAsync(cancellationToken);
                var hasMatchingLiteral = docRoot?.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .Any(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.ValueText == symbol.Name) ?? false;
                if (hasMatchingLiteral)
                {
                    throw new ToolTargetIneligibleException($"Potential reflection risk: symbol '{symbol.Name}' is referenced by a string literal in {doc.Name}, possible reflection/dynamic usage. Delete manually after verifying.", ToolCallHint.Call("Search", ("mode", "text")));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (handle-based resolution).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePathWrapper filePath, ISymbol symbol, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("DeleteUnusedSymbol"))
        {
            throw new ToolFeatureDisabledException("DeleteUnusedSymbol");
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new ToolNotFoundException($"File '{filePath}' is not in the loaded solution. Next: {ToolCallHint.Call("ListAll")} to confirm the owning project.", "file");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            throw new ToolInvalidArgumentException($"Could not get the syntax root of '{filePath}'; the file did not parse.");
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        if (references.Any(r => r.Locations.Any()))
        {
            var firstLocations = references.SelectMany(r => r.Locations.Select(l => l.Location)).Take(3)
                .Select(l => $"{Path.GetFileName(l.SourceTree?.FilePath ?? string.Empty)}:{l.GetLineSpan().StartLinePosition.Line + 1}");
            throw new ToolTargetIneligibleException($"Symbol '{symbol.Name}' has {references.Sum(r => r.Locations.Count())} usages and cannot be safely deleted. First locations: {string.Join(", ", firstLocations)}. Next: {ToolCallHint.Call("Search", ("mode", "references"))} to review them.", ToolCallHint.Call("Search", ("mode", "references")));
        }

        var reflectionRisk = await CheckReflectionRiskAsync(solution, filePath, symbol, cancellationToken);
        if (reflectionRisk != null)
        {
            return reflectionRisk;
        }

        // Find the node that declares this symbol in the document
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            throw new ToolInvalidArgumentException($"Could not get the semantic model of '{filePath}'; the file did not compile far enough to analyze.");
        }

        var node = root.DescendantNodes().FirstOrDefault(n =>
            SymbolEqualityComparer.Default.Equals(semanticModel.GetDeclaredSymbol(n, cancellationToken), symbol));

        if (node == null)
        {
            throw new ToolNotFoundException($"Declaration of symbol '{symbol.Name}' not found in '{filePath}'. Next: {ToolCallHint.Call("Search", ("mode", "symbol"), ("query", symbol.Name))} to locate it.", "symbol", symbol.Name);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = await RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync(document, root, node, SyntaxRemoveOptions.KeepUnbalancedDirectives, cancellationToken)
        };
    }
}

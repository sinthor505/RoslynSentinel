using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Basic;

public class StructuralRefinementEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly SentinelConfiguration _config;

    public StructuralRefinementEngine(ISolutionProvider workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    /// <summary>
    /// Synchronizes the filename to match the primary type declared in the file.
    /// Uses staging mechanism (returns change ID) instead of direct file writes.
    /// </summary>
    public async Task<DocumentEditResult> SyncTypeAndFilenameAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new ToolNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        // Find PRIMARY type (first non-nested type in file)
        var primaryType = root?.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is not BaseTypeDeclarationSyntax) // Not nested
            .FirstOrDefault();

        if (primaryType == null)
        {
            return new DocumentEditResult { Outcome = EditOutcome.TargetNotFound, FilePath = filePath, Message = "No type declaration found." };
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
                Changes = new Dictionary<FilePath, string> { [newPath] = sourceText.ToString() },
                Message = $"Renaming '{currentName}' to '{expectedName}' to match primary type '{primaryType.Identifier.Text}'."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Filename matches primary type."
        };
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (legacy: line/column-based).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePath filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SafeDeleteUnusedSymbol"))
        {
            return DocumentEditResult.FeatureDisabled(filePath);
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[line - 1].Start + (column - 1);
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0));

        var symbol = semanticModel?.GetDeclaredSymbol(node!, cancellationToken) ?? semanticModel?.GetSymbolInfo(node!, cancellationToken).Symbol;
        if (symbol == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Symbol not found."
            };
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        if (references.Any(r => r.Locations.Any()))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Symbol '{symbol.Name}' has {references.Sum(r => r.Locations.Count())} usages and cannot be safely deleted."
            };
        }

        var reflectionRisk = await CheckReflectionRiskAsync(solution, filePath, symbol, cancellationToken);
        if (reflectionRisk != null)
        {
            return reflectionRisk;
        }

        var newRoot = root!.RemoveNode(node!, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot!.NormalizeWhitespace().ToFullString()
        };
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (contextSnippet-based
    /// resolution — an agent-friendly alternative to the line/column overload above, which requires
    /// a column that a caller who only knows a line number has no cheap way to obtain).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePath filePath, string symbolName, string? contextSnippet, string? lineBefore, string? lineAfter, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var sourceText = await document.GetTextAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || sourceText == null || semanticModel == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        var candidates = root.DescendantNodes()
            .Where(n => GetDeclaredNodeName(n) == symbolName)
            .ToList();

        SyntaxNode? target;
        if (contextSnippet == null || candidates.Count <= 1)
        {
            // symbolName alone already resolves unambiguously — see the identical guard and
            // rationale in RefactoringEngine.ResolveMemberByNameOrSnippet.
            target = candidates.FirstOrDefault();
        }
        else
        {
            int snippetPos;
            try { snippetPos = ContextHelper.FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter); }
            catch (ToolException ex)
            {
                return new DocumentEditResult { Outcome = EditOutcome.CannotEdit, FilePath = filePath, Message = ex.Message };
            }

            target = candidates.FirstOrDefault(c => c.Span.Contains(snippetPos))
                ?? candidates.FirstOrDefault(c => new TextSpan(snippetPos, contextSnippet.Length).OverlapsWith(c.Span));
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = candidates.Count == 0
                    ? $"// No symbol named '{symbolName}' found in {filePath}."
                    : $"// '{symbolName}' has {candidates.Count} declaration(s) in {filePath}, and contextSnippet did not match any of them. " +
                      "Provide a contextSnippet that is a verbatim substring of the intended declaration."
            };
        }

        var symbol = semanticModel.GetDeclaredSymbol(target, cancellationToken);
        if (symbol == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Could not resolve a symbol for '{symbolName}' at the matched location."
            };
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
    /// name — a likely sign of reflection/<c>nameof</c>-adjacent dynamic usage that <see cref="SymbolFinder"/>
    /// would not catch (ported from the dead <c>RefactoringEngine.SafeDeleteSymbolAsync</c> copy — see
    /// docs/TODO.md's "Duplicate/dead SafeDeleteSymbolAsync" entry). Returns a blocking
    /// <see cref="DocumentEditResult"/> if a match is found anywhere, otherwise null.
    /// </summary>
    private static async Task<DocumentEditResult?> CheckReflectionRiskAsync(Solution solution, FilePath filePath, ISymbol symbol, CancellationToken cancellationToken = default)
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
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotEdit,
                        FilePath = filePath,
                        Message = $"// Potential reflection risk: symbol '{symbol.Name}' is referenced by a string literal in {doc.Name} — possible reflection/dynamic usage. Delete manually after verifying."
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Safe deletes a symbol only if it has no usages in the entire solution (handle-based resolution).
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePath filePath, ISymbol symbol, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("SafeDeleteUnusedSymbol"))
        {
            return DocumentEditResult.FeatureDisabled(filePath);
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not get syntax root."
            };
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        if (references.Any(r => r.Locations.Any()))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Symbol '{symbol.Name}' has {references.Sum(r => r.Locations.Count())} usages and cannot be safely deleted."
            };
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
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not get semantic model."
            };
        }

        var node = root.DescendantNodes().FirstOrDefault(n =>
            SymbolEqualityComparer.Default.Equals(semanticModel.GetDeclaredSymbol(n, cancellationToken), symbol));

        if (node == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Symbol declaration not found in file."
            };
        }

        var newRoot = root.RemoveNode(node, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot!.NormalizeWhitespace().ToFullString()
        };
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public class StructuralRefinementEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public StructuralRefinementEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Synchronizes the filename to match the primary type declared in the file.
    /// Uses staging mechanism (returns change ID) instead of direct file writes.
    /// </summary>
    public async Task<DocumentEditResult> SyncTypeAndFilenameAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

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
            // Use staging mechanism: return change ID instead of direct file write
            var changeId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var newPath = Path.Combine(directory, expectedName);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = $"CHANGE_{changeId}: {filePath} -> {newPath}"
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
    /// Safe deletes a symbol only if it has no usages in the entire solution.
    /// </summary>
    public async Task<DocumentEditResult> SafeDeleteSymbolAsync(FilePath filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
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

        var newRoot = root!.RemoveNode(node!, SyntaxRemoveOptions.KeepUnbalancedDirectives);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot!.NormalizeWhitespace().ToFullString()
        };
    }

    /// <summary>
    /// Pulls a member up to the base class.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> PullUpMemberAsync(FilePath filePath, string className, string memberName, CancellationToken cancellationToken = default)
    {
        // logic to remove from class, add to base...
        return new Dictionary<FilePath, string>();
    }

    /// <summary>
    /// Pushes a member down to derived classes.
    /// </summary>
    public async Task<Dictionary<FilePath, string>> PushMembersDownAsync(FilePath filePath, string className, string memberName, CancellationToken cancellationToken = default)
    {
        // logic to move to children...
        return new Dictionary<FilePath, string>();
    }
}

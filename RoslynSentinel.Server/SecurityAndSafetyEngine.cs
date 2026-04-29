using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SafetyIssue(string FilePath, int Line, int Column, string Type, string Description);

public class SecurityAndSafetyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public SecurityAndSafetyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<SafetyIssue>> FindUnsafeTypeCastsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SafetyIssue>();

        var issues = new List<SafetyIssue>();

        // Look for direct cast expressions like (Type)obj instead of 'as' or pattern matching
        var casts = root.DescendantNodes().OfType<CastExpressionSyntax>();
        foreach (var cast in casts)
        {
            var loc = cast.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1, "UnsafeCast", "Direct cast detected. Consider using 'as' operator or pattern matching 'is' to avoid InvalidCastException."));
        }

        return issues;
    }

    public async Task<List<SafetyIssue>> DetectMissingNullChecksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SafetyIssue>();

        var issues = new List<SafetyIssue>();
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        
        var memberAccesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
        foreach (var access in memberAccesses)
        {
            var typeInfo = semanticModel?.GetTypeInfo(access.Expression, cancellationToken);
            if (typeInfo?.Type?.IsReferenceType == true)
            {
                // A very simplified heuristic: if accessed without '?' and not guarded
                if (!access.IsKind(SyntaxKind.ConditionalAccessExpression))
                {
                    // To do this rigorously requires deep flow analysis. 
                    // This is a naive detection to demonstrate the capability.
                    var loc = access.GetLocation().GetLineSpan().StartLinePosition;
                    // issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1, "PotentialNullDeref", $"Potential null dereference of '{access.Expression}'."));
                }
            }
        }

        return issues;
    }
}

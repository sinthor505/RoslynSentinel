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

    // Numeric keyword aliases used as cast targets (C# syntax, not CLR names)
    private static readonly HashSet<string> NumericKeywords = new(StringComparer.Ordinal)
    {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "char", "nint", "nuint"
    };

    // CLR type names for numeric value types (used when checking source type via semantic model)
    private static readonly HashSet<string> NumericClrNames = new(StringComparer.Ordinal)
    {
        "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Single", "Double", "Decimal", "Char", "IntPtr", "UIntPtr"
    };

    public async Task<List<SafetyIssue>> FindUnsafeTypeCastsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return new List<SafetyIssue>();

        // Use semantic model to determine source types for accurate exclusion of safe numeric casts
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var issues = new List<SafetyIssue>();

        // Look for direct cast expressions like (Type)obj instead of 'as' or pattern matching.
        // A numeric cast (e.g., (int)myDouble) is safe only if the SOURCE type is also numeric.
        // Casting from object/reference to numeric (e.g., (int)untypedObject) IS unsafe and is flagged.
        var casts = root.DescendantNodes().OfType<CastExpressionSyntax>();
        foreach (var cast in casts)
        {
            var castTypeName = cast.Type.ToString().Trim();

            // Only skip the cast if BOTH target type and source type are numeric value types
            if (NumericKeywords.Contains(castTypeName) && semanticModel != null)
            {
                var sourceType = semanticModel.GetTypeInfo(cast.Expression, cancellationToken).Type;
                bool sourceIsNumeric = sourceType is { IsValueType: true } &&
                    (NumericClrNames.Contains(sourceType.Name) || NumericKeywords.Contains(sourceType.Name));
                if (sourceIsNumeric) continue; // e.g., (int)myDouble — safe narrowing/widening
            }

            var loc = cast.GetLocation().GetLineSpan().StartLinePosition;
            issues.Add(new SafetyIssue(filePath, loc.Line + 1, loc.Character + 1, "UnsafeCast",
                $"Direct cast to '{castTypeName}' detected. Consider using 'as' operator or pattern matching 'is' to avoid InvalidCastException."));
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

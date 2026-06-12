using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Common;

public readonly struct SymbolValidationResult
{
    public bool IsValid
    {
        get;
    }
    public string? ErrorMessage
    {
        get;
    }
    private SymbolValidationResult(bool isValid, string? errorMessage = null)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }
    public static SymbolValidationResult Valid() => new(true);
    public static SymbolValidationResult Error(string message) => new(false, message);
}

internal class SymbolValidator
{
    public static SymbolValidationResult IsValidMemberName(string name)
    {
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            return SymbolValidationResult.Error($"'{name}' is not a valid identifier.");
        }
        if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None)
        {
            return SymbolValidationResult.Error($"'{name}' is a reserved keyword.");
        }
        return SymbolValidationResult.Valid();
    }

    public static SymbolValidationResult IsValidQualifiedName(string text)
    {
        string trimmed = text.Trim();
        NameSyntax node = SyntaxFactory.ParseName(trimmed);
        if (node.ContainsDiagnostics)
        {
            return SymbolValidationResult.Error($"'{text}' is not a valid qualified name.");
        }
        // guard against a valid prefix + ignored trailing garbage
        if (node.ToFullString().Trim() != trimmed)
        {
            return SymbolValidationResult.Error($"'{text}' is not a valid qualified name.");
        }
        return SymbolValidationResult.Valid();
    }

    public static SymbolValidationResult IsValidSymbolId(string symbolId, Compilation compilation)
    {
        // at the top of rename_symbol, find_usages, etc.
        ImmutableArray<ISymbol> symbols =
            DocumentationCommentId.GetSymbolsForDeclarationId(symbolId, compilation);

        if (symbols.IsDefaultOrEmpty)
        {
            // distinguish the two failure modes for the agent:
            // - malformed/unparseable id
            // - well-formed id that resolves to nothing in this solution
            return SymbolValidationResult.Error($"symbolId '{symbolId}' did not resolve to any symbol. " +
                                                "Re-run locate_symbol; the workspace may have changed since the id was issued.");
        }

        ISymbol symbol = symbols[0]; // see ambiguity note below
        return SymbolValidationResult.Valid();
    }
}

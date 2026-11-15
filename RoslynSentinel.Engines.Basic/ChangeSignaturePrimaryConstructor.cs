using System.Collections.Generic;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

// Change-signature support for primary constructors. The rewrite itself runs in BasicRefactoringEngine.ChangeSignatureAsync (one planner for every
// constructor, so call sites, defaults and the call-binding gate behave identically); this class holds only what is specific to a primary constructor:
// finding the type, the record policy, the constructor symbol, the scope position of a new default and the wrapper refusal.
internal static class ChangeSignaturePrimaryConstructor
{
    /// <summary>
    /// The type named <paramref name="typeName"/> that declares a primary constructor (a parameter list on the type), or null when there is none. Two such types
    /// of that name in one file (nested or in different namespaces) cannot be told apart by name, so that is refused instead of picking one.
    /// </summary>
    internal static TypeDeclarationSyntax? FindTypeWithPrimaryConstructor(CompilationUnitSyntax root, string typeName)
    {
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(type => type.Identifier.Text == typeName && type.ParameterList != null)
            .ToList();
        if (matches.Count > 1)
        {
            var lines = string.Join(", ", matches.Select(type => type.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            throw new ToolTargetIneligibleException($"More than one type named '{typeName}' declares a primary constructor in this file (lines {lines}), so the target is ambiguous. Nothing was changed. Rename one of them first.");
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// The refusal for a record's primary constructor, or null when the change is allowed. A record's positional parameter is also a public property, a
    /// Deconstruct position and a with-expression member, so changing it is a public API change.
    /// </summary>
    internal static string? RecordPolicyRefusal(TypeDeclarationSyntax type, IReadOnlyList<SignatureParameterSpec> parameters)
    {
        // TODO implement (Step 3) per the owner's record policy (O1); until then every record is refused with the text ChangeSignature always had.
        return type is RecordDeclarationSyntax
            ? $"'{type.Identifier.Text}' is a type with a primary constructor, and ChangeSignature does not edit primary constructors (there is no constructor declaration to rewrite or to put a wrapper beside). Nothing was changed. Edit the type's parameter list and its construction sites directly, or add an ordinary constructor and change that one." // hint-ok: names the tool in prose, not a call to make
            : null;
    }

    /// <summary>
    /// The primary constructor of <paramref name="type"/> as a symbol: the instance constructor whose declaring syntax is the type declaration itself
    /// (an implicit default constructor of a struct has no declaring syntax). Null when it cannot be resolved.
    /// </summary>
    internal static IMethodSymbol? ResolvePrimaryConstructor(SemanticModel semanticModel, TypeDeclarationSyntax type, CancellationToken cancellationToken)
    {
        if (semanticModel.GetDeclaredSymbol(type, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            return null;
        }

        return typeSymbol.InstanceConstructors.FirstOrDefault(constructor => constructor.DeclaringSyntaxReferences
            .Any(reference => reference.SyntaxTree == type.SyntaxTree && reference.Span == type.Span));
    }

    /// <summary>Where a new parameter's default is bound: inside the type body when it has one, else at the end of the parameter list.</summary>
    internal static int ScopePosition(TypeDeclarationSyntax type) =>
        type.OpenBraceToken.IsMissing ? type.ParameterList!.Span.End : type.OpenBraceToken.Span.End;

    /// <summary>Why a primary constructor cannot keep its old signature as a deprecated wrapper (there is no constructor declaration to place one beside).</summary>
    internal static string WrapperRefusal(string typeName) =>
        $"'{typeName}' declares a primary constructor, which has no constructor declaration to place a deprecated wrapper beside";
}

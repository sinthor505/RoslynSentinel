using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Builds minimal text-span edits for adding/removing a base type or interface on a type declaration. Like
/// <see cref="AttributeTextEditBuilder"/>, every edit is expressed against ONE original source text, so edits on a
/// nested type and its containing type compose by construction (their base lists never overlap), and nothing outside
/// the edited base list is re-formatted. Compose with <see cref="AttributeTextEditBuilder.TryApply"/>.
/// </summary>
public static class BaseTypeTextEditBuilder
{
    /// <summary>Builds the insertion of <paramref name="baseTypeName"/> into the type's base list (creating the list when absent).</summary>
    public static AttributeTextEditBuilder.TextEdit BuildAddEdit(int editIndex, BaseTypeDeclarationSyntax type, string baseTypeName)
    {
        var baseList = type.BaseList;
        if (baseList != null && baseList.Types.Count > 0)
        {
            var end = baseList.Types[baseList.Types.Count - 1].Span.End;
            return new AttributeTextEditBuilder.TextEdit(end, end, ", " + baseTypeName, editIndex);
        }

        if (baseList != null)
        {
            var afterColon = baseList.ColonToken.Span.End;
            return new AttributeTextEditBuilder.TextEdit(afterColon, afterColon, " " + baseTypeName, editIndex);
        }

        var anchor = GetBaseListAnchorEnd(type);
        return new AttributeTextEditBuilder.TextEdit(anchor, anchor, " : " + baseTypeName, editIndex);
    }

    /// <summary>
    /// Builds the edit that removes every base type whose text contains <paramref name="baseTypeName"/>. Removing all of
    /// them removes the whole base list (including the colon); otherwise only the kept types are rewritten in place.
    /// Returns null when no base type matched.
    /// </summary>
    public static AttributeTextEditBuilder.TextEdit? BuildRemoveEdit(int editIndex, BaseTypeDeclarationSyntax type, string baseTypeName)
    {
        var baseList = type.BaseList;
        if (baseList == null || baseList.Types.Count == 0)
        {
            return null;
        }

        var remaining = baseList.Types.Where(t => !t.ToString().Contains(baseTypeName)).ToList();
        if (remaining.Count == baseList.Types.Count)
        {
            return null;
        }

        if (remaining.Count == 0)
        {
            return new AttributeTextEditBuilder.TextEdit(GetBaseListAnchorEnd(type), baseList.Span.End, string.Empty, editIndex);
        }

        var start = baseList.Types[0].SpanStart;
        var end = baseList.Types[baseList.Types.Count - 1].Span.End;
        return new AttributeTextEditBuilder.TextEdit(start, end, string.Join(", ", remaining.Select(t => t.ToString())), editIndex);
    }

    /// <summary>The end of whatever syntax a base list directly follows: primary-constructor parameters, type parameters, or the identifier.</summary>
    private static int GetBaseListAnchorEnd(BaseTypeDeclarationSyntax type)
    {
        if (type is TypeDeclarationSyntax typeDeclaration)
        {
            if (typeDeclaration.ParameterList != null)
            {
                return typeDeclaration.ParameterList.Span.End;
            }

            if (typeDeclaration.TypeParameterList != null)
            {
                return typeDeclaration.TypeParameterList.Span.End;
            }
        }

        return type.Identifier.Span.End;
    }
}

using System.Text;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>
/// Name forms the <see cref="EditEffect"/> emitters share, so every tool records the same shape: a simple name with no
/// container, generic arity or parameter list (the <see cref="EditEffectIndex"/> key), a container-qualified display name
/// without parameters, and a solution-relative file path.
/// </summary>
public static class EditEffectNames
{
    // Containing types and namespaces, no generics and no parameter list: Foo.Bar for a member, N.Foo for a type.
    private static readonly SymbolDisplayFormat DisplayFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    /// <summary>
    /// Reduces any spelling of a symbol name to its simple name: the last dotted segment with generic arguments, backtick arity
    /// and a parameter list removed. <c>N.Foo&lt;T&gt;.Bar(int)</c> becomes <c>Bar</c>; null or blank becomes the empty string.
    /// </summary>
    public static string SimpleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = name.Trim();
        var paren = text.IndexOf('(');
        if (paren > 0)
        {
            text = text[..paren];
        }

        var kept = new StringBuilder(text.Length);
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && c == '`')
            {
                while (i + 1 < text.Length && char.IsDigit(text[i + 1]))
                {
                    i++;
                }
            }
            else if (depth == 0)
            {
                kept.Append(c);
            }
        }

        var stripped = kept.ToString().Trim();
        var dot = stripped.LastIndexOf('.');
        return dot >= 0 ? stripped[(dot + 1)..] : stripped;
    }

    /// <summary>The container-qualified display name of <paramref name="symbol"/> with no parameter list, for example <c>N.Foo.Bar</c>.</summary>
    public static string Display(ISymbol symbol)
    {
        return symbol.ToDisplayString(DisplayFormat);
    }

    /// <summary>The path relative to <paramref name="solutionRoot"/> when it lies under it, otherwise the path as given.</summary>
    public static string DisplayPath(string path, string? solutionRoot)
    {
        if (solutionRoot is null || !Path.IsPathRooted(path))
        {
            return path;
        }

        var relative = Path.GetRelativePath(solutionRoot, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }
}

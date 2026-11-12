using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Gathers the one fact a MoveMember <see cref="EditEffect"/> needs that the tool's arguments do not give: the file the moved members now live in.
/// Never throws; null means the file could not be determined, and the effect is recorded without it.
/// </summary>
public static class MoveEditEffectFacts
{
    /// <summary>
    /// The file that declares the target type after the move: <paramref name="targetFilePath"/> when the caller named one, otherwise the first
    /// changed file other than <paramref name="sourceFile"/> whose proposed text declares a type named like <paramref name="targetClassName"/>, otherwise
    /// <paramref name="sourceFile"/> itself when it does (a same-file move). Reads the proposed texts, so it works for a target class the move creates.
    /// </summary>
    public static string? FindTargetFile(
        IReadOnlyDictionary<FilePathWrapper, string> changes,
        string targetClassName,
        FilePathWrapper? targetFilePath,
        FilePathWrapper sourceFile)
    {
        if (targetFilePath is { } given)
        {
            return given.Absolute;
        }

        var simple = EditEffectNames.SimpleName(targetClassName);
        if (simple.Length == 0)
        {
            return null;
        }

        string? inSourceFile = null;
        foreach (var (path, text) in changes)
        {
            // A cheap text test first: most changed files are call-site files that never mention the target.
            if (!text.Contains(simple, StringComparison.Ordinal) || !DeclaresType(text, simple))
            {
                continue;
            }

            if (path.Equals(sourceFile))
            {
                inSourceFile = path.Absolute;
                continue;
            }

            return path.Absolute;
        }

        return inSourceFile;
    }

    private static bool DeclaresType(string text, string simpleName)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        return root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == simpleName);
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

// TODO move the 14 private Edited copies (CRLF-aware form: ChangeSetTypeFromSpecScenario.cs:460) onto ScenarioTemplateEdits.ReplaceExactly
internal static class ScenarioTemplateEdits
{
    // oldText and newText are written with \n; the text's line endings are kept. Throws when the text does not contain
    // exactly expectedCount occurrences of oldText, so a template change cannot silently produce a wrong variant.
    internal static string ReplaceExactly(string text, string oldText, string newText, int expectedCount = 1)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("oldText must not be empty.", nameof(oldText));
        }

        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var actualCount = CountOccurrences(normalized, oldText);
        if (actualCount != expectedCount)
        {
            throw new InvalidOperationException(
                $"ScenarioTemplateEdits: '{oldText}' expected {expectedCount} occurrence(s) in the template, found {actualCount}.");
        }

        return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
    }

    // Removes each named method (with its leading trivia: attributes, doc comment and the blank line before it). Every name
    // must match exactly one MethodDeclarationSyntax in the source; overloads are rejected. Line endings elsewhere are kept.
    internal static string RemoveMethods(string source, params string[] methodNames)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var spans = new List<TextSpan>();
        foreach (var methodName in methodNames)
        {
            var matches = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == methodName)
                .ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"ScenarioTemplateEdits: method '{methodName}' matched {matches.Count} declaration(s), expected exactly 1.");
            }

            spans.Add(matches[0].FullSpan);
        }

        var result = source;
        foreach (var span in spans.OrderByDescending(span => span.Start))
        {
            result = result.Remove(span.Start, span.Length);
        }

        return result;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

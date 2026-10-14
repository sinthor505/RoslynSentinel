using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common.SnippetMatching;

/// <summary>
/// One matcher in the tier ladder. Exists so a matrix test can run every tier against every
/// matcher; callers go through the dispatcher, not through this interface.
/// </summary>
internal interface ISnippetMatcher
{
    /// <summary>The tier this matcher implements.</summary>
    WhitespaceTolerance Tier { get; }

    /// <summary>The matcher's name, copied into each <see cref="SnippetMatch"/> it returns.</summary>
    string Name { get; }

    /// <summary>
    /// Finds every match of <paramref name="snippet"/> in <paramref name="sourceText"/> at this matcher's tier
    /// (this matcher's own stage only; the dispatcher composes the ladder). Each returned span is the true
    /// span in the text, so replacing it with the matched text reproduces the file. Never throws for a
    /// snippet that simply does not match; an empty or whitespace-only snippet yields no matches. Takes a
    /// <see cref="SourceText"/> (not a string) because the collapse stages work on <c>SourceText.Lines</c>.
    /// </summary>
    IReadOnlyList<SnippetMatch> FindAll(SourceText sourceText, string snippet, SnippetCaseMode caseMode);
}

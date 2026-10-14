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
    /// Finds every match of <paramref name="snippet"/> in <paramref name="text"/> at this matcher's tier.
    /// Each returned span is the true span in <paramref name="text"/>, so replacing it with the matched
    /// text reproduces the file. Never throws for a snippet that simply does not match.
    /// </summary>
    IReadOnlyList<SnippetMatch> FindAll(string text, string snippet, SnippetCaseMode caseMode);
}

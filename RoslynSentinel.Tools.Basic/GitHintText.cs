using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Renders the tool-side hint text (sentences and call fragments that name other tools) for the keys the Git engine
/// attaches to a result, and applies them to the result at the tool boundary. Contract skeleton: bodies arrive in a later step.
/// </summary>
internal static class GitHintText
{
    /// <summary>The slot a key's rendered text goes into.</summary>
    internal static GitHintSlot SlotOf(GitHintKey key)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>The rendered tool-side text for a key and its call facts.</summary>
    internal static string Render(GitHintKey key, GitHintFacts? facts)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Renders every hint on <paramref name="result"/> into its slot and returns the same result.</summary>
    internal static T Apply<T>(T result) where T : GitResult
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>The tool-side advice appended to the engine's factual text for a root-lookup failure.</summary>
    internal static string RenderRootFailure(GitRootFailure failure)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}

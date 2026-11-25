namespace RoslynSentinel.Common;

/// <summary>
/// The effective value of a single config key, read through the reader's own doors so it declines exactly where the reader does: <see cref="Open"/> (process environment in
/// <c>OverridingEnvironmentVariables</c>, <c>extensions.worktreeConfig</c> with a <c>config.worktree</c>) and <see cref="ConfigValue"/> (a linked worktree reads
/// <c>&lt;commondir&gt;/config</c> plus the global and system levels, local over global as git does).
/// </summary>
internal sealed partial class Libgit2GitReader
{
    // TODO fold EffectiveConfigValue (Libgit2GitReader.IndexWrites.cs) into ConfigValue: one helper is the goal

    /// <summary>
    /// The effective value of <paramref name="key"/> across all config levels, the way git resolves it: local over global over system, and for a multi-valued key the last
    /// value wins. Null when the key is unset at every level, or when <paramref name="workingDirectory"/> is not inside a repository.
    /// </summary>
    /// <exception cref="NotSupportedException">The value is empty (<c>key=</c> or a valueless <c>key</c>): libgit2 does not say which, so git answers.</exception>
    public string? GetConfigValue(string workingDirectory, string key, TimeSpan cliTimeout)
    {
        using var repo = Open(workingDirectory);
        var text = repo is null ? null : ConfigValue(repo, key);
        if (text is null)
        {
            return null;
        }

        // An empty or valueless entry means different things to git ("key=" is the empty string, a bare "key" is boolean true) and libgit2 does not say which: the CLI decides.
        if (text.Length == 0)
        {
            throw new NotSupportedException($"Cannot tell whether the empty config value of '{key}' is an empty string or a valueless key.");
        }

        return text;
    }
}

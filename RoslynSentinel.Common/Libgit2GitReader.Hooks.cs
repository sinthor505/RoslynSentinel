using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The repository facts the hook runner (<see cref="GitHooks"/>, step 5c-2 of proposal_libgit2sharp_in_process_git.md) needs, read through the reader's own doors so it declines exactly where the
/// reader does: <see cref="Open"/> (process environment in <c>OverridingEnvironmentVariables</c>, <c>extensions.worktreeConfig</c> with a <c>config.worktree</c>) and the commondir-aware
/// <see cref="CommonDirectory"/> / <see cref="EffectiveConfigValue"/> (a linked worktree reads <c>&lt;commondir&gt;/config</c> plus the global and system levels, local over global as git does).
/// </summary>
internal sealed partial class Libgit2GitReader
{
    /// <summary>
    /// The work tree root, the common git directory, the effective <c>core.hooksPath</c> (null when unset at every level) and whether any <c>hook.*</c> config entry exists, for the repository
    /// containing <paramref name="workingDirectory"/>; null when there is none. The work tree root is where git runs hooks (null for a bare repository: git runs them in the git directory).
    /// </summary>
    /// <exception cref="NotSupportedException">One of the reader's environment or repository declines: git answers.</exception>
    internal static HookRepositoryLayout? ReadHookLayout(string workingDirectory)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return null;
        }

        var workTree = repo.Info.IsBare ? null : repo.Info.WorkingDirectory;
        return new HookRepositoryLayout(
            workTree is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workTree)),
            CommonDirectory(repo),
            EffectiveConfigValue(repo, "core.hookspath"),
            EffectiveConfigHasEntryWithPrefix(repo, "hook."));
    }

    /// <summary>
    /// The name of the first variable of <see cref="OverridingEnvironmentVariables"/> that <paramref name="getEnvironmentVariable"/> reports as set (present at all, even empty), null when none is.
    /// <see cref="Open"/> and the hook runner share this, so the lists cannot drift.
    /// </summary>
    internal static string? FirstOverridingEnvironmentVariable(Func<string, string?> getEnvironmentVariable)
    {
        foreach (var name in OverridingEnvironmentVariables)
        {
            if (getEnvironmentVariable(name) is not null)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>True when any config entry at any level (same level rules as <see cref="EffectiveConfigValue"/>) has a key starting with <paramref name="prefix"/> (case-insensitive).</summary>
    private static bool EffectiveConfigHasEntryWithPrefix(Repository repo, string prefix)
    {
        if (!IsLinkedWorktree(repo))
        {
            return repo.Config.Any(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        using var main = new Repository(CommonDirectory(repo));
        return main.Config.Any(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The configuration check of <see cref="GitCommitMessage.FormatForRepository"/> (step 5c-3 of proposal_libgit2sharp_in_process_git.md), read through the reader's own doors so it declines
/// exactly where the other in-process operations do (<see cref="Open"/>: process environment, worktree config) and sees every config level of a linked worktree
/// (<see cref="EffectiveConfigHasEntryWithPrefix"/>).
/// </summary>
internal sealed partial class Libgit2GitReader
{
    // Keys that change the text git stores for a -m message: the cleanup mode, the comment prefix (cleanup and trailer handling both read it) and the encoding git re-encodes the message to.
    // commit.template is deliberately absent: git ignores it for -m, also in its empty-message check (pinned by GitCommitMessageConfigTests).
    private static readonly string[] CommitMessageConfigKeys = ["commit.cleanup", "core.commentchar", "core.commentstring", "i18n.commitencoding"];

    /// <summary>
    /// The reason the message formatting of <see cref="GitCommitMessage"/> cannot be trusted for the repository containing <paramref name="workingDirectory"/>, or null when it can. Any entry (at any
    /// level, any value) of <see cref="CommitMessageConfigKeys"/> declines; with <paramref name="trailers"/> so does any <c>trailer.*</c> entry.
    /// </summary>
    /// <exception cref="NotSupportedException">One of the reader's environment or repository declines: git answers.</exception>
    internal static string? CommitMessageConfigDecline(string workingDirectory, bool trailers)
    {
        using var repo = Open(workingDirectory);
        if (repo is null)
        {
            return $"'{workingDirectory}' is not inside a git repository; git words the error.";
        }

        foreach (var key in CommitMessageConfigKeys)
        {
            if (EffectiveConfigHasEntryWithPrefix(repo, key))
            {
                return $"{key} is configured; git's message handling depends on it and this port does not reproduce that.";
            }
        }

        if (trailers && EffectiveConfigHasEntryWithPrefix(repo, "trailer."))
        {
            return "trailer.* is configured; it changes what git interpret-trailers does and this port does not reproduce that.";
        }

        return null;
    }
}

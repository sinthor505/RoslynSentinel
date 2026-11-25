namespace RoslynSentinel.Common;

/// <summary>How <see cref="GitCommitMessage"/> ended.</summary>
internal enum CommitMessageOutcome
{
    /// <summary>The final commit message text was produced and is byte-for-byte what <c>git commit -m</c> would have stored.</summary>
    Formatted,

    /// <summary>A condition git handles in a way this port does not reproduce (config, text git rejects or reads differently, an empty result): nothing was produced; the git CLI answers.</summary>
    Declined,
}

/// <summary>
/// Result of <see cref="GitCommitMessage.Format"/> and <see cref="GitCommitMessage.FormatForRepository"/>. <see cref="Message"/> is the complete, final text for
/// <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/> when <see cref="Outcome"/> is <see cref="CommitMessageOutcome.Formatted"/>; <see cref="Detail"/> is the decline reason otherwise.
/// </summary>
internal sealed record CommitMessageResult(CommitMessageOutcome Outcome, string? Message = null, string? Detail = null)
{
    internal bool IsFormatted => Outcome == CommitMessageOutcome.Formatted;

    internal static CommitMessageResult Formatted(string message) => new(CommitMessageOutcome.Formatted, Message: message);

    internal static CommitMessageResult Declined(string reason) => new(CommitMessageOutcome.Declined, Detail: reason);
}

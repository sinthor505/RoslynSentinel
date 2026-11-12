namespace RoslynSentinel.Common;

/// <summary>
/// The in-process commit route of the Git tool's commit (step 5c-4 of proposal_libgit2sharp_in_process_git.md): a plain new commit of the whole index, or of listed paths whose content the caller has
/// already staged, made through <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/> with the message <see cref="GitCommitMessage"/> produces, followed by an unawaited post-commit hook.
/// Everything else is declined, and a decline always means "the caller runs the git CLI exactly as it did before this route existed" (never an error): see <see cref="DeclineBeforeStaging"/>
/// and <see cref="Commit"/> for every rule and its reason text. The route is OFF unless git config <see cref="ConfigKey"/> is true (<see cref="IsEnabled"/>); a later change flips that default.
/// <para>
/// Counters (process-wide on <see cref="Shared"/>, shown in the <c>gitBackend</c> block of McpServerStatus beside the reader's): <see cref="TakenCount"/> commits made in process,
/// <see cref="DeclineCount"/> and <see cref="LastDeclineReason"/> for every call of an ENABLED route that went to the CLI (a disabled route counts nothing). A decline is normal traffic and is never logged to stderr.
/// </para>
/// <para>
/// Each outcome of an enabled route (taken or declined) is also appended as one JSON line to &lt;common git dir&gt;/roslynsentinel/commit-route.jsonl. The log survives server restarts; the append is best effort and never fails or changes a commit.
/// </para>
/// <para>
/// Not reproduced, accepted: git's <c>gc --auto</c> after a commit (the next CLI commit still runs it), and the index file's refreshed stat data (a later <c>git status</c> refreshes it, see the CAS primitive).
/// </para>
/// </summary>
internal sealed class InProcessCommitRoute
{
    /// <summary>The git config key (true enables the route; absent or false keeps the CLI path).</summary>
    internal const string ConfigKey = "roslynsentinel.inProcessCommit";

    // Hooks git runs around a commit that this route does not run: any of them present (or unresolvable) means the CLI commits. post-commit is launched in process (see Commit);
    // the three blocking hooks are checked by GitHooks.HasBlockingHook.
    private static readonly string[] UnsupportedCommitHooks = ["reference-transaction", "post-index-change"];

    // Serialises the commit-route log appends of this process: the route is called from concurrent tool calls.
    private static readonly object AppendLock = new();

    /// <summary>The process-wide route the Git tool uses and the status block reports.</summary>
    internal static InProcessCommitRoute Shared { get; } = new();

    private long _takenCount;
    private long _declineCount;
    private string? _lastDeclineReason;

    /// <summary>Test seam only: runs after the commit object exists and before the branch moves, to move the branch from outside (see <see cref="Libgit2GitReader.CommitWithCompareAndSwap"/>).</summary>
    internal Action? BetweenCommitAndRefUpdate { get; init; }

    /// <summary>Test seam only: hook resolution, shell lookup and environment of the hook checks and the post-commit launch; null is the real repository and process.</summary>
    internal GitHookOptions? HookOptions { get; init; }

    /// <summary>Commits made in process.</summary>
    internal long TakenCount => Interlocked.Read(ref _takenCount);

    /// <summary>Calls of an enabled route that the git CLI answered instead (every reason, including nothing-to-commit and a lost race).</summary>
    internal long DeclineCount => Interlocked.Read(ref _declineCount);

    /// <summary>The reason of the most recent decline; null while none has happened.</summary>
    internal string? LastDeclineReason => Volatile.Read(ref _lastDeclineReason);

    /// <summary>
    /// True when git config <see cref="ConfigKey"/> is true for the repository containing <paramref name="workingDirectory"/>, read through the reader seam like <c>roslynsentinel.commitTrailers</c>
    /// (every level, a linked worktree through its main repository). Absent, false, unreadable or any exception is false.
    /// </summary>
    internal bool IsEnabled(IGitReader reader, string workingDirectory, TimeSpan cliTimeout)
    {
        try
        {
            return reader.GetBoolConfig(workingDirectory, ConfigKey, cliTimeout) == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Counts one decline, keeps its reason and appends it to the commit-route log; the caller then runs the CLI path.</summary>
    internal void RecordDecline(string reason, string workingDirectory)
    {
        Interlocked.Increment(ref _declineCount);
        Volatile.Write(ref _lastDeclineReason, reason);
        AppendOutcome(workingDirectory, "declined", commit: null, reason);
    }

    /// <summary>
    /// The reason this commit must go to the CLI, decided BEFORE anything is staged, or null when the route may proceed. In order: an operation in progress (merge, cherry-pick, revert, rebase, am; read through the
    /// reader seam); the repository-level rules of <see cref="Libgit2GitReader.InProcessCommitDecline"/> (<c>commit.gpgsign</c>, <c>commit.signoff</c>, conversion attributes, and the reader's
    /// environment and repository declines); a pre-commit, prepare-commit-msg or commit-msg hook that exists or cannot be ruled out; a reference-transaction or post-index-change hook that exists or cannot be
    /// ruled out; a post-commit hook that exists but could not be launched in process. Never throws.
    /// </summary>
    /// <param name="listedPaths">The listed paths of a <c>--only</c> commit; null or empty for a whole-index commit.</param>
    internal string? DeclineBeforeStaging(IGitReader reader, string workingDirectory, IReadOnlyList<string>? listedPaths, TimeSpan cliTimeout)
    {
        try
        {
            if (reader.GetInProgressOperation(workingDirectory, cliTimeout) is { Length: > 0 } operation)
            {
                return $"a {operation} is in progress; git concludes it with its own commit rules.";
            }

            if (Libgit2GitReader.InProcessCommitDecline(workingDirectory, listedPaths) is { } reason)
            {
                return reason;
            }

            var blocking = GitHooks.HasBlockingHook(workingDirectory, HookOptions);
            switch (blocking.Outcome)
            {
                case BlockingHookOutcome.Found:
                    return $"the {blocking.HookName} hook exists; this route runs no blocking hooks.";
                case BlockingHookOutcome.Unknown:
                    return $"a blocking hook cannot be ruled out ({blocking.Detail}).";
            }

            foreach (var name in UnsupportedCommitHooks)
            {
                var hook = GitHooks.ResolveHookPath(workingDirectory, name, HookOptions);
                if (hook.Outcome != HookPathOutcome.NotFound)
                {
                    return hook.Outcome == HookPathOutcome.Found
                        ? $"the {name} hook exists; this route does not run it."
                        : $"the {name} hook cannot be ruled out ({hook.Detail}).";
                }
            }

            return GitHooks.PostHookLaunchBlocker(workingDirectory, "post-commit", HookOptions) is { } blocker
                ? $"the post-commit hook cannot be launched in process ({blocker})"
                : null;
        }
        catch (NotSupportedException ex) when (ex is not PlatformNotSupportedException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Commits in process: formats the message (<see cref="GitCommitMessage.FormatForRepository"/>: cleanup, plus the Tool-Call-Id / Tool-Agent trailers when <paramref name="toolCallId"/> is not null), commits
    /// the whole index or HEAD plus <paramref name="listedPaths"/> with the compare-and-swap primitive (which writes the branch and HEAD reflog entries <c>commit: &lt;subject&gt;</c>), and on success starts the
    /// repository's post-commit hook unawaited (its result is ignored, nothing waits for it). A result that is not <see cref="CasCommitOutcome.Committed"/> changed nothing and is counted as a decline with a
    /// reason here, so the caller only has to run the CLI path. Never throws: any exception before the commit exists is a decline.
    /// </summary>
    /// <param name="storedMessage">The exact text stored in the commit (set for Committed).</param>
    internal CasCommitResult Commit(string workingDirectory, string message, string? toolCallId, string? toolAgent, IReadOnlyList<string>? listedPaths, out string? storedMessage)
    {
        storedMessage = null;
        CasCommitResult result;
        string finalMessage;
        try
        {
            var formatted = GitCommitMessage.FormatForRepository(workingDirectory, message, toolCallId, toolAgent);
            if (!formatted.IsFormatted)
            {
                var declined = CasCommitResult.Declined($"message: {formatted.Detail}");
                RecordDecline(declined.Detail!, workingDirectory);
                return declined;
            }

            finalMessage = formatted.Message!;
            var whole = listedPaths is not { Count: > 0 };
            result = Libgit2GitReader.CommitWithCompareAndSwap(
                workingDirectory,
                finalMessage,
                whole ? CasCommitTreeSource.WholeIndex : CasCommitTreeSource.HeadPlusPaths,
                whole ? null : listedPaths,
                BetweenCommitAndRefUpdate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var declined = CasCommitResult.Declined($"{ex.GetType().Name}: {ex.Message}");
            RecordDecline(declined.Detail!, workingDirectory);
            return declined;
        }

        if (result.IsCommitted)
        {
            // From here on nothing may throw or decline: the commit exists, and the CLI path would make a second one.
            Interlocked.Increment(ref _takenCount);
            AppendOutcome(workingDirectory, "taken", result.CommitId, reason: null);
            storedMessage = finalMessage;
            LaunchPostCommit(workingDirectory);
            return result;
        }

        var declineReason = result.Outcome switch
        {
            CasCommitOutcome.Declined => $"the commit primitive declined: {result.Detail}",
            CasCommitOutcome.BranchMoved => $"the branch moved while the commit was built (expected {result.ExpectedTip ?? "unborn"}, now {result.ActualTip ?? "gone"}); nothing was written, the CLI commits.",
            CasCommitOutcome.LockContention => $"another git process holds a lock: {result.Detail}",
            CasCommitOutcome.NothingToCommit => "nothing to commit; the CLI words the refusal.",
            _ => $"libgit2 failed: {result.Detail}",
        };
        RecordDecline(declineReason, workingDirectory);
        return result;
    }

    /// <summary>
    /// Appends one JSON line (utc, outcome, commit and reason when not null, pid) to &lt;common git dir&gt;/roslynsentinel/commit-route.jsonl, creating the folder. The common git directory is
    /// the one the hook resolver reads (a linked worktree writes to its main repository). Best effort: every exception is ignored, and no repository means no line.
    /// </summary>
    private static void AppendOutcome(string workingDirectory, string outcome, string? commit, string? reason)
    {
        try
        {
            var commonDirectory = Libgit2GitReader.ReadHookLayout(workingDirectory)?.CommonDirectory;
            if (commonDirectory is null)
            {
                return;
            }

            var folder = Path.Combine(commonDirectory, "roslynsentinel");
            Directory.CreateDirectory(folder);
            var line = System.Text.Json.JsonSerializer.Serialize(
                new { utc = DateTimeOffset.UtcNow.ToString("O"), outcome, commit, reason, pid = Environment.ProcessId },
                new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + "\n";
            lock (AppendLock)
            {
                File.AppendAllText(Path.Combine(folder, "commit-route.jsonl"), line, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception)
        {
            // Best effort by design: the log must never fail or change a commit or a decline.
        }
    }

    private void LaunchPostCommit(string workingDirectory)
    {
        try
        {
            // Unawaited and never waited for; DeclineBeforeStaging already ruled out a hook that cannot be launched, so a failure here is a race (the hook appeared or vanished) and is ignored.
            _ = GitHooks.LaunchPostHook(workingDirectory, "post-commit", arguments: null, HookOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[RoslynSentinel.InProcessCommit] post-commit launch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

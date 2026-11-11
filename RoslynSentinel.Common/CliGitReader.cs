namespace RoslynSentinel.Common;

/// <summary>
/// <see cref="IGitReader"/> over the git CLI (<see cref="GitProcess.TryRun"/>): the reference behaviour the libgit2
/// implementation is compared against, and the fallback when native libgit2 is unusable. Output is parsed from the
/// NUL-separated (<c>-z</c>) forms so paths are never quoted and a path holding a tab or a quote cannot confuse a parser.
/// Config that would change the shape of the output is pinned on the command line (renames, untracked mode).
/// </summary>
internal sealed class CliGitReader : IGitReader
{
    private const char UnitSeparator = '\u001f';
    private const char RecordSeparator = '\u001e';

    public string? GetHeadSha(string workingDirectory, bool shortForm, TimeSpan cliTimeout)
    {
        var run = shortForm
            ? GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--short", "HEAD")
            : GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "HEAD");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        var text = run.Value.StdOut.Trim();
        return text.Length > 0 ? text : null;
    }

    public IReadOnlyList<ChangedCommit>? GetCommitsSince(string workingDirectory, string sha, TimeSpan cliTimeout)
    {
        // --no-renames: a rename lists both paths (and does not depend on the user's diff.renames); the trailing "--" ends the revision list.
        var run = GitProcess.TryRun(
            workingDirectory, cliTimeout, "log", sha + "..HEAD", "-z", "--name-only", "--no-renames", "--format=%h%x1f%s", "--");
        return run is { ExitCode: 0 } ? ParseLog(run.Value.StdOut) : null;
    }

    public IReadOnlyList<WorkingTreeChange>? GetStatus(string workingDirectory, IReadOnlyList<string>? pathspecs, TimeSpan cliTimeout)
    {
        var args = new List<string> { "status", "--porcelain", "-z", "--untracked-files=normal", "--renames" };
        if (pathspecs is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        var run = GitProcess.TryRun(workingDirectory, cliTimeout, [.. args]);
        return run is { ExitCode: 0 } ? ParseStatus(run.Value.StdOut) : null;
    }

    public WorkingTreeStatus? GetRepositoryStatus(string workingDirectory, TimeSpan cliTimeout)
    {
        // The arguments the Git tool has always used for status. -z makes quoting moot; core.quotePath=false is kept for parity with the old call.
        var status = GitProcess.TryRun(workingDirectory, cliTimeout, "-c", "core.quotePath=false", "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status is not { ExitCode: 0 })
        {
            return null;
        }

        // rev-parse --abbrev-ref HEAD exits 128 on an unborn branch: no branch name then.
        var branchRun = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = branchRun is { ExitCode: 0 } ? branchRun.Value.StdOut.Trim() : null;

        // The git directory comes from rev-parse, not workingDirectory + ".git", so a linked worktree (where .git is a file) resolves to its own state directory.
        var gitDirectory = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--absolute-git-dir");
        var inProgress = gitDirectory is { ExitCode: 0 } ? GitOperationState.Detect(gitDirectory.Value.StdOut.Trim()) : null;

        return new WorkingTreeStatus(branch, ParseStatus(status.Value.StdOut), inProgress);
    }

    public IReadOnlyList<LogCommit>? GetLog(string workingDirectory, int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow, TimeSpan cliTimeout)
    {
        // The Git tool's own runner forces UTF-8 for commit text (a repo-level i18n.* setting could otherwise change what reaches the pipe): same here.
        string[] args = ["-c", "i18n.logOutputEncoding=utf-8", "-c", "i18n.commitEncoding=utf-8", .. BuildLogArguments(maxCount, revision, paths, pickaxe, pickaxeRegex, follow)];
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, args);
        return run is { ExitCode: 0 } ? ParseLogEntries(run.Value.StdOut) : null;
    }

    /// <summary>The <c>git log</c> arguments (without the leading <c>-c</c> pairs) for <see cref="IGitReader.GetLog"/>; the Git tool's own CLI retry shares them.</summary>
    internal static List<string> BuildLogArguments(int maxCount, string? revision, IReadOnlyList<string>? paths, string? pickaxe, bool pickaxeRegex, bool follow)
    {
        // %B (the full body) can hold newlines, so fields are separated by the unit separator and commits end with the record separator.
        var format = $"%H{UnitSeparator}%h{UnitSeparator}%an{UnitSeparator}%aI{UnitSeparator}%B{RecordSeparator}";
        var args = new List<string> { "log", $"--max-count={maxCount}", $"--format={format}" };
        // Attached form (-S<text> / -G<regex>) so a value beginning with '-' is never read as an option.
        if (!string.IsNullOrWhiteSpace(pickaxe))
        {
            args.Add((pickaxeRegex ? "-G" : "-S") + pickaxe);
        }

        if (follow)
        {
            args.Add("--follow");
        }

        if (!string.IsNullOrWhiteSpace(revision))
        {
            args.Add(revision);
        }

        if (paths is not null)
        {
            args.Add("--");
            args.AddRange(paths);
        }

        return args;
    }

    /// <summary>
    /// Parses the <see cref="BuildLogArguments"/> output: records end at the record separator (the newline <c>--format</c> adds after each leads the next
    /// record), fields split at the unit separator, and a record with fewer than five fields is skipped. The message is returned untouched.
    /// </summary>
    internal static List<LogCommit> ParseLogEntries(string output)
    {
        var commits = new List<LogCommit>();
        foreach (var record in output.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = record.TrimStart('\n', '\r').Split(UnitSeparator);
            if (parts.Length < 5)
            {
                continue;
            }

            commits.Add(new LogCommit(parts[0], parts[1], parts[2], parts[3], parts[4]));
        }

        return commits;
    }

    public string? ReadBlobAtHead(string workingDirectory, string relativePath, TimeSpan cliTimeout)
    {
        // cat-file blob (not show): a directory is an error here, where "show" would print a tree listing. "./" makes the path relative to the working directory.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "cat-file", "blob", $"HEAD:./{relativePath}");
        return run is { ExitCode: 0 } ? run.Value.StdOut : null;
    }

    public IReadOnlyList<string>? ListTrackedPaths(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "ls-files", "-z", "--cached");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        // A path listed once per merge stage (an unmerged file) is reported once.
        return run.Value.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string>? ListIndexPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout) =>
        ListPathspecMatches(workingDirectory, cliTimeout, ["--literal-pathspecs", "ls-files", "-z", "--cached", "--", .. paths]);

    public IReadOnlyList<string>? ListHeadPathsMatching(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout) =>
        ListPathspecMatches(workingDirectory, cliTimeout, ["--literal-pathspecs", "ls-tree", "-r", "-z", "--name-only", "HEAD", "--", .. paths]);

    public IReadOnlyList<string>? FindIgnoredPaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout)
    {
        // One check per path, as the Git tool always did: exit 0 is ignored, 1 is not, and 128 (a path outside the repository) is not ignored either.
        var ignored = new List<string>();
        foreach (var path in paths)
        {
            if (GitProcess.TryRun(workingDirectory, cliTimeout, "check-ignore", "-q", "--", path) is { ExitCode: 0 })
            {
                ignored.Add(path);
            }
        }

        return ignored;
    }

    /// <summary>Runs an ls-files/ls-tree <c>-z</c> listing; the NUL-separated names, each once; null on a non-zero exit (a rejected pathspec, an unborn HEAD).</summary>
    private static IReadOnlyList<string>? ListPathspecMatches(string workingDirectory, TimeSpan cliTimeout, string[] args)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, args);
        return run is { ExitCode: 0 }
            ? run.Value.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList()
            : null;
    }

    public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "log", "--format=%H%x1f%B%x1e");
        return run is { ExitCode: 0 } ? ParseMessages(run.Value.StdOut) : null;
    }

    /// <summary>Parses <c>log -z --name-only --format=%h%x1f%s</c>: NUL-separated tokens, a header (it holds the unit separator) followed by its path tokens.</summary>
    internal static List<ChangedCommit> ParseLog(string output)
    {
        var commits = new List<ChangedCommit>();
        string? hash = null;
        var subject = string.Empty;
        var paths = new List<string>();

        foreach (var rawToken in output.Split('\0'))
        {
            // Tolerate a newline git may emit between records.
            var token = rawToken.Trim('\n', '\r');
            if (token.Length == 0)
            {
                continue;
            }

            var separator = token.IndexOf(UnitSeparator);
            if (separator >= 0)
            {
                if (hash is not null)
                {
                    commits.Add(new ChangedCommit(hash, subject, paths));
                }

                hash = token[..separator];
                subject = token[(separator + 1)..];
                paths = [];
            }
            else if (hash is not null)
            {
                paths.Add(token);
            }
        }

        if (hash is not null)
        {
            commits.Add(new ChangedCommit(hash, subject, paths));
        }

        return commits;
    }

    /// <summary>Parses <c>status --porcelain -z</c>: <c>XY path</c> tokens; a rename or copy is followed by one more token, the original path.</summary>
    internal static List<WorkingTreeChange> ParseStatus(string output)
    {
        var changes = new List<WorkingTreeChange>();
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4)
            {
                continue;
            }

            string? original = null;
            if ((token[0] is 'R' or 'C' || token[1] is 'R' or 'C') && i + 1 < tokens.Length)
            {
                original = tokens[++i];
            }

            changes.Add(new WorkingTreeChange(token[3..], original, token[0], token[1]));
        }

        return changes;
    }

    /// <summary>Parses <c>log --format=%H%x1f%B%x1e</c>; the format's terminator newline lands at the start of the next record, so only the hash is trimmed.</summary>
    internal static List<CommitMessage> ParseMessages(string output)
    {
        var messages = new List<CommitMessage>();
        foreach (var record in output.Split(RecordSeparator))
        {
            var separator = record.IndexOf(UnitSeparator);
            if (separator < 0)
            {
                continue;
            }

            var sha = record[..separator].Trim();
            if (sha.Length > 0)
            {
                messages.Add(new CommitMessage(sha, record[(separator + 1)..]));
            }
        }

        return messages;
    }
}

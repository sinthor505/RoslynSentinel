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

    public IReadOnlyList<BranchRef>? ListBranches(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "branch", "--list", "--all");
        return run is { ExitCode: 0 } ? ParseBranchList(run.Value.StdOut) : null;
    }

    /// <summary>
    /// Parses <c>git branch --list --all</c>: a two-character marker ("* " current, "+ " checked out in another worktree, "  " otherwise) and the name;
    /// a row with "->" (a symbolic ref such as remotes/origin/HEAD) is skipped. A detached HEAD row "(HEAD detached at abc)" comes through as a name.
    /// </summary>
    internal static List<BranchRef> ParseBranchList(string output)
    {
        var branches = new List<BranchRef>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var isCurrent = line.StartsWith("* ", StringComparison.Ordinal);
            var name = line[2..].Trim();
            if (name.Contains("->", StringComparison.Ordinal))
            {
                continue;
            }

            var isRemote = name.StartsWith("remotes/", StringComparison.Ordinal);
            branches.Add(new BranchRef(isRemote ? name["remotes/".Length..] : name, isCurrent, isRemote));
        }

        return branches;
    }

    public IReadOnlyList<StashRef>? ListStashes(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "stash", "list", "--format=%gd%x09%h%x09%cI%x09%gs");
        return run is { ExitCode: 0 } ? ParseStashList(run.Value.StdOut) : null;
    }

    /// <summary>Parses the tab-separated <c>stash list</c> rows (%gd, %h, %cI, %gs); a row that is not "stash@{N}" plus three more fields is skipped.</summary>
    internal static List<StashRef> ParseStashList(string output)
    {
        var stashes = new List<StashRef>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t', 4);
            if (parts.Length < 4)
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(parts[0], @"^stash@\{(\d+)\}$");
            if (!match.Success)
            {
                continue;
            }

            stashes.Add(new StashRef(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), parts[1], parts[2], parts[3]));
        }

        return stashes;
    }

    public int? CountStashes(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "stash", "list", "--format=%gd");
        return run is { ExitCode: 0 } ? run.Value.StdOut.Split('\n').Count(line => line.Trim().Length > 0) : null;
    }

    /// <summary>Runs an ls-files/ls-tree <c>-z</c> listing; the NUL-separated names, each once; null on a non-zero exit (a rejected pathspec, an unborn HEAD).</summary>
    private static IReadOnlyList<string>? ListPathspecMatches(string workingDirectory, TimeSpan cliTimeout, string[] args)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, args);
        return run is { ExitCode: 0 }
            ? run.Value.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList()
            : null;
    }

    private const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    public IReadOnlyList<NameStatusEntry>? GetNameStatus(string workingDirectory, string target, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        // Exactly what the Git tool's diff runs: core.quotePath=false so non-ASCII names are not octal-escaped; no rename flags, so git's default detection applies.
        var args = new List<string> { "-c", "core.quotePath=false", "diff", "--name-status" };
        if (target == "staged")
        {
            args.Add("--cached");
        }
        else if (target != "working")
        {
            args.Add(target);
        }

        AddPathLimit(args, paths);
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, [.. args]);
        return run is { ExitCode: 0 } ? ParseNameStatus(run.Value.StdOut) : null;
    }

    public IReadOnlyList<NameStatusEntry>? GetCommitNameStatus(string workingDirectory, string revision, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        var parent = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--verify", "--quiet", revision + "^");
        var args = new List<string> { "-c", "core.quotePath=false", "diff", "--name-status", parent is { ExitCode: 0 } ? revision + "^" : EmptyTreeHash, revision };
        AddPathLimit(args, paths);
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, [.. args]);
        return run is { ExitCode: 0 } ? ParseNameStatus(run.Value.StdOut) : null;
    }

    public CommitHeader? GetCommitHeader(string workingDirectory, string revision, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "show", "--format=%H%x1f%an%x1f%aI%x1f%B", "--no-patch", revision);
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        // The tool has always split on the separator and taken the fourth field as the message.
        var parts = run.Value.StdOut.TrimEnd('\n', '\r').Split(UnitSeparator);
        return parts.Length < 4 ? null : new CommitHeader(parts[0], parts[1], parts[2], parts[3]);
    }

    private static void AddPathLimit(List<string> args, IReadOnlyList<string>? paths)
    {
        if (paths is not null)
        {
            args.Add("--");
            args.AddRange(paths);
        }
    }

    /// <summary>Parses <c>--name-status</c> text: "M&lt;TAB&gt;path" or, for a rename or copy, "R087&lt;TAB&gt;old&lt;TAB&gt;new". Paths stay as git printed them.</summary>
    internal static List<NameStatusEntry> ParseNameStatus(string text)
    {
        var entries = new List<NameStatusEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.TrimEnd('\r').Split('\t');
            if (columns.Length >= 3)
            {
                entries.Add(new NameStatusEntry(columns[0], columns[2], columns[1]));
            }
            else if (columns.Length == 2)
            {
                entries.Add(new NameStatusEntry(columns[0], columns[1]));
            }
        }

        return entries;
    }

    public IReadOnlyList<CommitMessage>? GetCommitMessages(string workingDirectory, TimeSpan cliTimeout)
    {
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "log", "--format=%H%x1f%B%x1e");
        return run is { ExitCode: 0 } ? ParseMessages(run.Value.StdOut) : null;
    }

    public string? FindCommitByTrailer(string workingDirectory, string key, string value, int maxCommits, TimeSpan cliTimeout)
    {
        // git's own trailer parser decides what a trailer is: one record per commit, the matching values one per line.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "log", "-n", maxCommits.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"--format=%H%x1f%(trailers:key={key},valueonly,unfold)%x1e");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        foreach (var record in run.Value.StdOut.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = record.IndexOf('\u001f');
            if (separator < 0)
            {
                continue;
            }

            var values = record[(separator + 1)..].Split('\n').Select(v => v.Trim());
            if (values.Contains(value.Trim(), StringComparer.Ordinal))
            {
                return record[..separator].Trim();
            }
        }

        return null;
    }

    public bool? GetBoolConfig(string workingDirectory, string key, TimeSpan cliTimeout)
    {
        // Exit 1 = unset, 128 = not a boolean or not a repository: all null.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "config", "--type=bool", "--get", key);
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        return run.Value.StdOut.Trim() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    public HeadWithParents? GetHeadWithParents(string workingDirectory, TimeSpan cliTimeout)
    {
        // Exactly the command GitImpl.ReadHeadWithParentsAsync has always run; the trailing "--" ends the revision list. Non-zero = unborn HEAD or not a repository.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-list", "--parents", "-n", "1", "HEAD", "--");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        var tokens = run.Value.StdOut.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? null : new HeadWithParents(tokens[0], tokens[1..]);
    }

    public string? GetMergeHead(string workingDirectory, TimeSpan cliTimeout)
    {
        // Exactly the command GitImpl.ReadMergeHeadAsync has always run: --verify --quiet exits 1 without a message when MERGE_HEAD does not exist.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--verify", "--quiet", "MERGE_HEAD^{commit}");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        var text = run.Value.StdOut.Trim();
        return text.Length > 0 ? text : null;
    }

    public string? ResolveCommitAbbreviation(string workingDirectory, string abbreviation, TimeSpan cliTimeout)
    {
        // Exactly the command GitImpl.ResolveSummaryHashAsync has always run: --verify --quiet exits 1 without a message when the abbreviation is ambiguous or names no commit.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--verify", "--quiet", abbreviation + "^{commit}");
        if (run is not { ExitCode: 0 })
        {
            return null;
        }

        var text = run.Value.StdOut.Trim();
        return text.Length > 0 ? text : null;
    }

    public string? GetCommitMessage(string workingDirectory, string revision, TimeSpan cliTimeout)
    {
        // Exactly the command GitImpl.CommitAsync has always run for the committed message (with the same encoding config in front); the trailing "--" ends the revision list.
        var run = GitProcess.Run(workingDirectory, ["log", "-1", "--format=%B", revision, "--"], cliTimeout, default, IndexWriteRunOptions);
        if (!run.Completed || run.ExitCode != 0)
        {
            return null;
        }

        // git log ends the record with one line break that is not part of the message.
        var text = run.StdOut;
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    public string? GetUpstreamContainingHead(string workingDirectory, TimeSpan cliTimeout)
    {
        // Exactly the two commands GitImpl.CommitAsync has always run for an amend: exit 0 of the first = an upstream is configured (and exists); exit 0 of the second = HEAD is an
        // ancestor of (or equal to) the upstream tip.
        var upstream = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
        if (upstream is not { ExitCode: 0 })
        {
            return null;
        }

        var name = upstream.Value.StdOut.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var contained = GitProcess.TryRun(workingDirectory, cliTimeout, "merge-base", "--is-ancestor", "HEAD", "@{upstream}");
        return contained is { ExitCode: 0 } ? name : null;
    }

    public string? GetInProgressOperation(string workingDirectory, TimeSpan cliTimeout)
    {
        // The git dir comes from rev-parse (not workingDirectory + ".git") so a linked worktree, where .git is a file, resolves to its per-worktree git dir.
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, "rev-parse", "--absolute-git-dir");
        return run is { ExitCode: 0 } ? GitOperationState.Detect(run.Value.StdOut.Trim()) : null;
    }

    public bool? HasStagedChanges(string workingDirectory, IReadOnlyList<string>? paths, TimeSpan cliTimeout)
    {
        // Exactly the command GitImpl.NothingStagedAsync has always run: literal pathspecs, --quiet exits 1 when the index differs from HEAD and 0 when it does not.
        var args = new List<string> { "--literal-pathspecs", "diff", "--cached", "--quiet" };
        AddPathLimit(args, paths);
        var run = GitProcess.TryRun(workingDirectory, cliTimeout, [.. args]);
        return run?.ExitCode switch
        {
            0 => false,
            1 => true,
            _ => null,
        };
    }

    // What the stage and unstage commands run with: the resolved git executable and the encoding config GitImpl has always put in front of its commands.
    private static readonly GitRunOptions IndexWriteRunOptions = new()
    {
        GitExecutable = GitProcess.ResolveGitExecutablePath(),
        PrefixArgs = ["-c", "i18n.logOutputEncoding=utf-8", "-c", "i18n.commitEncoding=utf-8"],
    };

    public GitRunResult StageWholeFiles(string workingDirectory, IndexStageMode mode, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken)
    {
        // Exactly the commands GitImpl.StageAsync has always run (add -A without --literal-pathspecs, as before).
        List<string> args = mode switch
        {
            IndexStageMode.UpdateTracked => ["--literal-pathspecs", "add", "-u"],
            IndexStageMode.AddAll => ["add", "-A"],
            IndexStageMode.UpdateListed => ["--literal-pathspecs", "add", "-u", "--", .. paths],
            IndexStageMode.AddListed => ["--literal-pathspecs", "add", "--", .. paths],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown stage mode."),
        };
        return GitProcess.Run(workingDirectory, args, cliTimeout, cancellationToken, IndexWriteRunOptions);
    }

    public GitRunResult UnstagePaths(string workingDirectory, IReadOnlyList<string> paths, TimeSpan cliTimeout, CancellationToken cancellationToken)
    {
        // A plain mixed reset: un-stages without ever touching the working tree. With paths the pathspecs are git's own (globs and magic), as GitImpl.UnstageAsync passed them.
        List<string> args = paths.Count == 0 ? ["reset"] : ["reset", "--", .. paths];
        return GitProcess.Run(workingDirectory, args, cliTimeout, cancellationToken, IndexWriteRunOptions);
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

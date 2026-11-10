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

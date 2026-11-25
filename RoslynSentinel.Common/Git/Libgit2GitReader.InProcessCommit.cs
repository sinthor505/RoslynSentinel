namespace RoslynSentinel.Common;

/// <summary>
/// The repository-level declines of the in-process commit route (<see cref="InProcessCommitRoute"/>, step 5c-4 of proposal_libgit2sharp_in_process_git.md) that need this reader's private helpers:
/// the config that makes git sign or sign off a commit, and, for a commit of listed paths, anything that makes git store different bytes than the working-tree file holds
/// (a conversion attribute anywhere that applies, or a carriage return, which core.autocrlf / core.eol / text would convert). Read through the reader's own doors
/// (<see cref="Open"/>: process environment, worktree config; <see cref="EffectiveConfigValue"/>: every config level of a linked worktree), so it declines exactly where the other in-process operations do.
/// </summary>
internal sealed partial class Libgit2GitReader
{
    // A listed directory is expanded to its files to look for CR bytes and nested .gitattributes; past this many files the check gives up (and so declines): a spawn costs less than that walk.
    private const int MaxExpandedCommitFiles = 5000;

    /// <summary>
    /// The reason the repository containing <paramref name="workingDirectory"/> must not take the in-process commit route, or null when it may. Checked, in order: a bare repository; <c>commit.gpgsign</c> not
    /// false-like (git would sign; this route cannot); any <c>commit.signoff</c> entry (git would add a Signed-off-by line); and, when <paramref name="listedPaths"/> is not empty, for every listed path
    /// (a directory counts all the files under it): a .gitattributes, info/attributes, global or system attributes file mentioning filter, ident or working-tree-encoding (the same rule as the index write,
    /// <see cref="RequireNoConversionAttributes"/>). CR bytes are not checked here, because the listed paths are staged by git before the commit (the index write declines any CR file, so git
    /// converts line endings) and the primitive declines a working tree that still differs from the index.
    /// </summary>
    /// <exception cref="NotSupportedException">One of the reader's environment or repository declines: git answers.</exception>
    internal static string? InProcessCommitDecline(string workingDirectory, IReadOnlyList<string>? listedPaths)
    {
        using var repo = OpenForIndexWrite(workingDirectory);
        if (repo.Info.IsBare)
        {
            return "A bare repository has no index to commit.";
        }

        if (EffectiveConfigValue(repo, "commit.gpgsign") is { } sign && !IsFalseLike(sign))
        {
            return $"commit.gpgsign is '{sign}': git would sign the commit and this route cannot.";
        }

        if (EffectiveConfigHasEntryWithPrefix(repo, "commit.signoff"))
        {
            return "commit.signoff is configured: git would add a Signed-off-by line and this route does not.";
        }

        if (listedPaths is not { Count: > 0 })
        {
            return null;
        }

        var workdir = repo.Info.WorkingDirectory ?? throw Decline("The repository has no working tree.");
        var files = new List<string>();
        foreach (var path in listedPaths)
        {
            var full = Path.Combine(workdir, path);
            if (!Directory.Exists(full))
            {
                // A file, or a path that no longer exists (a deletion): the attribute rule still looks at its directories.
                files.Add(path);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(workdir, file).Replace('\\', '/');
                if (relative.StartsWith(".git/", StringComparison.Ordinal) || relative.Contains("/.git/", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(relative);
                if (files.Count > MaxExpandedCommitFiles)
                {
                    return $"A listed directory holds more than {MaxExpandedCommitFiles} files; the conversion check is left to git.";
                }
            }
        }

        try
        {
            RequireNoConversionAttributes(repo, workdir, files);
        }
        catch (NotSupportedException ex)
        {
            return ex.Message;
        }

        return null;
    }
}

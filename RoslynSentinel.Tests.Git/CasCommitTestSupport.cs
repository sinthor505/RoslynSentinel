using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>Throwaway-repository helpers shared by the compare-and-swap commit test classes (all git plumbing goes through <see cref="RoslynSentinel.Tests.Battery.Basic.GitTestRepo"/>).</summary>
internal static class CasCommitTestSupport
{
    /// <summary>A repository on branch master with a repo-local identity, no EOL conversion and (optionally) one commit holding a.txt; every Repository the code under test opens is disposed before this is deleted.</summary>
    internal static string NewRepo(string root, string name, bool withInitialCommit = true)
    {
        var directory = Path.Combine(root, name);
        InitRepo(directory);
        RunGit(directory, "symbolic-ref", "HEAD", "refs/heads/master");
        RunGit(directory, "config", "user.name", "Test User");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "core.autocrlf", "false");
        if (withInitialCommit)
        {
            Write(directory, "a.txt", "a\n");
            CommitAll(directory, "initial");
        }

        return directory;
    }

    internal static void Write(string directory, string relativePath, string content)
    {
        var full = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    internal static string Tip(string directory) => RunGit(directory, "rev-parse", "HEAD");

    internal static string Resolve(string directory, string revision) => RunGit(directory, "rev-parse", revision);

    internal static string TreeOfHead(string directory) => RunGit(directory, "rev-parse", "HEAD^{tree}");

    /// <summary>Asserts the two repositories' HEAD trees are the same object, printing both recursive listings (modes and ids) when they are not.</summary>
    internal static void AssertSameHeadTree(string ours, string twin)
    {
        var oursTree = TreeOfHead(ours);
        var twinTree = TreeOfHead(twin);
        Assert.That(
            oursTree,
            Is.EqualTo(twinTree),
            $"ours:{Environment.NewLine}{RunGit(ours, "ls-tree", "-r", "-t", "HEAD")}{Environment.NewLine}twin (git):{Environment.NewLine}{RunGit(twin, "ls-tree", "-r", "-t", "HEAD")}");
    }

    internal static int CommitCount(string directory) => int.Parse(RunGit(directory, "rev-list", "--count", "HEAD"));

    internal static string IndexLockPath(string directory) => Path.Combine(directory, ".git", "index.lock");

    /// <summary>Newest first: "reflog identity name|email|subject" per entry of the reflog of <paramref name="reference"/> (what the reflog text compares on; times differ by nature).</summary>
    internal static string[] Reflog(string directory, string reference) =>
        RunGitRaw(directory, "log", "-g", "--format=%gn|%ge|%gs", reference).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static void AssertFsckClean(string directory)
    {
        var (exitCode, stdOut, stdErr) = RunGitRaw(directory, "fsck", "--full", "--strict");
        Assert.That(exitCode, Is.Zero, $"git fsck --full --strict failed.{Environment.NewLine}{stdOut}{Environment.NewLine}{stdErr}");
    }

    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}

using NUnit.Framework;

namespace RoslynSentinel.Tests.Tools.Basic;

// Ratchet for the in-process commit route (plan_libgit2sharp_followups Step 18). Once the in-process route is default-on, a
// fixture that commits through GitImpl or the Git tool without pinning the route silently exercises the in-process route
// instead of the CLI route it was written for. This scan fails when a test source that commits does not also pin the route
// (InProcessCommitRoute.ConfigKey or the roslynsentinel.inProcessCommit key) or call InitRepo(, which pins it.
// To add an entry, key it by the repo-relative path with a reason; prefer pinning the route in the fixture.
// Known limit: this is a text heuristic. A file that calls InitRepo for one repo and `git init` for another passes.
[TestFixture]
[Category("GitCommitRoutePinRatchet")]
public class GitCommitRoutePinRatchetTests
{
    private const string CommitMarkerA = ".CommitAsync(";
    private const string CommitMarkerB = "GitOperation.commit";
    private const string PinMarkerA = "inProcessCommit";
    private const string PinMarkerB = "InProcessCommitRoute.ConfigKey";
    private const string PinMarkerC = "InitRepo(";
    private const string ThisFileName = "GitCommitRoutePinRatchetTests.cs";

    // Allowlist keyed by repo-relative path to a reason the file commits without a route pin. Expected to stay empty.
    private static readonly Dictionary<string, string> CommitRouteAllowlist = new(StringComparer.Ordinal)
    {
        // (no entries)
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    // Every *.cs under each RoslynSentinel.Tests* project directory, skipping bin, obj and Worktree folders.
    // Returned in relative-path order for stable messages.
    private static List<(string RelativePath, string Text)> ReadTestSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, string Text)>();
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            if (!Path.GetFileName(projectDir).StartsWith("RoslynSentinel.Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "Worktree"))
                {
                    continue;
                }

                result.Add((relativePath, File.ReadAllText(file)));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return result;
    }

    private static bool CommitsThroughGitImpl(string text) =>
        text.Contains(CommitMarkerA, StringComparison.Ordinal) || text.Contains(CommitMarkerB, StringComparison.Ordinal);

    private static bool PinsOrInitsRoute(string text) =>
        text.Contains(PinMarkerA, StringComparison.Ordinal)
        || text.Contains(PinMarkerB, StringComparison.Ordinal)
        || text.Contains(PinMarkerC, StringComparison.Ordinal);

    [Test]
    public void EveryTestSourceThatCommitsThroughGitImpl_PinsTheRouteOrCallsInitRepo()
    {
        var offenders = new List<string>();
        foreach (var (relativePath, text) in ReadTestSources())
        {
            if (Path.GetFileName(relativePath) == ThisFileName
                || !CommitsThroughGitImpl(text)
                || PinsOrInitsRoute(text)
                || CommitRouteAllowlist.ContainsKey(relativePath))
            {
                continue;
            }

            offenders.Add(relativePath);
        }

        Assert.That(
            offenders,
            Is.Empty,
            "Test sources that commit through GitImpl or the Git tool must pin the in-process route (InProcessCommitRoute.ConfigKey or roslynsentinel.inProcessCommit) or call InitRepo(, or be allowlisted with a reason. Offending files:\n"
            + string.Join("\n", offenders));
    }

    [Test]
    public void CommittingScan_FindsAtLeastFiveCommittingSources()
    {
        var sources = ReadTestSources();
        var committing = sources.Count(source => Path.GetFileName(source.RelativePath) != ThisFileName && CommitsThroughGitImpl(source.Text));

        Assert.That(
            committing,
            Is.GreaterThanOrEqualTo(5),
            $"Expected at least 5 committing test sources, found {committing} among {sources.Count} scanned files; the repo-root lookup may be broken.");
    }
}

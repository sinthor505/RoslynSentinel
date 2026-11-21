using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Environment variables are process-global, so both fixtures are NonParallelizable. Each test starts with the three
// RoslynSentinel variables cleared so the machine's own settings cannot influence it; TearDown restores them.
[NonParallelizable]
public class ScratchRootTests
{
    private static readonly string[] Variables =
    {
        ScratchRoot.EnvironmentVariable,
        ScratchRoot.ResultsRootEnvironmentVariable,
        RepoRootLocator.EnvironmentVariable,
    };

    private readonly Dictionary<string, string?> _saved = new();
    private string _root = string.Empty;

    [SetUp]
    public void SaveAndClearEnvironment()
    {
        foreach (string name in Variables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        _root = Path.Combine(Path.GetTempPath(), "RoslynSentinel-ScratchRootTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void RestoreEnvironmentAndDeleteTemp()
    {
        foreach (KeyValuePair<string, string?> pair in _saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        _saved.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Resolve_EnvironmentAbsolute_WinsAndTrailingSeparatorIsTrimmed()
    {
        string scratch = Path.Combine(_root, "scratch");
        Environment.SetEnvironmentVariable(ScratchRoot.EnvironmentVariable, scratch + Path.DirectorySeparatorChar);

        Assert.That(ScratchRoot.Resolve(Path.Combine(_root, "repo")), Is.EqualTo(scratch));
    }

    [Test]
    public void Resolve_EnvironmentRelative_ThrowsNamingVariableAndValue()
    {
        Environment.SetEnvironmentVariable(ScratchRoot.EnvironmentVariable, "relative-scratch");

        var ex = Assert.Throws<InvalidOperationException>(() => ScratchRoot.Resolve(Path.Combine(_root, "repo")));
        Assert.That(ex.Message, Does.Contain(ScratchRoot.EnvironmentVariable).And.Contain("relative-scratch"));
    }

    [Test]
    public void Resolve_NoEnvironment_SitsBesideTheRepo()
    {
        Assert.That(
            ScratchRoot.Resolve(Path.Combine(_root, "repo")),
            Is.EqualTo(Path.Combine(_root, ScratchRoot.DefaultFolderName)));
    }

    [Test]
    public void Resolve_EnvironmentAbsoluteAndNoRepoArgument_ReturnsEnvironmentValue()
    {
        // With the variable set, Resolve returns before RepoRootLocator is consulted, so no repo is needed.
        string scratch = Path.Combine(_root, "scratch");
        Environment.SetEnvironmentVariable(ScratchRoot.EnvironmentVariable, scratch);

        Assert.That(ScratchRoot.Resolve(), Is.EqualTo(scratch));
    }

    [Test]
    public void ResultsRoot_NoOverride_IsModelTestingResultsUnderScratch()
    {
        Assert.That(
            ScratchRoot.ResultsRoot(Path.Combine(_root, "repo")),
            Is.EqualTo(Path.Combine(_root, ScratchRoot.DefaultFolderName, "ModelTestingResults")));
    }

    [Test]
    public void ResultsRoot_ResultsOverrideAbsolute_BeatsScratchDefault()
    {
        string results = Path.Combine(_root, "results");
        Environment.SetEnvironmentVariable(ScratchRoot.ResultsRootEnvironmentVariable, results);

        Assert.That(ScratchRoot.ResultsRoot(Path.Combine(_root, "repo")), Is.EqualTo(results));
    }

    [Test]
    public void ResultsRoot_ResultsOverrideRelative_ThrowsNamingVariableAndValue()
    {
        Environment.SetEnvironmentVariable(ScratchRoot.ResultsRootEnvironmentVariable, "relative-results");

        var ex = Assert.Throws<InvalidOperationException>(() => ScratchRoot.ResultsRoot(Path.Combine(_root, "repo")));
        Assert.That(ex.Message, Does.Contain(ScratchRoot.ResultsRootEnvironmentVariable).And.Contain("relative-results"));
    }

    [Test]
    public void TestRunsRoot_NoOverride_IsTestRunsUnderScratch()
    {
        Assert.That(
            ScratchRoot.TestRunsRoot(Path.Combine(_root, "repo")),
            Is.EqualTo(Path.Combine(_root, ScratchRoot.DefaultFolderName, "TestRuns")));
    }
}

// Environment variables are process-global, so both fixtures are NonParallelizable (see ScratchRootTests).
[NonParallelizable]
public class RepoRootLocatorTests
{
    private static readonly string[] Variables =
    {
        ScratchRoot.EnvironmentVariable,
        ScratchRoot.ResultsRootEnvironmentVariable,
        RepoRootLocator.EnvironmentVariable,
    };

    private readonly Dictionary<string, string?> _saved = new();
    private string _root = string.Empty;

    [SetUp]
    public void SaveAndClearEnvironment()
    {
        foreach (string name in Variables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        _root = Path.Combine(Path.GetTempPath(), "RoslynSentinel-RepoRootLocatorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void RestoreEnvironmentAndDeleteTemp()
    {
        foreach (KeyValuePair<string, string?> pair in _saved)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        _saved.Clear();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void TryFind_NullStartAndValidEnvironment_EnvironmentWinsOverWalkUp()
    {
        string repo = MakeRepo(Path.Combine(_root, "envrepo"));
        Environment.SetEnvironmentVariable(RepoRootLocator.EnvironmentVariable, repo);

        Assert.That(RepoRootLocator.TryFind(), Is.EqualTo(Path.GetFullPath(repo)));
    }

    [Test]
    public void TryFind_StartDirectoryGiven_IgnoresEnvironmentAndReturnsNull()
    {
        // RunStampTests.ReadWorkingTreeStamp_OutsideARepo_ReturnsNull depends on this: the env var must never be consulted.
        string repo = MakeRepo(Path.Combine(_root, "envrepo"));
        string plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        Environment.SetEnvironmentVariable(RepoRootLocator.EnvironmentVariable, repo);

        Assert.That(RepoRootLocator.TryFind(plain), Is.Null);
    }

    [Test]
    public void TryFind_NullStartEnvironmentWithoutSolutionFile_FallsThroughToWalkUp()
    {
        string bogus = Path.Combine(_root, "no-slnx");
        Directory.CreateDirectory(bogus);
        Environment.SetEnvironmentVariable(RepoRootLocator.EnvironmentVariable, bogus);

        string? found = RepoRootLocator.TryFind();
        Assert.That(found, Is.Not.Null.And.Not.EqualTo(bogus));
    }

    [Test]
    public void TryFind_NullStartRelativeEnvironment_FallsThroughToWalkUp()
    {
        Environment.SetEnvironmentVariable(RepoRootLocator.EnvironmentVariable, "relative-repo");

        string? found = RepoRootLocator.TryFind();
        Assert.That(found, Is.Not.Null.And.Not.EqualTo("relative-repo"));
    }

    [Test]
    public void TryFind_NestedStartDirectory_WalksUpToTempRepo()
    {
        string repo = MakeRepo(Path.Combine(_root, "repo"));
        string nested = Path.Combine(repo, "src", "deeper");
        Directory.CreateDirectory(nested);

        Assert.That(RepoRootLocator.TryFind(nested), Is.EqualTo(Path.GetFullPath(repo)));
    }

    [Test]
    public void TryFind_NoSolutionAboveStartDirectory_ReturnsNull()
    {
        string plain = Path.Combine(_root, "plain", "nested");
        Directory.CreateDirectory(plain);

        Assert.That(RepoRootLocator.TryFind(plain), Is.Null);
    }

    [Test]
    public void Find_NoSolutionAboveStartDirectory_ThrowsDirectoryNotFoundException()
    {
        string plain = Path.Combine(_root, "plain", "nested");
        Directory.CreateDirectory(plain);

        var ex = Assert.Throws<DirectoryNotFoundException>(() => RepoRootLocator.Find(plain));
        Assert.That(ex.Message, Does.Contain(RepoRootLocator.SolutionFileName));
    }

    private static string MakeRepo(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RepoRootLocator.SolutionFileName), string.Empty);
        return folder;
    }
}

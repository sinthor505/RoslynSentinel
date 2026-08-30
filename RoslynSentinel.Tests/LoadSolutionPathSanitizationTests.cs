// LoadSolution path sanitization — agents sometimes pass solutionPath/baseRepoDir wrapped in
// stray quotes or whitespace (e.g. copied from a shell-quoted example). ResolveSolutionPath must
// strip those before checking File.Exists / combining with base directories, otherwise resolution
// fails with a ToolNotFoundException that embeds the literal quote characters in every candidate.

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class LoadSolutionPathSanitizationTests
{
    private IWorkspaceManager _workspaceManager = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task LoadSolutionAsync_RelativePathWrappedInQuotes_CandidatesHaveNoQuotes()
    {
        // Reproduces the reported bug: solutionPath arrives as "'./Samples/Foo/Foo.sln'"
        // (single quotes baked into the string itself, not a shell artifact).
        const string quotedPath = "'./Samples/DoesNotExist/DoesNotExist.sln'";

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _workspaceManager.LoadSolutionAsync(quotedPath));

        var triedCandidates = ex!.Message.Split("Tried: ")[1];
        Assert.That(triedCandidates, Does.Not.Contain("'"),
            "Resolved candidate paths must not contain a literal wrapping single quote.");
        Assert.That(triedCandidates, Does.Not.Contain("\""),
            "Resolved candidate paths must not contain a literal wrapping double quote.");
        Assert.That(ex.Message, Does.Contain("DoesNotExist.sln"),
            "The underlying filename must still appear in the error for diagnosability.");
    }

    [Test]
    public async Task LoadSolutionAsync_PathWithSurroundingWhitespace_IsTrimmedBeforeResolutionAsync()
    {
        const string paddedPath = "  ./Samples/DoesNotExist/DoesNotExist.sln  \n";

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _workspaceManager.LoadSolutionAsync(paddedPath));

        var triedCandidates = ex!.Message.Split("Tried: ")[1];
        Assert.That(triedCandidates, Does.Not.Contain("  ./").And.Not.Contain(" ./"),
            "Leading whitespace must be trimmed before the path is combined with base directories.");
    }

    [Test]
    public async Task LoadSolutionAsync_BaseRepoDirWrappedInQuotes_IsSanitizedBeforeCombineAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var quotedBaseRepoDir = $"\"{tempDir}\"";

            var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
                async () => await _workspaceManager.LoadSolutionAsync("Missing.sln", quotedBaseRepoDir));

            Assert.That(ex!.Message, Does.Contain(Path.Combine(tempDir, "Missing.sln")),
                "A quoted baseRepoDir must still be usable to build the combined candidate path.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 6): an agent fabricated a "
                 + "plausible-looking but nonexistent baseRepoDir (a macOS-style "
                 + "/Users/.../workspaces/... path on a Windows host) instead of omitting the "
                 + "argument as the tool description recommends for relative paths. The prior "
                 + "behavior silently dropped that candidate and fell through to the server-wide "
                 + "BaseRepoDirectory default, which happened to also resolve the same relative "
                 + "solutionPath — but to an unintended sibling directory, with no error raised to "
                 + "signal the mismatch. A nonexistent baseRepoDir must fail fast and say so, not "
                 + "be silently discarded.")]
    public async Task LoadSolutionAsync_BaseRepoDirDoesNotExist_ThrowsArgumentExceptionInsteadOfSilentlyFallingThroughAsync()
    {
        var nonexistentBaseRepoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_DoesNotExist_" + Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await _workspaceManager.LoadSolutionAsync("Samples/Foo/Foo.sln", nonexistentBaseRepoDir));

        Assert.That(ex!.Message, Does.Contain(nonexistentBaseRepoDir),
            "Error must name the specific nonexistent baseRepoDir that was rejected.");
        Assert.That(ex.Message, Does.Contain("omit baseRepoDir").IgnoreCase.Or.Contain("do not guess").IgnoreCase,
            "Error must steer the caller toward omitting baseRepoDir rather than guessing another value.");
    }

    [Test]
    public async Task LoadSolutionAsync_AbsolutePathWrappedInQuotes_ResolvesPastFileCheck()
    {
        // An absolute path wrapped in quotes/whitespace must still resolve to the real file
        // (i.e. the quotes must be stripped, not treated as part of the filename), so loading
        // proceeds to MSBuild instead of failing fast with ToolNotFoundException from
        // ResolveSolutionPath. The temp file has no real projects, so MSBuild itself finds
        // nothing to load — that failure is distinguishable by its message (which names the
        // resolved, unquoted path) from a path-resolution failure (which says "Tried: ...").
        var tempFile = Path.Combine(Path.GetTempPath(), $"RoslynSentinelTests_{Guid.NewGuid()}.sln");
        await File.WriteAllTextAsync(tempFile, "Microsoft Visual Studio Solution File, Format Version 12.00");
        try
        {
            var wrappedPath = $"  '{tempFile}'  ";

            var ex = await Assert.ThrowsAsync<ToolNotFoundException>(
                async () => await _workspaceManager.LoadSolutionAsync(wrappedPath));

            Assert.That(ex!.Message, Does.Contain(tempFile).And.Not.Contain("Tried: "),
                "Failure must come from MSBuild finding no projects in the resolved path, not from path resolution itself.");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task LoadSolutionAsync_RealSolutionOnDisk_LoadsWithNoWorkspaceErrorsAsync()
    {
        using var fixture = new TestSolutionFixture();

        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        Assert.That(_workspaceManager.CurrentSolution, Is.Not.Null);
        Assert.That(_workspaceManager.CurrentSolution!.Projects.Any(p => p.Name == "ContosoOrders.Core"), Is.True,
            "ContosoOrders.Core should be loaded as a project in the solution.");
        Assert.That(_workspaceManager.GetWorkspaceLoadErrors(), Is.Empty,
            "A real, well-formed solution should load without accumulating workspace errors.");
    }
}

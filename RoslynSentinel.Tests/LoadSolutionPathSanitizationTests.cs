// LoadSolution path sanitization — agents sometimes pass solutionPath/baseRepoDir wrapped in
// stray quotes or whitespace (e.g. copied from a shell-quoted example). ResolveSolutionPath must
// strip those before checking File.Exists / combining with base directories, otherwise resolution
// fails with a FileNotFoundException that embeds the literal quote characters in every candidate.

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

[TestFixture]
public class LoadSolutionPathSanitizationTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void LoadSolutionAsync_RelativePathWrappedInQuotes_CandidatesHaveNoQuotes()
    {
        // Reproduces the reported bug: solutionPath arrives as "'./Samples/Foo/Foo.sln'"
        // (single quotes baked into the string itself, not a shell artifact).
        const string quotedPath = "'./Samples/DoesNotExist/DoesNotExist.sln'";

        var ex = Assert.ThrowsAsync<FileNotFoundException>(
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
    public void LoadSolutionAsync_PathWithSurroundingWhitespace_IsTrimmedBeforeResolution()
    {
        const string paddedPath = "  ./Samples/DoesNotExist/DoesNotExist.sln  \n";

        var ex = Assert.ThrowsAsync<FileNotFoundException>(
            async () => await _workspaceManager.LoadSolutionAsync(paddedPath));

        var triedCandidates = ex!.Message.Split("Tried: ")[1];
        Assert.That(triedCandidates, Does.Not.Contain("  ./").And.Not.Contain(" ./"),
            "Leading whitespace must be trimmed before the path is combined with base directories.");
    }

    [Test]
    public void LoadSolutionAsync_BaseRepoDirWrappedInQuotes_IsSanitizedBeforeCombine()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelTests_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var quotedBaseRepoDir = $"\"{tempDir}\"";

            var ex = Assert.ThrowsAsync<FileNotFoundException>(
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
    public async Task LoadSolutionAsync_AbsolutePathWrappedInQuotes_ResolvesPastFileCheck()
    {
        // An absolute path wrapped in quotes/whitespace must still resolve to the real file
        // (i.e. the quotes must be stripped, not treated as part of the filename), so loading
        // proceeds to MSBuild instead of failing fast with FileNotFoundException.
        var tempFile = Path.Combine(Path.GetTempPath(), $"RoslynSentinelTests_{Guid.NewGuid()}.sln");
        File.WriteAllText(tempFile, "Microsoft Visual Studio Solution File, Format Version 12.00");
        try
        {
            var wrappedPath = $"  '{tempFile}'  ";

            // Must not throw FileNotFoundException from ResolveSolutionPath; any failure past
            // that point (e.g. MSBuild parse errors) is swallowed internally by LoadSolutionAsync.
            Assert.DoesNotThrowAsync(async () => await _workspaceManager.LoadSolutionAsync(wrappedPath));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

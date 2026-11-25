using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="GitStatusRenameHints.ReadMovePairs"/> over <c>move_file</c> blobs that move several files (plan Step 19, rows T19.1-T19.4).
/// Each blob is written with <see cref="OperationBlobWriter.BuildApplyItems"/> and <see cref="OperationBlobWriter.WriteAsync"/>, the item builder and
/// writer a MoveFile apply uses, so the destination-to-source pairing that BuildApplyItems does is exercised for real. A full MoveFile call needs a
/// loaded workspace (TestSolutionFixture), which RoslynSentinel.Tests.Git does not reference, so the apply result is assembled here instead.
/// </summary>
// Spawns git to create the repo root the blobs live under; excluded from RunTest suite "fast" (the default), run with suite "full".
[TestFixture]
[Category("OutOfProcess")]
public class GitStatusRenameHintMultiPairTests
{
    private const string Text1 = "namespace Demo;\n\npublic class One\n{\n    public int Value => 1;\n}\n";
    private const string Text2 = "namespace Demo;\n\npublic class Two\n{\n    public int Value => 2;\n}\n";
    private const string Text3 = "namespace Demo;\n\npublic class Three\n{\n    public int Value => 3;\n}\n";

    private string _repoDir = "";

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitRenameHintMultiPair_" + Guid.NewGuid().ToString("N"));
        InitRepo(_repoDir);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        if (!Directory.Exists(_repoDir))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repoDir, recursive: true);
    }

    private string Absolute(string repoRelativePath) =>
        Path.Combine(_repoDir, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));

    // One move_file blob holding every move in the call, written the way a MoveFile apply writes it: each source is a deleted item
    // (pre-image text, no post-image), each destination a created item (post-image text). BuildApplyItems then links each destination to its source.
    private async Task WriteMoveBlobAsync(string changeId, params (string Source, string Destination, string Text)[] moves)
    {
        var succeeded = new List<string>();
        var preImages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var postImages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        foreach (var (source, destination, text) in moves)
        {
            var sourcePath = Absolute(source);
            var destinationPath = Absolute(destination);
            succeeded.Add(sourcePath);
            preImages[sourcePath] = text;
            succeeded.Add(destinationPath);
            created.Add(destinationPath);
            postImages[destinationPath] = text;
        }

        var result = new ApplyChangesResult(
            IsError: false,
            SucceededFiles: succeeded,
            FailedFiles: new(),
            Summary: "moved",
            PreImages: preImages,
            PostImages: postImages,
            CreatedFiles: created);
        var items = OperationBlobWriter.BuildApplyItems(result);
        var written = await OperationBlobWriter.WriteAsync("move_file", changeId, items, _repoDir);
        Assert.That(written.Written, Is.True, written.Diagnostic);
    }

    [Test]
    public async Task MultiPairBlob_YieldsOneDefinitiveHintPerPairAsync()
    {
        await WriteMoveBlobAsync(
            "aaaa1111",
            ("src/One.cs", "moved/One.cs", Text1),
            ("src/Two.cs", "moved/Two.cs", Text2),
            ("src/Three.cs", "moved/Three.cs", Text3));

        var pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir);

        Assert.That(pairs, Has.Count.EqualTo(3), "A 3-pair MoveFile blob must yield one pair per move.");
        Assert.That(
            pairs.Select(p => (p.Source, p.Destination)),
            Is.EquivalentTo(new[]
            {
                ("src/One.cs", "moved/One.cs"),
                ("src/Two.cs", "moved/Two.cs"),
                ("src/Three.cs", "moved/Three.cs"),
            }));
        Assert.That(pairs.All(p => p.ChangeId == "aaaa1111"), Is.True, "Every pair must carry the blob's change id.");
    }

    [Test]
    public async Task MultiPairBlob_TwoSourcesWithIdenticalText_FallsBackToTier2ForThoseOnlyAsync()
    {
        // Same1 and Same2 share Text1, so their pairing is ambiguous and must not be returned; Unique is unambiguous and must be.
        await WriteMoveBlobAsync(
            "bbbb2222",
            ("src/Same1.cs", "moved/Same1.cs", Text1),
            ("src/Same2.cs", "moved/Same2.cs", Text1),
            ("src/Unique.cs", "moved/Unique.cs", Text2));

        var pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir);

        Assert.That(pairs, Has.Count.EqualTo(1), "Only the unambiguous pair may come back; the identical-text pairs are left to the tier-2 hint.");
        Assert.That(pairs[0].Source, Is.EqualTo("src/Unique.cs"));
        Assert.That(pairs[0].Destination, Is.EqualTo("moved/Unique.cs"));
    }

    [Test]
    public async Task ChainAcrossBlobs_AToBInOneBlob_BToCInAnother_YieldsJoinedPairAsync()
    {
        await WriteMoveBlobAsync(
            "aaaa1111",
            ("src/Old.cs", "moved/New.cs", Text1),
            ("src/Other.cs", "moved/Other.cs", Text2));
        await WriteMoveBlobAsync("cccc3333", ("moved/New.cs", "final/Last.cs", Text1));

        var pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir);

        Assert.That(pairs.Any(p => p.Source == "src/Old.cs" && p.Destination == "moved/New.cs"), Is.True, "The A to B pair from the first blob must be returned.");
        Assert.That(pairs.Any(p => p.Source == "moved/New.cs" && p.Destination == "final/Last.cs"), Is.True, "The B to C pair from the second blob must be returned.");
        Assert.That(pairs.Any(p => p.Source == "src/Old.cs" && p.Destination == "final/Last.cs"), Is.True, "A to B in one blob and B to C in another must join into A to C.");
    }

    [Test]
    public async Task MoveCycle_DoesNotHangAsync()
    {
        // No [Timeout] here: the repo does not use NUnit's TimeoutAttribute anywhere, so a regression that hangs will hang the run.
        await WriteMoveBlobAsync(
            "dddd4444",
            ("src/A.cs", "moved/B.cs", Text1),
            ("src/C.cs", "moved/D.cs", Text2));
        await WriteMoveBlobAsync(
            "eeee5555",
            ("moved/B.cs", "src/A.cs", Text1),
            ("moved/D.cs", "src/E.cs", Text2));

        IReadOnlyList<MovePair>? pairs = null;
        Assert.That(
            () => { pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir); },
            Throws.Nothing,
            "A multi-pair move cycle across blobs must return, not loop.");

        Assert.That(pairs, Is.Not.Null);
        Assert.That(pairs!.Where(p => p.Source == p.Destination), Is.Empty, "A move cycle must not produce a pair whose source equals its destination.");
        Assert.That(pairs.Any(p => p.Source == "src/C.cs" && p.Destination == "moved/D.cs"), Is.True, "An unrelated pair in the same blob as the cycle must still be returned.");
    }
}

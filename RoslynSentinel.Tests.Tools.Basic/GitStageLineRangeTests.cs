using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <c>GitImpl.StageHunksAsync</c> with a lineRange against a real temporary repository: a range may cut a hunk made purely of added
/// lines (two sessions appending to one file form ONE zero-context hunk), the staged bytes stay byte-exact (CRLF), a range that
/// partly covers a hunk with removed lines is refused with facts and options, and a change to the file's index entry between reading
/// the diff and applying the patch is refused as GitHunkStale. The race is made deterministic with the internal
/// <c>afterHunkDiffLoaded</c> constructor argument of <see cref="GitImpl"/>.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitStageLineRangeTests
{
    private const string Rel = "f.txt";

    private string _root = null!;
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-stage-lr-" + Guid.NewGuid().ToString("N")[..12]);
        _dir = Path.Combine(_root, "repo");
        InitRepo(_dir);
        RunGit(_dir, "config", "core.autocrlf", "false");
        RunGit(_dir, "config", "user.name", "Test");
        RunGit(_dir, "config", "user.email", "test@example.com");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static string Lines(string prefix, int from, int to, string eol) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(n => prefix + n + eol));

    private string FilePath => Path.Combine(_dir, Rel);

    // Commits ten base lines, then appends block A (3 lines, new-file lines 11-13) and block B (2 lines, 14-15) to the working tree.
    private (string Base, string BlockA, string BlockB) ArrangeAppendedBlocks(string eol)
    {
        var baseText = Lines("line ", 1, 10, eol);
        File.WriteAllText(FilePath, baseText);
        CommitAll(_dir, "base");
        var blockA = Lines("A", 1, 3, eol);
        var blockB = Lines("B", 1, 2, eol);
        File.WriteAllText(FilePath, baseText + blockA + blockB);
        return (baseText, blockA, blockB);
    }

    private static GitImpl NewImpl(Action<string>? afterHunkDiffLoaded = null) =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, GitReaders.Default, afterHunkDiffLoaded: afterHunkDiffLoaded);

    private Task<GitStageHunksResult> StageRangeAsync(GitImpl impl, string lineRange) =>
        impl.StageHunksAsync(_dir, Rel, hunkIds: null, hunkFingerprint: null, lineRange, CancellationToken.None);

    // The staged blob; ASCII content only, so decoding keeps every byte including CR.
    private string IndexText() => RunGitRaw(_dir, "show", ":" + Rel).StdOut;

    private string UnstagedDiff() => RunGitRaw(_dir, "diff", "-U0", "--no-color", "--", Rel).StdOut;

    // ---- partial hunks ----

    [Test]
    public async Task LineRange_SecondAppendedBlock_StagesOnlyThatBlock_ThenTheFirstBlockCompletesTheFileAsync()
    {
        var (baseText, blockA, blockB) = ArrangeAppendedBlocks("\n");
        var working = baseText + blockA + blockB;
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "14-15");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexText(), Is.EqualTo(baseText + blockB), "only block B is staged.");
        Assert.That(File.ReadAllText(FilePath), Is.EqualTo(working), "the working tree is never touched.");
        Assert.That(result.StagedHunks, Has.Count.EqualTo(1));
        Assert.That((result.StagedHunks[0].Index, result.StagedHunks[0].NewStart, result.StagedHunks[0].NewCount), Is.EqualTo((1, 14, 2)));
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(1));
        Assert.That(result.Note, Does.Contain("only in part").And.Contain("lines 14-15").And.Contain("stay unstaged"));
        Assert.That(result.Warning, Is.Null.Or.Empty, "a partial stage is a note, not a degradation.");
        var unstaged = UnstagedDiff();
        Assert.That(unstaged, Does.Contain("@@ -10,0 +11,3 @@"), "block A is still an unstaged hunk at its own lines.");
        Assert.That(unstaged, Does.Contain("+A1").And.Contain("+A3").And.Not.Contain("+B1"));

        var second = await StageRangeAsync(impl, "11-13");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working), "staging block A completes the file.");
        Assert.That(second.RemainingUnstagedHunks, Is.EqualTo(0));
        Assert.That(second.Warning, Is.Null.Or.Empty, "the remaining hunk was taken whole.");
        Assert.That(UnstagedDiff(), Is.Empty);
    }

    [Test]
    public async Task LineRange_CrlfFile_StagesBlockBytesExactlyWithoutIntroducingLfOnlyLinesAsync()
    {
        var (baseText, blockA, blockB) = ArrangeAppendedBlocks("\r\n");
        var working = baseText + blockA + blockB;
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "14-15");

        Assert.That(result.IsError, Is.False, result.Error);
        var index = IndexText();
        Assert.That(index, Is.EqualTo(baseText + blockB), "the staged bytes are the base plus block B, CRLF included.");
        Assert.That(Regex.IsMatch(index, "(?<!\r)\n"), Is.False, "no LF-only line may appear in the staged blob.");

        var second = await StageRangeAsync(impl, "11-13");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working), "after both blocks the staged bytes equal the working-tree bytes.");
        Assert.That(File.ReadAllText(FilePath), Is.EqualTo(working));
    }

    [Test]
    public async Task LineRange_PartOfAMixedHunk_IsRefusedWithFactsAndOptions_AndStagesNothingAsync()
    {
        var baseText = Lines("line ", 1, 10, "\n");
        File.WriteAllText(FilePath, baseText);
        CommitAll(_dir, "base");
        var lines = Enumerable.Range(1, 10).Select(n => "line " + n).ToList();
        lines.RemoveRange(3, 2); // replace lines 4-5 with three new lines: one hunk "-4,2 +4,3"
        lines.InsertRange(3, ["new 4", "new 5", "new 6"]);
        File.WriteAllText(FilePath, string.Join('\n', lines) + "\n");
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "5-6");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorKind, Is.EqualTo("GitHunkSelection"));
        Assert.That(result.Error, Does.Contain("Hunk 1 spans lines 4-6 but only partly lies in 5-6"));
        Assert.That(result.Error, Does.Contain("purely of added lines"));
        Assert.That(result.Error, Does.Contain("Widen the range to 4-6"));
        Assert.That(result.Error, Does.Contain("hunkIds").And.Contain("stage the whole file"));
        Assert.That(result.Error, Does.Not.Contain("must").And.Not.Contain("do not").IgnoreCase, "error text carries facts and options, not policy.");
        Assert.That(result.ErrorDetail, Is.Not.Empty);
        Assert.That(RunGit(_dir, "diff", "--cached", "--name-only"), Is.Empty, "a refused selection stages nothing.");
    }

    // ---- concurrent index change ----

    [Test]
    public async Task LineRange_IndexEntryChangedAfterTheDiffWasRead_IsRefusedAsHunkStale_AndAppliesNothingAsync()
    {
        var (baseText, blockA, blockB) = ArrangeAppendedBlocks("\n");
        var working = baseText + blockA + blockB;
        var seamCalls = 0;
        var impl = NewImpl(root =>
        {
            seamCalls++;
            RunGit(root, "add", "--", Rel); // another session stages the whole file at exactly this moment
        });

        var result = await StageRangeAsync(impl, "14-15");

        Assert.That(seamCalls, Is.EqualTo(1));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorKind, Is.EqualTo("GitHunkStale"));
        Assert.That(result.Error, Does.Contain("changed between reading the diff and applying it"));
        Assert.That(result.Error, Does.Contain("(was ").And.Contain(", now "));
        Assert.That(result.Error, Does.Contain("Nothing was staged"));
        Assert.That(result.ErrorDetail, Is.Not.Empty);
        Assert.That(IndexText(), Is.EqualTo(working), "the index is exactly what the other session staged; no patch was applied on top.");
    }

    [Test]
    public async Task HunkIds_IndexEntryChangedAfterTheDiffWasRead_IsRefusedAsHunkStaleAsync()
    {
        var (_, blockA, blockB) = ArrangeAppendedBlocks("\n");
        var listing = await NewImpl().HunksAsync(_dir, Rel, 10, CancellationToken.None);
        Assert.That(listing.IsError, Is.False, listing.Error);
        var impl = NewImpl(root => RunGit(root, "add", "--", Rel));

        var result = await impl.StageHunksAsync(_dir, Rel, hunkIds: "1", hunkFingerprint: listing.Fingerprint, lineRange: null, CancellationToken.None);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorKind, Is.EqualTo("GitHunkStale"));
        Assert.That(result.Error, Does.Contain("changed between reading the diff and applying it"));
        Assert.That(IndexText(), Is.EqualTo(Lines("line ", 1, 10, "\n") + blockA + blockB));
    }

    [Test]
    public async Task LineRange_WholeHunkRange_StillStagesThatHunkWithoutAPartialNoteAsync()
    {
        var (baseText, blockA, blockB) = ArrangeAppendedBlocks("\n");

        var result = await StageRangeAsync(NewImpl(), "1-100");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexText(), Is.EqualTo(baseText + blockA + blockB));
        Assert.That(result.Warning, Is.Null.Or.Empty);
        Assert.That(result.Note, Is.Null.Or.Empty);
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(0));
    }
}

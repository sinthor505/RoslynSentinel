using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <c>GitImpl.StageHunksAsync</c> with a lineRange against a real temporary repository: a range may cut a hunk made purely of added
/// lines (two sessions appending to one file form ONE zero-context hunk) or a mixed hunk of removed then added lines (the removed
/// lines go in only when the range covers the first added line), the staged bytes stay byte-exact (CRLF), a cut that cannot be made
/// safely is refused with facts and options, and a change to the file's index entry between reading
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

    // ---- mixed hunks (removed lines followed by added lines) ----

    private static string Text(string eol, IEnumerable<string> lines) => string.Concat(lines.Select(l => l + eol));

    private static IEnumerable<string> Numbered(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => "line " + n);

    // Commits `baseText`, then leaves `workingText` in the working tree.
    private void ArrangeBaseAndWorking(string baseText, string workingText)
    {
        File.WriteAllText(FilePath, baseText);
        CommitAll(_dir, "base");
        File.WriteAllText(FilePath, workingText);
    }

    // Ten base lines; lines 4-5 are replaced by "new 4", "new 5", "new 6": one zero-context hunk "-4,2 +4,3" (new-file lines 4-6).
    private (string Working, string Base) ArrangeReplacedLines(string eol)
    {
        var baseText = Text(eol, Numbered(1, 10));
        var working = Text(eol, Numbered(1, 3).Concat(["new 4", "new 5", "new 6"]).Concat(Numbered(6, 10)));
        ArrangeBaseAndWorking(baseText, working);
        return (working, baseText);
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public async Task LineRange_MixedHunk_RangeCoveringFirstAddedLine_StagesRemovedLinesWithThePrefix_ThenTheRestCompletesTheFileAsync(string eol)
    {
        var (working, _) = ArrangeReplacedLines(eol);
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "4-5");

        Assert.That(result.IsError, Is.False, result.Error);
        var expectedIndex = Text(eol, Numbered(1, 3).Concat(["new 4", "new 5"]).Concat(Numbered(6, 10)));
        Assert.That(IndexText(), Is.EqualTo(expectedIndex), "lines 4-5 are removed and the first two added lines staged; the third added line is not.");
        Assert.That(File.ReadAllText(FilePath), Is.EqualTo(working), "the working tree is never touched.");
        Assert.That(result.StagedHunks, Has.Count.EqualTo(1));
        Assert.That((result.StagedHunks[0].Index, result.StagedHunks[0].NewStart, result.StagedHunks[0].NewCount), Is.EqualTo((1, 4, 2)));
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(1));
        Assert.That(result.Note, Does.Contain("only in part").And.Contain("lines 4-5").And.Contain("plus its 2 removed lines").And.Contain("stay unstaged"));
        Assert.That(result.Warning, Is.Null.Or.Empty, "a partial stage is a note, not a degradation.");
        Assert.That(UnstagedDiff(), Does.Contain("@@ -5,0 +6 @@").And.Contain("+new 6"));

        var second = await StageRangeAsync(impl, "6");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working), "staging the remaining added line completes the file.");
        Assert.That(second.RemainingUnstagedHunks, Is.EqualTo(0));
        Assert.That(second.Warning, Is.Null.Or.Empty);
        Assert.That(UnstagedDiff(), Is.Empty);
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public async Task LineRange_MixedHunk_RangeMissingFirstAddedLine_KeepsRemovedLinesUnstaged_ThenTheHeadCompletesTheFileAsync(string eol)
    {
        var (working, _) = ArrangeReplacedLines(eol);
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "5-6");

        Assert.That(result.IsError, Is.False, result.Error);
        var expectedIndex = Text(eol, Numbered(1, 5).Concat(["new 5", "new 6"]).Concat(Numbered(6, 10)));
        Assert.That(IndexText(), Is.EqualTo(expectedIndex), "the removed lines stay in the index; the two added lines are inserted after old line 5.");
        Assert.That(File.ReadAllText(FilePath), Is.EqualTo(working), "the working tree is never touched.");
        Assert.That((result.StagedHunks[0].Index, result.StagedHunks[0].NewStart, result.StagedHunks[0].NewCount), Is.EqualTo((1, 5, 2)));
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(1));
        Assert.That(result.Note, Does.Contain("only in part").And.Contain("lines 5-6").And.Contain("stay unstaged").And.Contain("including its 2 removed lines"));
        Assert.That(result.Warning, Is.Null.Or.Empty, "a partial stage is a note, not a degradation.");
        Assert.That(UnstagedDiff(), Does.Contain("@@ -4,2 +4 @@").And.Contain("-line 4").And.Contain("+new 4"));

        var second = await StageRangeAsync(impl, "4");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working), "staging the remaining head hunk completes the file.");
        Assert.That(second.RemainingUnstagedHunks, Is.EqualTo(0));
        Assert.That(second.Warning, Is.Null.Or.Empty);
        Assert.That(UnstagedDiff(), Is.Empty);
    }

    [Test]
    public async Task LineRange_MixedHunk_OldSideLastLineWithoutNewline_RemovedLinesGoWithThePrefix_ThenTheRestCompletesTheFileAsync()
    {
        // The base file's last line has no newline; lines 9-10 are replaced by three lines and a final newline.
        var baseText = Text("\n", Numbered(1, 9)) + "line 10";
        var working = Text("\n", Numbered(1, 8).Concat(["new 9", "new 10", "new 11"]));
        ArrangeBaseAndWorking(baseText, working);
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "9-10");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexText(), Is.EqualTo(Text("\n", Numbered(1, 8).Concat(["new 9", "new 10"]))));
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(1));
        Assert.That(result.Warning, Is.Null.Or.Empty);

        var second = await StageRangeAsync(impl, "11");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working));
        Assert.That(second.RemainingUnstagedHunks, Is.EqualTo(0));
        Assert.That(UnstagedDiff(), Is.Empty);
    }

    [Test]
    public async Task LineRange_MixedHunk_NewSideLastLineWithoutNewline_MarkerGoesWithTheLastLine_ThenTheHeadCompletesTheFileAsync()
    {
        // The working file's last line has no newline; lines 9-10 are replaced by three lines.
        var baseText = Text("\n", Numbered(1, 10));
        var working = Text("\n", Numbered(1, 8).Concat(["new 9", "new 10"])) + "new 11";
        ArrangeBaseAndWorking(baseText, working);
        var impl = NewImpl();

        var result = await StageRangeAsync(impl, "10-11");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(IndexText(), Is.EqualTo(baseText + "new 10\nnew 11"), "the inserted run ends the file without a newline, like the working tree.");
        Assert.That(result.RemainingUnstagedHunks, Is.EqualTo(1));
        Assert.That(result.Warning, Is.Null.Or.Empty);

        var second = await StageRangeAsync(impl, "9");

        Assert.That(second.IsError, Is.False, second.Error);
        Assert.That(IndexText(), Is.EqualTo(working));
        Assert.That(second.RemainingUnstagedHunks, Is.EqualTo(0));
        Assert.That(UnstagedDiff(), Is.Empty);
    }

    [Test]
    public async Task LineRange_MixedHunk_CutThatWouldGlueTextOntoAnUnterminatedOldLine_IsRefusedWithFactsAndOptions_AndStagesNothingAsync()
    {
        var baseText = Text("\n", Numbered(1, 9)) + "line 10";
        var working = Text("\n", Numbered(1, 8).Concat(["new 9", "new 10", "new 11"]));
        ArrangeBaseAndWorking(baseText, working);
        var impl = NewImpl();

        // The range misses the first added line, so old line 10 (no newline) would stay and "new 10" would be appended to it.
        var result = await StageRangeAsync(impl, "10-11");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorKind, Is.EqualTo("GitHunkSelection"));
        Assert.That(result.Error, Does.Contain("Hunk 1 spans lines 9-11 but only partly lies in 10-11"));
        Assert.That(result.Error, Does.Contain("cannot be cut at that range"));
        Assert.That(result.Error, Does.Contain("Widen the range to 9-11"));
        Assert.That(result.Error, Does.Contain("hunkIds").And.Contain("stage the whole file"));
        Assert.That(result.Error, Does.Not.Contain("must").And.Not.Contain("do not").IgnoreCase, "error text carries facts and options, not policy.");
        Assert.That(result.Hints, Has.Some.Matches<GitHint>(hint => hint.Key == GitHintKey.HunkSelection), "the engine attaches the HunkSelection hint; the tool side renders it into ErrorDetail.");
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

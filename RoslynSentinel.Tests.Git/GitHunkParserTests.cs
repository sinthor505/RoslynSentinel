using System.Text;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("GitHunkParser")]
public class GitHunkParserTests
{
    private const string FileHeader =
        "diff --git a/f.txt b/f.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n";

    private const string TwoHunks =
        FileHeader +
        "@@ -3 +3 @@ void Foo()\n" +
        "-old3\n" +
        "+new3\n" +
        "@@ -10,0 +11,3 @@\n" +
        "+a\n" +
        "+b\n" +
        "+c\n";

    private static string Latin1OfUtf8(string s) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(s));

    // ---- Parse: hunks -------------------------------------------------------------------------

    [Test]
    public void Parse_TwoHunks_ReturnsCountsStartsHeaderAndRawText()
    {
        var diff = GitHunkParser.Parse(TwoHunks);

        Assert.That(diff.Hunks, Has.Count.EqualTo(2));
        Assert.That(diff.IsBinary, Is.False);
        Assert.That(diff.IsNewFile, Is.False);
        Assert.That(diff.IsDeletedFile, Is.False);
        Assert.That(diff.IsRenameOrModeOnly, Is.False);
        Assert.That(diff.HeaderText, Is.EqualTo(FileHeader));

        var h1 = diff.Hunks[0];
        Assert.That(h1.Index, Is.EqualTo(1));
        Assert.That((h1.OldStart, h1.OldCount, h1.NewStart, h1.NewCount), Is.EqualTo((3, 1, 3, 1)), "omitted counts mean 1");
        Assert.That(h1.Header, Is.EqualTo("void Foo()"));
        Assert.That(h1.RawText, Is.EqualTo("@@ -3 +3 @@ void Foo()\n-old3\n+new3\n"));

        var h2 = diff.Hunks[1];
        Assert.That(h2.Index, Is.EqualTo(2));
        Assert.That((h2.OldStart, h2.OldCount, h2.NewStart, h2.NewCount), Is.EqualTo((10, 0, 11, 3)));
        Assert.That(h2.Header, Is.Empty);
        Assert.That(h2.RawText, Is.EqualTo("@@ -10,0 +11,3 @@\n+a\n+b\n+c\n"));
    }

    [Test]
    public void Parse_PureAddAndPureDelete_KeepZeroCounts()
    {
        var text = FileHeader +
                   "@@ -10,0 +11,3 @@\n+a\n+b\n+c\n" +
                   "@@ -10,2 +9,0 @@\n-x\n-y\n";

        var diff = GitHunkParser.Parse(text);

        Assert.That(diff.Hunks, Has.Count.EqualTo(2));
        Assert.That((diff.Hunks[0].OldStart, diff.Hunks[0].OldCount, diff.Hunks[0].NewStart, diff.Hunks[0].NewCount),
            Is.EqualTo((10, 0, 11, 3)));
        Assert.That((diff.Hunks[1].OldStart, diff.Hunks[1].OldCount, diff.Hunks[1].NewStart, diff.Hunks[1].NewCount),
            Is.EqualTo((10, 2, 9, 0)));
    }

    [Test]
    public void Parse_NoNewlineMarker_StaysInsideItsHunk()
    {
        var text = FileHeader +
                   "@@ -5 +5 @@\n-last\n\\ No newline at end of file\n+last2\n\\ No newline at end of file\n" +
                   "@@ -9 +9 @@\n-z\n+y\n";

        var diff = GitHunkParser.Parse(text);

        Assert.That(diff.Hunks, Has.Count.EqualTo(2));
        Assert.That(diff.Hunks[0].RawText,
            Is.EqualTo("@@ -5 +5 @@\n-last\n\\ No newline at end of file\n+last2\n\\ No newline at end of file\n"));
        Assert.That(diff.Hunks[1].RawText, Is.EqualTo("@@ -9 +9 @@\n-z\n+y\n"));
    }

    [Test]
    public void Parse_CrlfBodies_KeepCarriageReturnsInRawText()
    {
        var text = FileHeader +
                   "@@ -2 +2 @@\n-old\r\n+new\r\n" +
                   "@@ -8,0 +9,1 @@\n+added\r\n";

        var diff = GitHunkParser.Parse(text);

        Assert.That(diff.Hunks, Has.Count.EqualTo(2));
        Assert.That(diff.Hunks[0].RawText, Is.EqualTo("@@ -2 +2 @@\n-old\r\n+new\r\n"));
        Assert.That(diff.Hunks[1].RawText, Is.EqualTo("@@ -8,0 +9,1 @@\n+added\r\n"));
    }

    [Test]
    public void Parse_HunkHeaderLineWithCarriageReturnInContext_StillParses()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -4 +4 @@ int Main()\r\n-a\n+b\n");

        var h = diff.Hunks.Single();
        Assert.That(h.NewStart, Is.EqualTo(4));
        Assert.That(h.Header, Is.EqualTo("int Main()"));
        Assert.That(h.RawText, Is.EqualTo("@@ -4 +4 @@ int Main()\r\n-a\n+b\n"));
    }

    // ---- BuildPatch ---------------------------------------------------------------------------

    [Test]
    public void BuildPatch_CrlfInput_IsByteIdenticalToSelectedSliceOfInput()
    {
        var hunk1 = "@@ -2 +2 @@\n-old\r\n+new\r\n";
        var hunk2 = "@@ -8,0 +9,1 @@\n+added\r\n";
        var hunk3 = "@@ -20 +21 @@\n-x\r\n+y\r\n";
        var input = FileHeader + hunk1 + hunk2 + hunk3;
        var diff = GitHunkParser.Parse(input);

        var patch = GitHunkParser.BuildPatch(diff, [3, 1]);

        // Ascending order. Hunk 2 is unselected and hunk 1 nets 0, so hunk 3 starts at 20, not the full-file 21.
        var expected = FileHeader + hunk1 + "@@ -20 +20 @@\n-x\r\n+y\r\n"; // header keeps its bare LF, as in the input
        Assert.That(Encoding.Latin1.GetBytes(patch), Is.EqualTo(Encoding.Latin1.GetBytes(expected)));
    }

    [Test]
    public void BuildPatch_AllHunks_RoundTripsInput()
    {
        var diff = GitHunkParser.Parse(TwoHunks);

        Assert.That(GitHunkParser.BuildPatch(diff, [1, 2]), Is.EqualTo(TwoHunks));
    }

    [Test]
    public void BuildPatch_DuplicateIndexes_AreCollapsed()
    {
        var diff = GitHunkParser.Parse(TwoHunks);

        Assert.That(GitHunkParser.BuildPatch(diff, [2, 2]), Is.EqualTo(FileHeader + diff.Hunks[1].RawText));
    }

    [Test]
    public void BuildPatch_IndexOutOfRange_Throws()
    {
        var diff = GitHunkParser.Parse(TwoHunks);

        Assert.Throws<ArgumentOutOfRangeException>(() => GitHunkParser.BuildPatch(diff, [3]));
    }

    [Test]
    public void BuildPatch_SelectedSubset_RewritesNewStartOnlyForPureHunks()
    {
        // Hunk 1 adds two net lines. With hunk 1 unselected, hunks 2 and 3 must not carry its shift.
        var text = FileHeader +
                   "@@ -3 +3,3 @@\n-old3\n+new3a\n+new3b\n+new3c\n" +
                   "@@ -10,0 +12,1 @@\n+ins\n" +
                   "@@ -20,2 +23,0 @@\n-x\n-y\n";
        var diff = GitHunkParser.Parse(text);

        var patch = GitHunkParser.BuildPatch(diff, [2, 3]);

        var expected = FileHeader +
                       "@@ -10,0 +11,1 @@\n+ins\n" +
                       "@@ -20,2 +20,0 @@\n-x\n-y\n";
        Assert.That(patch, Is.EqualTo(expected));
    }

    // ---- Fingerprint --------------------------------------------------------------------------

    [Test]
    public void Fingerprint_IsStable_TwelveLowerHexChars_AndChangesWithOneByte()
    {
        var a = GitHunkParser.Parse(TwoHunks).Fingerprint;
        var b = GitHunkParser.Parse(TwoHunks).Fingerprint;
        var changed = GitHunkParser.Parse(TwoHunks.Replace("+c\n", "+d\n", StringComparison.Ordinal)).Fingerprint;

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Does.Match("^[0-9a-f]{12}$"));
        Assert.That(changed, Is.Not.EqualTo(a));
    }

    [Test]
    public void Fingerprint_CoversEntireInput_NotJustHunks()
    {
        var a = GitHunkParser.Parse(TwoHunks).Fingerprint;
        var headerChanged = GitHunkParser.Parse(TwoHunks.Replace("1111111", "1111112", StringComparison.Ordinal)).Fingerprint;

        Assert.That(headerChanged, Is.Not.EqualTo(a));
    }

    [Test]
    public void Fingerprint_IsSha256OfLatin1BytesTruncated()
    {
        var text = FileHeader + "@@ -1 +1 @@\n-" + Latin1OfUtf8("café") + "\n+x\n";
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.Latin1.GetBytes(text)))
            .ToLowerInvariant()[..12];

        Assert.That(GitHunkParser.Parse(text).Fingerprint, Is.EqualTo(expected));
    }

    // ---- File flags ---------------------------------------------------------------------------

    [Test]
    public void Parse_BinaryFilesLine_SetsIsBinary()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/b.bin b/b.bin\nindex 1..2 100644\nBinary files a/b.bin and b/b.bin differ\n");

        Assert.That(diff.IsBinary, Is.True);
        Assert.That(diff.Hunks, Is.Empty);
        Assert.That(diff.IsNewFile, Is.False);
    }

    [Test]
    public void Parse_GitBinaryPatch_SetsIsBinary()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/b.bin b/b.bin\nindex 1..2 100644\nGIT binary patch\nliteral 3\nKcmZQ\n\n");

        Assert.That(diff.IsBinary, Is.True);
    }

    [Test]
    public void Parse_NewFile_SetsIsNewFileOnly()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..abc1234\n--- /dev/null\n+++ b/n.txt\n" +
            "@@ -0,0 +1,2 @@\n+a\n+b\n");

        Assert.That(diff.IsNewFile, Is.True);
        Assert.That(diff.IsDeletedFile, Is.False);
        Assert.That(diff.IsBinary, Is.False);
        Assert.That(diff.IsRenameOrModeOnly, Is.False);
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_DeletedFile_SetsIsDeletedFileOnly()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/d.txt b/d.txt\ndeleted file mode 100644\nindex abc1234..0000000\n--- a/d.txt\n+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n-a\n-b\n");

        Assert.That(diff.IsDeletedFile, Is.True);
        Assert.That(diff.IsNewFile, Is.False);
        Assert.That(diff.IsBinary, Is.False);
        Assert.That(diff.IsRenameOrModeOnly, Is.False);
    }

    [Test]
    public void Parse_RenameOnly_SetsIsRenameOrModeOnly()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/a.txt b/b.txt\nsimilarity index 100%\nrename from a.txt\nrename to b.txt\n");

        Assert.That(diff.IsRenameOrModeOnly, Is.True);
        Assert.That(diff.Hunks, Is.Empty);
    }

    [Test]
    public void Parse_ModeChangeOnly_SetsIsRenameOrModeOnly()
    {
        var diff = GitHunkParser.Parse("diff --git a/s.sh b/s.sh\nold mode 100644\nnew mode 100755\n");

        Assert.That(diff.IsRenameOrModeOnly, Is.True);
    }

    [Test]
    public void Parse_ModeChangeWithHunks_StillFlaggedByOldMode()
    {
        var diff = GitHunkParser.Parse(
            "diff --git a/s.sh b/s.sh\nold mode 100644\nnew mode 100755\nindex 1..2\n--- a/s.sh\n+++ b/s.sh\n@@ -1 +1 @@\n-a\n+b\n");

        Assert.That(diff.IsRenameOrModeOnly, Is.True);
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_EmptyInput_HasNoHunksAndIsRenameOrModeOnly()
    {
        var diff = GitHunkParser.Parse(string.Empty);

        Assert.That(diff.Hunks, Is.Empty);
        Assert.That(diff.IsRenameOrModeOnly, Is.True);
        Assert.That(diff.HeaderText, Is.Empty);
        Assert.That(diff.Fingerprint, Has.Length.EqualTo(12));
    }

    // ---- SelectByNewLineRange -----------------------------------------------------------------

    // Hunk 1 occupies new line 3, hunk 2 lines 11-13, hunk 3 (pure deletion, +19,0) the single position 19.
    private static GitFileDiff SelectionDiff() => GitHunkParser.Parse(
        FileHeader +
        "@@ -3 +3 @@\n-a\n+b\n" +
        "@@ -10,0 +11,3 @@\n+x\n+y\n+z\n" +
        "@@ -20,2 +19,0 @@\n-p\n-q\n");

    [Test]
    public void SelectByNewLineRange_HunkWhollyInside_IsSelected()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 1, 5, out var straddling);

        Assert.That(selected, Is.EqualTo(new[] { 1 }));
        Assert.That(straddling.Count, Is.EqualTo(0));
    }

    [Test]
    public void SelectByNewLineRange_SingleLineRange_SelectsOneLineHunk()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 3, 3, out var straddling);

        Assert.That(selected, Is.EqualTo(new[] { 1 }));
        Assert.That(straddling.Count, Is.EqualTo(0));
    }

    [Test]
    public void SelectByNewLineRange_PartialOverlap_GoesToStraddlingNotSelected()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 12, 20, out var straddling);

        Assert.That(selected, Is.EqualTo(new[] { 3 }));
        Assert.That(straddling.Select(h => h.Index), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void SelectByNewLineRange_RangeInsideOneHunk_IsStraddlingBecauseHunkIsNotWhollyInside()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 12, 12, out var straddling);

        Assert.That(selected.Count, Is.EqualTo(0));
        Assert.That(straddling.Select(h => h.Index), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void SelectByNewLineRange_NoHunksInRange_ReturnsEmptyBoth()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 14, 18, out var straddling);

        Assert.That(selected.Count, Is.EqualTo(0));
        Assert.That(straddling.Count, Is.EqualTo(0));
    }

    [Test]
    public void SelectByNewLineRange_DeletionOccupiesItsNewStartPosition()
    {
        var onPosition = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 19, 19, out var s1);
        var justAfter = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 20, 25, out var s2);

        Assert.That(onPosition, Is.EqualTo(new[] { 3 }));
        Assert.That(s1.Count, Is.EqualTo(0));
        Assert.That(justAfter.Count, Is.EqualTo(0));
        Assert.That(s2.Count, Is.EqualTo(0));
    }

    [Test]
    public void SelectByNewLineRange_WholeFile_SelectsEveryHunkInOrder()
    {
        var selected = GitHunkParser.SelectByNewLineRange(SelectionDiff(), 1, 1000, out var straddling);

        Assert.That(selected, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(straddling.Count, Is.EqualTo(0));
    }

    [Test]
    public void SelectByNewLineRange_DeletionAtTopOfFile_CountsAsLineOne()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -1,2 +0,0 @@\n-a\n-b\n");

        var selected = GitHunkParser.SelectByNewLineRange(diff, 1, 1, out _);

        Assert.That(selected, Is.EqualTo(new[] { 1 }));
    }

    // ---- SelectLines / BuildPatch(selection) --------------------------------------------------

    private const string SixAppended = "@@ -10,0 +11,6 @@\n+l1\n+l2\n+l3\n+l4\n+l5\n+l6\n";

    [Test]
    public void SelectLines_PureInsertionHeadOfHunk_YieldsSubHunkWithOnlyThoseLines()
    {
        var diff = GitHunkParser.Parse(FileHeader + SixAppended);

        var selection = GitHunkParser.SelectLines(diff, 11, 13);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.Empty);
        Assert.That(selection.Unsupported, Is.Empty);
        Assert.That(selection.SubHunks, Has.Count.EqualTo(1));
        Assert.That(selection.SubHunks[0].Index, Is.EqualTo(1));
        Assert.That(selection.SubHunks[0].NewCount, Is.EqualTo(3));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -10,0 +11,3 @@\n+l1\n+l2\n+l3\n"));
    }

    [Test]
    public void SelectLines_PureInsertionTailOfHunk_HeaderPositionIgnoresUnstagedLeadingLines()
    {
        var diff = GitHunkParser.Parse(FileHeader + SixAppended);

        var selection = GitHunkParser.SelectLines(diff, 14, 16);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        // l1-l3 are not in the index, so the kept lines land right after old line 10: +11, not +14.
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -10,0 +11,3 @@\n+l4\n+l5\n+l6\n"));
    }

    [Test]
    public void SelectLines_PureInsertionMiddleOfHunk_KeepsOnlyTheRun()
    {
        var diff = GitHunkParser.Parse(FileHeader + SixAppended);

        var patch = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 13, 14));

        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -10,0 +11,2 @@\n+l3\n+l4\n"));
    }

    [Test]
    public void SelectLines_WholeHunkBeforeHeadCut_DeltaUsesPriorHunkCount()
    {
        // Hunk 1 inserts p,q at new lines 4-5 (delta +2); hunk 2 is six lines at 13-18.
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,0 +4,2 @@\n+p\n+q\n" + "@@ -10,0 +13,6 @@\n+l1\n+l2\n+l3\n+l4\n+l5\n+l6\n");

        var selection = GitHunkParser.SelectLines(diff, 4, 15);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 1 }));
        Assert.That(selection.SubHunks.Select(h => h.Index), Is.EqualTo(new[] { 2 }));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -3,0 +4,2 @@\n+p\n+q\n" + "@@ -10,0 +13,3 @@\n+l1\n+l2\n+l3\n"));
    }

    [Test]
    public void SelectLines_TailCutBeforeWholeHunk_DeltaUsesSubHunkCountNotOriginal()
    {
        // Hunk 1: six lines at 11-16; hunk 2: one line at 27 (20 + 1 + 6).
        var diff = GitHunkParser.Parse(FileHeader + SixAppended + "@@ -20,0 +27 @@\n+z\n");

        var selection = GitHunkParser.SelectLines(diff, 14, 27);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 2 }));
        // Only three of hunk 1's lines are staged, so hunk 2 lands at 20 + 1 + 3 = 24.
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -10,0 +11,3 @@\n+l4\n+l5\n+l6\n" + "@@ -20,0 +24 @@\n+z\n"));
    }

    [Test]
    public void SelectLines_CrlfLines_AreKeptByteExact()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -10,0 +11,4 @@\n+a\r\n+b\r\n+c\r\n+d\r\n");

        var patch = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 12, 13));

        Assert.That(patch, Does.Contain("+b\r\n+c\r\n"));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -10,0 +11,2 @@\n+b\r\n+c\r\n"));
    }

    [Test]
    public void SelectLines_NoNewlineMarker_KeptOnlyWhenLastLineIsKept()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -10,0 +11,3 @@\n+a\n+b\n+c\n\\ No newline at end of file\n");

        var withoutLast = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 11, 12));
        var withLast = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 12, 13));

        Assert.That(withoutLast, Is.EqualTo(FileHeader + "@@ -10,0 +11,2 @@\n+a\n+b\n"));
        Assert.That(withLast, Is.EqualTo(FileHeader + "@@ -10,0 +11,2 @@\n+b\n+c\n\\ No newline at end of file\n"));
    }

    // A mixed hunk: removes old lines 3-4 (a, b) and adds x, y, z at new lines 3-5.
    private const string MixedTwoThree = "@@ -3,2 +3,3 @@\n-a\n-b\n+x\n+y\n+z\n";

    [Test]
    public void SelectLines_MixedHunkRangeCoversFirstAddedLine_TakesRemovedLinesAndAPrefix()
    {
        var diff = GitHunkParser.Parse(FileHeader + MixedTwoThree);

        var selection = GitHunkParser.SelectLines(diff, 3, 4);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.Empty);
        Assert.That(selection.Unsupported, Is.Empty);
        Assert.That(selection.SubHunks, Has.Count.EqualTo(1));
        var sub = selection.SubHunks[0];
        Assert.That((sub.Index, sub.OldStart, sub.OldCount, sub.NewStart, sub.NewCount), Is.EqualTo((1, 3, 2, 3, 2)));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -3,2 +3,2 @@\n-a\n-b\n+x\n+y\n"));
    }

    [Test]
    public void SelectLines_MixedHunkRangeEndsOnFirstAddedLine_TakesRemovedLinesAndOneAddedLine()
    {
        var diff = GitHunkParser.Parse(FileHeader + MixedTwoThree);

        var patch = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 1, 3));

        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -3,2 +3,1 @@\n-a\n-b\n+x\n"));
    }

    [Test]
    public void SelectLines_MixedHunkRangeMissesFirstAddedLine_KeepsRemovedLinesUnstagedAndInsertsTheRun()
    {
        var diff = GitHunkParser.Parse(FileHeader + MixedTwoThree);

        var selection = GitHunkParser.SelectLines(diff, 4, 5);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.Unsupported, Is.Empty);
        var sub = selection.SubHunks.Single();
        Assert.That((sub.Index, sub.OldStart, sub.OldCount, sub.NewStart, sub.NewCount), Is.EqualTo((1, 4, 0, 4, 2)));
        // Old lines 3-4 stay in the index, so y and z land right after old line 4: new line 5.
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -4,0 +5,2 @@\n+y\n+z\n"));
    }

    [Test]
    public void SelectLines_MixedHunkMiddleRun_IsAPureInsertionAfterTheLastRemovedLine()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,4 @@\n-a\n-b\n+w\n+x\n+y\n+z\n");

        var selection = GitHunkParser.SelectLines(diff, 4, 5);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.SubHunks.Single().OldCount, Is.EqualTo(0));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -4,0 +5,2 @@\n+x\n+y\n"));
    }

    [Test]
    public void SelectLines_MixedHunkCrlfLines_AreKeptByteExact()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,3 @@\n-a\r\n-b\r\n+x\r\n+y\r\n+z\r\n");

        var taken = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 3, 4));
        var left = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 4, 5));

        Assert.That(taken, Is.EqualTo(FileHeader + "@@ -3,2 +3,2 @@\n-a\r\n-b\r\n+x\r\n+y\r\n"));
        Assert.That(left, Is.EqualTo(FileHeader + "@@ -4,0 +5,2 @@\n+y\r\n+z\r\n"));
    }

    [Test]
    public void SelectLines_MixedHunkOldSideNoNewlineMarker_FollowsTheRemovedLinesOrRefusesTheCut()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,3 @@\n-a\n-b\n\\ No newline at end of file\n+x\n+y\n+z\n");

        var taken = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 3, 4));
        var left = GitHunkParser.SelectLines(diff, 4, 5);

        Assert.That(taken, Is.EqualTo(FileHeader + "@@ -3,2 +3,2 @@\n-a\n-b\n\\ No newline at end of file\n+x\n+y\n"));
        // Old line 4 has no newline, so text inserted after it would be glued onto it: the cut is refused, not staged corrupt.
        Assert.That(left.SubHunks, Is.Empty);
        Assert.That(left.Unsupported.Select(h => h.Index), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SelectLines_MixedHunkNewSideNoNewlineMarker_KeptOnlyWhenLastAddedLineIsKept()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,3 @@\n-a\n-b\n+x\n+y\n+z\n\\ No newline at end of file\n");

        var withoutLast = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 3, 4));
        var withLast = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 4, 5));

        Assert.That(withoutLast, Is.EqualTo(FileHeader + "@@ -3,2 +3,2 @@\n-a\n-b\n+x\n+y\n"));
        Assert.That(withLast, Is.EqualTo(FileHeader + "@@ -4,0 +5,2 @@\n+y\n+z\n\\ No newline at end of file\n"));
    }

    [Test]
    public void SelectLines_MixedHunkTakenAfterWholeHunk_DeltaUsesPriorHunkCount()
    {
        // Hunk 1 inserts p,q at new lines 2-3 (delta +2); hunk 2 replaces old lines 5-6 with x,y,z at new lines 7-9.
        var diff = GitHunkParser.Parse(FileHeader + "@@ -1,0 +2,2 @@\n+p\n+q\n" + "@@ -5,2 +7,3 @@\n-a\n-b\n+x\n+y\n+z\n");

        var selection = GitHunkParser.SelectLines(diff, 2, 8);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 1 }));
        Assert.That(selection.SubHunks.Select(h => h.Index), Is.EqualTo(new[] { 2 }));
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -1,0 +2,2 @@\n+p\n+q\n" + "@@ -5,2 +7,2 @@\n-a\n-b\n+x\n+y\n"));
    }

    [Test]
    public void SelectLines_MixedHunkCutBeforeWholeHunk_DeltaUsesSubHunkCountNotOriginal()
    {
        // Hunk 1 replaces old 3-4 with x,y,z (new 3-5, net +1); hunk 2 inserts q after old line 10, new line 12.
        var diff = GitHunkParser.Parse(FileHeader + MixedTwoThree + "@@ -10,0 +12 @@\n+q\n");

        var selection = GitHunkParser.SelectLines(diff, 4, 12);
        var patch = GitHunkParser.BuildPatch(diff, selection);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 2 }));
        // Only y,z are staged (the '-' lines stay), so the index grew by 2 and q lands at 10 + 1 + 2 = 13.
        Assert.That(patch, Is.EqualTo(FileHeader + "@@ -4,0 +5,2 @@\n+y\n+z\n" + "@@ -10,0 +13 @@\n+q\n"));
    }

    [Test]
    public void SelectLines_MixedHunkWithInterleavedBody_IsUnsupported()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,3 @@\n-a\n+x\n-b\n+y\n+z\n");

        var selection = GitHunkParser.SelectLines(diff, 4, 5);

        Assert.That(selection.WholeHunks, Is.Empty);
        Assert.That(selection.SubHunks, Is.Empty);
        Assert.That(selection.Unsupported.Select(h => h.Index), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SelectLines_MixedHunkWhoseLineCountsDisagreeWithTheHeader_IsUnsupported()
    {
        // The header promises three added lines; the body has two.
        var diff = GitHunkParser.Parse(FileHeader + "@@ -3,2 +3,3 @@\n-a\n-b\n+x\n+y\n");

        var selection = GitHunkParser.SelectLines(diff, 4, 5);

        Assert.That(selection.SubHunks, Is.Empty);
        Assert.That(selection.Unsupported.Select(h => h.Index), Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void SelectLines_PureDeletionOnItsPosition_IsWholeNeverSplit()
    {
        // The pure deletion occupies the single position 19; a range cannot cover "part" of it, so it is whole or absent.
        var diff = SelectionDiff();

        var selection = GitHunkParser.SelectLines(diff, 19, 19);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 3 }));
        Assert.That(selection.SubHunks, Is.Empty);
        Assert.That(selection.Unsupported, Is.Empty);
    }

    [Test]
    public void SelectLines_RangeCoveringHunk_BuildsSamePatchAsIndexSelection()
    {
        var diff = GitHunkParser.Parse(FileHeader + SixAppended);

        var selection = GitHunkParser.SelectLines(diff, 1, 100);

        Assert.That(selection.WholeHunks, Is.EqualTo(new[] { 1 }));
        Assert.That(selection.SubHunks, Is.Empty);
        Assert.That(GitHunkParser.BuildPatch(diff, selection), Is.EqualTo(GitHunkParser.BuildPatch(diff, new[] { 1 })));
    }

    [Test]
    public void SelectLines_TwoAppendedBlocksInOneHunk_AreStagedSeparately()
    {
        // Old file has 10 lines; session A appended two lines and session B three: one zero-context hunk of five lines.
        var diff = GitHunkParser.Parse(FileHeader + "@@ -10,0 +11,5 @@\n+A1\r\n+A2\r\n+B1\r\n+B2\r\n+B3\r\n");

        var blockA = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 11, 12));
        var blockB = GitHunkParser.BuildPatch(diff, GitHunkParser.SelectLines(diff, 13, 15));

        Assert.That(blockA, Is.EqualTo(FileHeader + "@@ -10,0 +11,2 @@\n+A1\r\n+A2\r\n"));
        Assert.That(blockB, Is.EqualTo(FileHeader + "@@ -10,0 +11,3 @@\n+B1\r\n+B2\r\n+B3\r\n"));
    }

    [Test]
    public void SelectLines_RangeOutsideEveryHunk_IsEmptyAndPatchIsHeaderOnly()
    {
        var diff = GitHunkParser.Parse(FileHeader + SixAppended);

        var selection = GitHunkParser.SelectLines(diff, 1, 5);

        Assert.That(selection.WholeHunks, Is.Empty);
        Assert.That(selection.SubHunks, Is.Empty);
        Assert.That(selection.Unsupported, Is.Empty);
        Assert.That(GitHunkParser.BuildPatch(diff, selection), Is.EqualTo(FileHeader));
    }

    // ---- TryParseHunkIds ----------------------------------------------------------------------

    [Test]
    public void TryParseHunkIds_Csv_ReturnsAscendingDistinctIds()
    {
        var ok = GitHunkParser.TryParseHunkIds(" 3 , 1,1 ", 3, out var ids, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(ids, Is.EqualTo(new[] { 1, 3 }));
        Assert.That(error, Is.Empty);
    }

    [Test]
    public void TryParseHunkIds_JsonStringArray_Parses()
    {
        var ok = GitHunkParser.TryParseHunkIds("[\"2\",\"3\"]", 3, out var ids, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(ids, Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public void TryParseHunkIds_JsonNumberArray_Parses()
    {
        var ok = GitHunkParser.TryParseHunkIds("[1, 3]", 3, out var ids, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(ids, Is.EqualTo(new[] { 1, 3 }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void TryParseHunkIds_NothingGiven_ErrorNamesRange(string? input)
    {
        var ok = GitHunkParser.TryParseHunkIds(input, 3, out var ids, out var error);

        Assert.That(ok, Is.False);
        Assert.That(ids, Is.Empty);
        Assert.That(error, Does.Contain("No hunk ids"));
        Assert.That(error, Does.Contain("1 to 3"));
    }

    [TestCase("0")]
    [TestCase("4")]
    [TestCase("1,9")]
    public void TryParseHunkIds_OutOfRange_ErrorNamesValidRange(string input)
    {
        var ok = GitHunkParser.TryParseHunkIds(input, 3, out var ids, out var error);

        Assert.That(ok, Is.False);
        Assert.That(ids, Is.Empty);
        Assert.That(error, Does.Contain("out of range"));
        Assert.That(error, Does.Contain("1 to 3"));
    }

    [TestCase("x")]
    [TestCase("1,two")]
    [TestCase("-1")]
    [TestCase("1.5")]
    public void TryParseHunkIds_NotANumber_ErrorNamesTokenAndRange(string input)
    {
        var ok = GitHunkParser.TryParseHunkIds(input, 3, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("is not a hunk id"));
        Assert.That(error, Does.Contain("1 to 3"));
    }

    [Test]
    public void TryParseHunkIds_MalformedJsonArray_ReturnsParserErrorWithRange()
    {
        var ok = GitHunkParser.TryParseHunkIds("[1,x]", 3, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("JSON array"));
        Assert.That(error, Does.Contain("1 to 3"));
    }

    [Test]
    public void TryParseHunkIds_NoHunks_ErrorSaysFileHasNoHunks()
    {
        var ok = GitHunkParser.TryParseHunkIds("1", 0, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("no hunks"));
    }

    // ---- TryParseLineRange --------------------------------------------------------------------

    [TestCase("52", 52, 52)]
    [TestCase("40-80", 40, 80)]
    [TestCase(" 40 - 80 ", 40, 80)]
    [TestCase("7-7", 7, 7)]
    public void TryParseLineRange_Valid_ReturnsBounds(string input, int expectedFrom, int expectedTo)
    {
        var ok = GitHunkParser.TryParseLineRange(input, out var from, out var to, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That((from, to), Is.EqualTo((expectedFrom, expectedTo)));
        Assert.That(error, Is.Empty);
    }

    [TestCase(null, "empty")]
    [TestCase("", "empty")]
    [TestCase("abc", "not a valid line range")]
    [TestCase("-5", "not a valid line range")]
    [TestCase("40-", "not a valid line range")]
    [TestCase("1-2-3", "not a valid line range")]
    [TestCase("0", "start at 1")]
    [TestCase("0-5", "start at 1")]
    [TestCase("80-40", "reversed")]
    public void TryParseLineRange_Invalid_ReturnsFalseWithSpecificError(string? input, string expectedFragment)
    {
        var ok = GitHunkParser.TryParseLineRange(input, out _, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain(expectedFragment));
    }

    // ---- PreviewLines -------------------------------------------------------------------------

    [Test]
    public void PreviewLines_ReturnsOnlyChangedLines_WithoutMarkersLinesOrLineEndings()
    {
        var diff = GitHunkParser.Parse(
            FileHeader + "@@ -5 +5 @@\n-last\r\n\\ No newline at end of file\n+last2\r\n\\ No newline at end of file\n");

        var preview = GitHunkParser.PreviewLines(diff.Hunks[0], 10, 100);

        Assert.That(preview, Is.EqualTo("-last\n+last2"));
    }

    [Test]
    public void PreviewLines_DecodesUtf8FromLatin1()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -1 +1 @@\n-x\n+" + Latin1OfUtf8("café 中") + "\n");

        var preview = GitHunkParser.PreviewLines(diff.Hunks[0], 10, 100);

        Assert.That(preview, Is.EqualTo("-x\n+café 中"));
    }

    [Test]
    public void PreviewLines_LongLine_IsTruncatedWithEllipsis()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -1 +1 @@\n-abcdefghij\n+k\n");

        var preview = GitHunkParser.PreviewLines(diff.Hunks[0], 10, 5);

        Assert.That(preview, Is.EqualTo("-abcd...\n+k"));
    }

    [Test]
    public void PreviewLines_MoreLinesThanMax_AppendsEllipsisLine()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -10,0 +11,3 @@\n+a\n+b\n+c\n");

        var preview = GitHunkParser.PreviewLines(diff.Hunks[0], 2, 100);

        Assert.That(preview, Is.EqualTo("+a\n+b\n..."));
    }

    [Test]
    public void PreviewLines_ExactlyMaxLines_HasNoEllipsis()
    {
        var diff = GitHunkParser.Parse(FileHeader + "@@ -10,0 +11,2 @@\n+a\n+b\n");

        Assert.That(GitHunkParser.PreviewLines(diff.Hunks[0], 2, 100), Is.EqualTo("+a\n+b"));
    }
}

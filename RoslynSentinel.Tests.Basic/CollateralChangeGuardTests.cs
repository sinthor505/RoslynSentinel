using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Slice B1 of docs/current/plans/plan_movemember_collateral_change_tripwire.md: the pure guard that compares a
// proposed text with "pre-image plus the edits the operation declared". Pure function, so no workspace is needed.
[TestFixture]
public class CollateralChangeGuardTests
{
    private const string Path = "C:\\x\\A.cs";

    private static TextChange Edit(int start, int length, string newText) =>
        new(new TextSpan(start, length), newText);

    private static CollateralChangeViolation? Check(string pre, string proposed, params TextChange[] edits) =>
        CollateralChangeGuard.Check(Path, pre, proposed, edits);

    [Test]
    public void Identical_ProposedIsPreImagePlusEdits_ReturnsNull()
    {
        const string pre = "a\nb\nc\n";

        Assert.That(Check(pre, "a\nX\nc\n", Edit(2, 1, "X")), Is.Null);
    }

    [Test]
    public void DifferingInTheMiddle_NamesTheFirstDifferingLineAndColumn()
    {
        const string pre = "alpha\nbeta\ngamma\ndelta\n";
        // The declared edit is on line 1; the proposal also rewrote line 3 (collateral).
        var proposed = "ALPHA\nbeta\nGAMMA\ndelta\n";

        var violation = Check(pre, proposed, Edit(0, 5, "ALPHA"));

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Kind, Is.EqualTo(CollateralChangeKind.ContentDiffers));
        Assert.That(violation.FilePath, Is.EqualTo(Path));
        Assert.That((violation.Line, violation.Column), Is.EqualTo((3, 1)));
        Assert.That(violation.Expected, Is.EqualTo("gamma\\n"));
        Assert.That(violation.Actual, Is.EqualTo("GAMMA\\n"));
    }

    [Test]
    public void DifferingMidLine_ReportsTheColumnOfTheFirstDifference()
    {
        var violation = Check("one two three\n", "one TWO three\n");

        Assert.That((violation!.Line, violation.Column), Is.EqualTo((1, 5)));
    }

    [Test]
    public void DifferingAtEndOfFile_LostFinalNewline_IsVisibleInTheExcerpt()
    {
        var violation = Check("a\nb\n", "a\nb");

        Assert.That(violation, Is.Not.Null);
        Assert.That((violation!.Line, violation.Column), Is.EqualTo((2, 2)));
        Assert.That(violation.Expected, Is.EqualTo("b\\n"));
        Assert.That(violation.Actual, Is.EqualTo("b"));
    }

    [Test]
    public void DifferingAtEndOfFile_ExtraTrailingText_ReportsEndOfTextForTheShorterSide()
    {
        var violation = Check("a\n", "a\nb\n");

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Line, Is.EqualTo(2));
        Assert.That(violation.Expected, Is.EqualTo("<end of text>"));
        Assert.That(violation.Actual, Is.EqualTo("b\\n"));
    }

    [Test]
    public void CrlfPreImage_ProposedConvertedToLf_IsFlaggedOnLineOneWithTheEscapesShown()
    {
        const string pre = "namespace T;\r\nclass A { }\r\n";
        var edits = new[] { Edit(pre.IndexOf("A {", StringComparison.Ordinal), 1, "B") };
        var asLf = "namespace T;\nclass B { }\n";

        var violation = CollateralChangeGuard.Check(Path, pre, asLf, edits);

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Line, Is.EqualTo(1));
        Assert.That(violation.Expected, Is.EqualTo("namespace T;\\r\\n"));
        Assert.That(violation.Actual, Is.EqualTo("namespace T;\\n"));
    }

    [Test]
    public void CrlfPreImage_ProposedKeepingCrlf_PassesAsExactEquality()
    {
        const string pre = "namespace T;\r\nclass A { }\r\n";
        var proposed = "namespace T;\r\nclass B { }\r\n";

        Assert.That(Check(pre, proposed, Edit(pre.IndexOf("A {", StringComparison.Ordinal), 1, "B")), Is.Null);
    }

    [Test]
    public void AdjacentEdits_AreAppliedWithoutGapOrOverlap()
    {
        // [0,1) -> "X" and [1,2) -> "Y" touch but do not overlap.
        Assert.That(Check("abcd", "XYcd", Edit(0, 1, "X"), Edit(1, 1, "Y")), Is.Null);
    }

    [Test]
    public void EditsGivenOutOfOrder_AreSortedBeforeApplying()
    {
        Assert.That(Check("abcd", "XbcY", Edit(3, 1, "Y"), Edit(0, 1, "X")), Is.Null);
    }

    [Test]
    public void InsertionsAtTheSamePosition_KeepTheirGivenOrder()
    {
        // Same start, same (zero) length: the sort is stable, so the caller's order decides, as in the engine.
        Assert.That(Check("ab", "a12b", Edit(1, 0, "1"), Edit(1, 0, "2")), Is.Null);
        var violation = Check("ab", "a21b", Edit(1, 0, "1"), Edit(1, 0, "2"));

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Kind, Is.EqualTo(CollateralChangeKind.ContentDiffers));
    }

    [Test]
    public void InsertionAdjacentToADeletion_IsAccepted()
    {
        // Insert at 2, delete [2,4): the zero-length edit sorts first and the two do not overlap.
        Assert.That(Check("abcdef", "abXef", Edit(2, 2, string.Empty), Edit(2, 0, "X")), Is.Null);
    }

    [Test]
    public void OverlappingEdits_ReportEditsNotApplicableInsteadOfThrowing()
    {
        // [0,3) and [2,4) overlap, so the engine's ApplyChanges would throw; the guard must return a structured failure.
        var violation = Check("abcdef", "anything", Edit(0, 3, "X"), Edit(2, 2, "Y"));

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Kind, Is.EqualTo(CollateralChangeKind.EditsNotApplicable));
        Assert.That(violation.Line, Is.EqualTo(0));
    }

    [Test]
    public void EditOutsideThePreImage_ReportsEditsNotApplicable()
    {
        var violation = Check("abc", "abcX", Edit(10, 0, "X"));

        Assert.That(violation!.Kind, Is.EqualTo(CollateralChangeKind.EditsNotApplicable));
    }

    [Test]
    public void EmptyEditList_ProposedEqualToPreImage_ReturnsNull()
    {
        Assert.That(Check("a\nb\n", "a\nb\n"), Is.Null);
    }

    [Test]
    public void EmptyEditList_ProposedDiffersFromPreImage_IsFlagged()
    {
        var violation = Check("a\nb\n", "a\nB\n");

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Line, Is.EqualTo(2));
    }

    [Test]
    public void EmptyPreImageAndProposal_ReturnsNull()
    {
        Assert.That(Check(string.Empty, string.Empty), Is.Null);
    }

    [Test]
    public void LongLine_ExcerptIsCappedAndWindowedAroundTheDifference()
    {
        var pre = new string('a', 400) + "\n";
        var proposed = new string('a', 300) + "Z" + new string('a', 99) + "\n";

        var violation = Check(pre, proposed);

        Assert.That(violation, Is.Not.Null);
        Assert.That(violation!.Column, Is.EqualTo(301));
        // Window of at most MaxExcerptChars source characters plus the two "..." markers and escapes.
        Assert.That(violation.Expected.Length, Is.LessThanOrEqualTo(CollateralChangeGuard.MaxExcerptChars + 6 + 2));
        Assert.That(violation.Actual, Does.Contain("Z"), "the window must include the differing character");
        Assert.That(violation.Actual, Does.StartWith("...").And.EndWith("..."));
    }

    [Test]
    public void BuildMessage_NamesTheFileLineAndOutcome_AndIsAscii()
    {
        var content = Check("a\nb\n", "a\nB\n")!;
        var notApplicable = Check("abcdef", "x", Edit(0, 3, "X"), Edit(2, 2, "Y"))!;

        var message = CollateralChangeGuard.BuildMessage("MoveMember", [content, notApplicable]);

        Assert.That(message, Does.StartWith("MoveMember: refused"));
        Assert.That(message, Does.Contain(Path).And.Contain("line 2").And.Contain("2 existing file(s)"));
        Assert.That(message, Does.Contain("No files were written"));
        Assert.That(message.All(c => c < 128), Is.True, "messages must be ASCII-only");
    }

    [Test]
    public void ErrorCode_IsDistinctAndMapsToFailed()
    {
        Assert.That(ToolErrorCode.CollateralChangeRefused, Is.EqualTo("CollateralChangeRefused"));
        Assert.That(ToolErrorCode.CollateralChangeRefused, Is.Not.EqualTo(ToolErrorCode.EolChangeRefused));
        Assert.That(ResultStatusExtensions.FromErrorCode(ToolErrorCode.CollateralChangeRefused), Is.EqualTo(ResultStatus.Failed));
    }

    // The guard must agree with the engine on how edits combine, or it would refuse the engine's own output.
    // Layering prevents Common from calling MoveMemberTextEdits.ApplyChanges, so the two are pinned together here.
    [TestCaseSource(nameof(ParityCases))]
    public void ApplyEdits_AgreesWithTheEnginesApplyChanges(string pre, TextChange[] edits)
    {
        var fromEngine = MoveMemberTextEdits.ApplyChanges(SourceText.From(pre), edits);

        Assert.That(CollateralChangeGuard.ApplyEdits(pre, edits), Is.EqualTo(fromEngine));
        Assert.That(CollateralChangeGuard.Check(Path, pre, fromEngine, edits), Is.Null);
    }

    private static IEnumerable<TestCaseData> ParityCases()
    {
        yield return new TestCaseData("abcd", new[] { Edit(0, 1, "X"), Edit(1, 1, "Y") }).SetName("Parity_Adjacent");
        yield return new TestCaseData("abcd", new[] { Edit(3, 1, "Y"), Edit(0, 1, "X") }).SetName("Parity_OutOfOrder");
        yield return new TestCaseData("ab", new[] { Edit(1, 0, "1"), Edit(1, 0, "2") }).SetName("Parity_SamePositionInsertions");
        yield return new TestCaseData("abcdef", new[] { Edit(2, 2, string.Empty), Edit(2, 0, "X") }).SetName("Parity_InsertionBesideDeletion");
        yield return new TestCaseData("a\r\nb\r\n", Array.Empty<TextChange>()).SetName("Parity_Empty");
    }
}

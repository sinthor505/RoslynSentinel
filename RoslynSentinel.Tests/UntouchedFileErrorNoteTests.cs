using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// Pure unit coverage for <see cref="ValidateAndApplyHelper.DescribeUntouchedFileErrors"/>: the note that names
/// files a failed compile gate reports new errors in, which the edit did not change.
/// </summary>
[TestFixture]
public class UntouchedFileErrorNoteTests
{
    private const string AtomicRoute = "one batch call with one item per file";

    [Test]
    public void AllErrorFilesWereEdited_ReturnsNull()
    {
        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(
            new[] { "src/OrderCheckout.cs", "src/Cart.cs" },
            new[] { "src/OrderCheckout.cs", "src/Cart.cs" });

        Assert.That(note, Is.Null);
    }

    [Test]
    public void ErrorInUntouchedFile_NamesTheFileAndTheAtomicRouteHint()
    {
        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(
            new[] { "src/Cart.cs" },
            new[] { "src/Cart.cs", "src/OrderCheckout.cs" },
            AtomicRoute);

        Assert.That(note, Does.Contain("OrderCheckout.cs"));
        Assert.That(note, Does.Contain(AtomicRoute));
    }

    [TestCase(@"C:\repo\src\OrderCheckout.cs", @"src\OrderCheckout.cs")]
    [TestCase("C:/repo/src/OrderCheckout.cs", "src/OrderCheckout.cs")]
    public void AbsoluteEditedPath_MatchesWorkspaceRelativeDiagnosticPath(string editedPath, string diagnosticPath)
    {
        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(
            new[] { editedPath },
            new[] { diagnosticPath });

        Assert.That(note, Is.Null);
    }

    [Test]
    public void MoreThanFiveUntouchedFiles_ShowsFiveAndCountsTheRest()
    {
        var diagnosticPaths = Enumerable.Range(1, 7).Select(i => $"src/File{i}.cs").ToList();

        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(Array.Empty<string>(), diagnosticPaths);

        Assert.That(note, Does.Contain("and 2 more"));
        Assert.That(note, Does.Contain("File5.cs"));
        Assert.That(note, Does.Not.Contain("File6.cs"));
    }

    [Test]
    public void NullAndBlankDiagnosticPaths_AreIgnored()
    {
        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(
            new[] { "src/Cart.cs" },
            new string?[] { null, "", "   ", "src/Cart.cs" });

        Assert.That(note, Is.Null);
    }

    [Test]
    public void SameUntouchedFileReportedTwice_IsNamedOnce()
    {
        var note = ValidateAndApplyHelper.DescribeUntouchedFileErrors(
            Array.Empty<string>(),
            new[] { "src/OrderCheckout.cs", "src/OrderCheckout.cs" });

        Assert.That(note, Does.Contain("file(s) this edit did not change: OrderCheckout.cs."));
    }
}

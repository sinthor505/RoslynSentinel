using System.Text;
using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class TestRunResultProjectorTests
{
    private static TestCaseResult FailedRow(int index, string? message = "boom", string? stack = null, string? output = null)
        => new($"RoslynSentinel.Tests.SampleClass.Test{index}", TestOutcome.Failed, TimeSpan.FromMilliseconds(5), message, stack, output);

    private static TestCaseResult SkippedRow(int index)
        => new($"RoslynSentinel.Tests.SampleClass.Skipped{index}", TestOutcome.Skipped, TimeSpan.Zero, null, null);

    private static TestRunResult BuildResult(
        IEnumerable<TestCaseResult> rows,
        int totalCount = 0,
        int failedCount = 0,
        int skippedCount = 0,
        List<GroupedCountSummary>? failureSummary = null,
        string? stdout = null,
        string? stderr = null,
        bool runCompleted = true,
        string? fullResultsId = null)
    {
        return new TestRunResult(
            RunSucceeded: failedCount == 0,
            ExitCode: failedCount == 0 ? 0 : 1,
            TotalCount: totalCount,
            PassedCount: totalCount - failedCount - skippedCount,
            FailedCount: failedCount,
            SkippedCount: skippedCount,
            FailureSummary: failureSummary ?? new List<GroupedCountSummary>(),
            Results: rows.ToList(),
            StdoutTail: stdout,
            StderrTail: stderr,
            Duration: TimeSpan.FromSeconds(1),
            RunCompleted: runCompleted,
            FullResultsId: fullResultsId);
    }

    [Test]
    public void Project_LongErrorMessage_IsCutToExactly300CharsEndingWithEllipsis()
    {
        var result = BuildResult(new[] { FailedRow(0, new string('x', 600)) }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.Results[0].ErrorMessage, Is.EqualTo(new string('x', 297) + "..."));
        Assert.That(projected.Results[0].ErrorMessage, Has.Length.EqualTo(300));
    }

    [Test]
    public void Project_ShortErrorMessage_IsUnchanged()
    {
        var result = BuildResult(new[] { FailedRow(0, "short failure") }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.Results[0].ErrorMessage, Is.EqualTo("short failure"));
    }

    [Test]
    public void Project_FirstTenRows_KeepStackTraceReducedToFirstTwoNonEmptyLines()
    {
        var stack = "  at A.First()\r\n\r\n   \r\n  at B.Second()\r\n  at C.Third()";
        var result = BuildResult(new[] { FailedRow(9, "boom", stack) }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.Results[0].ErrorStackTrace, Is.EqualTo("  at A.First()\n  at B.Second()"));
    }

    [Test]
    public void Project_FirstTenRows_CapStackTraceAt300Chars()
    {
        var longStack = new string('a', 200) + "\n" + new string('b', 200);
        var result = BuildResult(new[] { FailedRow(0, "boom", longStack) }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        var stack = projected.Results[0].ErrorStackTrace;
        Assert.That(stack, Is.EqualTo(new string('a', 200) + "\n" + new string('b', 96) + "..."));
        Assert.That(stack, Has.Length.EqualTo(300));
    }

    [Test]
    public void Project_RowIndexTenAndAbove_DropStackTraceAndCutMessageTo150()
    {
        var rows = Enumerable.Range(0, 11)
            .Select(i => FailedRow(i, new string('x', 600), "  at A.First()\n  at B.Second()"))
            .ToArray();
        var result = BuildResult(rows, totalCount: 11, failedCount: 11);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.Results[9].ErrorStackTrace, Is.Not.Null);
        Assert.That(projected.Results[10].ErrorStackTrace, Is.Null);
        Assert.That(projected.Results[10].ErrorMessage, Is.EqualTo(new string('x', 147) + "..."));
        Assert.That(projected.Results[10].ErrorMessage, Has.Length.EqualTo(150));
    }

    [Test]
    public void Project_IncludeOutputFalse_DropsRowOutput()
    {
        var result = BuildResult(new[] { FailedRow(0, "boom", null, "out-marker") }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.Results[0].Output, Is.Null);
    }

    [Test]
    public void Project_IncludeOutputTrue_KeepsRowOutput()
    {
        var result = BuildResult(new[] { FailedRow(0, "boom", null, "out-marker") }, totalCount: 1, failedCount: 1);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: true);

        Assert.That(projected.Results[0].Output, Is.EqualTo("out-marker"));
    }

    [Test]
    public void Project_CompletedRunWithTests_IncludeOutputFalse_DropsTails()
    {
        var result = BuildResult(new[] { FailedRow(0) }, totalCount: 1, failedCount: 1, stdout: "stdout-tail", stderr: "stderr-tail");

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.StdoutTail, Is.Null);
        Assert.That(projected.StderrTail, Is.Null);
    }

    [Test]
    public void Project_CompletedRunWithTests_IncludeOutputTrue_KeepsTails()
    {
        var result = BuildResult(new[] { FailedRow(0) }, totalCount: 1, failedCount: 1, stdout: "stdout-tail", stderr: "stderr-tail");

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: true);

        Assert.That(projected.StdoutTail, Is.EqualTo("stdout-tail"));
        Assert.That(projected.StderrTail, Is.EqualTo("stderr-tail"));
    }

    [Test]
    public void Project_IncompleteRun_KeepsTailsEvenWithoutIncludeOutput()
    {
        var result = BuildResult(new[] { FailedRow(0) }, totalCount: 1, failedCount: 1, stdout: "stdout-tail", stderr: "stderr-tail", runCompleted: false);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.StdoutTail, Is.EqualTo("stdout-tail"));
        Assert.That(projected.StderrTail, Is.EqualTo("stderr-tail"));
    }

    [Test]
    public void Project_ZeroTotalCount_KeepsTailsEvenWithoutIncludeOutput()
    {
        var result = BuildResult(Array.Empty<TestCaseResult>(), totalCount: 0, stdout: "stdout-tail", stderr: "stderr-tail");

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.StdoutTail, Is.EqualTo("stdout-tail"));
        Assert.That(projected.StderrTail, Is.EqualTo("stderr-tail"));
    }

    [Test]
    public void Project_FailureSummaryWithFifteenGroups_IsCappedAtTen()
    {
        var groups = Enumerable.Range(0, 15)
            .Select(i => new GroupedCountSummary($"signature-{i}", 1, $"ref-{i}"))
            .ToList();
        var result = BuildResult(new[] { FailedRow(0) }, totalCount: 15, failedCount: 15, failureSummary: groups);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.FailureSummary, Has.Count.EqualTo(10));
    }

    [Test]
    public void Project_FailedFilter_OmittedCountIsFailedCountMinusRows()
    {
        var rows = Enumerable.Range(0, 5).Select(i => FailedRow(i)).ToArray();
        var result = BuildResult(rows, totalCount: 30, failedCount: 20);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.OmittedResultCount, Is.EqualTo(15));
    }

    [Test]
    public void Project_SkippedFilter_OmittedCountUsesSkippedCount()
    {
        var rows = new[] { SkippedRow(0), SkippedRow(1) };
        var result = BuildResult(rows, totalCount: 30, failedCount: 20, skippedCount: 7);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.skipped, includeOutput: false);

        Assert.That(projected.OmittedResultCount, Is.EqualTo(5));
    }

    [Test]
    public void Project_AllFilter_OmittedCountUsesTotalCount()
    {
        var rows = Enumerable.Range(0, 5).Select(i => FailedRow(i)).ToArray();
        var result = BuildResult(rows, totalCount: 30, failedCount: 20);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.all, includeOutput: false);

        Assert.That(projected.OmittedResultCount, Is.EqualTo(25));
    }

    [Test]
    public void Project_OmittedCount_IsNeverNegative()
    {
        var rows = Enumerable.Range(0, 5).Select(i => FailedRow(i)).ToArray();
        var result = BuildResult(rows, totalCount: 5, failedCount: 2);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);

        Assert.That(projected.OmittedResultCount, Is.EqualTo(0));
    }

    [Test]
    public void Project_FourteenFailedRowsWithLargePayloads_SerializesUnder15KB()
    {
        var message = new string('m', 600);
        var stack = string.Join("\n", Enumerable.Repeat("   at Some.Namespace.SomeClass.SomeMethod() in C:\\repo\\src\\File.cs:line 42", 20));
        var output = new string('o', 2000);
        var rows = Enumerable.Range(0, 14).Select(i => FailedRow(i, message, stack, output)).ToArray();
        var stdoutLines = string.Join("\n", Enumerable.Repeat(new string('s', 80), 40));
        var result = BuildResult(rows, totalCount: 14, failedCount: 14, stdout: stdoutLines);

        var projected = TestRunResultProjector.Project(result, TestResultsFilter.failed, includeOutput: false);
        var json = JsonSerializer.Serialize(projected, SharedJsonOptions.Compact);

        Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThan(15360));
    }

    [Test]
    public void Project_FullResultsId_IsLeftUnchanged()
    {
        var withId = BuildResult(new[] { FailedRow(0) }, totalCount: 1, failedCount: 1, fullResultsId: "abc123");
        var withoutId = BuildResult(new[] { FailedRow(0) }, totalCount: 1, failedCount: 1);

        Assert.That(TestRunResultProjector.Project(withId, TestResultsFilter.failed, includeOutput: false).FullResultsId, Is.EqualTo("abc123"));
        Assert.That(TestRunResultProjector.Project(withoutId, TestResultsFilter.failed, includeOutput: false).FullResultsId, Is.Null);
    }
}

using System.Text.Json;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Pure precedence rules of TestRunOutcome: TimedOut > RunError > TestsFailed > NoTestsMatched > Passed. No process is started.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TestRunOutcomeTests
{
    // exitCode, failedCount, totalCount, hung, timedOut, trxExists -> outcome
    [TestCase(0, 0, 5, false, false, true, TestRunOutcome.Passed)]
    [TestCase(1, 2, 5, false, false, true, TestRunOutcome.TestsFailed)]
    [TestCase(0, 0, 0, false, false, true, TestRunOutcome.NoTestsMatched)]
    [TestCase(-1, 0, 0, false, true, false, TestRunOutcome.TimedOut)]
    [TestCase(-1, 3, 5, true, true, true, TestRunOutcome.TimedOut)]
    [TestCase(1, 0, 0, false, false, false, TestRunOutcome.RunError)]
    [TestCase(0, 0, 5, true, false, true, TestRunOutcome.RunError)]
    [TestCase(1, 2, 5, true, false, true, TestRunOutcome.RunError)]
    [TestCase(1, 0, 5, false, false, true, TestRunOutcome.RunError)]
    [TestCase(1, 0, 0, false, false, true, TestRunOutcome.RunError)]
    public void ClassifyOutcome_FollowsPrecedence(int exitCode, int failedCount, int totalCount, bool hung, bool timedOut, bool trxExists, TestRunOutcome expected)
    {
        Assert.That(TestRunEngine.ClassifyOutcome(exitCode, failedCount, totalCount, hung, timedOut, trxExists), Is.EqualTo(expected));
    }

    [TestCase("", "")]
    [TestCase("  \r\n", "\t")]
    [Category("NoTrxDetail")]
    public void NoTrxDetail_SilentExit_NamesExitCodeKnownCauseAndOptions(string stdout, string stderr)
    {
        var detail = TestRunEngine.NoTrxDetail(1, stdout, stderr);

        Assert.That(detail, Does.StartWith("No TRX result file was produced"));
        Assert.That(detail, Does.Contain("dotnet test exited with code 1"));
        Assert.That(detail, Does.Contain("printed nothing to stdout or stderr"));
        Assert.That(detail, Does.Contain("MSBuild worker node handshake failure"));
        Assert.That(detail, Does.Contain("global.json"));
        Assert.That(detail, Does.Contain("MSBUILDDEBUGCOMM"));
    }

    [TestCase("Build FAILED.", "")]
    [TestCase("", "error MSB1009: Project file does not exist.")]
    [Category("NoTrxDetail")]
    public void NoTrxDetail_RunThatPrintedOutput_NamesExitCodeAndNoSilentCause(string stdout, string stderr)
    {
        var detail = TestRunEngine.NoTrxDetail(2, stdout, stderr);

        Assert.That(detail, Does.Contain("dotnet test exited with code 2"));
        Assert.That(detail, Does.Not.Contain("handshake"));
        Assert.That(detail, Does.Not.Contain("MSBUILDDEBUGCOMM"));
    }

    // Facts and options only (reference_tool_error_text_facts_options_policy.md): no imperative about what the caller should do with the result.
    [Test]
    [Category("NoTrxDetail")]
    public void NoTrxDetail_CarriesNoPolicyWords()
    {
        var detail = TestRunEngine.NoTrxDetail(1, "", "");

        Assert.That(detail, Does.Not.Match(@"(?i)\b(must|stop|do not|never|always|report)\b"));
    }

    [Test]
    public void AggregateOutcome_TimedOutBeatsEverything()
    {
        var outcome = TestRunEngine.AggregateOutcome([TestRunOutcome.Passed, TestRunOutcome.RunError, TestRunOutcome.TimedOut, TestRunOutcome.TestsFailed], 10);

        Assert.That(outcome, Is.EqualTo(TestRunOutcome.TimedOut));
    }

    [Test]
    public void AggregateOutcome_RunErrorBeatsTestsFailed()
    {
        var outcome = TestRunEngine.AggregateOutcome([TestRunOutcome.TestsFailed, TestRunOutcome.RunError], 10);

        Assert.That(outcome, Is.EqualTo(TestRunOutcome.RunError));
    }

    [Test]
    public void AggregateOutcome_AnyTestsFailedProject_IsTestsFailed()
    {
        var outcome = TestRunEngine.AggregateOutcome([TestRunOutcome.Passed, TestRunOutcome.TestsFailed, TestRunOutcome.NoTestsMatched], 10);

        Assert.That(outcome, Is.EqualTo(TestRunOutcome.TestsFailed));
    }

    [Test]
    public void AggregateOutcome_NoTestRanInAnyProject_IsNoTestsMatched()
    {
        var outcome = TestRunEngine.AggregateOutcome([TestRunOutcome.NoTestsMatched, TestRunOutcome.NoTestsMatched], 0);

        Assert.That(outcome, Is.EqualTo(TestRunOutcome.NoTestsMatched));
    }

    [Test]
    public void AggregateOutcome_SomeTestsRanAndNoneFailed_IsPassedEvenIfAProjectMatchedNothing()
    {
        var outcome = TestRunEngine.AggregateOutcome([TestRunOutcome.Passed, TestRunOutcome.NoTestsMatched], 4);

        Assert.That(outcome, Is.EqualTo(TestRunOutcome.Passed));
    }

    [Test]
    public void Outcome_SerializesAsStringAndTheOldBoolFieldsAreGone()
    {
        var run = new TestRunResult(TestRunOutcome.NoTestsMatched, 0, 0, 0, 0, 0, [], [], null, null, TimeSpan.Zero);

        var json = JsonSerializer.Serialize(run, SharedJsonOptions.Default);

        // SharedJsonOptions.Default keeps the C# property casing; the wire casing is applied further out, so compare ignoring case.
        Assert.That(json, Does.Match("(?i)\"outcome\":\\s*\"NoTestsMatched\""));
        Assert.That(json, Does.Not.Contain("runSucceeded").IgnoreCase.And.Not.Contain("runCompleted").IgnoreCase.And.Not.Contain("ranToCompletion").IgnoreCase);
    }

    [Test]
    public void InvalidFilterDetail_ExitZero_IsNull()
    {
        Assert.That(TestRunEngine.InvalidFilterDetail("FullyQualifiedName~Foo", 0, "", "An exception occurred. Incorrect format for TestCaseFilter"), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void InvalidFilterDetail_BlankFilter_IsNull(string? filter)
    {
        Assert.That(TestRunEngine.InvalidFilterDetail(filter, 1, "", "Incorrect format for TestCaseFilter"), Is.Null);
    }

    [Test]
    public void InvalidFilterDetail_NoSignature_IsNull()
    {
        Assert.That(TestRunEngine.InvalidFilterDetail("FullyQualifiedName~Foo", 1, "Build FAILED.", "error CS1002: ; expected"), Is.Null);
    }

    [Test]
    public void InvalidFilterDetail_RejectedFilter_NamesAdapterLineAndOmitsStackTrace()
    {
        const string stderr = "An exception occurred while invoking executor 'executor://nunit3testexecutor/': Incorrect format for TestCaseFilter Error: Invalid Condition 'FullyQualifiedName~'. Specify the correct format and try again. Note that the incorrect format can lead to no test getting executed.\n"
            + "   at NUnit.Engine.Services.Filters.TestFilterFactory.Create(String filter)\n";

        var detail = TestRunEngine.InvalidFilterDetail("FullyQualifiedName~(Foo", 1, "", stderr);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail, Does.Contain("rejected the filter 'FullyQualifiedName~(Foo'"));
        Assert.That(detail, Does.Contain("exited with code 1"));
        Assert.That(detail, Does.Contain("Incorrect format for TestCaseFilter"));
        Assert.That(detail, Does.Not.Contain("TestFilterFactory"));
        Assert.That(detail, Does.Not.Contain("tests matched"));
    }
}

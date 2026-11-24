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
}

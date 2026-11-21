using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class RunDirectoryArchiveTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "RunDirectoryArchiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void CopyToResults_CopiesNestedFilesAndKeepsThePathAfterModelEval()
    {
        var runDirectory = Path.Combine(_root, "work", "model-eval", "SomeTest", "20260101-000000-000");
        Directory.CreateDirectory(Path.Combine(runDirectory, "sub"));
        File.WriteAllText(Path.Combine(runDirectory, "agent.log"), "log");
        File.WriteAllText(Path.Combine(runDirectory, "sub", "x.txt"), "x");
        var resultsRoot = Path.Combine(_root, "results");
        var expected = Path.Combine(resultsRoot, "112", "SomeTest", "20260101-000000-000");

        var destination = RunDirectoryArchive.CopyToResults(runDirectory, resultsRoot, "112");

        Assert.That(destination, Is.EqualTo(expected));
        Assert.That(File.Exists(Path.Combine(expected, "agent.log")), Is.True);
        Assert.That(File.Exists(Path.Combine(expected, "sub", "x.txt")), Is.True);
    }

    [Test]
    public void CopyToResults_SecondCallReturnsNullAndLeavesExistingContentUnchanged()
    {
        var runDirectory = Path.Combine(_root, "work", "model-eval", "SomeTest", "20260101-000000-000");
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(Path.Combine(runDirectory, "agent.log"), "log");
        var resultsRoot = Path.Combine(_root, "results");
        var expected = Path.Combine(resultsRoot, "112", "SomeTest", "20260101-000000-000");

        Assert.That(RunDirectoryArchive.CopyToResults(runDirectory, resultsRoot, "112"), Is.Not.Null);
        File.WriteAllText(Path.Combine(expected, "agent.log"), "existing");

        var second = RunDirectoryArchive.CopyToResults(runDirectory, resultsRoot, "112");

        Assert.That(second, Is.Null);
        Assert.That(File.ReadAllText(Path.Combine(expected, "agent.log")), Is.EqualTo("existing"));
    }

    [Test]
    public void CopyToResults_MissingRunDirectoryReturnsNullAndCreatesNothing()
    {
        var missing = Path.Combine(_root, "work", "model-eval", "Missing");
        var resultsRoot = Path.Combine(_root, "results");

        var destination = RunDirectoryArchive.CopyToResults(missing, resultsRoot, "112");

        Assert.That(destination, Is.Null);
        Assert.That(Directory.Exists(resultsRoot), Is.False);
    }

    [Test]
    public void CopyToResults_WithoutModelEvalSegmentKeepsTheRunDirectoryLeafName()
    {
        var runDirectory = Path.Combine(_root, "elsewhere", "20260101-000000-000");
        Directory.CreateDirectory(runDirectory);
        var resultsRoot = Path.Combine(_root, "results");

        var destination = RunDirectoryArchive.CopyToResults(runDirectory, resultsRoot, "112");

        Assert.That(destination, Is.EqualTo(Path.Combine(resultsRoot, "112", "20260101-000000-000")));
    }

    [Test]
    public void WriteTestResult_NullOutcomeWritesTheUnfinishedOutcome()
    {
        var runDirectory = Path.Combine(_root, "run");
        Directory.CreateDirectory(runDirectory);

        RunDirectoryArchive.WriteTestResult(runDirectory, null, "X", null, null);

        var lines = File.ReadAllLines(Path.Combine(runDirectory, "test-result.txt"));
        Assert.That(lines[0], Is.EqualTo("Outcome: " + RunDirectoryArchive.UnfinishedOutcome));
        Assert.That(lines[1], Is.EqualTo("Test: X"));
    }

    [Test]
    public void WriteTestResult_WritesOutcomeMessageAndStackTraceSections()
    {
        var runDirectory = Path.Combine(_root, "run");
        Directory.CreateDirectory(runDirectory);

        RunDirectoryArchive.WriteTestResult(runDirectory, "Failed", "X", "boom", "at Foo()");

        var lines = File.ReadAllLines(Path.Combine(runDirectory, "test-result.txt"));
        Assert.That(lines, Is.EqualTo(new[]
        {
            "Outcome: Failed",
            "Test: X",
            "",
            "Message:",
            "boom",
            "",
            "StackTrace:",
            "at Foo()",
        }));
    }
}

using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

// Order-dependent pair relying on NUnit running the fixture's tests sequentially; NonParallelizable
// keeps it from interleaving. If it fails only on full or parallel runs, suspect ordering.
[TestFixture]
[NonParallelizable]
public sealed class RewriteBodyArchivedOutcomeEndToEndTests
{
    private const string FirstTestName = nameof(BodyArchivesWhileUnfinished);

    private static string? _runDirectory;

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (_runDirectory is not null && Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }

        _runDirectory = null;
    }

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Test.Name != FirstTestName)
        {
            return;
        }

        // The rewrite runs in the assembly-level ITestAction's AfterTest, which is after TearDown,
        // so at this point the file must still say Unfinished.
        var text = File.ReadAllText(Path.Combine(_runDirectory!, "test-result.txt"));
        Assert.That(text, Does.Contain(RunDirectoryArchive.UnfinishedOutcome), "the rewrite must not have happened before TearDown ends");
    }

    [Test, Order(1)]
    public void BodyArchivesWhileUnfinished()
    {
        _runDirectory = Path.Combine(Path.GetTempPath(), "RewriteBodyArchivedOutcomeEndToEndTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_runDirectory);
        RunDirectoryArchive.WriteTestResult(_runDirectory, null, "X", null, null);
        PendingArchivedOutcomes.Register(TestContext.CurrentContext.Test.ID, _runDirectory, null);
    }

    [Test, Order(2)]
    public void AfterTestRewrote_AfterTearDown_WithTheRealOutcome()
    {
        Assert.That(_runDirectory, Is.Not.Null, "BodyArchivesWhileUnfinished must have run first");
        var text = File.ReadAllText(Path.Combine(_runDirectory!, "test-result.txt"));
        Assert.That(text, Does.StartWith("Outcome: Passed"));
        Assert.That(text, Does.Not.Contain("Unfinished"));
    }
}

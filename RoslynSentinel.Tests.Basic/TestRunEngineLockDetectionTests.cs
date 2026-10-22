using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// TestRunEngine.DetectFileLock turns a build-output lock error in `dotnet test` output into an
// actionable detail message. MSB3026 is the "could not copy, retrying" warning that precedes
// MSB3027, and is the only code visible when a retry eventually succeeds or the build is cut off.
[TestFixture]
public class TestRunEngineLockDetectionTests
{
    [Test]
    public void DetectFileLock_Msb3026InStdout_ReturnsLockDetailAsync()
    {
        var detail = TestRunEngine.DetectFileLock(
            "warning MSB3026: Could not copy \"a.dll\" to \"b.dll\". Beginning retry 1 in 1000ms.", string.Empty);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail, Does.Contain("locked").And.Contain("Test Explorer"));
    }

    [Test]
    public void DetectFileLock_Msb3027InStderr_ReturnsLockDetailAsync()
    {
        var detail = TestRunEngine.DetectFileLock(
            string.Empty, "error MSB3027: Could not copy \"a.dll\" to \"b.dll\". Exceeded retry count of 10.");

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail, Does.Contain("locked"));
    }

    [Test]
    public void DetectFileLock_CleanOutput_ReturnsNull()
    {
        var detail = TestRunEngine.DetectFileLock("Passed!  - Failed: 0, Passed: 3", string.Empty);

        Assert.That(detail, Is.Null);
    }

    [Test]
    public void ParseFixtureTearDownFailures_SampleStderr_ReturnsOneEntry()
    {
        const string stderr = "TearDown failed for test fixture FailingTearDown\nteardown boom\nStackTrace:    at FailingTearDown.Boom() in C:\\x\\Tests.cs:line 7\n\nTest Run Failed.\n";

        var entries = TestRunEngine.ParseFixtureTearDownFailures(stderr);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Signature, Does.StartWith(TestRunEngine.TearDownFailureSignaturePrefix).And.Contain("teardown boom"));
        Assert.That(entries[0].ExampleRef, Is.EqualTo("FailingTearDown"));
        Assert.That(entries[0].Count, Is.EqualTo(1));
    }

    [Test]
    public void ParseFixtureTearDownFailures_CleanOrEmptyStderr_ReturnsEmptyList()
    {
        Assert.That(TestRunEngine.ParseFixtureTearDownFailures("Passed!  - Failed: 0, Passed: 3"), Is.Empty);
        Assert.That(TestRunEngine.ParseFixtureTearDownFailures(string.Empty), Is.Empty);
    }

    [Test]
    public void ParseFixtureTearDownFailures_TwoFixtures_ReturnsTwoEntries()
    {
        const string stderr = "TearDown failed for test fixture FixtureA\nboom a\nStackTrace: at A\nTearDown failed for test fixture FixtureB\nboom b\nStackTrace: at B\n";

        var entries = TestRunEngine.ParseFixtureTearDownFailures(stderr);

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries[0].ExampleRef, Is.EqualTo("FixtureA"));
        Assert.That(entries[1].ExampleRef, Is.EqualTo("FixtureB"));
    }

    [Test]
    public void ParseFixtureTearDownFailures_MultiLineMessage_JoinsLinesWithSpaces()
    {
        var entries = TestRunEngine.ParseFixtureTearDownFailures(
            "TearDown failed for test fixture F\r\nline one\r\nline two\r\nStackTrace: at X");

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Signature, Is.EqualTo(TestRunEngine.TearDownFailureSignaturePrefix + "F: line one line two"));
    }

    [Test]
    public void ParseFixtureTearDownFailures_SameFixtureTwice_MergesToCountTwo()
    {
        const string once = "TearDown failed for test fixture F\nboom\nStackTrace: at F\n";

        var entries = TestRunEngine.ParseFixtureTearDownFailures(once + once);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Count, Is.EqualTo(2));
    }

    [Test]
    public void PrependTearDownFailures_TearDownEntriesComeFirst()
    {
        var testGroups = Enumerable.Range(1, 12)
            .Select(i => new GroupedCountSummary(Signature: $"test failure {i}", Count: 1, ExampleRef: $"Test{i}"))
            .ToList();
        var teardown = new GroupedCountSummary(Signature: TestRunEngine.TearDownFailureSignaturePrefix + "F: boom", Count: 1, ExampleRef: "F");

        var result = TestRunEngine.PrependTearDownFailures(testGroups, new[] { teardown });

        Assert.That(result[0], Is.EqualTo(teardown));
        Assert.That(result.Take(10), Does.Contain(teardown));
        Assert.That(result, Has.Count.EqualTo(13));
    }

    [Test]
    public void PrependTearDownFailures_NoTearDown_ReturnsTestGroupsUnchanged()
    {
        var testGroups = new List<GroupedCountSummary>
        {
            new(Signature: "first", Count: 2, ExampleRef: "A"),
            new(Signature: "second", Count: 1, ExampleRef: "B"),
        };

        var result = TestRunEngine.PrependTearDownFailures(testGroups, Array.Empty<GroupedCountSummary>());

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Signature, Is.EqualTo("first"));
        Assert.That(result[1].Signature, Is.EqualTo("second"));
    }
}

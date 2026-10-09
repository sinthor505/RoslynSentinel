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
}

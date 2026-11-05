using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Pins FilePathLock's platform-conditional key comparer (OrdinalIgnoreCase on Windows, Ordinal elsewhere);
// see docs/current/issues/issue_path_comparison_case_sensitivity.md. The path need not exist: the lock only normalizes it.
[TestFixture]
public class FilePathLockTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), "fpl-" + Guid.NewGuid().ToString("N") + ".txt");

    [Test]
    public async Task IsLocked_SamePathDifferentCasing_FollowsPlatformCaseSensitivity()
    {
        var path = NewTempPath();
        var differentCase = path.ToUpperInvariant();
        Assert.That(differentCase, Is.Not.EqualTo(path), "Test setup: the recased path must differ from the original.");

        using (await FilePathLock.AcquireAsync(new FilePathWrapper(path)))
        {
            // Windows folds case into one lock key; case-sensitive platforms treat the two spellings as different files.
            Assert.That(FilePathLock.IsLocked(new FilePathWrapper(differentCase)), Is.EqualTo(OperatingSystem.IsWindows()));
        }
    }

    [Test]
    public async Task IsLocked_SamePathSameCasing_IsTrueWhileHeldAndFalseAfterRelease()
    {
        var path = NewTempPath();

        using (await FilePathLock.AcquireAsync(new FilePathWrapper(path)))
        {
            Assert.That(FilePathLock.IsLocked(new FilePathWrapper(path)), Is.True);
        }

        Assert.That(FilePathLock.IsLocked(new FilePathWrapper(path)), Is.False);
    }
}

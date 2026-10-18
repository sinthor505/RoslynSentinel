using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("PersistentWorkspaceManagerWatcherPath")]
public class PersistentWorkspaceManagerWatcherPathTests
{
    [TestCase(@"C:\repo\Proj\obj\Debug\x.cs")]
    [TestCase(@"C:\repo\Proj\bin\Debug\x.cs")]
    [TestCase(@"C:\repo\Proj\.git\HEAD")]
    [TestCase(@"C:\repo\Proj\.vs\slnx.v17\x.cs")]
    [TestCase(@"C:\repo\Proj\node_modules\pkg\x.js")]
    [TestCase(@"C:\repo\Worktree\clone\A.cs")]
    [TestCase(@"C:/repo/Proj/obj/Debug/x.cs")]
    [TestCase(@"C:/repo/BIN/x.cs")]
    [TestCase(@"C:\repo\OBJ\x.cs")]
    [TestCase(@"C:\repo\worktree\clone\A.cs")]
    [TestCase(@"C:/repo/.GIT/x.cs")]
    [TestCase(@"C:/repo/Node_Modules/x.js")]
    public void IsIgnoredWatcherPath_DirectorySegmentIsIgnoredName_ReturnsTrue(string fullPath)
    {
        Assert.That(RoslynSentinel.Common.PersistentWorkspaceManager.IsIgnoredWatcherPath(fullPath), Is.True);
    }

    [TestCase(@"C:\repo\Proj\Foo.cs")]
    [TestCase(@"C:\repo\Proj\obj.cs")]
    [TestCase(@"C:\repo\Proj\Worktree.cs")]
    [TestCase(@"C:\repo\binary\x.cs")]
    [TestCase(@"C:\repo\objects\x.cs")]
    [TestCase(@"C:\repo\MyWorktree\x.cs")]
    public void IsIgnoredWatcherPath_NotIgnoredPath_ReturnsFalse(string fullPath)
    {
        Assert.That(RoslynSentinel.Common.PersistentWorkspaceManager.IsIgnoredWatcherPath(fullPath), Is.False);
    }
}

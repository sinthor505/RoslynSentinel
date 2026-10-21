namespace RoslynSentinel.Tests;

[TestFixture]
// The working directory is process-wide and the guard deletes every *.cs in it, so these tests must not run in parallel with other fixtures.
[NonParallelizable]
public class WorkingDirectoryLeakGuardTests
{
    [Test]
    public void DeleteLeakedSourceFiles_RemovesStaleCrlfOrderCs()
    {
        var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Order.cs");
        System.IO.File.WriteAllText(path, "namespace Stale;\r\npublic class Old { }\r\n");
        try
        {
            WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
            Assert.That(System.IO.File.Exists(path), Is.False);
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    [Test]
    public void DeleteLeakedSourceFiles_LeavesSubdirectoryFilesAlone()
    {
        var dir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "LeakGuardSub_" + System.Guid.NewGuid().ToString("N"));
        var file = System.IO.Path.Combine(dir, "X.cs");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(file, "namespace Sub;\r\npublic class X { }\r\n");
            WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
            Assert.That(System.IO.File.Exists(file), Is.True);
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }
}

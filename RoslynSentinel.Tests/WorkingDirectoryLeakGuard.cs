namespace RoslynSentinel.Tests;

public static class WorkingDirectoryLeakGuard
{
    /// <summary>
    /// Clears bare-relative-name .cs files (e.g. "Order.cs") that disk-writing tools leave in the process working
    /// directory. A later test reusing the name would see the stale file as its on-disk pre-image (the EOL guard
    /// reads disk, not the workspace). Callers MUST be [NonParallelizable]: the working directory is shared by
    /// parallel fixtures in the assembly (ParallelScope.Fixtures).
    /// </summary>
    public static void DeleteLeakedSourceFiles()
    {
        foreach (var leaked in System.IO.Directory.GetFiles(System.IO.Directory.GetCurrentDirectory(), "*.cs", System.IO.SearchOption.TopDirectoryOnly))
        {
            System.IO.File.Delete(leaked);
        }
    }
}

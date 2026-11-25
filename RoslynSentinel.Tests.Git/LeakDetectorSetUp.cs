// Global namespace on purpose - a SetUpFixture in a namespace only covers that namespace.
[SetUpFixture]
public sealed class LeakDetectorSetUp : RoslynSentinel.Tests.WorkingDirectoryLeakDetector
{
    // RepoQueryProvider.Current is process-wide static. The production install is AddRoslynSentinelEnginesBasic.
    // This install keeps unit tests that never build a provider on the same git route as production.
    // A test that swaps RepoQueryProvider.Current must restore it, and is [NonParallelizable] if it runs beside readers.
    [OneTimeSetUp]
    public void InstallRepoQueryProvider() => RoslynSentinel.Common.RepoQueryProvider.Current ??= RoslynSentinel.Common.GitReaders.Default;
}

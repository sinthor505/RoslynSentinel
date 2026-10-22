// Global namespace on purpose - a SetUpFixture in a namespace only covers that namespace.
[SetUpFixture]
public sealed class LeakDetectorSetUp : RoslynSentinel.Tests.WorkingDirectoryLeakDetector
{
}

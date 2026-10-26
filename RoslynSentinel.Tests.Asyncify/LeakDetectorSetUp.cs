// Global namespace on purpose - a SetUpFixture in a namespace only covers that namespace.
[SetUpFixture]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Global namespace on purpose - a SetUpFixture in a namespace only covers that namespace")]
public sealed class LeakDetectorSetUp : RoslynSentinel.Tests.WorkingDirectoryLeakDetector
{
}

namespace RoslynSentinel.Tests;

/// <summary>
/// This assembly builds in-process hosts (SubAgentRegistrationTests, TranscriptReplayTests, ModelEvalToolSurfaces),
/// so it applies the ambient test-host defaults before any of them run. A <c>[SetUpFixture]</c> covers only the
/// tests in its own namespace and below, so it sits in <c>RoslynSentinel.Tests</c>, the common prefix of the
/// Harness test namespaces (ModelEval, PlanStepRunner, SubAgent), to reach every test class.
/// </summary>
[SetUpFixture]
public class HarnessAssemblySetUp
{
    [OneTimeSetUp]
    public void ApplyAmbientTestHostDefaults() => TestHostAmbientDefaults.Apply();
}

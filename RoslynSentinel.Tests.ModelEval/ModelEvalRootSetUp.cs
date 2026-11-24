namespace RoslynSentinel.Tests;

/// <summary>
/// The LiveModel <c>ModelEvalAssemblySetUp</c> covers only its own namespace (<c>RoslynSentinel.Tests.ModelEval.LiveModel</c>
/// and below). This root-namespace fixture covers the whole assembly, so every ModelEval test that builds an in-process
/// host (for example EditArmToolSurfaceTests, ChainEvalToolSurfaceLeanTests, RunTestOnFixtureTests and
/// StopAdherenceControlRunTestTests) gets the ambient test-host defaults, including call transcripts going to
/// call-transcripts-tests. Apply is idempotent, so both fixtures may run.
/// </summary>
[SetUpFixture]
public class ModelEvalRootSetUp
{
    [OneTimeSetUp]
    public void ApplyAmbientTestHostDefaults() => TestHostAmbientDefaults.Apply();
}

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Guards the cross-file branch of SyncInterface action=sync: the class path is passed in a different letter case than the workspace holds it,
// and the interface file must still be the one that receives the synced member (path compares go through PathComparison, not raw string ==).
[TestFixture]
[Category("PathComparison")]
public sealed class SyncInterfaceCasedPathTests
{
    [Test]
    public async Task SyncInterface_sync_UpperCasedClassPath_UpdatesInterfaceFileLikeCorrectCasing()
    {
        const string workerSrc = @"namespace TestProj;
public class Worker : IWorker { public void Work() {} public void Extra() {} }";
        const string ifaceSrc = @"namespace TestProj;
public interface IWorker { void Work(); }";
        using var workspace = InMemoryWorkspace.Create(
            ("Worker.cs", workerSrc.ReplaceLineEndings("\r\n")),
            ("IWorker.cs", ifaceSrc.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs").ToUpperInvariant(), "IWorker", SyncInterfaceAction.sync, "Worker");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("IWorker.cs"), Does.Contain("void Extra()"));
        Assert.That(workspace.ReadText("Worker.cs"), Is.EqualTo(workerSrc.ReplaceLineEndings("\r\n")));
    }
}

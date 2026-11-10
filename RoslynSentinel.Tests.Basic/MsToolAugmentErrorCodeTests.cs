using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Pins the <c>ErrorCode</c> of <c>MsToolAugmentEngine</c> failures that public input can reach.
/// One test per distinct code. <c>Exception</c> (internal faults) and <c>EolChangeRefused</c> (needs a
/// write refusal) are not reachable from public input and are not pinned here.
/// </summary>
[TestFixture]
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class MsToolAugmentErrorCodeTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
    }

    private void SetSource(string source, string fileName = "Entity.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ExtractConstantSafe_InvalidIdentifier_ReturnsInvalidArgument()
    {
        SetSource("public class Entity\n{\n    public string Name() { return \"hello\"; }\n}\n");

        var result = await _engine.ExtractConstantSafeAsync("Entity.cs", "\"hello\"", "1bad");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ExtractMethodSafe_MissingMethodName_ReturnsNotFound()
    {
        SetSource("public class Entity\n{\n    public void Run() { int a = 1; }\n}\n");

        var result = await _engine.ExtractMethodSafeAsync("Entity.cs", "Extracted", "Missing", extractEntireBody: true);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ExtractMethodSafe_EmptyBody_ReturnsTargetIneligible()
    {
        SetSource("public class Entity\n{\n    public void Noop() { }\n}\n");

        var result = await _engine.ExtractMethodSafeAsync("Entity.cs", "Extracted", "Noop", extractEntireBody: true);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }

    [Test]
    public async Task ExtractMethodSafe_OverloadedMethodName_ReturnsAmbiguous()
    {
        SetSource("public class Entity\n{\n    public void Run() { int a = 1; }\n    public void Run(int x) { int b = 2; }\n}\n");

        var result = await _engine.ExtractMethodSafeAsync("Entity.cs", "Extracted", "Run", extractEntireBody: true);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
    }
}

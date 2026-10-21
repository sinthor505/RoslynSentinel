using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests; split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SyncTypeAndFilenameTests
{
    private const string SimpleSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }

    public Order(int orderId, string customerName)
    {
        OrderId = orderId;
        CustomerName = customerName;
    }

    public string GetLabel()
    {
        return string.Format(""{0}: {1}"", OrderId, CustomerName);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }
}

public interface IService
{
    string GetLabel();
}

public enum Status { Active = 1, Pending = 2 }
";

    private const string MismatchedSource = "namespace TestProj;\n\npublic class HelperResult\n{\n    public bool Ok { get; set; }\n}\n\npublic class MainService\n{\n    public int Id { get; set; }\n}\n";

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), config),
            new StructuralRefinementEngine(workspaceManager, config),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
    }

    // TODO SyncTypeAndFilenameTool reports a file that already matches its type name as an error; decide whether a no-op should be success
    // Pins current behaviour: a no-op rename is reported as an error (see TODO if that should change).
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_FileAlreadyMatchingItsType_ReportsNoChangeAndLeavesFileUnchanged()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", SimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Order.cs"));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("no change"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(SimpleSource));
    }

    // Regression test for blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md
    // Symptom 1: without targetTypeName, SyncTypeAndFilenameTool picked whichever type happened to be
    // declared first, not necessarily the one the caller actually wanted. This reproduces that
    // shape - targetTypeName must be able to select the LAST-declared type.
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_TargetTypeName_SelectsNonFirstDeclaredTypeAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("Mismatched.cs", MismatchedSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Mismatched.cs"), targetTypeName: "MainService");
        Assert.That(!result.IsError, Is.True, $"Expected rename to succeed; error: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.AffectedFiles, Has.Some.Matches<FilePathWrapper>(p => p.Absolute.Contains("MainService.cs")), "Should target MainService (explicitly named), not HelperResult (first-declared).");
    }

    // Companion negative case: an unknown targetTypeName must fail with an actionable error naming
    // the types that actually exist in the file, not silently fall back to the first-declared type.
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_UnknownTargetTypeName_ReturnsActionableErrorAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("Mismatched.cs", MismatchedSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Mismatched.cs"), targetTypeName: "DoesNotExist");
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("DoesNotExist"));
        Assert.That(result.ErrorData!.Message, Does.Contain("HelperResult"));
        Assert.That(result.ErrorData!.Message, Does.Contain("MainService"));
    }
}

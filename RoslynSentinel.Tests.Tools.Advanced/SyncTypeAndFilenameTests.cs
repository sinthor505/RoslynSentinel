using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests; split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SyncTypeAndFilenameTests
{
    private static readonly string SimpleSource = Sources.Order;

    private const string MismatchedSource = "namespace TestProj;\n\npublic class HelperResult\n{\n    public bool Ok { get; set; }\n}\n\npublic class MainService\n{\n    public int Id { get; set; }\n}\n";

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_FileAlreadyMatchingItsType_SucceedsWithoutChangeAndLeavesFileUnchanged()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", SimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Order.cs"));
        Assert.That(result.IsError, Is.False);
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
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Mismatched.cs"), targetTypeName: "DoesNotExist");
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("DoesNotExist"));
        Assert.That(result.ErrorData!.Message, Does.Contain("HelperResult"));
        Assert.That(result.ErrorData!.Message, Does.Contain("MainService"));
    }

    // An existing file at the rename target must be refused as TargetIneligible with a next step, and must not be
    // overwritten. The in-memory workspace root is never created on disk, so the colliding file is written there
    // directly (same directory as the document) and removed in finally.
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task SyncTypeAndFilename_TargetFileAlreadyExists_FailsWithTargetIneligibleAsync()
    {
        const string wrongSource = "namespace TestProj;\n\npublic class Right\n{\n    public int Id { get; set; }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Wrong.cs", wrongSource));
        var rootDir = Path.GetDirectoryName(workspace.PathOf("Wrong.cs"))!;
        var rightPath = Path.Combine(rootDir, "Right.cs");
        try
        {
            Directory.CreateDirectory(rootDir);
            File.WriteAllText(rightPath, "existing content");
            var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
            var result = await tools.SyncTypeAndFilenameTool(reason: "test message", workspace.PathOf("Wrong.cs"));
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
            Assert.That(result.ErrorData!.Message, Does.Contain("refusing to overwrite"));
            Assert.That(result.ErrorData!.Message, Does.Contain("targetTypeName"));
            Assert.That(File.ReadAllText(rightPath), Is.EqualTo("existing content"), "The existing file must not be overwritten.");
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }
}

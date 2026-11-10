using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveMember keepDelegator coverage moved from Tests.Tools.Advanced (BatteryTwentyFourTests, DryRunLineChangesTests) during the core split. In-memory (InMemoryWorkspace).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberKeepDelegatorTests
{
    private static void AssertPlausibleDryRunLineChanges(AppliedChangeSummary summary)
    {
        Assert.That(summary.DryRun, Is.True, "The summary must be flagged as a dry run.");
        Assert.That(summary.LineChanges, Is.Not.Null.And.Not.Empty, "A dry run must fill LineChanges.");
        Assert.That(summary.LineChanges!.Sum(change => change.LinesAdded), Is.GreaterThan(0), "LinesAdded must count the preview's added lines.");
        Assert.That(summary.LineChanges!.Sum(change => change.LinesRemoved), Is.GreaterThan(0), "LinesRemoved must count the preview's removed lines.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_NoEolOrCollateralRefusal()
    {
        const string original = "public class Src\r\n{\r\n    public static int Foo(int x) { return x + 1; }\r\n}\r\n\r\npublic class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(text, Does.Contain("public static int Foo(int x) => global::Target.Foo(x);"), "The forwarder is written.");
        Assert.That(text, Does.Not.Match("(?<!\r)\n"), "The forwarder must keep the file's CRLF line endings.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_DryRun_FillsLineChangesAndWritesNothing()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = workspace.ReadText("Refactor.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", dryRun: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        AssertPlausibleDryRunLineChanges(result.SuccessData!);
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(before), "A dry run must not write the file.");
    }
}

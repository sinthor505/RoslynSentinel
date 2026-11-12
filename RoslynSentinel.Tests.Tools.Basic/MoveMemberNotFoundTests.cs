using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// MoveMember not-found guidance (Decision 2 standard): a ranked "Did you mean" with one prefilled retry, or the
// report-it-does-not-exist option when no member name is similar. In-memory (InMemoryWorkspace).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberNotFoundTests
{
    private const string Original = "public class Src\r\n{\r\n    public static int CalcDisc(int x) { return x; }\r\n    public static int OrderCheckout(int x) { return x; }\r\n    public static int GetTotal(int x) { return x; }\r\n}\r\n\r\npublic class Target\r\n{\r\n}\r\n";

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NotFound_NearMissName_SuggestsNearestNameWithRetry()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", Original));
        var before = workspace.ReadText("Refactor.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["CalcDsc"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("Did you mean 'CalcDisc'"), "The nearest member name is suggested.");
        Assert.That(message, Does.Contain("MoveMember("), "The prefilled retry names the tool.");
        Assert.That(message, Does.Contain("CalcDsc"), "The requested name is echoed.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NotFound_NoSimilarName_SaysItDoesNotExist()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", Original));
        var before = workspace.ReadText("Refactor.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["ApplyLoyaltyBonus"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("has a similar name"), "The no-candidate sentence is present.");
        Assert.That(message, Does.Contain("tell the user"), "The report-it-does-not-exist option is present.");
        Assert.That(message, Does.Not.Contain("Did you mean"), "No suggestion is made without a similar name.");
        Assert.That(message, Does.Not.Contain("You MUST"), "No mandatory wording.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NotFound_PartialMatch_KeepsNothingMovedAndTellsTheUser()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", Original));
        var before = workspace.ReadText("Refactor.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["CalcDisc", "ApplyLoyaltyBonus"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("were not found"), "The partial-match branch is taken.");
        Assert.That(message, Does.Contain("Nothing was moved"), "The partial-match branch still says nothing was moved.");
        Assert.That(message, Does.Contain("tell the user"), "The report-it-does-not-exist option is present.");
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(before), "A refused move must not write the file.");
    }
}

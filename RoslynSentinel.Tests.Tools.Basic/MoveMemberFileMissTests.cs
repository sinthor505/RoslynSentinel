using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Basic;

// File-not-found text of the two MoveMember-family file lookups that moved onto DocumentLookup.RequireDocument: the MoveMember tool
// (MemberRefactoringEngine.MoveMemberAsync) and PreviewInstanceMoveCallSitesAsync. A near-miss file name gets the "Did you mean"
// candidate; an unrelated name gets the no-similar-name fact. Facts and options only: no policy wording.
// PreviewInstanceMoveCallSitesAsync has no tool of its own: it is reached only after MoveMemberAsync has resolved the same file, so its
// producer is tested through the engine (as PreviewInstanceMoveCallSitesTests does).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberFileMissTests
{
    private const string Fixture = "public class OrderPricingCalculator\r\n{\r\n    public static int Price(int qty) => qty;\r\n}\r\n\r\npublic class Target\r\n{\r\n}\r\n";

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_MisspeltFileName_OffersTheNearFileAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var before = workspace.ReadText("OrderPricingCalculator.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("OrderPricingCalculater.cs"), "OrderPricingCalculator", ["Price"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("Did you mean"));
        Assert.That(message, Does.Contain("OrderPricingCalculator.cs"));
        Assert.That(workspace.ReadText("OrderPricingCalculator.cs"), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NoSimilarFileName_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var before = workspace.ReadText("OrderPricingCalculator.cs");
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("ShippingLabelPrinter.cs"), "OrderPricingCalculator", ["Price"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("No file in the solution has a similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("OrderPricingCalculator.cs"), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task PreviewInstanceMoveCallSites_MisspeltFileName_ThrowsNotFoundWithTheNearFileCandidate()
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var engine = CreateEngine(workspace);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("OrderPricingCalculater.cs"), "OrderPricingCalculator", ["Price"], "Target"));
        Assert.That(ex!.Message, Does.Contain("Did you mean"));
        Assert.That(ex.Message, Does.Contain("OrderPricingCalculator.cs"));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("document"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task PreviewInstanceMoveCallSites_NoSimilarFileName_ThrowsNotFoundWithTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var engine = CreateEngine(workspace);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("ShippingLabelPrinter.cs"), "OrderPricingCalculator", ["Price"], "Target"));
        Assert.That(ex!.Message, Does.Contain("No file in the solution has a similar name"));
        Assert.That(ex.Message, Does.Not.Contain("Did you mean"));
        Assert.That(ex.Message, Does.Not.Contain("tell the user"));
        Assert.That(ex.Message, Does.Not.Contain("You MUST"));
    }

    private static MemberRefactoringEngine CreateEngine(InMemoryWorkspace workspace)
    {
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(workspace.Manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation);
    }
}

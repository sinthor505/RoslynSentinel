using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ExtractInterfaceNotFoundTests
{
    private static string SourceFixture() => """
        public class OrderPricingCalculator
        {
            public int Price(int qty) => qty;
        }
        """.ReplaceLineEndings("\r\n");

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractInterface_NearMissClassName_ReturnsNotFoundWithDidYouMean()
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "OrderPricingCalculater", ExtractAsType.@interface, newTypeName: "IOrderPricingCalculator");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Did you mean 'OrderPricingCalculator'"));
        Assert.That(result.ErrorData?.Message, Does.Contain("className"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractInterface_NoSimilarClassName_NamesOutlineAndNoCandidate()
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "ShippingLabelPrinter", ExtractAsType.@interface, newTypeName: "IShippingLabelPrinter");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("has a similar name"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Contain("does not exist"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    // An indexer has no name, so there is no candidate (Did you mean) case for ConvertIndexerToMethod: only the no-indexer text.
    [Test]
    [Category("BasicRefactoringEngine")]
    public async Task ConvertIndexerToMethod_NoIndexerDeclared_NamesOutlineAndNoTellTheUser()
    {
        var original = "public class Plain\r\n{\r\n    public int Value => 1;\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var engine = Battery.Basic.TestEngines.BasicRefactoringEngine(workspace.Manager, NullLogger<BasicRefactoringEngine>.Instance, Battery.Basic.TestEngines.SentinelConfiguration());
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.ConvertIndexerToMethodAsync(workspace.PathOf("Source.cs")));
        Assert.That(ex!.Message, Does.Contain("No indexer is declared"));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        Assert.That(ex.Message, Does.Not.Contain("tell the user"));
    }
}

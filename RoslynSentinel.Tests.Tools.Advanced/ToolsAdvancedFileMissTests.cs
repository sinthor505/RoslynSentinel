using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// File-not-found text of the advanced tools that take a file path. A near-miss file name gets the "Did you mean"
// candidate; an unrelated name gets the no-similar-name fact. Facts and options only: no policy wording.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ToolsAdvancedFileMissTests
{
    private const string Fixture = "public class OrderPricingCalculator\r\n{\r\n    public int Price(int qty) => qty;\r\n}\r\n";

    public enum FileMissTool
    {
        GenerateHttpClient,
        InterpolateStringSafe,
        IntroduceParameterObject,
        SyncInterfaceImplement,
    }

    [TestCase(FileMissTool.GenerateHttpClient)]
    [TestCase(FileMissTool.InterpolateStringSafe)]
    [TestCase(FileMissTool.IntroduceParameterObject)]
    [TestCase(FileMissTool.SyncInterfaceImplement)]
    public async Task NearMissFileName_ReturnsNotFoundWithDidYouMean(FileMissTool tool)
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var result = await CallAsync(tool, workspace, workspace.PathOf("OrderPricingCalculater.cs"));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("Did you mean"));
        Assert.That(message, Does.Contain("OrderPricingCalculator.cs"));
    }

    [TestCase(FileMissTool.GenerateHttpClient)]
    [TestCase(FileMissTool.InterpolateStringSafe)]
    [TestCase(FileMissTool.IntroduceParameterObject)]
    [TestCase(FileMissTool.SyncInterfaceImplement)]
    public async Task NoSimilarFileName_ReturnsNotFoundWithTheNoCandidateFact(FileMissTool tool)
    {
        using var workspace = InMemoryWorkspace.Create(("OrderPricingCalculator.cs", Fixture));
        var result = await CallAsync(tool, workspace, workspace.PathOf("ShippingLabelPrinter.cs"));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("No file in the solution has a similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("Verify the path"));
        Assert.That(message, Does.Not.Contain("You MUST"));
    }

    private static Task<SentinelCallToolResult<object>> CallAsync(FileMissTool tool, InMemoryWorkspace workspace, string path) => tool switch
    {
        FileMissTool.GenerateHttpClient => BatteryToolFactory.CreateGenerationTools(workspace.Manager).GenerateHttpClientTool(reason: "test message", path, "OrderPricingCalculator"),
        FileMissTool.InterpolateStringSafe => BatteryToolFactory.CreateGenerationTools(workspace.Manager).InterpolateStringSafeTool(reason: "test message", path, "string.Format("),
        FileMissTool.IntroduceParameterObject => BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager).IntroduceParameterObjectTool(reason: "test message", path, "Price"),
        _ => BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager).SyncInterfaceTool(reason: "test message", path, "IOrderPricingCalculator", SyncInterfaceAction.implement, "OrderPricingCalculator"),
    };
}

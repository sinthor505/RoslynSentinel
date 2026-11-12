using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found hints for declaration lookups (DeclarationNotFoundDetail). Each refused call must name the nearest
// declared name, or say the name does not exist in the file, and must write nothing.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class BasicRefactoringNotFoundTests
{
    private const string CalculatorSource = "public class OrderPricingCalculator\r\n{\r\n    public decimal CalcDisc(decimal total) => total * 0.9m;\r\n\r\n    public decimal OrderCheckout(decimal total) => CalcDisc(total);\r\n\r\n    public decimal GetTotal(decimal total) => total;\r\n}\r\n";

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_NearMissMethodName_SuggestsTheNearestMethodAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Calculator.cs", CalculatorSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calculator.cs"), "CalcDsc", new[] { new ChangeSignatureParameterInput(originalIndex: 0) });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("Did you mean 'CalcDisc'"));
        Assert.That(workspace.ReadText("Calculator.cs"), Is.EqualTo(CalculatorSource), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ChangeSignature_NoSimilarMethodName_SaysItDoesNotExistInTheFileAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create(("Calculator.cs", CalculatorSource));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.ChangeSignatureTool(reason: "test message", workspace.PathOf("Calculator.cs"), "ApplyLoyaltyBonus", new[] { new ChangeSignatureParameterInput(originalIndex: 0) });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("has a similar name").And.Contain("it does not exist in this file.").And.Contain("GetFileOutline"));
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("tell the user"));
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("Did you mean").And.Not.Contain("You MUST"));
        Assert.That(workspace.ReadText("Calculator.cs"), Is.EqualTo(CalculatorSource), "A refused call must not write.");
    }
}

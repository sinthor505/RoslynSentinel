using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the SymbolNavigationEngine lookups whose hosting tool is an Advanced tool (IntelligenceTools): plan_not_found_error_orientation,
// Step 5e slice 1. A near-miss name gets the ranked "Did you mean" candidate with its retry, an unrelated name gets the no-similar-name fact and
// (where the engine can offer one) a GetFileOutline option, and a refused call writes nothing (these tools are read-only, so the file text is
// compared before and after).
// Producers covered: GetCallGraphAsync and GetReverseCallGraphAsync (method lookup, through GetCallGraph direction forward and reverse),
// TraceVariableLifetimeAsync (variable lookup, through TraceVariableLifetime; the tool appends its own GetFileOutline option for NotFound).
// The Tools.Basic hosts of SymbolNavigationEngine are covered in Tests.Tools.Basic (SymbolNavigationMissTests).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("IntelligenceTools")]
public sealed class SymbolNavigationMissAdvancedTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax)
            {
                var subtotal = rate + tax;
                return subtotal;
            }
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // 1-based line of the first line of the fixture that contains the text.
    private static int LineOf(string text) =>
        Fixture.ReplaceLineEndings("\n").Split('\n').Select((line, index) => (line, index)).First(l => l.line.Contains(text)).index + 1;

    // Runs one refused call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunMissAsync(Func<IntelligenceTools, InMemoryWorkspace, Task<SentinelCallToolResult<object>>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await call(BatteryToolFactory.CreateIntelligenceTools(workspace.Manager), workspace);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return result.ErrorData?.Message ?? string.Empty;
    }

    // ---- GetCallGraph forward / reverse: GetCallGraphAsync, GetReverseCallGraphAsync ----

    [TestCase("forward")]
    [TestCase("reverse")]
    public async Task GetCallGraph_MisspeltMethod_OffersTheNearMethodAsync(string direction)
    {
        var message = await RunMissAsync((t, w) => t.GetCallGraphTool(reason: "test message", w.PathOf(FileName), "ComputeTotl", direction));
        Assert.That(message, Does.Contain("Did you mean 'ComputeTotal'? Retry with methodName: 'ComputeTotal'."));
        AssertFactsAndOptionsOnly(message);
    }

    [TestCase("forward")]
    [TestCase("reverse")]
    public async Task GetCallGraph_NoSimilarMethod_StatesTheNoCandidateFactAndOffersTheOutlineAsync(string direction)
    {
        var message = await RunMissAsync((t, w) => t.GetCallGraphTool(reason: "test message", w.PathOf(FileName), AlienName, direction));
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- TraceVariableLifetime: TraceVariableLifetimeAsync ----

    [Test]
    public async Task TraceVariableLifetime_MisspeltVariable_OffersTheNearVariableWithItsDeclarationLineAsync()
    {
        var line = LineOf("var subtotal");
        var message = await RunMissAsync((t, w) => t.TraceVariableLifetimeTool(reason: "test message", w.PathOf(FileName), "subtotl", line));
        Assert.That(message, Does.Contain($"Did you mean 'subtotal'? Retry with variableName: 'subtotal'. It is declared at line {line}; lineNumber: {line} selects it."));
        Assert.That(message, Does.Contain("searched variable declarations and parameters on that line"));
        Assert.That(message, Does.Contain("GetFileOutline"), "The tool appends its own outline option for a NotFound result.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task TraceVariableLifetime_NoSimilarVariable_StatesTheNoCandidateFactAndListsTheDeclaredNamesAsync()
    {
        var line = LineOf("var subtotal");
        var message = await RunMissAsync((t, w) => t.TraceVariableLifetimeTool(reason: "test message", w.PathOf(FileName), "zzzqqq", line));
        Assert.That(message, Does.Contain($"No variable or parameter declared in the member that contains line {line} has a similar name to 'zzzqqq'. Declared there: "));
        Assert.That(message, Does.Contain("subtotal"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"), "The tool appends its own outline option for a NotFound result.");
        AssertFactsAndOptionsOnly(message);
    }
}

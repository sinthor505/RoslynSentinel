using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the InventoryEngine file lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 2).
// GetCodeInventoryAsync is driven through GetCodeInventory, the tool that hosts it (IntelligenceTools, Advanced): a near-miss path gets the ranked
// "Did you mean" candidate, an unrelated path gets the no-similar-name fact, and both keep the existsOnDisk and projectsLoaded facts. A refused
// call writes nothing (the tool never writes, so the file text is compared before and after).
// GetCodeInventoryAsync2 is a second copy of the same method that no tool calls, so its test is engine-direct.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class InventoryMissAdvancedTests
{
    private const string FileName = "Calc.cs";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one refused call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<(bool IsError, string? Code, string? Message)>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var (isError, code, message) = await call(workspace);
        Assert.That(isError, Is.True, message);
        Assert.That(code, Is.EqualTo(nameof(ToolErrorCode.NotFound)), message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return message ?? string.Empty;
    }

    // ---- GetCodeInventory: GetCodeInventoryAsync ----

    [Test]
    [Category("InventoryEngine")]
    public async Task GetCodeInventory_MisspeltFile_OffersTheNearFileAndKeepsTheDiskFactsAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await BatteryToolFactory.CreateIntelligenceTools(w.Manager).GetCodeInventoryTool(reason: "test message", w.PathOf("Calcs.cs"));
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        Assert.That(message, Does.Contain("existsOnDisk=False, projectsLoaded=1."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("InventoryEngine")]
    public async Task GetCodeInventory_NoSimilarFile_StatesTheNoCandidateFactAndKeepsTheDiskFactsAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await BatteryToolFactory.CreateIntelligenceTools(w.Manager).GetCodeInventoryTool(reason: "test message", w.PathOf("Zzzqqq.cs"));
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Contain("existsOnDisk=False, projectsLoaded=1."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- GetCodeInventoryAsync2: no tool hosts it, so engine-direct ----

    [Test]
    [Category("InventoryEngine")]
    public async Task GetCodeInventoryAsync2_MisspeltFile_OffersTheNearFileAndKeepsTheDiskFactsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var engine = Battery.Basic.TestEngines.InventoryEngine(workspace.Manager);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await engine.GetCodeInventoryAsync2(workspace.PathOf("Calcs.cs")));

        Assert.That(ex!.Message, Does.Contain("Did you mean one of these?"));
        Assert.That(ex.Message, Does.Contain("Calc.cs"));
        Assert.That(ex.Message, Does.Contain("existsOnDisk=False, projectsLoaded=1."));
        AssertFactsAndOptionsOnly(ex.Message);
    }
}

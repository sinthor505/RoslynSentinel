using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the DiscoveryEngine lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 1).
// Each producer is driven through the tool that hosts it: a near-miss name gets the ranked "Did you mean" candidate with its retry, an unrelated
// name gets the no-similar-name fact, and a refused call writes nothing (none of these tools writes, so the file text is compared before and after).
// Producers covered: FindAttributeUsagesAsync (project lookup, through QuerySymbolRelationships attributeUsages), FindBestInsertionPointAsync (file
// lookup and type lookup, through GetBestInsertionPoint), PreviewRenameImpactAsync (file lookup, through PreviewRenameImpact).
// GetPublicApiSurfaceAsync (project lookup) is covered in Tests.Tools.Advanced (ProjectStructureMissAdvancedTests) because its tool is Advanced.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class DiscoveryMissTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        [Obsolete]
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

    private static SymbolRelationshipTools RelationshipTools(InMemoryWorkspace workspace)
    {
        var symbolNavigation = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var discovery = Battery.Basic.TestEngines.DiscoveryEngine(workspace.Manager, symbolNavigation);
        return new SymbolRelationshipTools(Battery.Basic.TestTools.SymbolRelationshipImpl(discovery, symbolNavigation, workspace.Manager, NullLogger<SymbolRelationshipImpl>.Instance));
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

    // ---- QuerySymbolRelationships(attributeUsages, projectName): FindAttributeUsagesAsync ----

    [Test]
    [Category("DiscoveryEngine")]
    public async Task QuerySymbolRelationshipsAttributeUsages_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).QuerySymbolRelationshipsTool(reason: "test message", "Obsolete", FindUsagesSearchKind.attributeUsages, projectName: "InMemoryProjct");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with projectName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiscoveryEngine")]
    public async Task QuerySymbolRelationshipsAttributeUsages_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).QuerySymbolRelationshipsTool(reason: "test message", "Obsolete", FindUsagesSearchKind.attributeUsages, projectName: "Zzz.Missing");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No loaded project has a similar name to 'Zzz.Missing'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- GetBestInsertionPoint: FindBestInsertionPointAsync (file lookup, then type lookup) ----

    [Test]
    [Category("DiscoveryEngine")]
    public async Task GetBestInsertionPoint_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).GetBestInsertionPointTool(reason: "test message", w.PathOf("Calcs.cs"), "OrderPricingCalculator", InsertionMemberKind.method);
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        Assert.That(message, Does.Contain("ReadFile"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiscoveryEngine")]
    public async Task GetBestInsertionPoint_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).GetBestInsertionPointTool(reason: "test message", w.PathOf("Zzzqqq.cs"), "OrderPricingCalculator", InsertionMemberKind.method);
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiscoveryEngine")]
    public async Task GetBestInsertionPoint_MisspeltType_OffersTheNearTypeAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).GetBestInsertionPointTool(reason: "test message", w.PathOf(FileName), "OrderPricingCalculatr", InsertionMemberKind.method);
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("Did you mean 'OrderPricingCalculator'? Retry with containerName: 'OrderPricingCalculator'."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiscoveryEngine")]
    public async Task GetBestInsertionPoint_NoSimilarType_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).GetBestInsertionPointTool(reason: "test message", w.PathOf(FileName), AlienName, InsertionMemberKind.method);
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- PreviewRenameImpact: PreviewRenameImpactAsync (file lookup) ----

    [Test]
    [Category("DiscoveryEngine")]
    public async Task PreviewRenameImpact_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).PreviewRenameImpactTool(reason: "test message", filePath: w.PathOf("Calcs.cs"), symbolName: "ComputeTotal");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("DiscoveryEngine")]
    public async Task PreviewRenameImpact_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(async w =>
        {
            var r = await RelationshipTools(w).PreviewRenameImpactTool(reason: "test message", filePath: w.PathOf("Zzzqqq.cs"), symbolName: "ComputeTotal");
            return (r.IsError, r.ErrorData?.ErrorCode.ToString(), r.ErrorData?.Message);
        });
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }
}

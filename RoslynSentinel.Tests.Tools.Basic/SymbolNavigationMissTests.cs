using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the SymbolNavigationEngine lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 1).
// Each producer is driven through the tool that hosts it: a near-miss name gets the ranked "Did you mean" candidate with its retry, an
// unrelated name gets the no-similar-name fact and one orientation call, and a refused read writes nothing (none of these tools writes, so
// the file text is compared before and after).
// Producers covered: FindCallersAsync (file-pinned miss, solution-wide miss), FindImplementationsForMemberAsync (file-pinned miss,
// solution-wide miss), GetContainerMembersAsync (through Member view), GetTypeHierarchyAsync (through GetTypeInfo).
// Not covered: GetMemberSourceAsync's defensive miss (resolved == null cannot occur once memberCandidates.Count > 0, so no tool reaches it).
// The call-graph and variable-lifetime producers live in Tests.Tools.Advanced (SymbolNavigationMissAdvancedTests) because their tools are Advanced.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SymbolNavigationMissTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;
        }

        public interface IPriced
        {
            int Price();
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

    private static SymbolNavigationTools NavigationTools(InMemoryWorkspace workspace)
    {
        var symbolNavigation = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var impactAnalyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspace.Manager, NullLogger<ImpactAnalyzer>.Instance);
        return new SymbolNavigationTools(new SymbolNavigationImpl(symbolNavigation, impactAnalyzer, workspace.Manager, NullLogger<SymbolNavigationImpl>.Instance));
    }

    // Runs one refused call and returns its error text; the call must be a NotFound error (or InvalidArgument for GetTypeInfo, see its TODO) and the file untouched.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<SentinelCallToolResult<object>>> call, string expectedCode = nameof(ToolErrorCode.NotFound))
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await call(workspace);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(expectedCode), result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return result.ErrorData?.Message ?? string.Empty;
    }

    // ---- FindReferences(callers): FindCallersAsync ----

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesCallers_FilePinnedMisspeltName_OffersTheNearNameAndSaysTheLookupNeverRanAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", "ComputeTotl", FindReferencesKind.callers, filePath: w.PathOf(FileName)));
        Assert.That(message, Does.Contain("Did you mean 'ComputeTotal'? Retry with symbolName: 'ComputeTotal'."));
        Assert.That(message, Does.Contain("not a zero-references result"));
        Assert.That(message, Does.Contain("Omitting filePath resolves symbolName by name across the solution."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesCallers_FilePinnedNoSimilarName_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", AlienName, FindReferencesKind.callers, filePath: w.PathOf(FileName)));
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Contain("not a zero-references result"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesCallers_SolutionWideMisspeltName_RanksTheNearSymbolWithAPrefilledCallAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", "ComputeTotl", FindReferencesKind.callers));
        Assert.That(message, Does.Contain("symbolName 'ComputeTotl' could not be resolved anywhere in the solution."));
        Assert.That(message, Does.Contain("not a zero-references result"));
        Assert.That(message, Does.Contain("ComputeTotal (method) in OrderPricingCalculator"));
        Assert.That(message, Does.Contain("FindReferences("));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesCallers_SolutionWideNoSimilarName_StatesTheNoCandidateFactAndOffersListAllAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", AlienName, FindReferencesKind.callers));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("ListAll"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- FindReferences(implementations): FindImplementationsForMemberAsync ----

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesImplementations_FilePinnedMisspeltName_OffersTheNearNameOnceAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", "IPricd", FindReferencesKind.implementations, filePath: w.PathOf(FileName)));
        Assert.That(message, Does.StartWith("FindReferences failed: FindImplementations: "), "The tool's error context precedes the engine text.");
        Assert.That(message, Does.Contain("Did you mean 'IPriced'? Retry with symbolName: 'IPriced'."));
        Assert.That(message, Does.Contain("not a zero-implementations result"));
        Assert.That(message.Split("Did you mean 'IPriced'?").Length - 1, Is.EqualTo(1), "The pinned-file candidates are given once, not repeated by the solution-wide text.");
        Assert.That(message, Does.Not.Contain("Did you mean one of these?"), "The solution-wide candidate list is not appended when the pinned file already named candidates.");
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesImplementations_FilePinnedNoSimilarName_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", AlienName, FindReferencesKind.implementations, filePath: w.PathOf(FileName)));
        Assert.That(message, Does.StartWith("FindReferences failed: FindImplementations: "), "The tool's error context precedes the engine text.");
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesImplementations_SolutionWideMisspeltName_RanksTheNearSymbolWithAPrefilledCallAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", "IPricd", FindReferencesKind.implementations));
        Assert.That(message, Does.StartWith("FindReferences failed: FindImplementations: symbolName 'IPricd' could not be resolved anywhere in the solution."));
        Assert.That(message, Does.Contain("not a zero-implementations result"));
        Assert.That(message, Does.Contain("IPriced (type)"));
        Assert.That(message, Does.Contain("FindReferences("));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindReferencesImplementations_SolutionWideNoSimilarName_StatesTheNoCandidateFactAndOffersListAllAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w).FindReferencesTool(reason: "test message", AlienName, FindReferencesKind.implementations));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("ListAll"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- Member(view): GetContainerMembersAsync ----

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task MemberView_MisspeltContainer_OffersTheNearContainerAndNamesWhatWasSearchedAsync()
    {
        var message = await RunMissAsync(w => Battery.Basic.TestTools.RefactoringStructuralTools(w.Manager).MemberTool(reason: "test message", w.PathOf(FileName), MemberAction.view, containerName: "OrderPricingCalculatr"));
        Assert.That(message, Does.Contain("Did you mean 'OrderPricingCalculator'? Retry with containerName: 'OrderPricingCalculator'."));
        Assert.That(message, Does.Contain("Searched: class, interface, struct, record and enum declarations"));
        Assert.That(message, Does.Contain("finds the name as text"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task MemberView_NoSimilarContainer_StatesTheNoCandidateFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync(w => Battery.Basic.TestTools.RefactoringStructuralTools(w.Manager).MemberTool(reason: "test message", w.PathOf(FileName), MemberAction.view, containerName: AlienName));
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        Assert.That(message, Does.Contain("Searched: class, interface, struct, record and enum declarations"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- GetTypeInfo: GetTypeHierarchyAsync (the tool codes this miss InvalidArgument today; see the TODO in SymbolNavigationImpl.GetTypeInfo) ----

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task GetTypeInfo_MisspeltType_RanksTheNearTypeWithAPrefilledGetTypeInfoCallAsync()
    {
        var message = await RunMissAsync(w => NavigationTools(w).GetTypeInfoTool(reason: "test message", "OrderPricingCalculatr", TypeInfoInclude.hierarchy), nameof(ToolErrorCode.InvalidArgument));
        Assert.That(message, Does.Contain("OrderPricingCalculator (type)"));
        Assert.That(message, Does.Contain("GetTypeInfo("));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task GetTypeInfo_NoSimilarType_StatesTheNoCandidateFactAndOffersListAllAsync()
    {
        var message = await RunMissAsync(w => NavigationTools(w).GetTypeInfoTool(reason: "test message", AlienName, TypeInfoInclude.hierarchy), nameof(ToolErrorCode.InvalidArgument));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("ListAll"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- InspectSymbol: file-lookup guard (plan_not_found_error_orientation, Step 5e) ----

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task InspectSymbol_MisspeltFile_ReturnsFileNearMatchAsync()
    {
        using var workspace = InMemoryWorkspace.Create((OrderPricingFixture.FileName, OrderPricingFixture.Source));
        var result = await NavigationTools(workspace).InspectSymbolTool(reason: "test message", filePath: "OrderPricingCalculater.cs", contextSnippet: "ComputeTotal", aspect: InspectSymbolAspect.info);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData?.Message);
        Assert.That(result.ErrorData?.Message, Does.Contain(OrderPricingFixture.FileName));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task InspectSymbol_SnippetNotInFile_OffersTheOutlineAsync()
    {
        using var workspace = InMemoryWorkspace.Create((OrderPricingFixture.FileName, OrderPricingFixture.Source));
        var before = workspace.ReadText(OrderPricingFixture.FileName);
        var result = await NavigationTools(workspace).InspectSymbolTool(reason: "test message", filePath: OrderPricingFixture.FileName, contextSnippet: "NoSuchTextAnywhere", aspect: InspectSymbolAspect.info);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData?.Message);
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("GetFileOutline("));
        Assert.That(message, Does.Contain(OrderPricingFixture.FileName));
        Assert.That(message, Does.Not.Contain("Re-check"));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(OrderPricingFixture.FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
    }
}

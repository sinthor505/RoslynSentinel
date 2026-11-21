using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// SymbolNavigationEngine next-call hints are surface-aware (TODO punchlist #13): the engine takes a callable predicate
// (production: ActiveToolSurface.IsCallable). On a surface where FindReferences is not callable the "did you mean" and
// snippet-miss hints name a prefilled Search call for the same symbol and file instead; with every tool callable (the
// default) the text is unchanged. Driven through FindReferences and GetTypeInfo like SymbolNavigationMissTests.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class SymbolNavigationSurfaceHintTests
{
    private const string FileName = "Calc.cs";

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

    private static readonly Func<string, bool> Everything = _ => true;

    private static readonly Func<string, bool> NoFindReferences = name => name != "FindReferences";

    private static readonly Func<string, bool> NoGetTypeInfo = name => name != "GetTypeInfo";

    private static SymbolNavigationEngine Engine(InMemoryWorkspace workspace, Func<string, bool>? isCallable) =>
        new(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance, isCallable);

    private static SymbolRelationshipTools RelationshipTools(InMemoryWorkspace workspace, Func<string, bool>? isCallable)
    {
        var symbolNavigation = Engine(workspace, isCallable);
        var discovery = Battery.Basic.TestEngines.DiscoveryEngine(workspace.Manager, symbolNavigation);
        return new SymbolRelationshipTools(Battery.Basic.TestTools.SymbolRelationshipImpl(discovery, symbolNavigation, workspace.Manager, NullLogger<SymbolRelationshipImpl>.Instance));
    }

    private static SymbolNavigationTools NavigationTools(InMemoryWorkspace workspace, Func<string, bool>? isCallable)
    {
        var symbolNavigation = Engine(workspace, isCallable);
        var impactAnalyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspace.Manager, NullLogger<ImpactAnalyzer>.Instance);
        return new SymbolNavigationTools(new SymbolNavigationImpl(symbolNavigation, impactAnalyzer, workspace.Manager, NullLogger<SymbolNavigationImpl>.Instance));
    }

    // Runs one refused call and returns its error text.
    private static async Task<string> RunMissAsync(Func<InMemoryWorkspace, Task<SentinelCallToolResult<object>>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await call(workspace);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        return result.ErrorData?.Message ?? string.Empty;
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindCallers_SolutionWideMiss_FindReferencesNotCallable_NamesSearchCallersInsteadAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w, NoFindReferences).FindReferencesTool(reason: "test message", "ComputeTotl", FindReferencesKind.callers));
        Assert.That(message, Does.Contain("ComputeTotal (method) in OrderPricingCalculator"));
        Assert.That(message, Does.Not.Contain("FindReferences("));
        Assert.That(message, Does.Contain("Search("));
        Assert.That(message, Does.Contain("mode: callers"));
        Assert.That(message, Does.Contain("query: \"ComputeTotal\""));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindImplementations_SolutionWideMiss_FindReferencesNotCallable_NamesSearchImplementationsInsteadAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w, NoFindReferences).FindReferencesTool(reason: "test message", "IPricd", FindReferencesKind.implementations));
        Assert.That(message, Does.Contain("IPriced (type)"));
        Assert.That(message, Does.Not.Contain("FindReferences("));
        Assert.That(message, Does.Contain("Search("));
        Assert.That(message, Does.Contain("mode: implementations"));
        Assert.That(message, Does.Contain("query: \"IPriced\""));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindCallers_SnippetMiss_FindReferencesNotCallable_NamesSearchWithTheFileAndDeclarationSnippetAsync()
    {
        var message = await RunMissAsync(w => RelationshipTools(w, NoFindReferences).FindReferencesTool(
            reason: "test message", "ComputeTotal", FindReferencesKind.callers, filePath: w.PathOf(FileName), contextSnippet: "public int NoSuchLine() => 0;"));
        Assert.That(message, Does.Not.Contain("FindReferences("));
        Assert.That(message, Does.Contain("Search("));
        Assert.That(message, Does.Contain("query: \"ComputeTotal\""));
        Assert.That(message, Does.Contain("contextSnippet: \"public int ComputeTotal(int rate, int tax) => rate + tax;\""));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindCallers_SolutionWideMiss_EveryToolCallable_IsIdenticalToTheDefaultAndNamesFindReferencesAsync()
    {
        var byDefault = await RunMissAsync(w => RelationshipTools(w, null).FindReferencesTool(reason: "test message", "ComputeTotl", FindReferencesKind.callers));
        var everything = await RunMissAsync(w => RelationshipTools(w, Everything).FindReferencesTool(reason: "test message", "ComputeTotl", FindReferencesKind.callers));
        Assert.That(everything, Is.EqualTo(byDefault));
        Assert.That(byDefault, Does.Contain("FindReferences("));
        Assert.That(byDefault, Does.Not.Contain("Search(mode: callers"));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task FindCallers_SnippetMiss_EveryToolCallable_IsIdenticalToTheDefaultAndNamesFindReferencesAsync()
    {
        Task<string> Run(Func<string, bool>? isCallable) => RunMissAsync(w => RelationshipTools(w, isCallable).FindReferencesTool(
            reason: "test message", "ComputeTotal", FindReferencesKind.callers, filePath: w.PathOf(FileName), contextSnippet: "public int NoSuchLine() => 0;"));

        var byDefault = await Run(null);
        var everything = await Run(Everything);
        Assert.That(everything, Is.EqualTo(byDefault));
        Assert.That(byDefault, Does.Contain("FindReferences("));
    }

    [Test]
    [Category("SymbolNavigationEngine")]
    public async Task GetTypeInfo_Miss_GetTypeInfoNotCallable_NamesSearchSymbolInsteadAsync()
    {
        var message = await RunMissAsync(w => NavigationTools(w, NoGetTypeInfo).GetTypeInfoTool(reason: "test message", "OrderPricingCalculatr", TypeInfoInclude.hierarchy));
        Assert.That(message, Does.Contain("OrderPricingCalculator (type)"));
        Assert.That(message, Does.Not.Contain("GetTypeInfo("));
        Assert.That(message, Does.Contain("Search("));
        Assert.That(message, Does.Contain("mode: symbol"));
        Assert.That(message, Does.Contain("query: \"OrderPricingCalculator\""));
    }
}

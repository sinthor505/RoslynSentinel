using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the ImpactAnalyzer lookups that moved onto the shared helpers (plan_not_found_error_orientation, Step 5e slice 2).
// AnalyzeImpactAsync is driven through InspectSymbol(aspect: blastRadius), the tool that reaches it: the file miss (candidate and no-candidate),
// the position miss (a snippet whose innermost node has no symbol: the file and line facts and a GetFileOutline option) and the metadata-symbol
// miss. The tool never writes, so the file text is compared before and after.
// GetDataFlowAsync, FindDerivedTypesAsync and FindImplementationsAsync (through the private FindSymbolRelationsAsync) are not called by any tool, so
// their tests are engine-direct: the file miss and the position miss of the line/column form.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ImpactMissAdvancedTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;

            public object Make() => new System.Text.StringBuilder();
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    // Runs one refused InspectSymbol(blastRadius) call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunBlastRadiusMissAsync(string filePathRelative, string snippet, (string Name, string Source)? extraFile = null)
    {
        using var workspace = extraFile is { } extra
            ? InMemoryWorkspace.Create((FileName, Fixture), (extra.Name, extra.Source))
            : InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var r = await BatteryToolFactory.CreateSymbolNavigationTools(workspace.Manager).InspectSymbolTool(reason: "test message", workspace.PathOf(filePathRelative), snippet, InspectSymbolAspect.blastRadius);
        Assert.That(r.IsError, Is.True, r.ErrorData?.Message);
        Assert.That(r.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), r.ErrorData?.Message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused lookup must not write the file.");
        return r.ErrorData?.Message ?? string.Empty;
    }

    // ---- InspectSymbol(blastRadius): AnalyzeImpactAsync ----

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task InspectSymbolBlastRadius_MisspeltFile_OffersTheNearFileAsync()
    {
        var message = await RunBlastRadiusMissAsync("Calcs.cs", "ComputeTotal");
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task InspectSymbolBlastRadius_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        var message = await RunBlastRadiusMissAsync(AlienName + ".cs", "ComputeTotal");
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task InspectSymbolBlastRadius_SnippetOnNodeWithoutSymbol_StatesFileLineAndNodeAndOffersTheOutlineAsync()
    {
        var message = await RunBlastRadiusMissAsync("Usings.cs", "using", ("Usings.cs", "using System;\n"));
        Assert.That(message, Does.Contain("at line 1 of"));
        Assert.That(message, Does.Contain("Usings.cs"));
        Assert.That(message, Does.Contain("innermost node (UsingDirectiveSyntax)"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task InspectSymbolBlastRadius_SnippetOnLibraryConstructor_StatesTheSymbolIsNotDeclaredInTheSolutionAsync()
    {
        var message = await RunBlastRadiusMissAsync(FileName, "new System.Text.StringBuilder()");
        Assert.That(message, Does.Contain("is not declared in this solution"));
        Assert.That(message, Does.Contain("metadata"));
        Assert.That(message, Does.Contain("lineBefore/lineAfter"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- Engine-direct: no tool calls these three ----

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task GetDataFlow_MisspeltFile_OffersTheNearFileAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var analyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspace.Manager, NullLogger<ImpactAnalyzer>.Instance);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await analyzer.GetDataFlowAsync(workspace.PathOf("Calcs.cs"), 5, 5, 5, 20));

        Assert.That(ex!.Message, Does.Contain("Did you mean one of these?"));
        Assert.That(ex.Message, Does.Contain("Calc.cs"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task FindDerivedTypes_NoSimilarFile_StatesTheNoCandidateFactAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var analyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspace.Manager, NullLogger<ImpactAnalyzer>.Instance);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await analyzer.FindDerivedTypesAsync(workspace.PathOf(AlienName + ".cs"), 3, 14));

        Assert.That(ex!.Message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(ex.Message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(ex.Message);
    }

    [Test]
    [Category("ImpactAnalyzer")]
    public async Task FindImplementations_PositionWithoutSymbol_StatesFileAndPositionAndOffersTheOutlineAsync()
    {
        // A comment-only file has no declaration above the position, so the walk up from the token finds no symbol.
        using var workspace = InMemoryWorkspace.Create(("Empty.cs", "// nothing is declared here\n"));
        var analyzer = Battery.Basic.TestEngines.ImpactAnalyzer(workspace.Manager, NullLogger<ImpactAnalyzer>.Instance);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await analyzer.FindImplementationsAsync(workspace.PathOf("Empty.cs"), 1, 1));

        Assert.That(ex!.Message, Does.Contain("No symbol was found at line 1, column 1 of"));
        Assert.That(ex.Message, Does.Contain("Empty.cs"));
        Assert.That(ex.Message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(ex.Message);
    }
}

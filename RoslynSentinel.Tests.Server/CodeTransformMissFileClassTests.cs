// Not-found text of the SolutionStructureEngine producers FixMismatchedNamespacesAsync (file lookup) and ConvertToBackgroundServiceAsync (file
// and class lookup), which moved onto DocumentLookup.RequireDocument and MemberRefactoringNotFound (plan_not_found_error_orientation, Step 5e
// slice 1). CodeTransformTools is the only tool class that reaches them, and it lives in Tools.Experimental, so these tests are here rather than
// in Tests.Tools.Basic. Runs the real tool class against the real engines resolved from the production registration, over an in-memory test
// solution. The two transforms return the transformed text and never write the workspace, so "nothing written" is asserted as an error result
// without SuccessData. Facts and options only: no policy wording, no bare "not found.".
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("CodeTransformTools")]
public class CodeTransformMissFileClassTests
{
    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        public class OrderPricingCalculator
        {
            public int ComputeTotal(int rate) => rate;
        }
        """;

    private ServiceProvider _provider = null!;
    private IWorkspaceManager _workspace = null!;
    private CodeTransformTools _tools = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRoslynSentinelEnginesAdvanced();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsAdvanced(
            services, new HashSet<string>(["Experimental"], StringComparer.OrdinalIgnoreCase));

        _provider = services.BuildServiceProvider();
        _workspace = _provider.GetRequiredService<IWorkspaceManager>();
        _tools = _provider.GetRequiredService<CodeTransformTools>();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private string SetSource(string fileName, string source)
    {
        var path = Path.Combine(Path.GetTempPath(), "TestProj", fileName);
        _workspace.SetTestSolution(RoslynSentinel.Tests.TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(path, source)]));
        return path;
    }

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private static void AssertClassCandidate(string message)
    {
        Assert.That(message, Does.Contain("Did you mean 'OrderPricingCalculator'? Retry with className: 'OrderPricingCalculator'."));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertClassNoCandidate(string message)
    {
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertFileCandidate(string message)
    {
        Assert.That(message, Does.Contain("was not found in the loaded solution"));
        Assert.That(message, Does.Contain("Did you mean one of these?"));
        Assert.That(message, Does.Contain("Calc.cs"));
        Assert.That(message, Does.Contain("ReadFile"));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertFileNoCandidate(string message)
    {
        Assert.That(message, Does.Contain("No file in the solution has a similar name."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- fix_mismatched_namespaces: FixMismatchedNamespacesAsync file lookup ----

    [Test]
    public async Task FixMismatchedNamespaces_MisspeltFile_OffersTheNearFileAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyFileCodeTransform("miss text test", Path.Combine(Path.GetDirectoryName(path)!, "Calcs.cs"), "fix_mismatched_namespaces");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertFileCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    [Test]
    public async Task FixMismatchedNamespaces_NoSimilarFile_StatesTheNoCandidateFactAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyFileCodeTransform("miss text test", Path.Combine(Path.GetDirectoryName(path)!, "Zzzqqq.cs"), "fix_mismatched_namespaces");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertFileNoCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    // ---- convert_to_background_service: ConvertToBackgroundServiceAsync file lookup, then class lookup ----

    [Test]
    public async Task ConvertToBackgroundService_MisspeltFile_OffersTheNearFileAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", Path.Combine(Path.GetDirectoryName(path)!, "Calcs.cs"), "OrderPricingCalculator", "convert_to_background_service");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertFileCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    [Test]
    public async Task ConvertToBackgroundService_NoSimilarFile_StatesTheNoCandidateFactAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", Path.Combine(Path.GetDirectoryName(path)!, "Zzzqqq.cs"), "OrderPricingCalculator", "convert_to_background_service");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertFileNoCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    [Test]
    public async Task ConvertToBackgroundService_MisspeltClass_OffersTheNearClassAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", path, "OrderPricingCalculater", "convert_to_background_service");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertClassCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    [Test]
    public async Task ConvertToBackgroundService_NoSimilarClass_StatesTheNoCandidateFactAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", path, AlienName, "convert_to_background_service");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertClassNoCandidate(result.ErrorData?.Message ?? string.Empty);
    }
}

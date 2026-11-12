// Not-found text of the MemberRefactoringEngine producers MakeMethodStaticAsync (method lookup) and SortMembersAsync (container lookup),
// which moved onto MemberRefactoringNotFound (plan_not_found_error_orientation, Step 5c sweep slice B1). CodeTransformTools is the only
// tool class that reaches them, and it lives in Tools.Experimental, so these tests are here rather than in Tests.Tools.Basic.
// Runs the real tool class against the real engines resolved from the production registration, over an in-memory test solution.
// The two transforms return the transformed text and never write the workspace, so "nothing written" is asserted as an error result
// without SuccessData. Facts and options only: no policy wording, no bare "not found.".
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
[Category("CodeTransformTools")]
public class CodeTransformMissMethodTypeTests
{
    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        public class OrderPricingCalculator
        {
            public int ApplyDiscount(int rate) => rate;

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

    private static void AssertCandidate(string message, string candidate, string paramName)
    {
        Assert.That(message, Does.Contain($"Did you mean '{candidate}'? Retry with {paramName}: '{candidate}'."));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertNoCandidate(string message)
    {
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("GetFileOutline"));
        AssertFactsAndOptionsOnly(message);
    }

    // ---- make_method_static: MakeMethodStaticAsync method lookup ----

    [Test]
    public async Task MakeMethodStatic_MisspeltMethod_OffersTheNearMethodAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyMethodCodeTransform("miss text test", path, "ComputeTotl", "make_method_static");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "ComputeTotal", "methodName");
    }

    [Test]
    public async Task MakeMethodStatic_NoSimilarMethod_StatesTheNoCandidateFactAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyMethodCodeTransform("miss text test", path, AlienName, "make_method_static");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
    }

    // ---- sort_members: SortMembersAsync container lookup (the tool's parameter is className, so the retry hint names it) ----

    [Test]
    public async Task SortMembers_MisspeltClass_OffersTheNearTypeWithTheToolsClassNameParameter()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", path, "OrderPricingCalculater", "sort_members");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "className");
    }

    [Test]
    public async Task SortMembers_NoSimilarClass_StatesTheNoCandidateFactAndReturnsNoSource()
    {
        var path = SetSource("Calc.cs", Fixture);
        var result = await _tools.ApplyClassCodeTransform("miss text test", path, AlienName, "sort_members");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Null, "A refused transform returns no source.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
    }
}

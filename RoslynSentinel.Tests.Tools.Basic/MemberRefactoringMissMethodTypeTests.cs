using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the MemberRefactoringEngine method, type and namespace lookups that moved onto MemberRefactoringNotFound
// (plan_not_found_error_orientation, Step 5c sweep slice B1). Each producer is driven through the tool that reaches it: a near-miss
// name gets the ranked "Did you mean" candidate with its retry, an unrelated name gets the no-similar-name fact and the GetFileOutline
// option, and a refused edit writes nothing. Facts and options only: no policy wording, no bare "not found.".
// Engine-direct (no tool reaches the site):
//  - AddRemoveParamsAsync: no tool class calls it.
//  - ConvertMethodToPropertyAsync: no tool class calls it.
// MakeMethodStatic and SortMembers are reached only through CodeTransformTools (Tools.Experimental), which this project cannot
// reference; their tool-level tests are in RoslynSentinel.Tests.Server/CodeTransformMissMethodTypeTests.cs.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRefactoringMissMethodTypeTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator : IPriced
        {
            public int ComputeTotal(int rate, int tax) => rate + tax;

            public int ApplyDiscount(int rate) => rate;
        }

        public interface IPriced
        {
        }
        """;

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

    private static RefactoringSignatureTools SignatureTools(InMemoryWorkspace workspace) =>
        new(Battery.Basic.TestTools.RefactoringSignatureImpl(workspace.Manager));

    private static MemberRefactoringEngine CreateEngine(InMemoryWorkspace workspace)
    {
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(workspace.Manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation);
    }

    // ---- MethodSignature view: GetMethodParametersAsync ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureView_MisspeltMethod_OffersTheNearMethodAndKeepsTheSearchedFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.view, "ComputeTotl");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertCandidate(message, "ComputeTotal", "methodName");
        Assert.That(message, Does.Contain("Searched: method declarations matching this name"));
        Assert.That(message, Does.Contain("finds the name as text"), "The Search option is kept (a catalog-less hint renders the bare tool name, so no parenthesis is asserted).");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureView_NoSimilarMethod_StatesTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.view, AlienName);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertNoCandidate(message);
        Assert.That(message, Does.Contain("Searched: method declarations matching this name"));
    }

    // ---- MethodSignature add / remove: AddMethodParameterAsync, RemoveMethodParameterAsync ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureAdd_MisspeltMethod_OffersTheNearMethodAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.add, "ComputeTotl", paramName: "extra", paramType: "int");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "ComputeTotal", "methodName");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureAdd_NoSimilarMethod_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.add, AlienName, paramName: "extra", paramType: "int");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureRemove_MisspeltMethod_OffersTheNearMethodAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, "ComputeTotl", paramName: "tax");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "ComputeTotal", "methodName");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task MethodSignatureRemove_NoSimilarMethod_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).MethodSignatureTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, AlienName, paramName: "tax");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- ModifyBaseType: AddBaseTypeAsync, RemoveBaseTypeAsync ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyBaseTypeAdd_MisspeltType_OffersTheNearTypeAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf(FileName), "OrderPricingCalculater", "IDisposable", AddRemoveAction.add);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "typeName");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyBaseTypeAdd_NoSimilarType_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf(FileName), AlienName, "IDisposable", AddRemoveAction.add);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyBaseTypeRemove_MisspeltType_OffersTheNearTypeAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf(FileName), "OrderPricingCalculater", "IPriced", AddRemoveAction.remove);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "typeName");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyBaseTypeRemove_NoSimilarType_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyBaseTypeTool(reason: "test message", workspace.PathOf(FileName), AlienName, "IPriced", AddRemoveAction.remove);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- Member addTopLevelType: AddTopLevelTypeAsync namespace lookup ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberAddTopLevelType_MisspeltNamespace_OffersTheNearNamespaceAndListsTheFilesNamespaces()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf(FileName), MemberAction.addTopLevelType, namespaceName: "Billing.Ordrs", newMemberSource: "public class Gadget { }");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertCandidate(message, "Billing.Orders", "namespaceName");
        Assert.That(message, Does.Contain("Namespaces declared in"));
        Assert.That(message, Does.Not.Contain("Available:"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task MemberAddTopLevelType_NoSimilarNamespace_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf(FileName), MemberAction.addTopLevelType, namespaceName: AlienName, newMemberSource: "public class Gadget { }");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertNoCandidate(message);
        Assert.That(message, Does.Contain("Namespaces declared in"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- Engine-direct: AddRemoveParamsAsync method lookup (no tool class calls it) ----

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task AddRemoveParams_MisspeltMethod_ThrowsNotFoundWithTheNearMethod()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).AddRemoveParamsAsync(workspace.PathOf(FileName), "ComputeTotl"));
        AssertCandidate(ex!.Message, "ComputeTotal", "methodName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("ComputeTotl"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task AddRemoveParams_NoSimilarMethod_ThrowsNotFoundWithTheNoCandidateFactAndOneOutlineOption()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).AddRemoveParamsAsync(workspace.PathOf(FileName), AlienName));
        AssertNoCandidate(ex!.Message);
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
        Assert.That(ex.Message.Split("GetFileOutline").Length - 1, Is.EqualTo(1), "The outline option is named once, not duplicated.");
    }

    // ---- Engine-direct: ConvertMethodToPropertyAsync method lookup (no tool class calls it) ----

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ConvertMethodToProperty_MisspeltMethod_ThrowsNotFoundWithTheNearMethod()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).ConvertMethodToPropertyAsync(workspace.PathOf(FileName), "ApplyDiscont"));
        AssertCandidate(ex!.Message, "ApplyDiscount", "methodName");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("ApplyDiscont"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ConvertMethodToProperty_NoSimilarMethod_ThrowsNotFoundWithTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).ConvertMethodToPropertyAsync(workspace.PathOf(FileName), AlienName));
        AssertNoCandidate(ex!.Message);
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
    }
}

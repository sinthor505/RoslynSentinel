using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the MemberRefactoringEngine class, enum, constructor-parameter and enum-member lookups that moved onto
// MemberRefactoringNotFound (plan_not_found_error_orientation, Step 5c sweep slice A). Each producer is driven through the tool that
// reaches it: a near-miss name gets the ranked "Did you mean" candidate with its retry, an unrelated name gets the no-similar-name fact
// and the GetFileOutline option, and a refused edit writes nothing. Facts and options only: no policy wording, no bare "not found.".
// Engine-direct (no tool reaches the site):
//  - PreviewInstanceMoveCallSitesAsync class lookup: MoveMemberAsync resolves the same class first, so only the engine reaches it.
//  - RemoveEnumMemberAsync / ReplaceEnumMemberAsync member lookup: the Member tool only calls them for a member name it already found
//    inside an enum, so the tool cannot supply a missing member.
// Not tested (unreachable by construction, converted for consistency): the MoveMembersToBaseType base-class lookup (the class node is
// looked up in the file the base symbol came from) and the two same-file re-lookups in MoveInstanceMembersAsync (the class was already
// resolved at the top of MoveMemberAsync).
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRefactoringMissClassEnumTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        public class OrderPricingCalculator
        {
            private readonly int _rate;

            public OrderPricingCalculator(int rate, string currency)
            {
                _rate = rate;
            }

            public OrderPricingCalculator(int rate)
            {
                _rate = rate;
            }

            public int Total => _rate;
        }

        public enum OrderStatus
        {
            Pending,
            Shipped,
            Delivered
        }

        public class Target
        {
        }
        """;

    private static int FixtureLineOf(string snippet) => Fixture.Split('\n').ToList().FindIndex(l => l.Contains(snippet, StringComparison.Ordinal)) + 1;

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

    // ---- ConstructorParameter add: class lookup (ResolveAddConstructorTargetClass) ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterAdd_MisspeltClass_OffersTheNearClassAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.add, "OrderPricingCalculater", "tax", "int");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "className");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterAdd_NoSimilarClass_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.add, AlienName, "tax", "int");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- ConstructorParameter remove: class lookup, parameter lookup, snippet outside constructors ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterRemove_MisspeltClass_OffersTheNearClassAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, "OrderPricingCalculater", "rate");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "className");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterRemove_NoSimilarClass_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, AlienName, "rate");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterRemove_MisspeltParameter_OffersTheNearParameterAndListsTheConstructorParameters()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, "OrderPricingCalculator", "curency");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertCandidate(message, "currency", "paramName");
        Assert.That(message, Does.Contain("Declared in the constructors of 'OrderPricingCalculator': rate, currency."));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterRemove_NoSimilarParameter_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, "OrderPricingCalculator", AlienName);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // The contextSnippet-outside-constructors producer has a single shape (it is not a name miss), so it has one test.
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterRemove_SnippetOutsideEveryConstructor_NamesTheConstructorLinesAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.remove, "OrderPricingCalculator", "rate", contextSnippet: "public int Total => _rate;");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        Assert.That(message, Does.Contain("contextSnippet is not inside any constructor of 'OrderPricingCalculator' that declares parameter 'rate'."));
        Assert.That(message, Does.Contain($"Constructors declaring 'rate' start at line {FixtureLineOf("public OrderPricingCalculator(int rate, string currency)")}, {FixtureLineOf("public OrderPricingCalculator(int rate)")}."));
        Assert.That(message, Does.Contain("ReadFile"));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- ConstructorParameter view: GetConstructorParametersAsync class lookup (keeps the search-scope fact it always stated) ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterView_MisspeltClass_OffersTheNearClassAndKeepsTheSearchedFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.view, "OrderPricingCalculater");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertCandidate(message, "OrderPricingCalculator", "className");
        Assert.That(message, Does.Contain("Searched class, interface, struct, record and enum declarations matching this name"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task ConstructorParameterView_NoSimilarClass_StatesTheNoCandidateFactAndKeepsTheSearchedFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await SignatureTools(workspace).ConstructorParameterTool(reason: "test message", workspace.PathOf(FileName), AddRemoveViewAction.view, AlienName);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        var message = result.ErrorData?.Message ?? string.Empty;
        AssertNoCandidate(message);
        Assert.That(message, Does.Contain("Searched class, interface, struct, record and enum declarations matching this name"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    // ---- ModifyEnum: enum lookup ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyEnum_MisspeltEnum_OffersTheNearEnumAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyEnumTool(reason: "test message", workspace.PathOf(FileName), "OrderStatu", "Pending,Shipped,Delivered");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderStatus", "enumName");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyEnum_NoSimilarEnum_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);
        var result = await tools.ModifyEnumTool(reason: "test message", workspace.PathOf(FileName), AlienName, "Pending");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- MoveMember: class lookup (MoveMemberAsync) ----

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_MisspeltClass_OffersTheNearClassAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf(FileName), "OrderPricingCalculater", ["Total"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertCandidate(result.ErrorData?.Message ?? string.Empty, "OrderPricingCalculator", "className");
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused move must not write the file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NoSimilarClass_StatesTheNoCandidateFactAndWritesNothing()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf(FileName), AlienName, ["Total"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNoCandidate(result.ErrorData?.Message ?? string.Empty);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused move must not write the file.");
    }

    // ---- Engine-direct: PreviewInstanceMoveCallSitesAsync class lookup (no tool of its own) ----

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task PreviewInstanceMoveCallSites_MisspeltClass_ThrowsNotFoundWithTheNearClassCandidate()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).PreviewInstanceMoveCallSitesAsync(workspace.PathOf(FileName), "OrderPricingCalculater", ["Total"], "Target"));
        AssertCandidate(ex!.Message, "OrderPricingCalculator", "className");
        Assert.That(ex.MissingTargetKind, Is.EqualTo("class"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("OrderPricingCalculater"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task PreviewInstanceMoveCallSites_NoSimilarClass_ThrowsNotFoundWithTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).PreviewInstanceMoveCallSitesAsync(workspace.PathOf(FileName), AlienName, ["Total"], "Target"));
        AssertNoCandidate(ex!.Message);
        Assert.That(ex.MissingTargetKind, Is.EqualTo("class"));
    }

    // ---- Engine-direct: RemoveEnumMemberAsync / ReplaceEnumMemberAsync member lookup (the Member tool only reaches them for a member it already found) ----

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task RemoveEnumMember_MisspeltMember_ThrowsNotFoundWithTheNearMemberAndTheEnumMembers()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).RemoveEnumMemberAsync(workspace.PathOf(FileName), "OrderStatus", "Shiped"));
        AssertCandidate(ex!.Message, "Shipped", "memberName");
        Assert.That(ex.Message, Does.Contain("Declared in enum 'OrderStatus': Pending, Shipped, Delivered."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum member"));
        Assert.That(ex.MissingTargetName, Is.EqualTo("Shiped"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task RemoveEnumMember_NoSimilarMember_ThrowsNotFoundWithTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).RemoveEnumMemberAsync(workspace.PathOf(FileName), "OrderStatus", AlienName));
        AssertNoCandidate(ex!.Message);
        Assert.That(ex.Message, Does.Contain($"'{AlienName}' is not among them, so it does not exist in enum 'OrderStatus'."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum member"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ReplaceEnumMember_MisspeltMember_ThrowsNotFoundWithTheNearMemberAndTheEnumMembers()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).ReplaceEnumMemberAsync(workspace.PathOf(FileName), "OrderStatus", "Shiped", "Dispatched"));
        AssertCandidate(ex!.Message, "Shipped", "memberName");
        Assert.That(ex.Message, Does.Contain("Declared in enum 'OrderStatus': Pending, Shipped, Delivered."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum member"));
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ReplaceEnumMember_NoSimilarMember_ThrowsNotFoundWithTheNoCandidateFact()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(() => CreateEngine(workspace).ReplaceEnumMemberAsync(workspace.PathOf(FileName), "OrderStatus", AlienName, "Dispatched"));
        AssertNoCandidate(ex!.Message);
        Assert.That(ex.Message, Does.Contain($"'{AlienName}' is not among them, so it does not exist in enum 'OrderStatus'."));
        Assert.That(ex.MissingTargetKind, Is.EqualTo("enum member"));
    }
}

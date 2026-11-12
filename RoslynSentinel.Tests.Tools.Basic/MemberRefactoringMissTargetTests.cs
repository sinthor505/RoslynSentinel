using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Not-found text of the MemberRefactoringEngine target, attribute, modifier and base-type lookups that moved onto MemberRefactoringNotFound
// (plan_not_found_error_orientation, Step 5c sweep slice B2). Each producer is driven through the tool that reaches it: a near-miss
// name gets the ranked "Did you mean" candidate with its retry, an unrelated name gets the no-similar-name fact and the GetFileOutline
// option, and a refused edit writes nothing. The batch forms name the failing entry as batchEdits[i] in the retry. Facts and options
// only: no policy wording, no bare "not found.".
// Producers covered: ChangeAccessibilityAsync, AddModifierAsync, RemoveModifierAsync, AddAttributeAsync, ReplaceAttributeAsync,
// RemoveAttributeAsync (target lookup), ReplaceAttributeAsync (attribute lookup), ApplyModifierBatchAsync, ApplyAttributeBatchAsync,
// ApplyBaseTypeBatchAsync, RemoveModifierAsync (modifier result message), RemoveBaseTypeAsync (base type result message).
// Engine-direct (no tool reaches the site): the near-miss modifier of RemoveModifierAsync. ModifyModifier's modifier parameter is an enum
// (NonAccessibilityModifier), so a misspelt modifier cannot arrive through the tool.
// Not covered, by design of the factory: ModifierMessage and BaseTypeMessage pass no file path, so those two result messages carry the
// no-similar-name fact and the declared names but no GetFileOutline option. RemoveBaseTypeAsync reaches its message only for a type with no
// base list, so it has no near-miss case.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRefactoringMissTargetTests
{
    private const string FileName = "Calc.cs";

    private const string AlienName = "Zzzqqq";

    private const string Fixture = """
        namespace Billing.Orders;

        public class OrderPricingCalculator : IPriced
        {
            [Obsolete]
            public int ComputeTotal(int rate, int tax) => rate + tax;

            public static int ApplyDiscount(int rate) => rate;
        }

        public interface IPriced
        {
        }

        public class PlainHolder
        {
        }
        """;

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
        Assert.That(message, Does.Not.Contain("Cannot edit"), "The old 'Cannot edit: ...' wording is gone.");
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

    private static RefactoringStructuralTools StructuralTools(InMemoryWorkspace workspace) =>
        Battery.Basic.TestTools.RefactoringStructuralTools(workspace.Manager);

    private static MemberRefactoringEngine CreateEngine(InMemoryWorkspace workspace)
    {
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(workspace.Manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation);
    }

    // Runs one refused call: the result is a NotFound error whose text passes the candidate or no-candidate check, and the file is untouched.
    private static async Task AssertMissAsync(Func<InMemoryWorkspace, Task<SentinelCallToolResult<AppliedChangeSummary>>> call, string? candidate, string paramName)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await call(workspace);
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), result.ErrorData?.Message);
        var message = result.ErrorData?.Message ?? string.Empty;
        if (candidate is null)
        {
            AssertNoCandidate(message);
        }
        else
        {
            AssertCandidate(message, candidate, paramName);
        }

        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused edit must not write the file.");
    }

    // ---- ChangeAccessibility: ChangeAccessibilityAsync ----

    [Test]
    [Category("RefactoringSignatureTools")]
    public Task ChangeAccessibility_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => SignatureTools(w).ChangeAccessibilityTool(reason: "test message", w.PathOf(FileName), "ComputeTotl", AccessibilityLevel.@private), "ComputeTotal", "targetName");

    [Test]
    [Category("RefactoringSignatureTools")]
    public Task ChangeAccessibility_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => SignatureTools(w).ChangeAccessibilityTool(reason: "test message", w.PathOf(FileName), AlienName, AccessibilityLevel.@private), null, "targetName");

    // ---- ModifyModifier add / remove: AddModifierAsync, RemoveModifierAsync (target lookup) ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierAdd_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotl", modifier: NonAccessibilityModifier.@virtual, action: AddRemoveAction.add), "ComputeTotal", "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierAdd_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", filePath: w.PathOf(FileName), targetName: AlienName, modifier: NonAccessibilityModifier.@virtual, action: AddRemoveAction.add), null, "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierRemove_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ApplyDiscont", modifier: NonAccessibilityModifier.@static, action: AddRemoveAction.remove), "ApplyDiscount", "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierRemove_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", filePath: w.PathOf(FileName), targetName: AlienName, modifier: NonAccessibilityModifier.@static, action: AddRemoveAction.remove), null, "targetName");

    // ---- ModifyModifier remove: RemoveModifierAsync (modifier the target does not carry) ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyModifierRemove_ModifierTheTargetLacks_StatesTheDeclaredModifiersAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await StructuralTools(workspace).ModifyModifierTool(reason: "test message", filePath: workspace.PathOf(FileName), targetName: "ComputeTotal", modifier: NonAccessibilityModifier.@static, action: AddRemoveAction.remove);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var message = result.StatusMessage ?? string.Empty;
        Assert.That(message, Does.Contain("Modifier 'static' was not found in 'ComputeTotal'."));
        Assert.That(message, Does.Contain("similar name"));
        Assert.That(message, Does.Contain("Declared in 'ComputeTotal': public."));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A no-op edit must not write the file.");
    }

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task RemoveModifier_MisspeltModifier_OffersTheNearModifierEngineDirectAsync()
    {
        // Engine-direct: the tool's modifier parameter is an enum, so a misspelt modifier cannot reach this site through it.
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var result = await CreateEngine(workspace).RemoveModifierAsync(workspace.PathOf(FileName), "ApplyDiscount", "statik");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.AlreadyInTargetState));
        var message = result.Message ?? string.Empty;
        AssertCandidate(message, "static", "modifier");
        Assert.That(message, Does.Contain("Declared in 'ApplyDiscount': public, static."));
    }

    // ---- ModifyAttribute add / replace / remove: AddAttributeAsync, ReplaceAttributeAsync, RemoveAttributeAsync (target lookup) ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeAdd_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotl", existingAttribute: "Serializable", action: AttributeModifyAction.add), "ComputeTotal", "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeAdd_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: AlienName, existingAttribute: "Serializable", action: AttributeModifyAction.add), null, "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeReplace_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotl", existingAttribute: "Obsolete", action: AttributeModifyAction.replace, newAttribute: "Obsolete(\"x\")"), "ComputeTotal", "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeReplace_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: AlienName, existingAttribute: "Obsolete", action: AttributeModifyAction.replace, newAttribute: "Obsolete(\"x\")"), null, "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeRemove_MisspeltTarget_OffersTheNearTargetAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotl", existingAttribute: "Obsolete", action: AttributeModifyAction.remove), "ComputeTotal", "targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeRemove_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: AlienName, existingAttribute: "Obsolete", action: AttributeModifyAction.remove), null, "targetName");

    // ---- ModifyAttribute replace: ReplaceAttributeAsync (attribute lookup on a located target) ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeReplace_MisspeltAttribute_OffersTheNearAttributeAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotal", existingAttribute: "Obsolet", action: AttributeModifyAction.replace, newAttribute: "Obsolete(\"x\")"), "Obsolete", "existingAttribute");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeReplace_NoSimilarAttribute_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", filePath: w.PathOf(FileName), targetName: "ComputeTotal", existingAttribute: "Serializable", action: AttributeModifyAction.replace, newAttribute: "Obsolete(\"x\")"), null, "existingAttribute");

    // ---- Batch forms: ApplyModifierBatchAsync, ApplyAttributeBatchAsync, ApplyBaseTypeBatchAsync ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierBatch_MisspeltTarget_OffersTheNearTargetForTheFailingEntryAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", batchEdits:
        [
            new ModifierEdit { FilePath = w.PathOf(FileName), TargetName = "ApplyDiscount", Modifier = NonAccessibilityModifier.@sealed, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = w.PathOf(FileName), TargetName = "ComputeTotl", Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
        ]), "ComputeTotal", "batchEdits[1].targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyModifierBatch_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyModifierTool(reason: "test message", batchEdits:
        [
            new ModifierEdit { FilePath = w.PathOf(FileName), TargetName = AlienName, Modifier = NonAccessibilityModifier.@virtual, Action = AddRemoveAction.add },
        ]), null, "batchEdits[0].targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeBatch_MisspeltTarget_OffersTheNearTargetForTheFailingEntryAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", batchEdits:
        [
            new AttributeEdit { FilePath = w.PathOf(FileName), TargetName = "ApplyDiscount", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            new AttributeEdit { FilePath = w.PathOf(FileName), TargetName = "ComputeTotl", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
        ]), "ComputeTotal", "batchEdits[1].targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyAttributeBatch_NoSimilarTarget_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyAttributeTool(reason: "test message", batchEdits:
        [
            new AttributeEdit { FilePath = w.PathOf(FileName), TargetName = AlienName, Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
        ]), null, "batchEdits[0].targetName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyBaseTypeBatch_MisspeltType_OffersTheNearTypeForTheFailingEntryAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyBaseTypeTool(reason: "test message", batchEdits:
        [
            new BaseTypeEdit { FilePath = w.PathOf(FileName), TypeName = "PlainHolder", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
            new BaseTypeEdit { FilePath = w.PathOf(FileName), TypeName = "OrderPricingCalculater", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
        ]), "OrderPricingCalculator", "batchEdits[1].typeName");

    [Test]
    [Category("RefactoringStructuralTools")]
    public Task ModifyBaseTypeBatch_NoSimilarType_StatesTheNoCandidateFactAndWritesNothingAsync() =>
        AssertMissAsync(w => StructuralTools(w).ModifyBaseTypeTool(reason: "test message", batchEdits:
        [
            new BaseTypeEdit { FilePath = w.PathOf(FileName), TypeName = AlienName, BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
        ]), null, "batchEdits[0].typeName");

    // ---- ModifyBaseType remove: RemoveBaseTypeAsync (type without a base list) ----

    [Test]
    [Category("RefactoringStructuralTools")]
    public async Task ModifyBaseTypeRemove_TypeWithNoBaseList_StatesThatNoBaseTypesAreDeclaredAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Fixture));
        var before = workspace.ReadText(FileName);
        var result = await StructuralTools(workspace).ModifyBaseTypeTool(reason: "test message", filePath: workspace.PathOf(FileName), typeName: "PlainHolder", baseTypeName: "IPriced", action: AddRemoveAction.remove);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var message = result.StatusMessage ?? string.Empty;
        Assert.That(message, Does.Contain("Base type 'IPriced' was not found in the base list of 'PlainHolder'."));
        Assert.That(message, Does.Contain("No base types are declared in the base list of 'PlainHolder'."));
        AssertFactsAndOptionsOnly(message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A no-op edit must not write the file.");
    }
}

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Declaration (slice 4a-1a) merges ModifyModifier and ChangeAccessibility behind an 'operation' enum and delegates to
// the same Impl methods, so each operation must behave exactly like its original tool on the same input. Every case runs
// the original on one InMemoryWorkspace and Declaration on a second, identical one, then compares outcome and file text.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class DeclarationToolTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/DeclarationFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class DeclarationTarget
    {
        void MethodOne() { }

        void MethodTwo() { }
    }
    """;

    // Source for the attribute and baseType operations: MethodOne carries [Marker], DeclarationTarget implements IMarker.
    private const string RichSource = """
    namespace ContosoOrders.Core;

    public class MarkerAttribute : System.Attribute { }

    public class OtherAttribute : System.Attribute { }

    public interface IMarker { }

    public interface IOther { }

    public class DeclarationTarget : IMarker
    {
        [Marker]
        void MethodOne() { }

        void MethodTwo() { }
    }
    """;

    private sealed record Tools(RefactoringStructuralTools Structural, RefactoringSignatureTools Signature, DeclarationTools Declaration);

    private static Tools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var basicEngine = new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var navigation = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var memberEngine = new MemberRefactoringEngine(workspaceManager, navigation, validation, config);
        var structuralImpl = new RefactoringStructuralImpl(
            basicEngine, memberEngine, new StructuralRefinementEngine(workspaceManager, config), navigation, workspaceManager, validation,
            NullLogger<RefactoringStructuralImpl>.Instance);
        var signatureImpl = new RefactoringSignatureImpl(basicEngine, memberEngine, workspaceManager, validation, navigation, NullLogger<RefactoringSignatureImpl>.Instance);
        return new Tools(new RefactoringStructuralTools(structuralImpl), new RefactoringSignatureTools(signatureImpl), new DeclarationTools(structuralImpl, signatureImpl));
    }

    [TestCase(AddRemoveAction.add, NonAccessibilityModifier.@static, "static void MethodOne")]
    [TestCase(AddRemoveAction.add, NonAccessibilityModifier.@new, "new void MethodOne")]
    public async Task Declaration_Modifier_MatchesModifyModifierAsync(AddRemoveAction action, NonAccessibilityModifier modifier, string expectedText)
    {
        using var original = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var expected = await BuildTools(original.Manager).Structural.ModifyModifier(
            reason: "parity original", filePath: original.PathOf(FixtureRelativePath), targetName: "MethodOne", modifier: modifier, action: action,
            dryRun: false, returnDiff: false, cancellationToken: default);
        var actual = await BuildTools(merged.Manager).Declaration.Declaration(
            reason: "parity merged", operation: DeclarationOperation.modifier, filePath: merged.PathOf(FixtureRelativePath), targetName: "MethodOne",
            modifier: modifier, action: ToDeclarationAction(action), cancellationToken: default);

        AssertSameOutcome(expected, actual, original.ReadText(FixtureRelativePath), merged.ReadText(FixtureRelativePath));
        Assert.That(merged.ReadText(FixtureRelativePath), Does.Contain(expectedText));
    }

    [Test]
    public async Task Declaration_ModifierEditsBatch_MatchesModifyModifierBatchAsync()
    {
        using var original = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        List<ModifierEdit> EditsFor(InMemoryWorkspace ws) =>
        [
            new ModifierEdit { FilePath = ws.PathOf(FixtureRelativePath), TargetName = "MethodOne", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
            new ModifierEdit { FilePath = ws.PathOf(FixtureRelativePath), TargetName = "MethodTwo", Modifier = NonAccessibilityModifier.@static, Action = AddRemoveAction.add },
        ];

        var expected = await BuildTools(original.Manager).Structural.ModifyModifier(reason: "parity original", edits: EditsFor(original), dryRun: false, returnDiff: false, cancellationToken: default);
        var actual = await BuildTools(merged.Manager).Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.modifier, edits: EditsFor(merged), cancellationToken: default);

        AssertSameOutcome(expected, actual, original.ReadText(FixtureRelativePath), merged.ReadText(FixtureRelativePath));
        Assert.That(merged.ReadText(FixtureRelativePath), Does.Contain("static void MethodOne").And.Contain("static void MethodTwo"));
    }

    [TestCase(AccessibilityLevel.@public, "public void MethodOne")]
    [TestCase(AccessibilityLevel.@internal, "internal void MethodOne")]
    [TestCase(AccessibilityLevel.protectedInternal, "protected internal void MethodOne")]
    public async Task Declaration_Accessibility_MatchesChangeAccessibilityAsync(AccessibilityLevel level, string expectedText)
    {
        using var original = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var expected = await BuildTools(original.Manager).Signature.ChangeAccessibility(
            reason: "parity original", filePath: original.PathOf(FixtureRelativePath), targetName: "MethodOne", accessibility: level, cancellationToken: default);
        var actual = await BuildTools(merged.Manager).Declaration.Declaration(
            reason: "parity merged", operation: DeclarationOperation.accessibility, filePath: merged.PathOf(FixtureRelativePath), targetName: "MethodOne",
            accessibility: level, cancellationToken: default);

        AssertSameOutcome(expected, actual, original.ReadText(FixtureRelativePath), merged.ReadText(FixtureRelativePath));
        Assert.That(merged.ReadText(FixtureRelativePath), Does.Contain(expectedText));
    }

    [Test]
    public async Task Declaration_Accessibility_DryRun_MatchesAndDoesNotWriteAsync()
    {
        using var original = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var expected = await BuildTools(original.Manager).Signature.ChangeAccessibility(
            reason: "parity original", filePath: original.PathOf(FixtureRelativePath), targetName: "MethodOne", accessibility: AccessibilityLevel.@public, dryRun: true, cancellationToken: default);
        var actual = await BuildTools(merged.Manager).Declaration.Declaration(
            reason: "parity merged", operation: DeclarationOperation.accessibility, filePath: merged.PathOf(FixtureRelativePath), targetName: "MethodOne",
            accessibility: AccessibilityLevel.@public, dryRun: true, cancellationToken: default);

        AssertSameOutcome(expected, actual, original.ReadText(FixtureRelativePath), merged.ReadText(FixtureRelativePath));
        Assert.That(merged.ReadText(FixtureRelativePath), Does.Not.Contain("public void MethodOne"));
    }

    [Test]
    public async Task Declaration_Accessibility_MissingTarget_RejectsAsInvalidArgumentNamingTheParamsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "missing accessibility", operation: DeclarationOperation.accessibility, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("'accessibility'").And.Contain("'filePath'").And.Contain("'targetName'"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("public void MethodOne"));
    }

    [Test]
    public async Task Declaration_Accessibility_WithModifierParams_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "mixed params", operation: DeclarationOperation.accessibility, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            accessibility: AccessibilityLevel.@public, modifier: NonAccessibilityModifier.@static, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("does not take 'modifier'"));
    }

    [Test]
    public async Task Declaration_Modifier_WithAccessibilityParam_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "mixed params", operation: DeclarationOperation.modifier, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            modifier: NonAccessibilityModifier.@static, action: DeclarationAction.add, accessibility: AccessibilityLevel.@public, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("does not take 'accessibility'"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("static void MethodOne"));
    }

    [Test]
    public async Task Declaration_Modifier_MissingParams_ReturnsTheSameErrorAsModifyModifierAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var expected = await tools.Structural.ModifyModifier(reason: "missing params", dryRun: false, returnDiff: false, cancellationToken: default);
        var actual = await tools.Declaration.Declaration(reason: "missing params", operation: DeclarationOperation.modifier, cancellationToken: default);

        Assert.That(actual.IsSuccess, Is.False);
        Assert.That(actual.ErrorData!.ErrorCode, Is.EqualTo(expected.ErrorData!.ErrorCode));
        Assert.That(actual.ErrorData.Message, Is.EqualTo(expected.ErrorData.Message));
    }

    [Test]
    public async Task Declaration_AttributeAdd_MatchesModifyAttributeAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyAttribute(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodTwo", existingAttribute: "Other", action: AttributeModifyAction.add, cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.attribute, filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodTwo", existingAttribute: "Other", action: DeclarationAction.add, cancellationToken: default));

        Assert.That(text, Does.Contain("[Other]"));
    }

    [Test]
    public async Task Declaration_AttributeReplace_MatchesModifyAttributeAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyAttribute(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: AttributeModifyAction.replace, newAttribute: "Other", cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.attribute, filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: DeclarationAction.replace, newAttribute: "Other", cancellationToken: default));

        Assert.That(text, Does.Contain("[Other]").And.Not.Contain("[Marker]"));
    }

    [Test]
    public async Task Declaration_AttributeRemove_MatchesModifyAttributeAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyAttribute(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: AttributeModifyAction.remove, cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.attribute, filePath: ws.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: DeclarationAction.remove, cancellationToken: default));

        Assert.That(text, Does.Not.Contain("[Marker]"));
    }

    [Test]
    public async Task Declaration_AttributeBatch_MatchesModifyAttributeBatchAsync()
    {
        List<AttributeEdit> EditsFor(InMemoryWorkspace ws) =>
        [
            new AttributeEdit { FilePath = ws.PathOf(FixtureRelativePath), TargetName = "MethodTwo", ExistingAttribute = "Other", Action = AttributeModifyAction.add },
            new AttributeEdit { FilePath = ws.PathOf(FixtureRelativePath), TargetName = "MethodOne", ExistingAttribute = "Marker", Action = AttributeModifyAction.remove },
        ];

        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyAttribute(reason: "parity original", batchEdits: EditsFor(ws), cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.attribute, batchEdits: EditsFor(ws), cancellationToken: default));

        Assert.That(text, Does.Contain("[Other]").And.Not.Contain("[Marker]"));
    }

    [Test]
    public async Task Declaration_BaseTypeAdd_MatchesModifyBaseTypeAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyBaseType(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), typeName: "DeclarationTarget", baseTypeName: "IOther", action: AddRemoveAction.add, cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.baseType, filePath: ws.PathOf(FixtureRelativePath), typeName: "DeclarationTarget", baseTypeName: "IOther", action: DeclarationAction.add, cancellationToken: default));

        Assert.That(text, Does.Contain("IMarker, IOther"));
    }

    [Test]
    public async Task Declaration_BaseTypeRemove_MatchesModifyBaseTypeAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyBaseType(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), typeName: "DeclarationTarget", baseTypeName: "IMarker", action: AddRemoveAction.remove, cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.baseType, filePath: ws.PathOf(FixtureRelativePath), typeName: "DeclarationTarget", baseTypeName: "IMarker", action: DeclarationAction.remove, cancellationToken: default));

        Assert.That(text, Does.Not.Contain("DeclarationTarget : IMarker"));
    }

    [Test]
    public async Task Declaration_BaseTypeBatch_MatchesModifyBaseTypeBatchAsync()
    {
        List<BaseTypeEdit> EditsFor(InMemoryWorkspace ws) =>
        [
            new BaseTypeEdit { FilePath = ws.PathOf(FixtureRelativePath), TypeName = "DeclarationTarget", BaseTypeName = "IOther", Action = AddRemoveAction.add },
        ];

        var text = await AssertParityAsync(
            (t, ws) => t.Structural.ModifyBaseType(reason: "parity original", edits: EditsFor(ws), cancellationToken: default),
            (t, ws) => t.Declaration.Declaration(reason: "parity merged", operation: DeclarationOperation.baseType, baseTypeEdits: EditsFor(ws), cancellationToken: default));

        Assert.That(text, Does.Contain("IMarker, IOther"));
    }

    [Test]
    public async Task Declaration_AttributeReplaceWithoutNewAttribute_ReturnsTheSameErrorAsModifyAttributeAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));
        var tools = BuildTools(workspace.Manager);

        var expected = await tools.Structural.ModifyAttribute(reason: "missing newAttribute", filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: AttributeModifyAction.replace, cancellationToken: default);
        var actual = await tools.Declaration.Declaration(reason: "missing newAttribute", operation: DeclarationOperation.attribute, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne", existingAttribute: "Marker", action: DeclarationAction.replace, cancellationToken: default);

        Assert.That(actual.IsSuccess, Is.False);
        Assert.That(actual.ErrorData!.ErrorCode, Is.EqualTo(expected.ErrorData!.ErrorCode));
        Assert.That(actual.ErrorData.Message, Is.EqualTo(expected.ErrorData.Message));
    }

    [Test]
    public async Task Declaration_Attribute_MissingParams_NamesThemAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "missing params", operation: DeclarationOperation.attribute, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne", cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("'existingAttribute'").And.Contain("'action'").And.Contain("'batchEdits'"));
    }

    [Test]
    public async Task Declaration_Attribute_WithSingularAndBatch_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "both forms", operation: DeclarationOperation.attribute, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            existingAttribute: "Marker", action: DeclarationAction.remove, batchEdits: [new AttributeEdit { FilePath = workspace.PathOf(FixtureRelativePath), TargetName = "MethodOne", ExistingAttribute = "Marker" }],
            cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("not both"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("[Marker]"));
    }

    [Test]
    public async Task Declaration_Attribute_WithModifierParam_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "mixed params", operation: DeclarationOperation.attribute, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            existingAttribute: "Marker", action: DeclarationAction.remove, modifier: NonAccessibilityModifier.@static, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("does not take 'modifier'"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("[Marker]"));
    }

    [Test]
    public async Task Declaration_BaseType_MissingParams_NamesThemAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "missing params", operation: DeclarationOperation.baseType, filePath: workspace.PathOf(FixtureRelativePath), cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("'typeName'").And.Contain("'baseTypeName'").And.Contain("'baseTypeEdits'"));
    }

    [Test]
    public async Task Declaration_BaseType_WithTargetName_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "wrong name param", operation: DeclarationOperation.baseType, filePath: workspace.PathOf(FixtureRelativePath), targetName: "DeclarationTarget",
            baseTypeName: "IOther", action: DeclarationAction.add, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("does not take 'targetName'"));
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("IOther,"));
    }

    [Test]
    public async Task Declaration_ReplaceAction_OnModifier_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "replace action", operation: DeclarationOperation.modifier, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            modifier: NonAccessibilityModifier.@static, action: DeclarationAction.replace, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("only valid for operation 'attribute'"));
    }

    [Test]
    public async Task Declaration_ReplaceAction_OnBaseType_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "replace action", operation: DeclarationOperation.baseType, filePath: workspace.PathOf(FixtureRelativePath), typeName: "DeclarationTarget",
            baseTypeName: "IOther", action: DeclarationAction.replace, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("only valid for operation 'attribute'"));
    }

    // Runs the original tool on one InMemoryWorkspace and Declaration on a second identical one, asserts the same outcome
    // and file text, and returns the merged workspace's file text for operation-specific assertions.
    private static async Task<string> AssertParityAsync(
        Func<Tools, InMemoryWorkspace, Task<SentinelCallToolResult<AppliedChangeSummary>>> runOriginal,
        Func<Tools, InMemoryWorkspace, Task<SentinelCallToolResult<AppliedChangeSummary>>> runMerged)
    {
        using var original = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, RichSource));

        var expected = await runOriginal(BuildTools(original.Manager), original);
        var actual = await runMerged(BuildTools(merged.Manager), merged);

        AssertSameOutcome(expected, actual, original.ReadText(FixtureRelativePath), merged.ReadText(FixtureRelativePath));
        return merged.ReadText(FixtureRelativePath);
    }

    private static DeclarationAction ToDeclarationAction(AddRemoveAction action) =>
        action == AddRemoveAction.add ? DeclarationAction.add : DeclarationAction.remove;

    private static string FileNameOf(FilePathWrapper path) => Path.GetFileName((string)path);

    private static void AssertSameOutcome(
        SentinelCallToolResult<AppliedChangeSummary> expected, SentinelCallToolResult<AppliedChangeSummary> actual, string expectedText, string actualText)
    {
        Assert.That(expected.IsSuccess, Is.True, expected.ErrorData?.Message);
        Assert.That(actual.IsSuccess, Is.True, actual.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(actual.SuccessData!.Description, Is.EqualTo(expected.SuccessData!.Description));
            Assert.That(actual.SuccessData.DryRun, Is.EqualTo(expected.SuccessData.DryRun));
            Assert.That(actual.SuccessData.Validated, Is.EqualTo(expected.SuccessData.Validated));
            Assert.That(actual.SuccessData.AffectedFiles.Select(FileNameOf), Is.EqualTo(expected.SuccessData.AffectedFiles.Select(FileNameOf)));
            Assert.That(actualText, Is.EqualTo(expectedText));
        });
    }
}

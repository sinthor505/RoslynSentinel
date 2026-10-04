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
            modifier: modifier, action: action, cancellationToken: default);

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
        Assert.That(result.ErrorData.Message, Does.Contain("operation 'modifier'"));
    }

    [Test]
    public async Task Declaration_Modifier_WithAccessibilityParam_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).Declaration.Declaration(
            reason: "mixed params", operation: DeclarationOperation.modifier, filePath: workspace.PathOf(FixtureRelativePath), targetName: "MethodOne",
            modifier: NonAccessibilityModifier.@static, action: AddRemoveAction.add, accessibility: AccessibilityLevel.@public, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("operation 'accessibility'"));
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

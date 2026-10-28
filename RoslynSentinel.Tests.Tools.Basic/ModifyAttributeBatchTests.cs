using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// These tests are about ModifyAttributeTool's code-correctness (which batchEdits apply, what text results, how the batch is
// validated and reported), so they run on an InMemoryWorkspace: no temp directory, MSBuild load or disk write. Disk
// fidelity (BOM, line endings, write-through) is covered by the dedicated disk tests, not here.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class ModifyAttributeBatchTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/AttributeBatchFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    [Obsolete]
    public class AttributeBatchTargetA
    {
    }

    [Obsolete]
    public class AttributeBatchTargetB
    {
    }
    """;

    private const string SecondFixtureRelativePath = "ContosoOrders.Core/AttributeBatchFixtureSecond.cs";

    private const string SecondFixtureSource = """
    namespace ContosoOrders.Core;

    public class AttributeBatchTargetC
    {
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    [Test]
    public async Task ModifyAttribute_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test same file two batchEdits",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.remove },
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetB", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.replace, NewAttribute = "Obsolete(\"v2\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var newContent = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Not.Match(@"\[Obsolete\]\s*public class AttributeBatchTargetA"));
            Assert.That(newContent, Does.Contain("Obsolete(\"v2\")"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource), (SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test across two files",
            batchEdits:
            [
                new AttributeEdit { FilePath = workspace.PathOf(FixtureRelativePath), TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = workspace.PathOf(SecondFixtureRelativePath), TargetName = "AttributeBatchTargetC", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("[Serializable]"));
            Assert.That(workspace.ReadText(SecondFixtureRelativePath), Does.Contain("[Serializable]"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var beforeContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test same node collision",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyAttribute_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var beforeContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test target not found",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetDoesNotExist", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyAttribute_BatchAddWithComment_ReturnsStructuredInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);
        var beforeContent = workspace.ReadText(FixtureRelativePath);

        // The first edit is valid; the whole batch must still be rejected before any write.
        var result = await tools.ModifyAttributeTool(
            reason: "batch test comment in added attribute",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetB", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"x\" /* why */)" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(RoslynSentinel.Common.ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("attribute text must not contain comments; remove the comment and retry"));
            Assert.That(result.ErrorData.Message, Does.Contain("batchEdits[1]"));
            Assert.That(result.ErrorData.Message, Does.Not.Contain("NormalizeWhitespace"));
            Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchReplaceWithComment_ReturnsStructuredInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);
        var beforeContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test comment in replacement attribute",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetA", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.replace, NewAttribute = "Obsolete(\"v2\")" },
                new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetB", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.replace, NewAttribute = "[Obsolete] // why" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(RoslynSentinel.Common.ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("attribute text must not contain comments; remove the comment and retry"));
            Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
        });
    }

    [Test]
    public async Task ModifyAttribute_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test both supplied",
            filePath: path,
            targetName: "AttributeBatchTargetA",
            existingAttribute: "Obsolete",
            action: AttributeModifyAction.remove,
            batchEdits: [new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetB", Action = AttributeModifyAction.add, NewAttribute = "Serializable" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ModifyAttribute_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits"));
    }

    [Test]
    public async Task ModifyAttribute_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyAttributeTool(
            reason: "batch test empty batchEdits array",
            batchEdits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }

    [Test]
    public async Task ModifyAttribute_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new AttributeEdit { FilePath = path, TargetName = $"NonexistentType{i}", Action = AttributeModifyAction.add, NewAttribute = "Serializable" })
            .ToList();

        var result = await tools.ModifyAttributeTool(reason: "batch test over cap", batchEdits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }

    // ---- Regression: docs/current/blockers/resolved/blocking_error_modifyattribute_batch_drops_nested_edit_and_reformats_type_body.md ----

    private const string NestedFixtureRelativePath = "ContosoOrders.Core/AttributeNestedFixture.cs";

    private const string NestedFixtureSource = """
    namespace ContosoOrders.Core;

    public class AttributeNestedTarget
    {
        public void First()
        {
        }

        [Obsolete]
        public void Second()
        {
        }
    }
    """;

    private const string TypeRemoveFixtureRelativePath = "ContosoOrders.Core/AttributeTypeRemoveFixture.cs";

    private const string TypeRemoveFixtureSource = """
    namespace ContosoOrders.Core;

    [Serializable]
    public class AttributeRemoveTypeTarget
    {
        public void Method()
        {
        }
    }
    """;

    private const string WeirdFormattingRelativePath = "ContosoOrders.Core/AttributeFormattingFixture.cs";

    // Deliberately non-canonical whitespace in members the batchEdits never target: a Roslyn Formatter pass
    // over the enclosing type would rewrite it (spacing in signatures, expression operators, case-block indent).
    private const string WeirdFormattingSource = """
    namespace ContosoOrders.Core;

    public class AttributeFormattingTarget
    {
        public int   Weird( int a,int b )
        {
            switch (a)
            {
                case 1:
                    {
                        return   b;
                    }
                default:
                    return   a+b;
            }
        }

        public void   Other(  )
        {
        }
    }
    """;

    private static string EolOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    [TestCase(true)]
    [TestCase(false)]
    public async Task ModifyAttribute_BatchTypeAddAndMethodAddSameFile_BothAttributesPresentAsync(bool typeEditFirst)
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);

        var typeEdit = new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" };
        var methodEdit = new AttributeEdit { FilePath = path, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"first\")" };

        var result = await tools.ModifyAttributeTool(
            reason: "regression: ancestor/descendant targets in one batch",
            batchEdits: typeEditFirst ? [typeEdit, methodEdit] : [methodEdit, typeEdit],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var newContent = workspace.ReadText(NestedFixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Match(@"\[Serializable\]\s*public class AttributeNestedTarget"), "type-level attribute missing");
            Assert.That(newContent, Does.Match(@"\[Obsolete\(""first""\)\]\s*public void First"), "method-level attribute dropped");
            Assert.That(newContent, Does.Match(@"\[Obsolete\]\s*public void Second"), "pre-existing attribute on an untargeted method must survive");
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchRemoveOnMethodAndAddOnType_ComposeAndLeaveRestByteIdenticalAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);
        var before = workspace.ReadText(NestedFixtureRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: remove on member plus add on its type",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "Second", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
                new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before
            .Replace("public class AttributeNestedTarget", "[Serializable]" + eol + "public class AttributeNestedTarget")
            .Replace("    [Obsolete]" + eol, string.Empty);
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_BatchTypeLevelAddOnly_LeavesUnrelatedMembersByteIdenticalAsync()
    {
        using var workspace = InMemoryWorkspace.Create((WeirdFormattingRelativePath, WeirdFormattingSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(WeirdFormattingRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: type-level add must not reformat members",
            batchEdits: [new AttributeEdit { FilePath = workspace.PathOf(WeirdFormattingRelativePath), TargetName = "AttributeFormattingTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("public class AttributeFormattingTarget", "[Serializable]" + eol + "public class AttributeFormattingTarget");
        Assert.That(workspace.ReadText(WeirdFormattingRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_SingleTypeLevelAdd_LeavesUnrelatedMembersByteIdenticalAsync()
    {
        using var workspace = InMemoryWorkspace.Create((WeirdFormattingRelativePath, WeirdFormattingSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(WeirdFormattingRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: single-edit type-level add must not reformat members",
            filePath: workspace.PathOf(WeirdFormattingRelativePath),
            targetName: "AttributeFormattingTarget",
            existingAttribute: "Serializable",
            action: AttributeModifyAction.add,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("public class AttributeFormattingTarget", "[Serializable]" + eol + "public class AttributeFormattingTarget");
        Assert.That(workspace.ReadText(WeirdFormattingRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_SingleRemoveOnTypeLevelAttribute_RemovesItAsync()
    {
        using var workspace = InMemoryWorkspace.Create((TypeRemoveFixtureRelativePath, TypeRemoveFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(TypeRemoveFixtureRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: single-edit remove whose target is a type-level declaration",
            filePath: workspace.PathOf(TypeRemoveFixtureRelativePath),
            targetName: "AttributeRemoveTypeTarget",
            existingAttribute: "Serializable",
            action: AttributeModifyAction.remove,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(TypeRemoveFixtureRelativePath), Is.EqualTo(before.Replace("[Serializable]" + eol, "")));
    }

    [Test]
    public async Task ModifyAttribute_BatchMethodLevelAddOnly_LeavesEverythingElseByteIdenticalAsync()
    {
        using var workspace = InMemoryWorkspace.Create((WeirdFormattingRelativePath, WeirdFormattingSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(WeirdFormattingRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: method-level add keeps indentation and leaves siblings alone",
            batchEdits: [new AttributeEdit { FilePath = workspace.PathOf(WeirdFormattingRelativePath), TargetName = "Other", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("    public void   Other(  )", "    [Obsolete]" + eol + "    public void   Other(  )");
        Assert.That(workspace.ReadText(WeirdFormattingRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_BatchAllEditsApply_DescriptionReportsExactAppliedCountAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: reported applied count matches written batchEdits",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"first\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var newContent = workspace.ReadText(NestedFixtureRelativePath);
        var writtenAttributes = new[] { "[Serializable]", "[Obsolete(\"first\")]" }.Count(a => newContent.Contains(a));
        Assert.That(writtenAttributes, Is.EqualTo(2));
        Assert.That(result.SuccessData!.Description, Is.EqualTo("Applied 2 attribute edit(s) across 1 file(s)."));
    }

    [Test]
    public async Task ModifyAttribute_BatchOneEditHasNoEffect_DescriptionDoesNotCountItAsAppliedAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: a no-effect edit must not be reported as applied",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "First", Action = AttributeModifyAction.remove, ExistingAttribute = "Conditional" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var description = result.SuccessData!.Description;
        Assert.Multiple(() =>
        {
            Assert.That(description, Does.Contain("Applied 1 of 2 attribute edit(s)"));
            Assert.That(description, Does.Contain("batchEdits[1]"));
            Assert.That(description, Does.Not.Contain("Applied 2"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchNoEditHasAnyEffect_FailsInsteadOfReportingSuccessAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "regression: zero effective batchEdits is not a success",
            batchEdits: [new AttributeEdit { FilePath = workspace.PathOf(NestedFixtureRelativePath), TargetName = "First", Action = AttributeModifyAction.remove, ExistingAttribute = "Conditional" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits[0]"));
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before));
    }

    // ---- 'attribute' is an alias for 'existingAttribute' ----

    [Test]
    public async Task ModifyAttribute_SingleAddWithAttributeAlias_AddsTheAttributeAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(SecondFixtureRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: singular add using 'attribute' instead of 'existingAttribute'",
            filePath: workspace.PathOf(SecondFixtureRelativePath),
            targetName: "AttributeBatchTargetC",
            attribute: "Serializable",
            action: AttributeModifyAction.add,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(SecondFixtureRelativePath), Is.EqualTo(before.Replace("public class AttributeBatchTargetC", "[Serializable]" + eol + "public class AttributeBatchTargetC")));
    }

    [Test]
    public async Task ModifyAttribute_SingleRemoveWithAttributeAlias_RemovesTheAttributeAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(NestedFixtureRelativePath);
        var eol = EolOf(before);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: singular remove using 'attribute'",
            filePath: workspace.PathOf(NestedFixtureRelativePath),
            targetName: "Second",
            attribute: "Obsolete",
            action: AttributeModifyAction.remove,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before.Replace("    [Obsolete]" + eol, "")));
    }

    [Test]
    public async Task ModifyAttribute_SingleReplaceWithAttributeAlias_ReplacesTheAttributeAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: singular replace using 'attribute' as the existing attribute",
            filePath: workspace.PathOf(NestedFixtureRelativePath),
            targetName: "Second",
            attribute: "Obsolete",
            newAttribute: "Obsolete(\"v2\")",
            action: AttributeModifyAction.replace,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before.Replace("[Obsolete]", "[Obsolete(\"v2\")]")));
    }

    [Test]
    public async Task ModifyAttribute_BatchReplaceWithAttributeAlias_ReplacesTheAttributeAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: batch item replace using 'attribute' as the existing attribute",
            batchEdits:
            [
                new AttributeEdit { FilePath = workspace.PathOf(NestedFixtureRelativePath), TargetName = "Second", Action = AttributeModifyAction.replace, Attribute = "Obsolete", NewAttribute = "Obsolete(\"v2\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before.Replace("[Obsolete]", "[Obsolete(\"v2\")]")));
    }

    [Test]
    public async Task ModifyAttribute_SingleRemoveBothParamsDifferentValues_RejectsNamingBothParametersAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: conflicting values on remove must be rejected before anything is written",
            filePath: workspace.PathOf(NestedFixtureRelativePath),
            targetName: "Second",
            existingAttribute: "Obsolete",
            attribute: "Serializable",
            action: AttributeModifyAction.remove,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsError, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("'attribute'"));
            Assert.That(result.ErrorData!.Message, Does.Contain("'existingAttribute'"));
            Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task ModifyAttribute_SingleBothParamsSameValue_IsAcceptedAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: both params with the same value are not a conflict",
            filePath: workspace.PathOf(SecondFixtureRelativePath),
            targetName: "AttributeBatchTargetC",
            existingAttribute: "Serializable",
            attribute: "[Serializable]",
            action: AttributeModifyAction.add,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
    }

    [Test]
    public async Task ModifyAttribute_SingleBothParamsDifferentValues_RejectsNamingBothParametersAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(SecondFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: conflicting values must be rejected, not silently resolved",
            filePath: workspace.PathOf(SecondFixtureRelativePath),
            targetName: "AttributeBatchTargetC",
            existingAttribute: "Obsolete",
            attribute: "Serializable",
            action: AttributeModifyAction.add,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("'attribute'"));
            Assert.That(result.ErrorData!.Message, Does.Contain("'existingAttribute'"));
            Assert.That(workspace.ReadText(SecondFixtureRelativePath), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task ModifyAttribute_AttributeAliasWithEdits_RejectsAsMixedSingularAndBatchAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(SecondFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: a singular 'attribute' alongside batchEdits is still the mixed form",
            attribute: "Serializable",
            batchEdits: [new AttributeEdit { FilePath = path, TargetName = "AttributeBatchTargetC", Action = AttributeModifyAction.add, Attribute = "Obsolete" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ModifyAttribute_BatchItemsUsingAttributeAlias_AllApplyAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: batch items using 'attribute' (add and remove) alongside an existingAttribute item",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, Attribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "Second", Action = AttributeModifyAction.remove, Attribute = "Obsolete" },
                new AttributeEdit { FilePath = path, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"first\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var newContent = workspace.ReadText(NestedFixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Match(@"\[Serializable\]\s*public class AttributeNestedTarget"));
            Assert.That(newContent, Does.Not.Match(@"\[Obsolete\]\s*public void Second"));
            Assert.That(newContent, Does.Match(@"\[Obsolete\(""first""\)\]\s*public void First"));
            Assert.That(result.SuccessData!.Description, Is.EqualTo("Applied 3 attribute edit(s) across 1 file(s)."));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchItemBothParamsDifferentValues_RejectsNamingEditAndBothParametersAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: conflicting values in a batch item must be rejected before anything is written",
            batchEdits:
            [
                new AttributeEdit { FilePath = path, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, Attribute = "Serializable" },
                new AttributeEdit { FilePath = path, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete", Attribute = "Conditional(\"X\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits[1]"));
            Assert.That(result.ErrorData!.Message, Does.Contain("'attribute'"));
            Assert.That(result.ErrorData!.Message, Does.Contain("'existingAttribute'"));
            Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchItemNeitherAttributeNorExistingAttribute_RejectsNamingBothParametersAsync()
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyAttributeTool(
            reason: "alias: a batch item that supplies neither name gets an error that names both",
            batchEdits: [new AttributeEdit { FilePath = workspace.PathOf(NestedFixtureRelativePath), TargetName = "First", Action = AttributeModifyAction.add }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("existingAttribute").And.Contain("'attribute'"));
    }
}

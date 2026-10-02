using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]

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
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), config),
            new StructuralRefinementEngine(workspaceManager, config),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
    }

    [Test]
    public async Task ModifyAttribute_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test same file two edits",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.remove },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetB", ExistingAttribute = "Obsolete", Action = AttributeModifyAction.replace, NewAttribute = "Obsolete(\"v2\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Not.Contain("[Obsolete]\r\npublic class AttributeBatchTargetA"));
            Assert.That(newContent, Does.Contain("Obsolete(\"v2\")"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource, reloadSolution: false);
        await fixture.AddFileToSolution(workspaceManager, SecondFixtureRelativePath, SecondFixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test across two files",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            new AttributeEdit { FilePath = SecondFixtureRelativePath, TargetName = "AttributeBatchTargetC", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var firstContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        var secondContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, SecondFixtureRelativePath));
        Assert.Multiple(() =>
        {
            Assert.That(firstContent, Does.Contain("[Serializable]"));
            Assert.That(secondContent, Does.Contain("[Serializable]"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyAttribute(
            reason: "batch test same node collision",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyAttribute_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var beforeContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));

        var result = await tools.ModifyAttribute(
            reason: "batch test target not found",
            edits:
            [
                new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetA", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetDoesNotExist", Action = AttributeModifyAction.add, NewAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);

        var afterContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, FixtureRelativePath));
        Assert.That(afterContent, Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyAttribute_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test both supplied",
            filePath: FixtureRelativePath,
            targetName: "AttributeBatchTargetA",
            existingAttribute: "Obsolete",
            action: AttributeModifyAction.remove,
            edits: [new AttributeEdit { FilePath = FixtureRelativePath, TargetName = "AttributeBatchTargetB", Action = AttributeModifyAction.add, NewAttribute = "Serializable" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ModifyAttribute_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("edits"));
    }

    [Test]
    public async Task ModifyAttribute_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "batch test empty edits array",
            edits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }

    [Test]
    public async Task ModifyAttribute_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, FixtureRelativePath, FixtureSource);
        var tools = BuildTools(workspaceManager);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new AttributeEdit { FilePath = FixtureRelativePath, TargetName = $"NonexistentType{i}", Action = AttributeModifyAction.add, NewAttribute = "Serializable" })
            .ToList();

        var result = await tools.ModifyAttribute(reason: "batch test over cap", edits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }

    // ---- Regression: docs/current/blockers/blocking_error_modifyattribute_batch_drops_nested_edit_and_reformats_type_body.md ----

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

    private const string WeirdFormattingRelativePath = "ContosoOrders.Core/AttributeFormattingFixture.cs";

    // Deliberately non-canonical whitespace in members the edits never target: a Roslyn Formatter pass
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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, NestedFixtureRelativePath, NestedFixtureSource);
        var tools = BuildTools(workspaceManager);

        var typeEdit = new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" };
        var methodEdit = new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"first\")" };

        var result = await tools.ModifyAttribute(
            reason: "regression: ancestor/descendant targets in one batch",
            edits: typeEditFirst ? [typeEdit, methodEdit] : [methodEdit, typeEdit],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, NestedFixtureRelativePath));
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
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, NestedFixtureRelativePath, NestedFixtureSource);
        var tools = BuildTools(workspaceManager);
        var path = Path.Combine(fixture.SolutionDirectory, NestedFixtureRelativePath);
        var before = await File.ReadAllTextAsync(path);
        var eol = EolOf(before);

        var result = await tools.ModifyAttribute(
            reason: "regression: remove on member plus add on its type",
            edits:
            [
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "Second", Action = AttributeModifyAction.remove, ExistingAttribute = "Obsolete" },
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var expected = before
            .Replace("public class AttributeNestedTarget", "[Serializable]" + eol + "public class AttributeNestedTarget")
            .Replace("    [Obsolete]" + eol, string.Empty);
        var after = await File.ReadAllTextAsync(path);
        Assert.That(after, Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_BatchTypeLevelAddOnly_LeavesUnrelatedMembersByteIdenticalAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, WeirdFormattingRelativePath, WeirdFormattingSource);
        var tools = BuildTools(workspaceManager);
        var path = Path.Combine(fixture.SolutionDirectory, WeirdFormattingRelativePath);
        var before = await File.ReadAllTextAsync(path);
        var eol = EolOf(before);

        var result = await tools.ModifyAttribute(
            reason: "regression: type-level add must not reformat members",
            edits: [new AttributeEdit { FilePath = WeirdFormattingRelativePath, TargetName = "AttributeFormattingTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("public class AttributeFormattingTarget", "[Serializable]" + eol + "public class AttributeFormattingTarget");
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_SingleTypeLevelAdd_LeavesUnrelatedMembersByteIdenticalAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, WeirdFormattingRelativePath, WeirdFormattingSource);
        var tools = BuildTools(workspaceManager);
        var path = Path.Combine(fixture.SolutionDirectory, WeirdFormattingRelativePath);
        var before = await File.ReadAllTextAsync(path);
        var eol = EolOf(before);

        var result = await tools.ModifyAttribute(
            reason: "regression: single-edit type-level add must not reformat members",
            filePath: WeirdFormattingRelativePath,
            targetName: "AttributeFormattingTarget",
            existingAttribute: "Serializable",
            action: AttributeModifyAction.add,
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("public class AttributeFormattingTarget", "[Serializable]" + eol + "public class AttributeFormattingTarget");
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_BatchMethodLevelAddOnly_LeavesEverythingElseByteIdenticalAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, WeirdFormattingRelativePath, WeirdFormattingSource);
        var tools = BuildTools(workspaceManager);
        var path = Path.Combine(fixture.SolutionDirectory, WeirdFormattingRelativePath);
        var before = await File.ReadAllTextAsync(path);
        var eol = EolOf(before);

        var result = await tools.ModifyAttribute(
            reason: "regression: method-level add keeps indentation and leaves siblings alone",
            edits: [new AttributeEdit { FilePath = WeirdFormattingRelativePath, TargetName = "Other", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);

        var expected = before.Replace("    public void   Other(  )", "    [Obsolete]" + eol + "    public void   Other(  )");
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyAttribute_BatchAllEditsApply_DescriptionReportsExactAppliedCountAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, NestedFixtureRelativePath, NestedFixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "regression: reported applied count matches written edits",
            edits:
            [
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "First", Action = AttributeModifyAction.add, ExistingAttribute = "Obsolete(\"first\")" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
        var newContent = await File.ReadAllTextAsync(Path.Combine(fixture.SolutionDirectory, NestedFixtureRelativePath));
        var writtenAttributes = new[] { "[Serializable]", "[Obsolete(\"first\")]" }.Count(a => newContent.Contains(a));
        Assert.That(writtenAttributes, Is.EqualTo(2));
        Assert.That(result.SuccessData!.Description, Is.EqualTo("Applied 2 attribute edit(s) across 1 file(s)."));
    }

    [Test]
    public async Task ModifyAttribute_BatchOneEditHasNoEffect_DescriptionDoesNotCountItAsAppliedAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, NestedFixtureRelativePath, NestedFixtureSource);
        var tools = BuildTools(workspaceManager);

        var result = await tools.ModifyAttribute(
            reason: "regression: a no-effect edit must not be reported as applied",
            edits:
            [
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "AttributeNestedTarget", Action = AttributeModifyAction.add, ExistingAttribute = "Serializable" },
                new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "First", Action = AttributeModifyAction.remove, ExistingAttribute = "Conditional" },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.True, result.ErrorData?.Message);
        var description = result.SuccessData!.Description;
        Assert.Multiple(() =>
        {
            Assert.That(description, Does.Contain("Applied 1 of 2 attribute edit(s)"));
            Assert.That(description, Does.Contain("edits[1]"));
            Assert.That(description, Does.Not.Contain("Applied 2"));
        });
    }

    [Test]
    public async Task ModifyAttribute_BatchNoEditHasAnyEffect_FailsInsteadOfReportingSuccessAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await fixture.AddFileToSolution(workspaceManager, NestedFixtureRelativePath, NestedFixtureSource);
        var tools = BuildTools(workspaceManager);
        var path = Path.Combine(fixture.SolutionDirectory, NestedFixtureRelativePath);
        var before = await File.ReadAllTextAsync(path);

        var result = await tools.ModifyAttribute(
            reason: "regression: zero effective edits is not a success",
            edits: [new AttributeEdit { FilePath = NestedFixtureRelativePath, TargetName = "First", Action = AttributeModifyAction.remove, ExistingAttribute = "Conditional" }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("edits[0]"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(before));
    }
}

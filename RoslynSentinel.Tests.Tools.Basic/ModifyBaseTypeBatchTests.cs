using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// About ModifyBaseType's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk
// write). See ModifyAttributeBatchTests for the pattern.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ModifyBaseTypeBatchTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/BaseTypeBatchFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public interface IBaseTypeBatchMarker
    {
    }

    public class BaseTypeBatchTargetA
    {
    }

    public class BaseTypeBatchTargetB : IBaseTypeBatchMarker
    {
    }
    """;

    private const string SecondFixtureRelativePath = "ContosoOrders.Core/BaseTypeBatchFixtureSecond.cs";

    private const string SecondFixtureSource = """
    namespace ContosoOrders.Core;

    public class BaseTypeBatchTargetC
    {
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, symbolNavigationEngine, validationEngine, config),
            new StructuralRefinementEngine(workspaceManager),
            symbolNavigationEngine,
            workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance));
    }

    [Test]
    public async Task ModifyBaseType_BatchTwoEditsSameFile_BothApplyAgainstOriginalSnapshotAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "batch test same file two batchEdits",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var newContent = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(newContent, Does.Match(@"BaseTypeBatchTargetA\s*:\s*IBaseTypeBatchMarker"));
            Assert.That(newContent, Does.Not.Match(@"BaseTypeBatchTargetB\s*:\s*IBaseTypeBatchMarker"));
        });
    }

    [Test]
    public async Task ModifyBaseType_BatchAcrossTwoFiles_AppliesBothInOneCallAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource), (SecondFixtureRelativePath, SecondFixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyBaseType(
            reason: "batch test across two files",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = workspace.PathOf(FixtureRelativePath), TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = workspace.PathOf(SecondFixtureRelativePath), TypeName = "BaseTypeBatchTargetC", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Match(@"BaseTypeBatchTargetA\s*:\s*IBaseTypeBatchMarker"));
            Assert.That(workspace.ReadText(SecondFixtureRelativePath), Does.Match(@"BaseTypeBatchTargetC\s*:\s*IBaseTypeBatchMarker"));
        });
    }

    [Test]
    public async Task ModifyBaseType_BatchSameNodeTwice_RejectsWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var beforeContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "batch test same node collision",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove },
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IDisposable", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("same target"), "must be rejected for the collision, not for an unrelated reason");
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyBaseType_BatchOneEditTargetNotFound_RejectsWholeBatchWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var beforeContent = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "batch test target not found",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetDoesNotExist", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(beforeContent));
    }

    [Test]
    public async Task ModifyBaseType_BothEditsAndSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "batch test both supplied",
            filePath: path,
            typeName: "BaseTypeBatchTargetA",
            baseTypeName: "IBaseTypeBatchMarker",
            action: AddRemoveAction.add,
            batchEdits: [new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.remove }],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("not both"));
    }

    [Test]
    public async Task ModifyBaseType_NeitherEditsNorSingularParamsSupplied_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyBaseType(
            reason: "batch test neither supplied",
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits"));
    }

    [Test]
    public async Task ModifyBaseType_EmptyEditsArray_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.ModifyBaseType(
            reason: "batch test empty batchEdits array",
            batchEdits: [],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("empty"));
    }

    [Test]
    public async Task ModifyBaseType_BatchExceedsMaxEditsCap_RejectsBeforeResolvingTargetsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);

        var edits = Enumerable.Range(0, 21)
            .Select(i => new BaseTypeEdit { FilePath = path, TypeName = $"NonexistentType{i}", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add })
            .ToList();

        var result = await tools.ModifyBaseType(reason: "batch test over cap", batchEdits: edits, dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.Message, Does.Contain("20"));
    }

    // ---- Regression: a nested type and its containing type in one batch (text-span batchEdits, not a ReplaceNode fold) ----

    private const string NestedFixtureRelativePath = "ContosoOrders.Core/BaseTypeNestedFixture.cs";

    private const string NestedFixtureSource = """
    namespace ContosoOrders.Core;

    public interface IOuterMarker
    {
    }

    public interface IInnerMarker
    {
    }

    public class OuterHost
    {
        public class InnerNested
        {
        }
    }
    """;

    [TestCase(true)]
    [TestCase(false)]
    public async Task ModifyBaseType_BatchNestedTypeAndContainingType_BothEditsApplyAsync(bool outerFirst)
    {
        using var workspace = InMemoryWorkspace.Create((NestedFixtureRelativePath, NestedFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(NestedFixtureRelativePath);
        var before = workspace.ReadText(NestedFixtureRelativePath);

        var outerEdit = new BaseTypeEdit { FilePath = path, TypeName = "OuterHost", BaseTypeName = "IOuterMarker", Action = AddRemoveAction.add };
        var innerEdit = new BaseTypeEdit { FilePath = path, TypeName = "InnerNested", BaseTypeName = "IInnerMarker", Action = AddRemoveAction.add };

        var result = await tools.ModifyBaseType(
            reason: "regression: ancestor/descendant type targets in one batch",
            batchEdits: outerFirst ? [outerEdit, innerEdit] : [innerEdit, outerEdit],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before
            .Replace("public class OuterHost", "public class OuterHost : IOuterMarker")
            .Replace("public class InnerNested", "public class InnerNested : IInnerMarker");
        Assert.That(workspace.ReadText(NestedFixtureRelativePath), Is.EqualTo(expected), "both base types must be added and nothing else may change");
        Assert.That(result.SuccessData!.Description, Is.EqualTo("Applied 2 base type edit(s) across 1 file(s)."));
    }

    private const string FormattingFixtureRelativePath = "ContosoOrders.Core/BaseTypeFormattingFixture.cs";

    // Deliberately non-canonical whitespace outside the edited base lists: a Formatter pass over the edited types would rewrite it.
    private const string FormattingFixtureSource = """
    namespace ContosoOrders.Core;

    public interface IFormatMarker
    {
    }

    public interface IOtherFormatMarker
    {
    }

    public class   Weird<T>   where T : class
    {
        public int   Spaced( int a,int b )   { return   a+b; }
    }

    public class Keeper : IFormatMarker, IOtherFormatMarker
    {
        public void   Touch(  ) { }
    }

    public class Solo : IFormatMarker
    {
    }
    """;

    [Test]
    public async Task ModifyBaseType_BatchAddWithConstraintRemoveOneOfTwoRemoveOnly_ChangeOnlyTheBaseListsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FormattingFixtureRelativePath, FormattingFixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FormattingFixtureRelativePath);
        var before = workspace.ReadText(FormattingFixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "regression: base-list batchEdits must not reformat the rest of the type",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "Weird", BaseTypeName = "IFormatMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "Keeper", BaseTypeName = "IOtherFormatMarker", Action = AddRemoveAction.remove },
                new BaseTypeEdit { FilePath = path, TypeName = "Solo", BaseTypeName = "IFormatMarker", Action = AddRemoveAction.remove },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);

        var expected = before
            .Replace("Weird<T>   where", "Weird<T> : IFormatMarker   where")
            .Replace("Keeper : IFormatMarker, IOtherFormatMarker", "Keeper : IFormatMarker")
            .Replace("public class Solo : IFormatMarker", "public class Solo");
        Assert.That(workspace.ReadText(FormattingFixtureRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ModifyBaseType_BatchRemoveOfBaseTypeNotPresent_RejectsNamingTheEditWithoutWritingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var path = workspace.PathOf(FixtureRelativePath);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.ModifyBaseType(
            reason: "regression: a remove that matches nothing must not be reported as applied",
            batchEdits:
            [
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetA", BaseTypeName = "IBaseTypeBatchMarker", Action = AddRemoveAction.add },
                new BaseTypeEdit { FilePath = path, TypeName = "BaseTypeBatchTargetB", BaseTypeName = "INotThere", Action = AddRemoveAction.remove },
            ],
            dryRun: false, returnDiff: false, cancellationToken: default);

        Assert.That(!result.IsError, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.Message, Does.Contain("batchEdits[1]"));
            Assert.That(result.ErrorData!.Message, Does.Contain("INotThere"));
            Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before));
        });
    }

    [Test]
    public void BaseTypeTextEdits_TwoEditsRewritingTheSameBaseList_AreRejectedNamingBothEdits()
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From("public class C : IA, IB\n{\n}\n");
        var type = CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

        // Removing IA rewrites the whole list to "IB" and removing IB rewrites it to "IA": the spans genuinely overlap.
        var edits = new[]
        {
            BaseTypeTextEditBuilder.BuildRemoveEdit(0, type, "IA")!.Value,
            BaseTypeTextEditBuilder.BuildRemoveEdit(1, type, "IB")!.Value,
        };

        var applied = AttributeTextEditBuilder.TryApply(text, edits, out var error);

        Assert.That(applied, Is.Null, "overlapping batchEdits must never produce a partial result");
        Assert.That(error, Does.Contain("batchEdits[0]").And.Contain("batchEdits[1]"));
    }

    // ---- RoslynFormattingHelper.ReplaceNodesFormattedAsync must not skip an unlocatable replacement silently ----

    private static async Task<(Document Document, SyntaxNode Root)> ParseAsync(AdhocWorkspace adhoc, string source)
    {
        var project = adhoc.AddProject("ReplaceNodesHelperTest", LanguageNames.CSharp);
        var document = project.AddDocument("Source.cs", source);
        return (document, (await document.GetSyntaxRootAsync())!);
    }

    [Test]
    public async Task ReplaceNodesFormatted_AncestorAndDescendantBothReplaced_ReportsTheDescendantAsUnlocatedAsync()
    {
        using var adhoc = new AdhocWorkspace();
        var (document, root) = await ParseAsync(adhoc, "public class Outer\n{\n    public class Inner\n    {\n    }\n}\n");
        var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var outer = classes.Single(c => c.Identifier.Text == "Outer");
        var inner = classes.Single(c => c.Identifier.Text == "Inner");

        var result = await RoslynFormattingHelper.ReplaceNodesFormattedAsync(
            document,
            root,
            new Dictionary<SyntaxNode, SyntaxNode>
            {
                [outer] = outer.WithIdentifier(SyntaxFactory.Identifier("OuterRenamed")),
                [inner] = inner.WithIdentifier(SyntaxFactory.Identifier("InnerRenamed")),
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.AllReplacementsApplied, Is.False, "the lost descendant replacement must be reported, not skipped silently");
            Assert.That(result.UnlocatedNodes, Has.Count.EqualTo(1));
            Assert.That(result.UnlocatedNodes[0], Is.SameAs(inner));
        });
    }

    [Test]
    public async Task ReplaceNodesFormatted_IndependentReplacements_AllApplyAndNothingIsReportedUnlocatedAsync()
    {
        using var adhoc = new AdhocWorkspace();
        var (document, root) = await ParseAsync(adhoc, "public class First\n{\n}\n\npublic class Second\n{\n}\n");
        var first = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "First");
        var second = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == "Second");

        var result = await RoslynFormattingHelper.ReplaceNodesFormattedAsync(
            document,
            root,
            new Dictionary<SyntaxNode, SyntaxNode>
            {
                [first] = first.WithIdentifier(SyntaxFactory.Identifier("FirstRenamed")),
                [second] = second.WithIdentifier(SyntaxFactory.Identifier("SecondRenamed")),
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.AllReplacementsApplied, Is.True);
            Assert.That(result.UnlocatedNodes, Is.Empty);
            Assert.That(result.Text, Does.Contain("FirstRenamed").And.Contain("SecondRenamed"));
        });
    }
}

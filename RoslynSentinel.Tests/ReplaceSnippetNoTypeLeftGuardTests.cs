// Coverage for the no-type-left guard on ReplaceSnippet: an edit that leaves a .cs file with no type declaration
// (when it had one) is refused before any write, for the single-edit and batchEdits paths.
// Runs on InMemoryWorkspace (real compile gate, no disk writes).
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("WorkspaceTools")]
public class ReplaceSnippetNoTypeLeftGuardTests
{
    private const string PricingRelativePath = "ContosoOrders.Core/OrderPricingCalculator.cs";
    private const string InterfaceRelativePath = "ContosoOrders.Core/IOrderPricingCalculator.cs";
    private const string TwoTypesRelativePath = "ContosoOrders.Core/Pair.cs";
    private const string EmptyRelativePath = "ContosoOrders.Core/Empty.cs";

    private const string PricingSource = """
    namespace ContosoOrders.Core;

    public class OrderPricingCalculator
    {
        public decimal CalcDisc(decimal a) => a;
    }
    """;

    private const string InterfaceSource = """
    namespace ContosoOrders.Core;

    public interface IOrderPricingCalculator
    {
        decimal CalcDisc(decimal a);
    }
    """;

    private const string TwoTypesSource = """
    namespace ContosoOrders.Core;

    public class First { public int A() => 1; }
    public class Second { public int B() => 2; }
    """;

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create((PricingRelativePath, PricingSource), (InterfaceRelativePath, InterfaceSource), (TwoTypesRelativePath, TwoTypesSource));

    [Test]
    public async Task ReplaceSnippet_WholeFileToEmpty_IsRefusedAndFileUnchanged()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalInterface = workspace.ReadText(InterfaceRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "clear the whole interface file by replacing its entire content with nothing",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(InterfaceRelativePath),
            oldContent: InterfaceSource,
            newContent: "");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an edit that leaves a type-less file must be refused");
            Assert.That(result.ErrorData?.Message, Does.Contain("no type declaration"));
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
            Assert.That(result.ErrorData?.Message, Does.Contain("IOrderPricingCalculator.cs"));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain(workspace.PathOf(InterfaceRelativePath)), "the refusal names the workspace-relative path, not an absolute one");
        });
        Assert.That(workspace.ReadText(InterfaceRelativePath), Is.EqualTo(originalInterface), "a refused edit must not write the file");
    }

    [Test]
    public async Task ReplaceSnippet_RemovingOneOfTwoTypes_IsApplied()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "remove the second class and keep the first",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(TwoTypesRelativePath),
            oldContent: "public class Second { public int B() => 2; }",
            newContent: "");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(TwoTypesRelativePath), Does.Contain("public class First"));
            Assert.That(workspace.ReadText(TwoTypesRelativePath), Does.Not.Contain("class Second"));
        });
    }

    [Test]
    public async Task ReplaceSnippetBatch_OneItemEmptiesAFile_NothingWritten()
    {
        using var workspace = CreateWorkspace();
        var tools = TestTools.WorkspaceTools(workspace.Manager);
        var originalPricing = workspace.ReadText(PricingRelativePath);
        var originalInterface = workspace.ReadText(InterfaceRelativePath);

        var result = await tools.ReplaceSnippetTool(
            reason: "annotate the calculator and clear the interface file in one batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = workspace.PathOf(PricingRelativePath), OldContent = "public class OrderPricingCalculator", NewContent = "public class OrderPricingCalculator // reviewed" },
                new SnippetEdit { FilePath = workspace.PathOf(InterfaceRelativePath), OldContent = InterfaceSource, NewContent = "" },
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a batch that empties a file must be refused as a whole");
            Assert.That(result.ErrorData?.Message, Does.Contain("ReplaceSnippet batch: "));
            Assert.That(result.ErrorData?.Message, Does.Contain("no type declaration"));
        });
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(PricingRelativePath), Is.EqualTo(originalPricing), "nothing in a refused batch may be written");
            Assert.That(workspace.ReadText(InterfaceRelativePath), Is.EqualTo(originalInterface), "nothing in a refused batch may be written");
        });
    }

    [Test]
    public async Task ReplaceSnippet_OnEmptyFile_SaysEmptyAndNamesRecoveryTools()
    {
        using var workspace = InMemoryWorkspace.Create((EmptyRelativePath, ""), (InterfaceRelativePath, InterfaceSource));
        var tools = TestTools.WorkspaceTools(workspace.Manager);

        var result = await tools.ReplaceSnippetTool(
            reason: "insert a class into a file that was emptied earlier",
            ProposedChangeAction.apply,
            filePath: workspace.PathOf(EmptyRelativePath),
            oldContent: "",
            newContent: "public class Restored { }");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an empty file has no text for oldContent to match");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("is empty"));
            Assert.That(result.ErrorData?.Message, Does.Contain("CreateFile"));
            Assert.That(result.ErrorData?.Message, Does.Contain("Empty.cs"));
        });
        Assert.That(workspace.ReadText(EmptyRelativePath), Is.EqualTo(""));
    }

    [Test]
    public async Task CreateFile_ExistingEmptyFile_WritesContentAndSaysSo()
    {
        using var fixture = new TestSolutionFixture();
        var emptyPath = Path.Combine(fixture.SolutionDirectory, "EmptyHolder.cs");
        await File.WriteAllTextAsync(emptyPath, "");
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = TestTools.WorkspaceTools(workspaceManager);
        var content = "public class EmptyHolder { }";

        var result = await tools.CreateFileTool(reason: "restore the emptied file with new content", filePath: emptyPath, content: content);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
            Assert.That(result.StatusMessage, Does.Contain("already existed but was empty"));
        });
        Assert.That(await File.ReadAllTextAsync(emptyPath), Is.EqualTo(content));
    }

    [Test]
    public async Task CreateFile_ExistingNonEmptyFile_StillRefused()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tools = TestTools.WorkspaceTools(workspaceManager);
        var existingPath = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = await File.ReadAllTextAsync(existingPath);

        var result = await tools.CreateFileTool(reason: "try to overwrite a populated file with CreateFile", filePath: existingPath, content: "replacement content");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.Message, Does.Contain("already exists"));
        });
        Assert.That(await File.ReadAllTextAsync(existingPath), Is.EqualTo(originalContent));
    }
}

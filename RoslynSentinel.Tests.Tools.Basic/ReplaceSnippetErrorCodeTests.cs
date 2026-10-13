// Regression coverage for finding_batch_edit_errors_lose_tool_exception_code.
//
// A ReplaceSnippet batch whose oldContent matched several places answered errorCode
// "InvalidArgument" even though the message said "ambiguous": the batch loop caught the
// ToolAmbiguousMatchException and kept only its text. The code must follow the cause.
//
// About the tool's error codes, so it runs on an InMemoryWorkspace (no temp directory, MSBuild load or disk write).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ReplaceSnippetErrorCodeTests
{
    private const string DupHolderRelativePath = "ContosoOrders.Core/DupHolder.cs";
    private const string DupHolderSource = "class DupHolder { void A() { Run(); } void B() { Run(); } void Run() { } }";

    private const string MemberDupRelativePath = "ContosoOrders.Core/MemberDup.cs";
    private const string MemberDupSource =
        "class MemberDup\n" +
        "{\n" +
        "    void FirstMethod()\n" +
        "    {\n" +
        "        var total = Compute();\n" +
        "        Log(total);\n" +
        "    }\n" +
        "\n" +
        "    void SecondMethod()\n" +
        "    {\n" +
        "        var total = Compute();\n" +
        "        Log(total);\n" +
        "    }\n" +
        "\n" +
        "    int Compute() => 1;\n" +
        "\n" +
        "    void Log(int x) { }\n" +
        "}\n";

    private const string MemberDupRepeatedBlock = "        var total = Compute();\n        Log(total);";

    private sealed record Harness(InMemoryWorkspace Workspace, WorkspaceTools Tools) : IDisposable
    {
        public string DupHolderPath => Workspace.PathOf(DupHolderRelativePath);

        public string DupHolderText => Workspace.ReadText(DupHolderRelativePath);

        public void Dispose() => Workspace.Dispose();
    }

    private static Harness CreateHarness()
    {
        var workspace = InMemoryWorkspace.Create(
            (DupHolderRelativePath, DupHolderSource),
            (MemberDupRelativePath, MemberDupSource));
        var manager = workspace.Manager;

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var diagnosticEngine = new DiagnosticEngine(manager);
        var validationEngine = new ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = new SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = new WorkspaceTools(
            manager,
            validationEngine,
            diffEngine, diagnosticEngine,
            new SolutionManagementEngine(manager),
            new StructuralRefinementEngine(manager, config),
            new DependencyEngine(manager),
            new ProjectConsistencyEngine(manager),
            config, NullLogger<WorkspaceTools>.Instance,
            new BuildEngine(manager, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(manager),
            new WorkspaceReadNavigationImpl(manager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return new Harness(workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = CreateHarness();
        var original = h.DupHolderText;

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression ambiguous batch",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "Run();", NewContent = "Run(1);" }]);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("oldContent is ambiguous").And.Not.Contain("contextSnippet"),
            "the message must name the parameter the caller actually passed");
        Assert.That(h.DupHolderText, Is.EqualTo(original), "a rejected batch must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_MissingOldContent_ReportsNotFoundCodeAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression missing batch",
            ProposedChangeAction.apply,
            batchEdits: [new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "ThisTextIsNotInTheFile();", NewContent = "x" }]);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousAndMissingTogether_ReportsAmbiguousCodeAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression mixed batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "Run();", NewContent = "Run(1);" },
                new SnippetEdit { FilePath = h.DupHolderPath, OldContent = "ThisTextIsNotInTheFile();", NewContent = "x" },
            ]);

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous),
            "when every rejection is a lookup-style failure the actionable one (disambiguate) wins");
    }

    [Test]
    public async Task ReplaceSnippet_Single_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippet(
            reason: "error code regression ambiguous single",
            ProposedChangeAction.apply,
            filePath: h.DupHolderPath,
            oldContent: "Run();",
            newContent: "Run(1);");

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("oldContent is ambiguous").And.Not.Contain("contextSnippet"));
    }

    [Test]
    public async Task ReplaceSnippet_Single_AmbiguousMultiLineOldContent_NamesEnclosingMemberPerMatchAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);

        var result = await h.Tools.ReplaceSnippet(
            reason: "member names in ambiguity error single",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: MemberDupRepeatedBlock,
            newContent: "        Log(Compute());");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("in FirstMethod").And.Contain("in SecondMethod"));
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(original), "a rejected edit must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousMultiLineOldContent_NamesEnclosingMemberPerMatchAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);

        var result = await h.Tools.ReplaceSnippet(
            reason: "member names in ambiguity error batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit
                {
                    FilePath = h.Workspace.PathOf(MemberDupRelativePath),
                    OldContent = MemberDupRepeatedBlock,
                    NewContent = "        Log(Compute());",
                },
            ]);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("in FirstMethod").And.Contain("in SecondMethod"));
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(original), "a rejected batch must not write anything");
    }

    /// <summary>
    /// The offset and the 1-based line of the Nth (0-based) occurrence of <see cref="MemberDupRepeatedBlock"/>,
    /// computed from the real harness text so the tests never hard-code a line number.
    /// </summary>
    private static (int Offset, int Line) LocateRepeatedBlock(string text, int occurrence)
    {
        var offset = -1;
        for (var i = 0; i <= occurrence; i++)
        {
            offset = text.IndexOf(MemberDupRepeatedBlock, offset + 1, StringComparison.Ordinal);
            Assert.That(offset, Is.GreaterThanOrEqualTo(0), "the harness source must contain the repeated block");
        }

        var line = text.Take(offset).Count(c => c == '\n') + 1;
        return (offset, line);
    }

    [Test]
    public async Task ReplaceSnippet_Single_UniqueMatchInFileWithRepeatedBlocks_StillSucceedsAsync()
    {
        using var h = CreateHarness();

        var result = await h.Tools.ReplaceSnippet(
            reason: "unique match unchanged",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: "int Compute() => 1;",
            newContent: "int Compute() => 2;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Does.Contain("int Compute() => 2;"));
    }

    [Test]
    public async Task ReplaceSnippet_Single_WithinMember_SelectsThatMatchAndEditsOnlyThereAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        const string replacement = "        Log(Compute());";
        var (firstOffset, _) = LocateRepeatedBlock(original, 0);

        var result = await h.Tools.ReplaceSnippet(
            reason: "withinMember selects first match",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: MemberDupRepeatedBlock,
            newContent: replacement,
            withinMember: "FirstMethod");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var expected = original.Remove(firstOffset, MemberDupRepeatedBlock.Length).Insert(firstOffset, replacement);
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(expected), "only the FirstMethod block may change");
    }

    [Test]
    public async Task ReplaceSnippet_Single_MatchLine_SelectsTheMatchStartingThereAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        const string replacement = "        Log(Compute());";
        var (secondOffset, secondLine) = LocateRepeatedBlock(original, 1);

        var result = await h.Tools.ReplaceSnippet(
            reason: "matchLine selects second match",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: MemberDupRepeatedBlock,
            newContent: replacement,
            matchLine: secondLine);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var expected = original.Remove(secondOffset, MemberDupRepeatedBlock.Length).Insert(secondOffset, replacement);
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(expected), "only the SecondMethod block may change");
    }

    [Test]
    public async Task ReplaceSnippet_Single_StaleMatchLine_ReturnsSelectorMismatchErrorAndWritesNothingAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        var (_, firstLine) = LocateRepeatedBlock(original, 0);
        var staleLine = firstLine - 1;

        var result = await h.Tools.ReplaceSnippet(
            reason: "stale matchLine",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: MemberDupRepeatedBlock,
            newContent: "        Log(Compute());",
            matchLine: staleLine);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain($"matchLine={staleLine}").And.Contain("none satisfied the selector"));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("is ambiguous"), "a stale selector is a distinct error from an ambiguity");
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(original), "a rejected edit must not write anything");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_MatchLineAndWithinMember_ResolveAgainstTheOriginalTextAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        var path = h.Workspace.PathOf(MemberDupRelativePath);
        const string firstReplacement = "        Log(Compute());";
        const string secondReplacement = "        Log(Compute() + 1);";
        var (firstOffset, firstLine) = LocateRepeatedBlock(original, 0);
        var (secondOffset, secondLine) = LocateRepeatedBlock(original, 1);

        // The first edit shrinks two lines to one; the second edit's matchLine is still the line
        // in the ORIGINAL file, which only works if every batch edit is anchored against one snapshot.
        var result = await h.Tools.ReplaceSnippet(
            reason: "batch matchLine against original",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = path, OldContent = MemberDupRepeatedBlock, NewContent = firstReplacement, MatchLine = firstLine },
                new SnippetEdit { FilePath = path, OldContent = MemberDupRepeatedBlock, NewContent = secondReplacement, WithinMember = "SecondMethod", MatchLine = secondLine },
            ]);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var expected = original
            .Remove(secondOffset, MemberDupRepeatedBlock.Length).Insert(secondOffset, secondReplacement)
            .Remove(firstOffset, MemberDupRepeatedBlock.Length).Insert(firstOffset, firstReplacement);
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(expected));
    }

    [Test]
    public async Task ReplaceSnippet_TopLevelSelectorWithBatchEdits_IsRejectedAsInvalidArgumentAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        var path = h.Workspace.PathOf(MemberDupRelativePath);
        var batch = new List<SnippetEdit>
        {
            new() { FilePath = path, OldContent = "int Compute() => 1;", NewContent = "int Compute() => 2;" },
        };

        var withMember = await h.Tools.ReplaceSnippet(
            reason: "top-level withinMember with batchEdits",
            ProposedChangeAction.apply,
            withinMember: "FirstMethod",
            batchEdits: batch);
        var withLine = await h.Tools.ReplaceSnippet(
            reason: "top-level matchLine with batchEdits",
            ProposedChangeAction.apply,
            matchLine: 5,
            batchEdits: batch);

        foreach (var result in new[] { withMember, withLine })
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("not both"));
        }

        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(original), "a rejected call must not write anything");
    }
}

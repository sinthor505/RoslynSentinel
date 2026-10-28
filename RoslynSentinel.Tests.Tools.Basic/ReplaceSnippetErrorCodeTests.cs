// Regression coverage for finding_batch_edit_errors_lose_tool_exception_code.
//
// A ReplaceSnippetTool batch whose oldContent matched several places answered errorCode
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

    // The file has "    int Compute() => 1;", a blank line, then "    void Log(int x) { }". This block drops the
    // indentation on both lines, so it matches only at the looser (IgnoreIndentation) tier and is unique.
    private const string MemberDupLooserBlock = "int Compute() => 1;\n\nvoid Log(int x) { }";

    private const string MemberDupMissingBlock = "NotInTheFile();";

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

        var config = TestEngines.SentinelConfiguration();
        var diffEngine = TestEngines.DiffEngine();
        var diagnosticEngine = TestEngines.DiagnosticEngine(manager);
        var validationEngine = TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNavigationEngine = TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance);
        var tools = TestTools.WorkspaceTools(
            manager,
            validationEngine,
            diffEngine, diagnosticEngine,
            TestEngines.SolutionManagementEngine(manager),
            TestEngines.StructuralRefinementEngine(manager, config),
            TestEngines.DependencyEngine(manager),
            TestEngines.ProjectConsistencyEngine(manager),
            config, NullLogger<WorkspaceTools>.Instance,
            TestEngines.BuildEngine(manager, diagnosticEngine),
            symbolNavigationEngine,
            new TestRunEngine(manager),
            TestTools.WorkspaceReadNavigationImpl(manager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        return new Harness(workspace, tools);
    }

    [Test]
    public async Task ReplaceSnippet_Batch_AmbiguousOldContent_ReportsAmbiguousCodeAndNamesOldContentAsync()
    {
        using var h = CreateHarness();
        var original = h.DupHolderText;

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
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

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "stale matchLine",
            ProposedChangeAction.apply,
            filePath: h.Workspace.PathOf(MemberDupRelativePath),
            oldContent: MemberDupRepeatedBlock,
            newContent: "        Log(Compute());",
            matchLine: staleLine);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("but not at the matchLine you supplied").And.Contain($"matchLine={firstLine} "));
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
        var result = await h.Tools.ReplaceSnippetTool(
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
    public async Task ReplaceSnippet_Batch_ReturnDiff_EmitsOneHunkPerEditRegionWithCountsThatMatchTheFileAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        var path = h.Workspace.PathOf(MemberDupRelativePath);
        const string firstReplacement = "        Log(Compute());";
        const string secondReplacement = "        Log(Compute() + 1);";
        var (_, firstLine) = LocateRepeatedBlock(original, 0);
        var (_, secondLine) = LocateRepeatedBlock(original, 1);

        // Both edits shrink two lines to one, so the second hunk's new-side start is one line
        // earlier than its old-side start; the second edit's matchLine is an ORIGINAL-file line.
        var result = await h.Tools.ReplaceSnippetTool(
            reason: "batch returnDiff hunk shape",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = path, OldContent = MemberDupRepeatedBlock, NewContent = firstReplacement, MatchLine = firstLine },
                new SnippetEdit { FilePath = path, OldContent = MemberDupRepeatedBlock, NewContent = secondReplacement, MatchLine = secondLine },
            ],
            returnDiff: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var diff = result.SuccessData!.DiffContent;
        Assert.That(diff, Is.Not.Null.And.Not.Empty);
        Assert.That(diff, Does.Not.Contain("\r"), "diff text must use \\n only");

        var diffLines = diff!.Split('\n');
        var headers = diffLines.Where(l => l.StartsWith("@@ ", StringComparison.Ordinal)).ToList();
        Assert.That(headers, Is.EqualTo(new[]
        {
            $"@@ -{firstLine},2 +{firstLine},1 @@",
            $"@@ -{secondLine},2 +{secondLine - 1},1 @@",
        }), "one hunk per edit region, counts and starts in standard unified-diff form");

        // Each hunk body is one contiguous '-' run followed by one contiguous '+' run.
        var bodySigns = new List<string>();
        foreach (var line in diffLines)
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                bodySigns.Add(string.Empty);
            }
            else if (bodySigns.Count > 0 && (line.StartsWith('-') || line.StartsWith('+')))
            {
                bodySigns[^1] += line[0];
            }
        }
        Assert.That(bodySigns, Is.EqualTo(new[] { "--+", "--+" }));

        var originalLines = original.Split('\n');
        var changedLines = h.Workspace.ReadText(MemberDupRelativePath).Split('\n');
        Assert.That(diff, Does.Contain($"-{originalLines[firstLine - 1]}\n-{originalLines[firstLine]}\n+{firstReplacement}\n"));
        Assert.That(diff, Does.Contain($"-{originalLines[secondLine - 1]}\n-{originalLines[secondLine]}\n+{secondReplacement}\n"));
        Assert.That(changedLines[firstLine - 1], Is.EqualTo(firstReplacement), "first hunk's new start must point at its replacement in the real file");
        Assert.That(changedLines[secondLine - 2], Is.EqualTo(secondReplacement), "second hunk's new start must point at its replacement in the real file");
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

        var withMember = await h.Tools.ReplaceSnippetTool(
            reason: "top-level withinMember with batchEdits",
            ProposedChangeAction.apply,
            withinMember: "FirstMethod",
            batchEdits: batch);
        var withLine = await h.Tools.ReplaceSnippetTool(
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

    [Test]
    public async Task ReplaceSnippet_Single_MatchLineCopiedFromAmbiguityError_EditsExactlyThatMatchAsync()
    {
        using var h = CreateHarness();
        var original = h.Workspace.ReadText(MemberDupRelativePath);
        var path = h.Workspace.PathOf(MemberDupRelativePath);
        const string replacement = "        Log(Compute());";
        var (secondOffset, secondLine) = LocateRepeatedBlock(original, 1);

        var ambiguous = await h.Tools.ReplaceSnippetTool(
            reason: "read selectors off the ambiguity error",
            ProposedChangeAction.apply,
            filePath: path,
            oldContent: MemberDupRepeatedBlock,
            newContent: replacement);

        Assert.That(ambiguous.IsError, Is.True);
        Assert.That(ambiguous.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        var listed = System.Text.RegularExpressions.Regex.Match(
            ambiguous.ErrorData.Message, @"Candidate 2 of 2 \([^)]*\):\s+(?:withinMember=\""[^\""]*\"" )?matchLine=(\d+) ");
        Assert.That(listed.Success, Is.True, ambiguous.ErrorData.Message);
        var copiedLine = int.Parse(listed.Groups[1].Value);
        Assert.That(copiedLine, Is.EqualTo(secondLine));

        var retry = await h.Tools.ReplaceSnippetTool(
            reason: "retry with the copied matchLine",
            ProposedChangeAction.apply,
            filePath: path,
            oldContent: MemberDupRepeatedBlock,
            newContent: replacement,
            matchLine: copiedLine);

        Assert.That(retry.IsError, Is.False, retry.ErrorData?.Message);
        var expected = original.Remove(secondOffset, MemberDupRepeatedBlock.Length).Insert(secondOffset, replacement);
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(expected), "only the second match may change");
    }

    [Test]
    public async Task ReplaceSnippet_Batch_LooserTierAndNotFoundTogether_ReportsAmbiguousCodeAndWritesNothingAsync()
    {
        using var h = CreateHarness();
        var path = h.Workspace.PathOf(MemberDupRelativePath);
        var original = h.Workspace.ReadText(MemberDupRelativePath);

        var result = await h.Tools.ReplaceSnippetTool(
            reason: "mixed looser-tier and missing batch",
            ProposedChangeAction.apply,
            batchEdits:
            [
                new SnippetEdit { FilePath = path, OldContent = MemberDupLooserBlock, NewContent = "void Log(int x) { }" },
                new SnippetEdit { FilePath = path, OldContent = MemberDupMissingBlock, NewContent = "x" },
            ]);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous),
            "a mix of MatchedLooserThanRequested and NotFound reports Ambiguous, not InvalidArgument");
        Assert.That(h.Workspace.ReadText(MemberDupRelativePath), Is.EqualTo(original),
            "a rejected batch must not write anything");
    }
}

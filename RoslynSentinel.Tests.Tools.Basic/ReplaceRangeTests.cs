using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// ReplaceRange (proposal_replace_range_tool.md, slice 10-2): replaces or deletes the whole lines between two anchor lines,
// each of which must match exactly one line of the file. Most tests run on an InMemoryWorkspace (the refusals and the text
// edits); the undo round trip needs a disk-backed solution and the stale-read refusal a real PersistentWorkspaceManager.
[TestFixture]
[Category("WorkspaceTools")]
public class ReplaceRangeTests
{
    private const string TargetRelativePath = "ContosoOrders.Core/RangeTarget.cs";

    // Line numbers (1-based) the tests rely on: BEGIN-OLD is line 6, END-OLD line 9, Keep2 line 10, "Run();" lines 14 and 19.
    private static readonly string[] TargetLines =
    [
        "namespace ContosoOrders.Core;",
        "",
        "public class RangeTarget",
        "{",
        "    public int Keep1() => 1;",
        "    // BEGIN-OLD",
        "    public int Old1() => 10;",
        "    public int Old2() => 20;",
        "    // END-OLD",
        "    public int Keep2() => 2;",
        "",
        "    public void A()",
        "    {",
        "        Run();",
        "    }",
        "",
        "    public void B()",
        "    {",
        "        Run();",
        "    }",
        "",
        "    private void Run() { }",
        "}",
    ];

    private const string OnlyTypeRelativePath = "ContosoOrders.Core/OnlyType.cs";

    private static readonly string[] OnlyTypeLines =
    [
        "namespace ContosoOrders.Core;",
        "",
        "public class OnlyType",
        "{",
        "    public int Value => 1;",
        "}",
    ];

    private const string StaleOriginal = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    // Reading agent's StartSeq past any competing write, so ConcurrentEditRefused cannot pre-empt StaleRead on one process.
    private const long StartSeqAfterEveryWrite = long.MaxValue;

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static string Join(IEnumerable<string> lines, string eol = "\n") => string.Join(eol, lines) + eol;

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static InMemoryWorkspace CreateWorkspace() =>
        InMemoryWorkspace.Create(
            (TargetRelativePath, Join(TargetLines)),
            (OnlyTypeRelativePath, Join(OnlyTypeLines)));

    private static Task<SentinelCallToolResult<ReplaceSnippetResult>> RangeAsync(
        InMemoryWorkspace workspace, string firstLineText, string lastLineText, string newContent, string relativePath = TargetRelativePath) =>
        TestTools.WorkspaceTools(workspace.Manager).ReplaceRangeTool(
            reason: "replace range test", filePath: workspace.PathOf(relativePath),
            firstLineText: firstLineText, lastLineText: lastLineText, newContent: newContent);

    private static string ExpectedTarget(int firstLine, int lastLine, params string[] replacement) =>
        Join(TargetLines.Take(firstLine - 1).Concat(replacement).Concat(TargetLines.Skip(lastLine)));

    [Test]
    public async Task ReplaceRange_InclusiveRange_ReplacesBothAnchorLinesAndEverythingBetweenAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RangeAsync(workspace, "// BEGIN-OLD", "// END-OLD", "    public int New1() => 100;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(6, 9, "    public int New1() => 100;")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 6-9 (4 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_AnchorsWithoutIndentation_StillMatchAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RangeAsync(workspace, "public int Old1() => 10;", "public int Old2() => 20;", "    public int New1() => 100;\n");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(7, 8, "    public int New1() => 100;")),
            "the replacement's own trailing newline must not add a blank line");
    }

    [Test]
    public async Task ReplaceRange_EmptyNewContent_DeletesTheLinesIncludingTheirTerminatorsAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RangeAsync(workspace, "// BEGIN-OLD", "// END-OLD", "");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(6, 9)));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("deleted lines 6-9"));
    }

    [Test]
    public async Task ReplaceRange_OneLineRange_ReplacesThatLineAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RangeAsync(workspace, "public int Keep2() => 2;", "public int Keep2() => 2;", "    public int Keep2() => 22;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(10, 10, "    public int Keep2() => 22;")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("line 10 (1 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_UnindentedNewContent_IsReindentedToBlockAsync()
    {
        const string relativePath = "ContosoOrders.Core/IndentTarget.cs";
        var lines = new[]
        {
            "namespace ContosoOrders.Core;",
            "",
            "public class IndentTarget",
            "{",
            "    public int Compute(int weightGrams, int zone)",
            "    {",
            "        var total = 0;",
            "        total += weightGrams;",
            "        return total;",
            "    }",
            "}",
        };
        using var workspace = InMemoryWorkspace.Create((relativePath, Join(lines)));

        var result = await RangeAsync(workspace, "total += weightGrams;", "total += weightGrams;", "total += weightGrams * zone;", relativePath);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(relativePath)), Does.Contain("\n        total += weightGrams * zone;\n"),
            "the column-0 newContent must be re-indented to the 8-space block it replaces");
    }

    [Test]
    public async Task ReplaceRange_AnchorMatchesNoLine_IsRefusedWithCountZeroAndNearbyLinesAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var missing = await RangeAsync(workspace, "// NO-SUCH-LINE", "// END-OLD", "");
        var partial = await RangeAsync(workspace, "public int Old", "// END-OLD", "");

        Assert.That(missing.IsError, Is.True);
        Assert.That(missing.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(missing.ErrorData.Message, Does.Contain("firstLineText matches 0 lines"));
        Assert.That(partial.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(partial.ErrorData.Message, Does.Contain("7: public int Old1() => 10;").And.Contain("8: public int Old2() => 20;"),
            "a zero-match refusal lists the nearby lines that contain the anchor text");
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_AnchorMatchesSeveralLines_IsRefusedListingEveryLineAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "Run();", "// END-OLD", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("firstLineText matches 2 lines").And.Contain("lines 14, 19"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_MultiLineFirstAnchorMatchingOneBlock_StartsTheRangeAtTheBlocksFirstLineAsync()
    {
        using var workspace = CreateWorkspace();

        var result = await RangeAsync(workspace, "// BEGIN-OLD\n    public int Old1() => 10;", "// END-OLD", "    public int New1() => 100;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(6, 9, "    public int New1() => 100;")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 6-9 (4 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_MultiLineLastAnchorMatchingOneBlock_EndsTheRangeAtTheBlocksLastLineAsync()
    {
        using var workspace = CreateWorkspace();

        // CRLF and dropped indentation in the anchor still match the file's lines.
        var result = await RangeAsync(workspace, "// BEGIN-OLD", "public int Old2() => 20;\r\n// END-OLD", "    public int New1() => 100;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(6, 9, "    public int New1() => 100;")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 6-9 (4 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_SameMultiLineBlockAsBothAnchors_ReplacesExactlyThatBlockAsync()
    {
        using var workspace = CreateWorkspace();
        const string block = "// BEGIN-OLD\n    public int Old1() => 10;";

        var result = await RangeAsync(workspace, block, block, "    public int New1() => 100;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(6, 7, "    public int New1() => 100;")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 6-7 (2 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_MultiLineAnchorMatchingTwoBlocks_IsRefusedListingBothBlocksAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "{\n        Run();", "// END-OLD", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("firstLineText matches 2 blocks").And.Contain("lines 13-14, 18-19"),
            "both blocks are listed by their line spans");
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_MultiLineAnchorMatchingNoBlock_IsRefusedNamingTheClosestBlockAndTheFirstDifferingLineAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "// BEGIN-OLD\npublic int Wrong() => 1;", "// END-OLD", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("firstLineText matches 0 blocks")
            .And.Contain("starts at line 6").And.Contain("agrees for 1 of 2 lines")
            .And.Contain("'public int Wrong() => 1;'").And.Contain("'public int Old1() => 10;'"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_AmbiguousSingleLineAnchor_ListsEveryCandidateLineWithItsTextAsync()
    {
        using var workspace = CreateWorkspace();

        // firstLineText is ambiguous too, so lastLineText is not resolved against it and its own candidates are listed.
        var result = await RangeAsync(workspace, "Run();", "}", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("lastLineText matches 3 lines").And.Contain("lines 15, 20, 23")
            .And.Contain("15: }").And.Contain("23: }"), "each candidate carries its line number and text");
    }

    [Test]
    public async Task ReplaceRange_LastAnchorMatchingSeveralLines_ResolvesToTheFirstMatchAtOrAfterTheFirstAnchorAsync()
    {
        using var workspace = CreateWorkspace();

        // '}' is on lines 15, 20 and 23; the first at or after line 12 (public void A()) is line 15.
        var result = await RangeAsync(workspace, "public void A()", "}", "    public void A() { }");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(TargetRelativePath)), Is.EqualTo(ExpectedTarget(12, 15, "    public void A() { }")));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 12-15 (4 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_NestedMethodAndClassClosingBraces_EndTheRangeAtTheMethodsBraceAsync()
    {
        const string relativePath = "ContosoOrders.Core/Nested.cs";
        var lines = new[]
        {
            "namespace ContosoOrders.Core;",
            "",
            "public class Nested",
            "{",
            "    public int Value()",
            "    {",
            "        return 1;",
            "    }",
            "}",
        };
        using var workspace = InMemoryWorkspace.Create((relativePath, Join(lines)));

        // The method's '}' (line 8) and the class's '}' (line 9) both match; the first at or after the method line wins.
        var result = await RangeAsync(workspace, "public int Value()", "}", "    public int Value() => 2;", relativePath);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(Normalize(workspace.ReadText(relativePath)),
            Is.EqualTo(Join(lines.Take(4).Concat(["    public int Value() => 2;"]).Concat(lines.Skip(8)))));
        Assert.That(result.SuccessData!.ReplacementResult!.Summary, Does.Contain("lines 5-8 (4 line(s))"));
    }

    [Test]
    public async Task ReplaceRange_LastAnchorMatchingOnlyBeforeTheFirstAnchor_IsRefusedAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        // 'Run();' is on lines 14 and 19, both above 'private void Run() { }' (line 22).
        var result = await RangeAsync(workspace, "private void Run() { }", "Run();", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("lastLineText matches 2 places").And.Contain("lines 14, 19").And.Contain("none starts at or after line 22"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_LastAnchorAboveFirst_IsRefusedWithBothLineNumbersAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "// END-OLD", "// BEGIN-OLD", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("line 9").And.Contain("line 6"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_BlankAnchor_IsRefusedAndWritesNothingAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "   ", "// END-OLD", "");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before));
    }

    [Test]
    public async Task ReplaceRange_BreakingCompileReplacement_IsRejectedAndFileIsUnchangedAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(TargetRelativePath);

        var result = await RangeAsync(workspace, "// BEGIN-OLD", "// END-OLD", "    public int Bad() => MissingSymbol();");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(result.ErrorData.Message, Does.Contain("ReplaceRange").And.Contain("introduces new compiler errors"));
        Assert.That(workspace.ReadText(TargetRelativePath), Is.EqualTo(before), "a rejected range must not write");
    }

    [Test]
    public async Task ReplaceRange_RangeThatRemovesTheLastType_IsRefusedByTheNoTypeLeftGuardAsync()
    {
        using var workspace = CreateWorkspace();
        var before = workspace.ReadText(OnlyTypeRelativePath);

        var result = await RangeAsync(workspace, "public class OnlyType", "}", "// gone", OnlyTypeRelativePath);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData.Message, Does.Contain("ReplaceRange").And.Contain("no type declaration"));
        Assert.That(workspace.ReadText(OnlyTypeRelativePath), Is.EqualTo(before), "a refused range must not write");
    }

    [Test]
    public async Task ReplaceRange_DiskBackedApply_ReturnsChangeIdThatUndoChangeRevertsAsync()
    {
        const string relativePath = "ContosoOrders.Core/Rd/RangeDisk.cs";
        var source = Join(
            [
                "namespace Rd;",
                "",
                "public class RangeDisk",
                "{",
                "    public int Keep() => 1;",
                "    // BEGIN-OLD",
                "    public int Old() => 2;",
                "    // END-OLD",
                "}",
            ],
            "\r\n");
        using var fixture = new TestSolutionFixture();
        using var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        await fixture.AddFileToSolution(manager, relativePath, source, reloadSolution: true);
        var validationEngine = TestEngines.ValidationEngine(manager);
        var editImpl = new WorkspaceFileEditImpl(
            manager,
            TestTools.WorkspaceReadNavigationImpl(manager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            validationEngine,
            TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        string ReadDisk() => File.ReadAllText(Path.Combine(fixture.SolutionDirectory, relativePath));

        var apply = await editImpl.ReplaceRange(
            new ToolCallReason("testing replace range undo"), relativePath, "// BEGIN-OLD", "// END-OLD", "    public int New() => 3;");

        Assert.That(apply.IsError, Is.False, apply.ErrorData?.Message);
        Assert.That(apply.SuccessData!.ChangeId, Is.Not.Null);
        Assert.That(apply.SuccessData.ReplacementResult!.Summary, Does.Contain("UndoChange"));
        Assert.That(ReadDisk(), Does.Contain("New() => 3").And.Not.Contain("Old()"));

        var undo = await editImpl.UndoChange(new ToolCallReason("testing undo of replace range"), apply.SuccessData.ChangeId!);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        Assert.That(ReadDisk(), Is.EqualTo(source));
    }

    [Test]
    public async Task ReplaceRange_ObservedThenChangedOnDisk_RefusesWithStaleReadAndWritesNothingAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ReplaceRangeStaleRead", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            var external = StaleOriginal.Replace("=> 1", "=> 3");
            File.WriteAllText(file, StaleOriginal);
            // Same harness as StaleReadWriteToolsTests; the framework reference keeps the compile gate from rejecting the edit before the stale guard runs.
            var project = new AdhocWorkspace().AddProject("ReplaceRangeStaleRead", LanguageNames.CSharp)
                .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            var solution = project.AddDocument(Path.GetFileName(file), SourceText.From(StaleOriginal), filePath: file).Project.Solution;
            using var manager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            manager.EolModeOverride = EolMode.Crlf;
            manager.SetTestSolution(solution);
            ToolCallContext.Set(new ToolCallContext("agentA", null, "ReplaceRange", null, StartSeqAfterEveryWrite));
            manager.RecordObserved(new FilePathWrapper(file), StaleOriginal);
            File.WriteAllText(file, external);

            var result = await TestTools.WorkspaceTools(manager).ReplaceRangeTool(
                reason: "test message", filePath: file,
                firstLineText: "public int X() => 1;", lastLineText: "public int X() => 1;", newContent: "    public int X() => 2;");

            Assert.That(result.IsError, Is.True, "a stale target must be refused");
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.StaleRead), result.ErrorData.Message);
            Assert.That(result.ErrorData.Message, Does.Contain(Path.GetFileName(file)).And.Contain("ReadFile"));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(external), "the externally written disk text must be left untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

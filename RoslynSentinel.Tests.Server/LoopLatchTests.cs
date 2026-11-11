using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// The server loop latch (proposal_loop_latch.md): <see cref="ToolLoopLatch"/> state, the <see cref="LoopLatchFilter"/> that
/// drives it, the <see cref="LoopLatchOptions"/> flag, and the loopLatch field of McpServerStatus. No live model and no
/// running server: the filter logic is exercised through its seam with scripted results. The harness side
/// (LoopLatchIgnored) is in Tests.ModelEval's LoopLatchRunnerTests.
/// </summary>
// LoopLatchOptions is process-wide static state, so the fixture must not overlap another fixture that flips it.
[TestFixture]
[NonParallelizable]
public class LoopLatchTests
{
    private const string NextStepSentence = "use RenameSymbol(symbolName: \"Foo\") instead of editing the text.";

    [SetUp]
    public void SetUp() => LoopLatchOptions.Configure(["--loop-latch"]);

    [TearDown]
    public void TearDown() => LoopLatchOptions.Configure([]);

    // ---- ToolLoopLatch state ----

    [Test]
    public void TwoSameWayFailures_LatchTheTool_AndOnlyTheSecondReportsTheTrip()
    {
        var latch = new ToolLoopLatch();

        var first = latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "no match");
        Assert.That(first, Is.False);
        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Null, "one failure does not latch");

        var second = latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "no match");
        Assert.That(second, Is.True);
        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Does.StartWith("ReplaceSnippet is DISABLED until another tool succeeds"));
        Assert.That(latch.TryRefuse("a", "ReadFile"), Is.Null, "only the looping tool is restricted");
    }

    [Test]
    public void AlternatingArgumentErrorsOfTheSameCode_Count_BecauseArgumentsAreNotPartOfTheKey()
    {
        var latch = new ToolLoopLatch();

        latch.RecordOutcome(null, "ReplaceSnippet", false, ToolErrorCode.InvalidArgument, "'oldContent' is required");
        latch.RecordOutcome(null, "ReplaceSnippet", false, ToolErrorCode.InvalidArgument, "'newContent' is required");

        Assert.That(latch.TryRefuse(null, "ReplaceSnippet"), Is.Not.Null);
    }

    [Test]
    public void TwoFailuresWithDifferentErrorCodes_DoNotLatch()
    {
        var latch = new ToolLoopLatch();

        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.InvalidArgument, "y");

        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Null);
        Assert.That(latch.Snapshot(), Is.Empty);
    }

    [Test]
    public void CompileGateRejections_AreTheSameWayOnlyWithTheSameFirstDiagnosticId()
    {
        var differentIds = new ToolLoopLatch();
        differentIds.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.ValidationFailed, "ReplaceSnippet validate failed: CS0103 name does not exist");
        differentIds.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.ValidationFailed, "ReplaceSnippet validate failed: CS1002 ; expected");
        Assert.That(differentIds.TryRefuse("a", "ReplaceSnippet"), Is.Null);

        var sameId = new ToolLoopLatch();
        sameId.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.ValidationFailed, "ReplaceSnippet validate failed: CS0103 name does not exist in Foo");
        sameId.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.ValidationFailed, "ReplaceSnippet validate failed: CS0103 name does not exist in Bar");
        Assert.That(sameId.TryRefuse("a", "ReplaceSnippet"), Is.Not.Null);

        Assert.That(ToolLoopLatch.FirstDiagnosticId(ToolErrorCode.NotFound, "CS0103 here"), Is.Null, "only compile-gate rejections key on a diagnostic id");
    }

    [Test]
    public void SuccessOfAnotherTool_ReleasesTheLatch_AndClearsTheFailureStreak()
    {
        var latch = new ToolLoopLatch();
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        Assume.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Not.Null);

        latch.RecordOutcome("a", "ReadFile", true, null, null);

        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Null);
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Null, "the earlier failures no longer count once a success intervened");
    }

    [Test]
    public void FailureOfAnotherTool_DoesNotReleaseTheLatch()
    {
        var latch = new ToolLoopLatch();
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");

        latch.RecordOutcome("a", "ReadFile", false, ToolErrorCode.NotFound, "no file");

        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Not.Null);
    }

    [Test]
    public void LatchIsPerAgent_OneAgentsLoopDoesNotBlockAnother_AndNoIdSharesTheDefaultKey()
    {
        var latch = new ToolLoopLatch();
        latch.RecordOutcome("agent-a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        latch.RecordOutcome("agent-a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");

        Assert.That(latch.TryRefuse("agent-a", "ReplaceSnippet"), Is.Not.Null);
        Assert.That(latch.TryRefuse("agent-b", "ReplaceSnippet"), Is.Null, "agent B is not latched by agent A's loop");
        Assert.That(latch.TryRefuse(null, "ReplaceSnippet"), Is.Null);

        // A success by agent B does not release agent A either.
        latch.RecordOutcome("agent-b", "ReadFile", true, null, null);
        Assert.That(latch.TryRefuse("agent-a", "ReplaceSnippet"), Is.Not.Null);

        latch.RecordOutcome(null, "Build", false, ToolErrorCode.BuildFailed, "x");
        latch.RecordOutcome("  ", "Build", false, ToolErrorCode.BuildFailed, "x");
        Assert.That(latch.TryRefuse(null, "Build"), Is.Not.Null, "null and blank ids share the default key");
        Assert.That(latch.Snapshot().Select(s => (s.AgentId, s.Tool)),
            Is.EqualTo(new[] { ("agent-a", "ReplaceSnippet"), (ToolLoopLatch.DefaultAgentKey, "Build") }.OrderBy(t => t.Item1, StringComparer.Ordinal)));
    }

    [Test]
    public void Refusal_WithANextStep_QuotesItVerbatim()
    {
        var latch = new ToolLoopLatch();
        var message = "ReplaceSnippet: no match for the snippet.\nNext step: " + NextStepSentence + "\nMore detail.";
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, message);
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, message);

        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.EqualTo(
            "ReplaceSnippet is DISABLED until another tool succeeds: it failed the same way 2 times. You MUST do the next step now: " + NextStepSentence));
    }

    [Test]
    public void Refusal_WithoutANextStep_PointsAtTheCallableReadTools_AndNamesNoOther()
    {
        var latch = new ToolLoopLatch();
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "no match");
        latch.RecordOutcome("a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "no match");

        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.EqualTo(
            "ReplaceSnippet is DISABLED until another tool succeeds: it failed the same way 2 times. "
            + "Read the current code first with ReadFile, GetFileOutline or Search, then choose a different approach."));

        var onlyOutline = latch.TryRefuse("a", "ReplaceSnippet", tool => tool == "GetFileOutline");
        Assert.That(onlyOutline, Does.Contain("Read the current code first with GetFileOutline, then"));
        Assert.That(onlyOutline, Does.Not.Contain("ReadFile").And.Not.Contain("Search"));

        var none = latch.TryRefuse("a", "ReplaceSnippet", _ => false);
        Assert.That(none, Does.EndWith("Read the current code first, then choose a different approach."));
    }

    [Test]
    public void Refusal_ForALatchedReadTool_DoesNotPointBackAtThatTool()
    {
        var text = ToolLoopLatch.BuildRefusal("ReadFile", 2, null);

        Assert.That(text, Does.Contain("with GetFileOutline or Search,"));
        Assert.That(text.Replace("ReadFile is DISABLED", ""), Does.Not.Contain("ReadFile"));
    }

    // ---- LoopLatchFilter ----

    [Test]
    public async Task Filter_RefusesTheThirdCallWithoutRunningIt_AsATargetIneligibleFailureBody()
    {
        var latch = new ToolLoopLatch();
        var runs = 0;
        ValueTask<CallToolResult> Failing()
        {
            runs++;
            return ValueTask.FromResult(ErrorBody(ToolErrorCode.NotFound, "ReplaceSnippet: nothing matched. Next step: " + NextStepSentence));
        }

        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, Failing);
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, Failing);
        var refused = await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, Failing);

        Assert.That(runs, Is.EqualTo(2), "the refused call must not execute");
        Assert.That(refused.IsError, Is.True);
        Assert.That(ToolTelemetryRecorder.ExtractErrorCode(refused), Is.EqualTo(ToolErrorCode.TargetIneligible));
        var text = ((TextContentBlock)refused.Content![0]).Text;
        Assert.That(ToolLoopLatch.RefusalMessage("ReplaceSnippet", text), Does.EndWith("You MUST do the next step now: " + NextStepSentence));
        Assert.That(ToolLoopLatch.IsRefusalBody("Search", text), Is.False, "a refusal belongs to one tool");
    }

    [Test]
    public async Task Filter_AnotherToolSucceeding_LiftsTheRefusal()
    {
        var latch = new ToolLoopLatch();
        var failing = () => ValueTask.FromResult(ErrorBody(ToolErrorCode.NotFound, "x"));
        var ok = () => ValueTask.FromResult(new CallToolResult { IsError = false });
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, failing);
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, failing);
        Assume.That(ToolLoopLatch.IsRefusalBody("ReplaceSnippet", Text(await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, failing))), Is.True);

        await LoopLatchFilter.InvokeAsync(latch, "a", "ReadFile", null, ok);

        var ran = false;
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, () =>
        {
            ran = true;
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        });
        Assert.That(ran, Is.True);
    }

    [Test]
    public async Task Filter_PlainTextRejections_CountAsTheSameWay()
    {
        // The argument validator's rejections are plain text with no error code: two of them latch the tool.
        var latch = new ToolLoopLatch();
        var rejection = () => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Missing required parameter 'oldContent' for tool 'ReplaceSnippet'." }],
        });

        await LoopLatchFilter.InvokeAsync(latch, null, "ReplaceSnippet", null, rejection);
        await LoopLatchFilter.InvokeAsync(latch, null, "ReplaceSnippet", null, rejection);

        Assert.That(latch.TryRefuse(null, "ReplaceSnippet"), Is.Not.Null);
    }

    [Test]
    public async Task Filter_FlagOff_IsAPureBypass_NothingIsRecordedOrRefused()
    {
        LoopLatchOptions.Configure([]);
        Assume.That(LoopLatchOptions.Enabled, Is.False, "ROSLYNSENTINEL_LOOP_LATCH must not be set for this test");
        var latch = new ToolLoopLatch();
        var runs = 0;
        ValueTask<CallToolResult> Failing()
        {
            runs++;
            return ValueTask.FromResult(ErrorBody(ToolErrorCode.NotFound, "x"));
        }

        for (var i = 0; i < 4; i++)
        {
            var result = await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, Failing);
            Assert.That(ToolLoopLatch.IsRefusalBody("ReplaceSnippet", Text(result)), Is.False);
        }

        Assert.That(runs, Is.EqualTo(4));
        Assert.That(latch.TryRefuse("a", "ReplaceSnippet"), Is.Null);
        Assert.That(latch.Snapshot(), Is.Empty);
    }

    [Test]
    public async Task Filter_ReadOnlyCallOfTheLatchedTool_IsRefusedToo()
    {
        var latch = new ToolLoopLatch();
        var failing = () => ValueTask.FromResult(ErrorBody(ToolErrorCode.NotFound, "x"));
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, failing);
        await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, failing);

        // The filter sees only the tool name, so a validate-only call is refused like any other (no validate exception).
        var validateOnly = await LoopLatchFilter.InvokeAsync(latch, "a", "ReplaceSnippet", null, () => throw new InvalidOperationException("must not run"));

        Assert.That(validateOnly.IsError, Is.True);
    }

    // ---- options ----

    [TestCase("--loop-latch", true)]
    [TestCase("--loop-latch=true", true)]
    [TestCase("--loop-latch=1", true)]
    [TestCase("--loop-latch=on", true)]
    [TestCase("--loop-latch=false", false)]
    [TestCase("--loop-latch=0", false)]
    [TestCase("--loop-latch=maybe", false)]
    public void Configure_ParsesTheFlag(string arg, bool expected)
    {
        LoopLatchOptions.Configure([arg]);

        Assert.That(LoopLatchOptions.Enabled, Is.EqualTo(expected));
    }

    [Test]
    public void Configure_BareFlagFollowedByAnotherOption_StaysOn_AndSeparateFalseTurnsItOff()
    {
        LoopLatchOptions.Configure(["--loop-latch", "--transport=stdio"]);
        Assert.That(LoopLatchOptions.Enabled, Is.True);

        LoopLatchOptions.Configure(["--loop-latch", "false"]);
        Assert.That(LoopLatchOptions.Enabled, Is.False);
    }

    [Test]
    public void Configure_NoFlag_IsOffByDefault()
    {
        LoopLatchOptions.Configure([]);

        Assume.That(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_LOOP_LATCH"), Is.Null.Or.Empty);
        Assert.That(LoopLatchOptions.Enabled, Is.False);
    }

    // ---- McpServerStatus ----

    [Test]
    public void McpServerStatus_LoopLatch_IsPresentOnlyWhileTheFlagIsOn()
    {
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var latch = new ToolLoopLatch();
        latch.RecordOutcome("agent-a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");
        latch.RecordOutcome("agent-a", "ReplaceSnippet", false, ToolErrorCode.NotFound, "x");

        var on = StatusFor(manager, latch).LoopLatch;
        Assert.That(on, Is.Not.Null);
        Assert.That(on!.Single(), Is.EqualTo(new LoopLatchAgentState("agent-a", "ReplaceSnippet", 2, ToolErrorCode.NotFound)));

        LoopLatchOptions.Configure([]);
        Assume.That(LoopLatchOptions.Enabled, Is.False);
        Assert.That(StatusFor(manager, latch).LoopLatch, Is.Null, "flag off: the field is omitted");

        LoopLatchOptions.Configure(["--loop-latch"]);
        Assert.That(StatusFor(manager, new ToolLoopLatch()).LoopLatch, Is.Empty, "flag on, nothing latched: an empty list");
    }

    [Test]
    public void FilterRegistration_LoopLatchSitsFourth_OutsideArgumentValidation()
    {
        var names = RoslynSentinelServiceExtensionsBasic.CallToolFilterNames;

        Assert.That(names[3], Is.EqualTo("LoopLatch"));
        Assert.That(names.ToList().IndexOf("LoopLatch"), Is.LessThan(names.ToList().IndexOf("ArgumentValidation")));
    }

    private static McpServerStatusResult StatusFor(IWorkspaceManager manager, ToolLoopLatch latch)
    {
        var tools = new ServerStatusTools(
            manager,
            new ActiveToolSurface("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null),
            loopLatch: latch);

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason");
        return result.SuccessData!;
    }

    private static CallToolResult ErrorBody(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = System.Text.Json.JsonSerializer.Serialize(new { isError = true, errorData = new { errorCode = code, message } }),
        }],
    };

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content![0]).Text;
}

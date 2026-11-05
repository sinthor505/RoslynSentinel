using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Behaviour of <see cref="ToolTelemetry"/> counters, the outcome mapping of <see cref="ToolTelemetryRecorder"/>, the
/// cap on tool names, and the toolTelemetry field on McpServerStatus. The filter-order pin lives in
/// <see cref="ToolTelemetryFilterOrderTests"/>.
/// </summary>
[TestFixture]
public class ToolTelemetryTests
{
    private static ToolTelemetry NewTelemetry(TimeProvider? clock = null) =>
        new(clock, instanceId: "test", buildSha: null);

    [Test]
    public async Task RecordAsync_SuccessResult_CountsOneSuccess()
    {
        var telemetry = NewTelemetry();

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search",
            () => ValueTask.FromResult(new CallToolResult { IsError = false }));

        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That((stats.Calls, stats.Successes, stats.Errors), Is.EqualTo((1L, 1L, 0L)));
    }

    [Test]
    public async Task RecordAsync_ErrorBodyWithErrorData_CountsErrorUnderItsCode()
    {
        var telemetry = NewTelemetry();

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "{\"isError\":true,\"errorData\":{\"errorCode\":\"NotFound\",\"message\":\"x\"}}" }],
        }));

        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That(stats.Errors, Is.EqualTo(1L));
        Assert.That(stats.ErrorsByCode!["NotFound"], Is.EqualTo(1));
    }

    [Test]
    public async Task RecordAsync_PlainTextErrorWithoutCode_CountsErrorAsUnknown()
    {
        // Also the breaker short-circuit shape: the refusal is plain text and no work was done.
        var telemetry = NewTelemetry();

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "Refused: circuit breaker open." }],
        }));

        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That(stats.Errors, Is.EqualTo(1L));
        Assert.That(stats.ErrorsByCode!["Unknown"], Is.EqualTo(1));
    }

    [Test]
    public async Task RecordAsync_ThrownException_CountsErrorUnderTypeNameAndRethrowsUnchanged()
    {
        var telemetry = NewTelemetry();
        var thrown = new InvalidOperationException("boom");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => throw thrown));

        Assert.That(caught, Is.SameAs(thrown));
        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That(stats.Errors, Is.EqualTo(1L));
        Assert.That(stats.ErrorsByCode!["InvalidOperationException"], Is.EqualTo(1));
    }

    [Test]
    public async Task RecordAsync_NullTelemetry_ReturnsNextResultUnchanged()
    {
        var expected = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "x" }] };

        var result = await ToolTelemetryRecorder.RecordAsync(null, "Search", () => ValueTask.FromResult(expected));

        Assert.That(result, Is.SameAs(expected));
    }

    [Test]
    public void Snapshot_BeforeAnyCall_HasNoToolsAndHasCallsIsFalse()
    {
        var telemetry = NewTelemetry();

        Assert.That(telemetry.HasCalls, Is.False);
        Assert.That(telemetry.Snapshot().Tools, Is.Empty);
    }

    [Test]
    public void Snapshot_ListsMostCalledFirstThenByName()
    {
        // Zeta and Beta are inserted before Alpha, so only the name tie-break can put Alpha ahead of Beta.
        var telemetry = NewTelemetry();
        telemetry.Record("Zeta", false, null, TimeSpan.Zero);
        telemetry.Record("Beta", false, null, TimeSpan.Zero);
        telemetry.Record("Alpha", false, null, TimeSpan.Zero);
        telemetry.Record("Alpha", false, null, TimeSpan.Zero);
        telemetry.Record("Beta", false, null, TimeSpan.Zero);

        Assert.That(telemetry.HasCalls, Is.True);
        var names = telemetry.Snapshot().Tools.Select(t => t.ToolName).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "Alpha", "Beta", "Zeta" }));
    }

    [Test]
    public void Record_TracksFirstAndLastCallTimesAndDurationTotalAndMax()
    {
        var start = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new MovingClock(start);
        var telemetry = NewTelemetry(clock);

        clock.Now = start.AddSeconds(1);
        telemetry.Record("Search", false, null, TimeSpan.FromMilliseconds(30));
        clock.Now = start.AddSeconds(5);
        telemetry.Record("Search", true, "NotFound", TimeSpan.FromMilliseconds(70));

        var stats = telemetry.Snapshot().Tools.Single();
        Assert.That(stats.FirstCallUtc, Is.EqualTo(start.AddSeconds(1).UtcDateTime));
        Assert.That(stats.LastCallUtc, Is.EqualTo(start.AddSeconds(5).UtcDateTime));
        Assert.That(stats.TotalDurationMs, Is.EqualTo(100L));
        Assert.That(stats.MaxDurationMs, Is.EqualTo(70L));
    }

    [Test]
    public void Record_ConcurrentCalls_KeepSuccessesPlusErrorsEqualToCalls()
    {
        var telemetry = NewTelemetry();

        Parallel.For(0, 1000, i =>
            telemetry.Record(i % 2 == 0 ? "Search" : "Outline", i % 3 == 0, "NotFound", TimeSpan.FromMilliseconds(1)));

        var tools = telemetry.Snapshot().Tools;
        Assert.That(tools.Sum(t => t.Calls), Is.EqualTo(1000L));
        Assert.That(tools.All(t => t.Successes + t.Errors == t.Calls), Is.True);
        // Multiples of 3 in 0..999: 334 errors.
        Assert.That(tools.Sum(t => t.Errors), Is.EqualTo(334L));
    }

    [Test]
    public void Record_MoreThanMaxToolNames_BucketsTheOverflowUnderOther()
    {
        var telemetry = NewTelemetry();
        const int overflow = 88;
        for (var i = 0; i < ToolTelemetry.MaxTools + overflow; i++)
        {
            telemetry.Record($"Tool{i:D4}", false, null, TimeSpan.Zero);
        }

        // A name admitted before the cap keeps its own bucket.
        telemetry.Record("Tool0000", false, null, TimeSpan.Zero);

        var tools = telemetry.Snapshot().Tools;
        Assert.That(tools, Has.Count.EqualTo(ToolTelemetry.MaxTools + 1));
        Assert.That(tools.Single(t => t.ToolName == ToolTelemetry.OverflowKey).Calls, Is.EqualTo((long)overflow));
        Assert.That(tools.Single(t => t.ToolName == "Tool0000").Calls, Is.EqualTo(2L));
    }

    [Test]
    public void McpServerStatus_ToolTelemetry_IsNullWithoutCallsAndListsToolAfterACall()
    {
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var telemetry = NewTelemetry();

        Assert.That(StatusFor(manager, telemetry).ToolTelemetry, Is.Null);

        telemetry.Record("Search", false, null, TimeSpan.FromMilliseconds(5));

        var snapshot = StatusFor(manager, telemetry).ToolTelemetry;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.Tools.Select(t => t.ToolName), Does.Contain("Search"));
    }

    private static McpServerStatusResult StatusFor(IWorkspaceManager manager, ToolTelemetry telemetry)
    {
        var tools = new ServerStatusTools(
            manager,
            new ActiveToolSurface("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null),
            toolTelemetry: telemetry);

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason");
        return result.SuccessData!;
    }

    private sealed class MovingClock : TimeProvider
    {
        private DateTimeOffset _now;

        public MovingClock(DateTimeOffset start) => _now = start;

        public DateTimeOffset Now
        {
            get => _now;
            set => _now = value;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

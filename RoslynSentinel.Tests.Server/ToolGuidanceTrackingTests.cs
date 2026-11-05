using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ToolGuidanceTrackingTests
{
    private static readonly MethodInfo ProbeMethod = typeof(ToolGuidanceTrackingTests).GetMethod(nameof(ProbeTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ProbeTarget()
    {
    }

    private static ToolTelemetry NewTelemetry(TimeProvider? clock = null) =>
    new(clock, instanceId: "t", buildSha: null);

    private static ValueTask<CallToolResult> Ok() =>
    ValueTask.FromResult(new CallToolResult { IsError = false });

    private static ValueTask<CallToolResult> Error() =>
    ValueTask.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Refused." }] });

    private static ToolCallStats StatsFor(ToolTelemetry telemetry, string toolName) =>
    telemetry.Snapshot().Tools.Single(t => t.ToolName == toolName);

    [Test]
    public async Task Issue_CallEmitsHintNamingSearch_CountsIssuedForOriginatingTool()
    {
        var telemetry = NewTelemetry();

        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        Assert.That(StatsFor(telemetry, "ToolA").GuidanceIssued, Is.EqualTo(1L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Follow_NextSearchCall_CountsFollowedAndSucceededForOriginatingTool()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", Ok);

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceFollowed, origin.GuidanceFollowedSucceeded), Is.EqualTo((1L, 1L)));
        var search = StatsFor(telemetry, "Search");
        Assert.That(search.Calls, Is.EqualTo(1L));
        Assert.That(
            (search.GuidanceIssued, search.GuidanceFollowed, search.GuidanceFollowedSucceeded, search.GuidanceIgnored),
            Is.EqualTo((0L, 0L, 0L, 0L)));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Follow_SearchCallReturnsError_CountsFollowedButNotSucceeded()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", Error);

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceFollowed, origin.GuidanceFollowedSucceeded), Is.EqualTo((1L, 0L)));
    }

    [Test]
    public async Task Ignored_FourthCallAfterIssue_ExpiresTheSuggestionBeforeItCanMatch()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        for (var i = 1; i <= 3; i++)
        {
            await ToolTelemetryRecorder.RecordAsync(telemetry, $"Unrelated{i}", Ok);
        }

        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(1), "still pending after three subsequent calls");

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Unrelated4", Ok);

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceIgnored, origin.GuidanceFollowed), Is.EqualTo((1L, 0L)));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Follow_SearchAsThirdCallAfterIssue_StillCountsAsFollowed()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Unrelated1", Ok);
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Unrelated2", Ok);
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", Ok);

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceFollowed, origin.GuidanceIgnored), Is.EqualTo((1L, 0L)));
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

    [Test]
    public async Task Ignored_OlderThanTwoMinutes_ExpiresOnTheNextCall()
    {
        var start = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new MovingClock(start);
        var telemetry = NewTelemetry(clock);
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        clock.Now = start + ToolTelemetry.GuidanceTimeout + TimeSpan.FromSeconds(1);
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Unrelated", Ok);

        Assert.That(StatsFor(telemetry, "ToolA").GuidanceIgnored, Is.EqualTo(1L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Ignored_ExactlyTwoMinutesOld_DoesNotExpire()
    {
        var start = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new MovingClock(start);
        var telemetry = NewTelemetry(clock);
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        clock.Now = start + ToolTelemetry.GuidanceTimeout;
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Unrelated", Ok);

        Assert.That(StatsFor(telemetry, "ToolA").GuidanceIgnored, Is.EqualTo(0L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Follow_SelfSuggestion_IsFollowedByTheToolsOwnRetry()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("ToolA");
            return Ok();
        });

        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", Ok);

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceIssued, origin.GuidanceFollowed, origin.GuidanceFollowedSucceeded), Is.EqualTo((1L, 1L, 1L)));
    }

    [Test]
    public async Task Follow_TwoPendingSearchSuggestions_OneCallConsumesOnlyTheOldest()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolB", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });

        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", Ok);

        Assert.That(StatsFor(telemetry, "ToolA").GuidanceFollowed, Is.EqualTo(1L));
        Assert.That(StatsFor(telemetry, "ToolB").GuidanceFollowed, Is.EqualTo(0L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(1));

        for (var i = 1; i <= 4; i++)
        {
            await ToolTelemetryRecorder.RecordAsync(telemetry, $"Unrelated{i}", Ok);
        }

        Assert.That(StatsFor(telemetry, "ToolB").GuidanceIgnored, Is.EqualTo(1L));
        Assert.That(StatsFor(telemetry, "ToolA").GuidanceIgnored, Is.EqualTo(0L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(0));
    }

    [Test]
    public void Hint_WithNoCollectorOpen_DoesNotThrowAndLeavesTelemetryUnchanged()
    {
        var telemetry = NewTelemetry();
        var versionBefore = telemetry.Version;

        Assert.DoesNotThrow(() => ToolCallHint.Call("Search"));

        Assert.That(telemetry.Version, Is.EqualTo(versionBefore));
        Assert.That(telemetry.Snapshot().Tools, Is.Empty);
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(0));
    }

    [Test]
    public void Record_AfterCollectorDisposed_IsIgnored()
    {
        var collector = ToolGuidanceCollector.Open();
        collector.Dispose();

        ToolGuidanceCollector.Record("Search");

        Assert.That(collector.Suggestions, Is.Empty);
    }

    [Test]
    public async Task Hint_RecordedAfterAwaitAndInsideTaskRun_IsStillCollected()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", async () =>
        {
            await Task.Yield();
            ToolCallHint.Call("Search");
            await Task.Run(() => ToolCallHint.Call("Outline"));
            return new CallToolResult { IsError = false };
        });

        var origin = StatsFor(telemetry, "ToolA");
        Assert.That(origin.GuidanceIssued, Is.EqualTo(2L));
        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(2));
    }

    [Test]
    public void GuidanceHint_IsNotSerializedWithTheServerOptions()
    {
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = "ok",
            GuidanceHint = new GuidanceHint(new[] { "Search" }, "ToolA"),
        };

        var json = JsonSerializer.Serialize(result, SharedJsonOptions.Default);

        Assert.That(json, Does.Not.Contain("guidancehint").IgnoreCase);
        Assert.That(json, Does.Not.Contain("Search"));
    }

    [Test]
    public async Task Marshaller_ForwardsGuidanceHintNamesToOpenCollector()
    {
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object>
        {
            IsError = false,
            SuccessData = "ok",
            GuidanceHint = new GuidanceHint(new[] { "Search", "Outline" }, "ToolA"),
        };
        using var collector = ToolGuidanceCollector.Open();

        await marshaller.MarshalAsync(
            "ToolA", ProbeMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        Assert.That(collector.Suggestions.Select(s => s.ToolName), Is.EqualTo(new[] { "Search", "Outline" }));
    }

    [Test]
    public async Task Exception_InFollowedCall_IsNotCreditedAsSucceeded_AndIsRethrown()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });
        var thrown = new InvalidOperationException("boom");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => throw thrown));

        Assert.That(caught, Is.SameAs(thrown));
        var origin = StatsFor(telemetry, "ToolA");
        Assert.That((origin.GuidanceFollowed, origin.GuidanceFollowedSucceeded), Is.EqualTo((1L, 0L)));
    }

    [Test]
    public void Pending_MoreThanTheCap_KeepsSixtyFourAndCountsTheDroppedOneIgnored()
    {
        var telemetry = NewTelemetry();
        var suggestions = Enumerable.Range(0, ToolTelemetry.MaxPendingGuidance + 1)
            .Select(i => ($"Tool{i:D3}", (string?)null))
            .ToArray();

        telemetry.IssueGuidance("Issuer", suggestions);

        Assert.That(telemetry.PendingGuidanceCount, Is.EqualTo(64));
        var issuer = StatsFor(telemetry, "Issuer");
        Assert.That((issuer.GuidanceIssued, issuer.GuidanceIgnored), Is.EqualTo((65L, 1L)));
    }

    [Test]
    public async Task Concurrent_RecordAsyncCalls_KeepPerToolGuidanceCountersConsistent()
    {
        var telemetry = NewTelemetry();
        string[] tools = { "ToolA", "ToolB", "ToolC", "Search" };

        var calls = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            var tool = tools[Random.Shared.Next(tools.Length)];
            var fail = Random.Shared.Next(4) == 0;
            return await ToolTelemetryRecorder.RecordAsync(telemetry, tool, () =>
            {
                ToolCallHint.Call("Search");
                return fail ? Error() : Ok();
            });
        })).ToArray();

        await Task.WhenAll(calls);

        var snapshot = telemetry.Snapshot();
        Assert.That(snapshot.Tools.Sum(t => t.Calls), Is.EqualTo(200));
        foreach (var stats in snapshot.Tools)
        {
            Assert.That(stats.GuidanceFollowed, Is.LessThanOrEqualTo(stats.GuidanceIssued),
                $"{stats.ToolName}: followed exceeds issued");
            Assert.That(stats.GuidanceFollowedSucceeded, Is.LessThanOrEqualTo(stats.GuidanceFollowed),
                $"{stats.ToolName}: succeeded exceeds followed");
        }
    }

    [Test]
    public async Task Snapshot_SerializesGuidanceCountersOnlyWhenNonZero()
    {
        var telemetry = NewTelemetry();
        await ToolTelemetryRecorder.RecordAsync(telemetry, "ToolA", () =>
        {
            ToolCallHint.Call("Search");
            return Ok();
        });
        await ToolTelemetryRecorder.RecordAsync(telemetry, "Search", () => Ok());

        var toolA = JsonSerializer.Serialize(StatsFor(telemetry, "ToolA"), SharedJsonOptions.Default);
        var search = JsonSerializer.Serialize(StatsFor(telemetry, "Search"), SharedJsonOptions.Default);

        using var toolADoc = JsonDocument.Parse(toolA);
        var toolANames = toolADoc.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.That(toolANames, Does.Contain("guidanceissued"));
        Assert.That(toolANames, Does.Contain("guidancefollowed"));
        Assert.That(toolANames, Does.Not.Contain("guidanceignored"));

        using var searchDoc = JsonDocument.Parse(search);
        var searchNames = searchDoc.RootElement.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.That(searchNames.Where(n => n.StartsWith("guidance", StringComparison.Ordinal)), Is.Empty);
    }
}

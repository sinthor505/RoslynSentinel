using System.Text.Json;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Minimal proof of the persistence seams (<see cref="ToolTelemetryFlusher"/>, <see cref="ToolTelemetryFlushService"/>);
/// a test-writer extends the coverage.
/// </summary>
[TestFixture]
public class ToolTelemetryFlusherTests
{
    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "toolTelemetryFlusher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ToolTelemetry NewTelemetry() => new(clock: null, instanceId: "inst", buildSha: null);

    private ServerInstancePaths Explicit() => ServerInstancePaths.Resolve([$"--working-dir={_dir}"], _dir);

    private ServerInstancePaths NotExplicit() => ServerInstancePaths.Resolve([], _dir);

    [Test]
    public async Task FlushIfDirtyAsync_ExplicitDir_WritesSnapshotWithEnvelopeOnlyWhenDirty()
    {
        var telemetry = NewTelemetry();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());

        Assert.That(await flusher.FlushIfDirtyAsync(), Is.False, "nothing recorded yet");

        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.FromMilliseconds(5));
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.True);
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.False, "clean tick must not rewrite");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "server-telemetry_inst.json")));
        var root = doc.RootElement;
        Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("instanceId").GetString(), Is.EqualTo("inst"));
        Assert.That(root.GetProperty("tools")[0].GetProperty("toolName").GetString(), Is.EqualTo("Search"));
    }

    [Test]
    public async Task FlushIfDirtyAsync_NotExplicitDir_WritesNothing()
    {
        var telemetry = NewTelemetry();
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);

        var flusher = new ToolTelemetryFlusher(telemetry, NotExplicit());

        Assert.That(await flusher.FlushIfDirtyAsync(), Is.False);
        Assert.That(Directory.GetFiles(_dir), Is.Empty);
    }

    [Test]
    public async Task FlushOnShutdownAsync_WritesPendingChanges()
    {
        var telemetry = NewTelemetry();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());
        var service = new ToolTelemetryFlushService(flusher);
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);

        await service.FlushOnShutdownAsync();

        Assert.That(File.Exists(flusher.FilePath), Is.True);
    }

    [Test]
    public async Task FlushIfDirtyAsync_RoundTrip_FileDeserializesToRecordedCounters()
    {
        var telemetry = NewTelemetry();
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.FromMilliseconds(5));
        telemetry.Record("Search", isError: true, errorCode: "InvalidArgument", TimeSpan.FromMilliseconds(20));
        telemetry.Record("Search", isError: true, errorCode: "InvalidArgument", TimeSpan.FromMilliseconds(1));
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.That(await flusher.FlushIfDirtyAsync(), Is.True);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(flusher.FilePath!));
        var root = doc.RootElement;
        Assert.That(root.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("writtenUtc").GetDateTime(), Is.GreaterThanOrEqualTo(before));
        Assert.That(root.GetProperty("instanceId").GetString(), Is.EqualTo("inst"));

        var tools = root.GetProperty("tools");
        Assert.That(tools.GetArrayLength(), Is.EqualTo(1));
        var search = tools[0];
        Assert.That(search.GetProperty("toolName").GetString(), Is.EqualTo("Search"));
        Assert.That(search.GetProperty("calls").GetInt64(), Is.EqualTo(3));
        Assert.That(search.GetProperty("successes").GetInt64(), Is.EqualTo(1));
        Assert.That(search.GetProperty("errors").GetInt64(), Is.EqualTo(2));
        Assert.That(search.GetProperty("errorsByCode").GetProperty("InvalidArgument").GetInt32(), Is.EqualTo(2));
        Assert.That(search.GetProperty("totalDurationMs").GetInt64(), Is.EqualTo(26));
        Assert.That(search.GetProperty("maxDurationMs").GetInt64(), Is.EqualTo(20));
    }

    [Test]
    public void NotExplicit_FlusherIsDisabledWithNoFilePath()
    {
        var flusher = new ToolTelemetryFlusher(NewTelemetry(), NotExplicit());

        Assert.That(flusher.IsEnabled, Is.False);
        Assert.That(flusher.FilePath, Is.Null);
    }

    [Test]
    public async Task FlushIfDirtyAsync_RecordAfterFlush_RewritesInPlaceWithNewTotal()
    {
        var telemetry = NewTelemetry();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());

        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.True);
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.False, "nothing recorded since the last write");

        telemetry.Record("Search", isError: true, errorCode: "Boom", TimeSpan.Zero);
        telemetry.Record("Build", isError: false, errorCode: null, TimeSpan.Zero);
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.True, "a new Record makes the telemetry dirty again");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(flusher.FilePath!));
        var tools = doc.RootElement.GetProperty("tools");
        Assert.That(tools.GetArrayLength(), Is.EqualTo(2));
        var search = tools.EnumerateArray().Single(t => t.GetProperty("toolName").GetString() == "Search");
        Assert.That(search.GetProperty("calls").GetInt64(), Is.EqualTo(2), "overwrite must reflect the new total, not the first write");
        Assert.That(search.GetProperty("errorsByCode").GetProperty("Boom").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public void FileNameFor_StripsInvalidFileNameCharacters()
    {
        var name = ToolTelemetryFlusher.FileNameFor("a/b:c");

        Assert.That(name, Is.EqualTo("server-telemetry_abc.json"));
        Assert.That(name.IndexOfAny(Path.GetInvalidFileNameChars()), Is.EqualTo(-1));
        Assert.That(name, Does.StartWith("server-telemetry_"));
        Assert.That(name, Does.EndWith(".json"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void FileNameFor_BlankInstanceId_UsesDefault(string instanceId)
    {
        Assert.That(ToolTelemetryFlusher.FileNameFor(instanceId), Is.EqualTo("server-telemetry_default.json"));
    }

    [Test]
    public async Task FlushIfDirtyAsync_WriteFails_DoesNotThrowLogsOnceAndRetriesWhenReleased()
    {
        var telemetry = NewTelemetry();
        var logger = new CapturingLogger();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit(), logger);
        // A directory squatting on the target file name makes every write fail.
        Directory.CreateDirectory(flusher.FilePath!);
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);

        var written = true;
        await Assert.DoesNotThrowAsync(async () => { written = await flusher.FlushIfDirtyAsync(); });
        Assert.That(written, Is.False);

        await flusher.FlushIfDirtyAsync();
        Assert.That(logger.WarningCount, Is.EqualTo(1), "identical repeated failures warn once");

        Directory.Delete(flusher.FilePath!);
        Assert.That(await flusher.FlushIfDirtyAsync(), Is.True, "telemetry stayed dirty, so the retry writes");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(flusher.FilePath!));
        Assert.That(doc.RootElement.GetProperty("tools")[0].GetProperty("calls").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public async Task FlushOnShutdownAsync_PreCancelledToken_DoesNotThrowAndWritesNothing()
    {
        var telemetry = NewTelemetry();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());
        var service = new ToolTelemetryFlushService(flusher);
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.DoesNotThrowAsync(() => service.FlushOnShutdownAsync(cts.Token));

        Assert.That(File.Exists(flusher.FilePath), Is.False, "a cancelled flush gives up without writing");
    }

    [Test]
    public async Task StopAsync_WithoutStart_WritesPendingChanges()
    {
        var telemetry = NewTelemetry();
        var flusher = new ToolTelemetryFlusher(telemetry, Explicit());
        var service = new ToolTelemetryFlushService(flusher);
        telemetry.Record("Search", isError: false, errorCode: null, TimeSpan.Zero);

        await service.StopAsync(CancellationToken.None);

        Assert.That(File.Exists(flusher.FilePath), Is.True);
    }
}
/// <summary>Counts warning-level log entries; the flusher's warnings are the only thing asserted.</summary>
internal sealed class CapturingLogger : ILogger
{
    public int WarningCount
    {
        get; private set;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            WarningCount++;
        }
    }
}

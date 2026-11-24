using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the always-on call transcript (<see cref="ToolCallTranscript"/> and its call-tool filter): an in-process
/// <see cref="McpClient"/>/<see cref="McpServer"/> pair writes one JSONL line per call to a temp folder, a write failure never
/// breaks a call, the sweep removes old files, and the line carries no null properties.
/// </summary>
[TestFixture]
public class ToolCallTranscriptTests
{
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        // Under the scratch root, never in the repo.
        _directory = Path.Combine(ScratchRoot.TestRunsRoot(), "ToolCallTranscriptTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of scratch output.
        }
    }

    [Test]
    public async Task InProcessServer_WritesOneLinePerCall_WithArgumentsResponseAndIsError()
    {
        var transcript = new ToolCallTranscript(_directory);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddSingleton(transcript); // pre-registered, so AddRoslynSentinelToolsBasic's TryAdd keeps this temp-folder instance
        services.AddRoslynSentinelEnginesBasic();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);
        mcpBuilder.WithTools<TranscriptProbeTool>();

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        _ = host.RunAsync();
        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

        var ok = await client.CallToolAsync(
            "TranscriptEcho", new Dictionary<string, object?> { ["text"] = "alpha-123" }!, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var failed = await client.CallToolAsync(
            "TranscriptFail", new Dictionary<string, object?> { ["text"] = "beta-456" }!, cancellationToken: TestContext.CurrentContext.CancellationToken);
        var okText = string.Join("\n", ok.Content.OfType<TextContentBlock>().Select(b => b.Text));
        var failedText = string.Join("\n", failed.Content.OfType<TextContentBlock>().Select(b => b.Text));
        await host.StopAsync();

        Assert.That(ok.IsError, Is.Not.True);
        Assert.That(failed.IsError, Is.True, "The failing probe must come back as an error for the transcript to prove isError.");
        Assert.That(transcript.FilePath, Is.Not.Null);
        Assert.That(File.Exists(transcript.FilePath), Is.True, "The file is created by the first call.");
        Assert.That(Path.GetFileName(transcript.FilePath), Does.Match(@"^calls-\d{8}-\d{6}-\d+-[0-9a-f]{8}\.jsonl$"));

        var lines = File.ReadAllLines(transcript.FilePath!).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.That(lines, Has.Count.EqualTo(2), "One line per tools/call.");

        Assert.That(lines[0].GetProperty("tool").GetString(), Is.EqualTo("TranscriptEcho"));
        Assert.That(lines[0].GetProperty("arguments").ValueKind, Is.EqualTo(JsonValueKind.Object), "Arguments are a JSON object, not a string.");
        Assert.That(lines[0].GetProperty("arguments").GetProperty("text").GetString(), Is.EqualTo("alpha-123"));
        Assert.That(lines[0].GetProperty("isError").GetBoolean(), Is.False);
        Assert.That(lines[0].GetProperty("response").GetString(), Does.Contain("echoed:alpha-123"));
        Assert.That(lines[0].GetProperty("response").GetString(), Is.EqualTo(okText), "The logged response is the text as the client received it.");
        Assert.That(lines[0].GetProperty("durationMs").GetInt64(), Is.GreaterThanOrEqualTo(0));
        Assert.That(DateTimeOffset.TryParse(lines[0].GetProperty("utc").GetString(), out _), Is.True);

        Assert.That(lines[1].GetProperty("tool").GetString(), Is.EqualTo("TranscriptFail"));
        Assert.That(lines[1].GetProperty("arguments").GetProperty("text").GetString(), Is.EqualTo("beta-456"));
        Assert.That(lines[1].GetProperty("isError").GetBoolean(), Is.True);
        Assert.That(lines[1].GetProperty("response").GetString(), Is.EqualTo(failedText));
    }

    [Test]
    public async Task Record_WhenTheDirectoryIsAFile_DoesNotThrow_AndTheRecorderReturnsTheSameResult()
    {
        var blocker = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var transcript = new ToolCallTranscript(blocker);
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "payload" }] };

        Assert.DoesNotThrow(() => transcript.Record(DateTimeOffset.UtcNow, "Any", null, false, "payload", 1));
        Assert.DoesNotThrow(() => transcript.Record(DateTimeOffset.UtcNow, "Any", null, false, "payload", 1), "A repeated failure is also silent.");

        var returned = await ToolCallTranscriptRecorder.RecordAsync(transcript, "Any", new Dictionary<string, JsonElement>(), () => ValueTask.FromResult(result));
        Assert.That(returned, Is.SameAs(result), "The recorder returns the same CallToolResult object, untouched.");
        Assert.That(File.ReadAllText(blocker), Is.EqualTo("x"), "The blocking file is left alone.");
    }

    [Test]
    public async Task RecordAsync_WithNoTranscript_IsAPassThrough()
    {
        var result = new CallToolResult();

        var returned = await ToolCallTranscriptRecorder.RecordAsync(null, "Any", null, () => ValueTask.FromResult(result));

        Assert.That(returned, Is.SameAs(result));
    }

    [Test]
    public void Constructor_SweepsTranscriptFilesOlderThanTheMaxAge_AndLeavesTheRest()
    {
        var old = Path.Combine(_directory, "calls-20200101-000000-1-aaaaaaaa.jsonl");
        var recent = Path.Combine(_directory, "calls-20260101-000000-2-bbbbbbbb.jsonl");
        var otherStem = Path.Combine(_directory, "other-old.jsonl");
        foreach (var path in new[] { old, recent, otherStem })
        {
            File.WriteAllText(path, "{}\n");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - RuntimeStateRetention.LogMaxAge - TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(otherStem, DateTime.UtcNow - RuntimeStateRetention.LogMaxAge - TimeSpan.FromDays(1));

        _ = new ToolCallTranscript(_directory);

        Assert.That(File.Exists(old), Is.False, "A transcript file older than the max age is swept.");
        Assert.That(File.Exists(recent), Is.True, "A recent transcript file is kept.");
        Assert.That(File.Exists(otherStem), Is.True, "A file that does not carry the transcript prefix is never touched.");
    }

    [Test]
    public void Constructor_SweepsTheOldestTranscriptFilesBeyondTheMaxCount()
    {
        var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(_directory, $"calls-2026010{i}-000000-{i}-cccccccc.jsonl")).ToList();
        for (var i = 0; i < paths.Count; i++)
        {
            File.WriteAllText(paths[i], "{}\n");
            File.SetLastWriteTimeUtc(paths[i], DateTime.UtcNow - TimeSpan.FromHours(10 - i));
        }

        _ = new ToolCallTranscript(_directory, maxFiles: 3);

        // The new transcript counts toward the limit, so two of the four survive and the two oldest go.
        Assert.That(paths.Select(File.Exists), Is.EqualTo(new[] { false, false, true, true }));
    }

    [Test]
    public void Record_OmitsNullProperties_AndWritesUtf8WithoutBom()
    {
        var transcript = new ToolCallTranscript(_directory);
        var arguments = new JsonObject { ["q"] = "a\"b+c" };

        transcript.Record(DateTimeOffset.UtcNow, "Search", arguments, isError: false, response: null, durationMs: 5);

        var bytes = File.ReadAllBytes(transcript.FilePath!);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "No UTF-8 byte order mark.");
        var line = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes)).RootElement;
        Assert.That(line.TryGetProperty("callId", out _), Is.False, "A call id that is not known is omitted, not null.");
        Assert.That(line.TryGetProperty("agentId", out _), Is.False);
        Assert.That(line.TryGetProperty("response", out _), Is.False);
        Assert.That(line.GetProperty("arguments").GetProperty("q").GetString(), Is.EqualTo("a\"b+c"));
        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Does.EndWith("\n"));
    }

    [Test]
    public void DefaultFolder_UnderATestAssembly_IsTheTestHostFolder()
    {
        // The SetUpFixture base applied UseTestHostDefaults, so the parameterless ctor (the production DI default) must not
        // point at the real-session folder.
        var transcript = new ToolCallTranscript();

        Assert.That(transcript.DirectoryPath, Does.EndWith(Path.DirectorySeparatorChar + ToolCallTranscript.TestHostFolderName));
    }

    [Test]
    public void ProductionRegistration_InAHostWithoutPreRegisteredTranscript_ResolvesTheTestHostFolder()
    {
        // No pre-registered transcript: the production registration must fall back to the ambient test-host default.
        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();
        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        using var host = hostBuilder.Build();
        var transcript = host.Services.GetRequiredService<ToolCallTranscript>();

        Assert.That(transcript.DirectoryPath, Does.EndWith(Path.DirectorySeparatorChar + ToolCallTranscript.TestHostFolderName));
    }
}

/// <summary>Test-only tools for <see cref="ToolCallTranscriptTests"/>: one that succeeds and one that throws (the SDK turns the throw into an error result).</summary>
[McpServerToolType]
public class TranscriptProbeTool
{
    [McpServerTool(Name = "TranscriptEcho")]
    [System.ComponentModel.Description("Test-only tool that echoes its text, for call-transcript tests.")]
    public string TranscriptEcho([System.ComponentModel.Description("Text to echo.")] string text)
    {
        return $"echoed:{text}";
    }

    [McpServerTool(Name = "TranscriptFail")]
    [System.ComponentModel.Description("Test-only tool that always throws, for call-transcript tests.")]
    public string TranscriptFail([System.ComponentModel.Description("Text for the exception.")] string text)
    {
        throw new InvalidOperationException($"transcript-boom {text}");
    }
}

using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Tests.ModelEval.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Replays a previously-recorded <see cref="AgentTranscript"/> (transcript.json, written by
/// <see cref="ModelAgentRunner"/>) against a freshly-built <see cref="SizeGraduatedReproducer"/>
/// fixture of the matching size, calling each tool with its recorded <c>ArgumentsJson</c> verbatim
/// via the real in-memory MCP client/server — no LLM involved. This turns "the model produced a
/// confusing failure three turns deep in an overnight sweep" into a single deterministic re-run:
/// point ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT at the saved transcript.json and rerun.
///
/// Reads the padding-method count (the fixture size, e.g. "n60") from the transcript's own parent
/// directory name rather than requiring it as a separate input, since
/// <see cref="SizeThresholdAgentTests"/> and <see cref="WholeFileRewriteAgentTests"/> both write
/// transcripts under a path containing that size. Falls back to
/// ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE when the path doesn't carry it (e.g. a copied-out
/// transcript).
/// </summary>
[TestFixture]
public class TranscriptReplayTests
{
    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Generation", "Refactoring", "Workspace",
    };

    private static readonly Regex SizeFromPathRegex = new(@"[/\\]n(\d+)[/\\]", RegexOptions.Compiled);

    private IHost _host = null!;
    private McpClient _mcpClient = null!;
    private RoslynSentinel.Tests.TestSolutionFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new RoslynSentinel.Tests.TestSolutionFixture();

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesAdvanced();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.WithTasks(
            new InMemoryMcpTaskStore(),
            o => o.ExecutionModeSelector = RoslynSentinelTaskTools.SelectExecutionMode);
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _mcpClient = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _fixture?.Dispose();
    }

    /// <summary>
    /// Replays every tool call in ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT against a fresh fixture,
    /// printing each call's recorded vs. replayed result so a divergence (or a reproduced failure) is
    /// visible directly in test output. Ignored (not failed) when the env var isn't set, since this
    /// is an on-demand troubleshooting tool, not part of the regular suite.
    /// </summary>
    [Test]
    public async Task ReplayTranscript()
    {
        var transcriptPath = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT");
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_MODELEVAL_REPLAY_TRANSCRIPT is not set — point it at a saved " +
                "transcript.json to replay it deterministically against a fresh fixture.");
        }

        if (!File.Exists(transcriptPath))
        {
            Assert.Fail($"Transcript not found: '{transcriptPath}'.");
        }

        var unrelatedMethodCount = ResolveUnrelatedMethodCount(transcriptPath);
        TestContext.Out.WriteLine($"Replaying '{transcriptPath}' against a size-{unrelatedMethodCount} fixture.");

        var workspaceManager = _host.Services.GetRequiredService<IWorkspaceManager>();
        await workspaceManager.LoadSolutionAsync(_fixture.SolutionPath, TestContext.CurrentContext.CancellationToken);

        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockEditHelpers.cs"),
            SizeGraduatedReproducer.HelperFileContent,
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "BlockConverter.cs"),
            SizeGraduatedReproducer.BuildBuggyFileContent(unrelatedMethodCount),
            reloadSolution: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        await _fixture.AddFileToSolution(
            workspaceManager,
            Path.Combine("ContosoOrders.Core", "FixtureHelpers", "Shape.cs"),
            SizeGraduatedReproducer.TargetAbstractClassFileContent,
            reloadSolution: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var json = await File.ReadAllTextAsync(transcriptPath, TestContext.CurrentContext.CancellationToken);
        var transcript = JsonSerializer.Deserialize<ReplayTranscriptDto>(json)
            ?? throw new InvalidOperationException($"Transcript at '{transcriptPath}' deserialized to null.");

        // The recorded ArgumentsJson bakes in the ORIGINAL run's TestSolutionFixture temp directory
        // (a fresh GUID every run, e.g. "RoslynSentinelTests_afab827f-..."). Replaying against a new
        // fixture means every absolute path in the transcript points at a directory that no longer
        // exists, so every call after the first fails with FileNotFound regardless of what the
        // original bug was. Detect the original directory from the transcript itself and rewrite it
        // to this run's _fixture.SolutionDirectory before replaying each call.
        var originalDirectory = FindOriginalSolutionDirectory(transcript);
        TestContext.Out.WriteLine(
            originalDirectory is null
                ? "No original solution directory detected in the transcript's arguments — replaying paths verbatim."
                : $"Rewriting original solution directory '{originalDirectory}' -> '{_fixture.SolutionDirectory}'.");

        // ArgumentsJson is itself JSON text (it gets re-parsed by ReplayToolCallAsync below), so a
        // Windows path inside it is backslash-escaped (e.g. "C:\\Users\\..."). originalDirectory was
        // captured directly from that raw text, so it's ALREADY in escaped form — only the
        // replacement (a real path with single backslashes, from _fixture.SolutionDirectory) needs
        // JsonEncode. Escaping originalDirectory again here would double-escape it and never match.
        var replacementDirectoryEscaped = originalDirectory is null ? null : JsonEncode(_fixture.SolutionDirectory);

        var mismatchCount = 0;
        var callIndex = 0;
        foreach (var turn in transcript.Turns)
        {
            foreach (var recordedCallRaw in turn.ToolCalls)
            {
                var recordedCall = originalDirectory is null
                    ? recordedCallRaw
                    : new ReplayToolCallDto
                    {
                        ToolName = recordedCallRaw.ToolName,
                        ArgumentsJson = recordedCallRaw.ArgumentsJson.Replace(
                            originalDirectory, replacementDirectoryEscaped, StringComparison.Ordinal),
                        ResultJson = recordedCallRaw.ResultJson,
                        IsError = recordedCallRaw.IsError,
                    };

                callIndex++;
                TestContext.Out.WriteLine(
                    $"--- Turn {turn.TurnNumber}, call {callIndex}: {recordedCall.ToolName} ---");
                TestContext.Out.WriteLine($"Arguments: {recordedCall.ArgumentsJson}");

                var (replayedText, replayedIsError) = await ReplayToolCallAsync(recordedCall, TestContext.CurrentContext.CancellationToken);

                TestContext.Out.WriteLine($"Recorded  IsError={recordedCall.IsError}: {Truncate(recordedCall.ResultJson)}");
                TestContext.Out.WriteLine($"Replayed  IsError={replayedIsError}: {Truncate(replayedText)}");

                if (replayedIsError != recordedCall.IsError)
                {
                    mismatchCount++;
                    TestContext.Out.WriteLine(
                        $"*** MISMATCH at turn {turn.TurnNumber} call {callIndex}: recorded IsError=" +
                        $"{recordedCall.IsError} but replay produced IsError={replayedIsError}. Fixture " +
                        "state has now diverged from the original transcript (each call mutates disk) " +
                        "— treat only this first mismatch as meaningful.");
                }
            }
        }

        TestContext.Out.WriteLine(
            mismatchCount == 0
                ? $"Replayed {callIndex} tool call(s) across {transcript.Turns.Count} turn(s); all IsError flags matched the recording."
                : $"Replayed {callIndex} tool call(s); {mismatchCount} call(s) had an IsError flag that diverged from the recording — see above.");
    }

    private async Task<(string ResultText, bool IsError)> ReplayToolCallAsync(
        ReplayToolCallDto recordedCall, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(recordedCall.ArgumentsJson) ?? [];
        }
        catch (JsonException ex)
        {
            return ($$"""{"success":false,"error":"Malformed recorded arguments JSON: {{ex.Message}}"}""", true);
        }

        CallToolResult result;
        try
        {
            result = await _mcpClient.CallToolAsync(
                recordedCall.ToolName,
                arguments,
                progress: null,
                options: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($$"""{"success":false,"error":"MCP call threw: {{ex.Message}}"}""", true);
        }

        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? "";

        return (text, result.IsError == true || BodyReportsFailure(text));
    }

    private static bool BodyReportsFailure(string resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultText);
            return doc.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly Regex OriginalSolutionDirectoryRegex = new(
        @"[A-Za-z]:[\\/].*?RoslynSentinelTests_[0-9a-fA-F-]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Finds the original run's <see cref="RoslynSentinel.Tests.TestSolutionFixture.SolutionDirectory"/>
    /// baked into the transcript's recorded arguments (a "RoslynSentinelTests_&lt;guid&gt;" temp
    /// directory), so it can be rewritten to this replay's own fresh fixture directory. Returns null
    /// if no call in the transcript references such a path (e.g. a transcript with no file-path
    /// arguments at all).
    /// </summary>
    private static string? FindOriginalSolutionDirectory(ReplayTranscriptDto transcript)
    {
        foreach (var turn in transcript.Turns)
        {
            foreach (var call in turn.ToolCalls)
            {
                var match = OriginalSolutionDirectoryRegex.Match(call.ArgumentsJson);
                if (match.Success)
                {
                    return match.Value;
                }
            }
        }

        return null;
    }

    private static int ResolveUnrelatedMethodCount(string transcriptPath)
    {
        var match = SizeFromPathRegex.Match(transcriptPath.Replace('\\', '/') + "/");
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value);
        }

        var envSize = Environment.GetEnvironmentVariable("ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE");
        if (!string.IsNullOrWhiteSpace(envSize) && int.TryParse(envSize, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Could not determine the fixture size (unrelated-method count) from transcript path " +
            $"'{transcriptPath}' (expected an 'nNN' path segment, e.g. '.../n60/...'). Set " +
            "ROSLYNSENTINEL_MODELEVAL_REPLAY_SIZE explicitly to override.");
    }

    private static string JsonEncode(string value) => JsonSerializer.Serialize(value)[1..^1];

    private static string Truncate(string text, int maxLength = 500)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + $"... [{text.Length - maxLength} more chars]";
    }

    // AgentTranscript's own Turns property is get-only ({ get; } = []) so System.Text.Json can't
    // bind a deserialized array into it — it silently leaves the default empty list instead of
    // throwing. Mirror only the fields replay actually needs with plain settable properties so
    // reading a transcript.json written by ModelAgentRunner works without touching that shared type.
    private sealed class ReplayTranscriptDto
    {
        public List<ReplayTurnDto> Turns { get; set; } = [];
    }

    private sealed class ReplayTurnDto
    {
        public int TurnNumber { get; set; }
        public List<ReplayToolCallDto> ToolCalls { get; set; } = [];
    }

    private sealed class ReplayToolCallDto
    {
        public string ToolName { get; set; } = "";
        public string ArgumentsJson { get; set; } = "";
        public string ResultJson { get; set; } = "";
        public bool IsError { get; set; }
    }
}

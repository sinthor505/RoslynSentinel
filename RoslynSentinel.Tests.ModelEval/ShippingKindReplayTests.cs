using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.Fixtures;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// The fast way to count the "Overnight" misspelling at the first-edit decision turn. It takes a saved ShippingKind transcript
/// (ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT), keeps its turns 1..K (_RESUME_KEEP_TURNS, normally up to and including the read of
/// ShippingKinds.cs), seeds the conversation with them and samples exactly one live turn, _RESUME_SAMPLES times. Unlike
/// <see cref="ShippingKindResumeAgentTests"/> there is no fixture, no solution load and no MCP server: the tool list is rebuilt from
/// the transcript header and a call the live model makes is answered with a placeholder, because the question is only what the model
/// writes in that turn. With a small model a sample takes seconds, so hundreds are practical.
/// ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE=Capitalized rewrites the quoted values "standard", "express", "overnight" and "pickup"
/// in the kept history to "Standard", ... (<see cref="TranscriptResume.CapitalizeShippingKindLiterals"/>) so the value is spelled
/// exactly like its constant name: the A/B arm for the hypothesis that the lowercase literal primes the misspelling.
/// Each sample is cut as soon as the model has written a ShippingKinds.O... member (the decision is made, the rest of the turn is
/// skipped); ROSLYNSENTINEL_MODELEVAL_REPLAY_EARLY_STOP=0 replays whole turns.
/// It asserts nothing about the model: one results.csv row per sample plus a per-sample transcript.json that
/// scripts/Get-ShippingKindTypoCounts.ps1 reads (the decision turn is found the same way as for a full run).
/// <para>
/// Sampler: only temperature and top-p are sent per request (<c>LlmOptions.Temperature</c>/<c>TopP</c>; no test
/// sets them itself). Top-k, min-p and the penalties come from LM Studio's preset, or its defaults after a
/// fresh model load, because /v1/responses ignores them if sent.
/// </para>
/// </summary>
[TestFixture]
public class ShippingKindReplayTests
{
    /// <summary>Original (default) keeps the history as recorded; Capitalized capitalizes the quoted kind values in it.</summary>
    public const string LiteralCaseVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE";

    /// <summary>Early stop is on by default; 0, false or off (case-insensitive) replays whole turns instead.</summary>
    public const string EarlyStopVariable = "ROSLYNSENTINEL_MODELEVAL_REPLAY_EARLY_STOP";

    private const string CsvHeader = "sampleIndex,seconds,literalCase,typoInTurn,unknownNames,inReasoning,inContent,inCalls,firstToolName,stopReason";

    /// <summary>What a live tool call is answered with: a neutral success that names nothing from the transcript.</summary>
    private const string PlaceholderText = "{\"isError\":false,\"statusMessage\":\"transcript replay: tool calls are not executed here\"}";

    private static readonly string[] FallbackMemberNames =
        ["Standard", "Express", "Overnight", "Pickup", "SameDay", "IsKnown", "IsExpedited", "IsPickup", "All", "Normalize"];

    // Prose placeholders only; every other unknown name is recorded raw. scripts/Get-ShippingKindTypoCounts.ps1 -ReplayResults classifies near miss vs invented, so typoInTurn means any unknown name.
    private static readonly string[] PlaceholderNames = ["ConstantName", "Name", "Xxx", "Foo", "Member", "Constant"];

    private static readonly Regex DeclarationRegex = new(
        @"(?:const\s+string\s+|static\s+[\w<>\?\[\]]+\s+)([A-Za-z_]\w*)\s*(?:=|\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemberUseRegex = new(
        @"ShippingKinds\.([A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Test]
    [Explicit("Requires loaded model. Manually run using roslynsentinel-modeleval.ps1 -Test ShippingKindReplay -ResumeTranscript <path> -Samples <n> [-LiteralCase Capitalized]")]
    public async Task ShippingKindReplay_SamplesTheDecisionTurn()
    {
        LlmOptions.Configure([]);
        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        var settings = ResumeSettings.FromEnvironment();
        if (settings.TranscriptPath is null)
        {
            Assert.Ignore($"{ResumeSettings.TranscriptVariable} is not set - point it at a saved ShippingKind transcript.json (or its run folder).");
        }

        var literalCase = ReadLiteralCase();
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var source = TranscriptResume.Load(TranscriptResume.ResolveTranscriptPath(settings.TranscriptPath!));
        var host = new ReplayToolHost(source);
        var keptTurns = TranscriptResume.KeepTurns(source, settings.KeepTurns);

        // TODO the env variable and script parameter are still named LiteralCase although the variants now go beyond casing
        Func<string?, string?> rewrite = literalCase switch
        {
            HistoryVariant.Capitalized => TranscriptResume.CapitalizeShippingKindLiterals,
            HistoryVariant.QualifiedResult => TranscriptResume.AddQualifiedShippingKindMembers,
            _ => text => text,
        };
        var seed = TranscriptResume.RewriteTurnsText(keptTurns, rewrite);
        var systemPrompt = rewrite(source.SystemPrompt)!;
        var userPrompt = rewrite(source.UserPrompt)!;
        if (literalCase == HistoryVariant.ExampleOvernight)
        {
            const string example = "(for example ShippingKinds.Express)";
            if (!userPrompt.Contains(example, StringComparison.Ordinal))
            {
                Assert.Ignore($"ExampleOvernight needs a user prompt containing '{example}' (the step 4 prompt).");
            }

            userPrompt = userPrompt.Replace(example, "(for example ShippingKinds.Overnight)", StringComparison.Ordinal);
        }
        var declared = DeclaredNames(seed);

        var runDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-eval",
            TestContext.CurrentContext.Test.Name,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        // TODO the replay test archives its run folder only at the end, so the evalqueue NoRunFolderMinutes/TimeLimit watchdogs kill long runs with no saved output
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");
        File.WriteAllText(csvPath, CsvHeader + "\r\n");

        var earlyStop = ReadEarlyStop() ? new EarlyStopLoggerProvider() : null;
        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(runDirectory, "agent.log")));
        if (earlyStop is not null)
        {
            hostBuilder.Logging.AddProvider(earlyStop);
        }
        using var appHost = hostBuilder.Build();
        var agentClient = appHost.Services.GetRequiredService<LmStudioAgentClient>();

        TestContext.Out.WriteLine(
            $"Replaying '{settings.TranscriptPath}' after turn {settings.KeepTurns} of {source.Turns.Count}: " +
            $"{settings.Samples} sample(s), literal case {literalCase}, declared names: {string.Join(", ", declared)}.");

        var withTypo = 0;
        var measured = 0;
        try
        {
            for (var sampleIndex = 1; sampleIndex <= settings.Samples; sampleIndex++)
            {
                var sampleDirectory = Path.Combine(runDirectory, $"sample-{sampleIndex:D2}");
                var stopwatch = Stopwatch.StartNew();
                var runner = new ModelAgentRunner(
                    agentClient, host,
                    repeatedFailureLimit: 10,
                    maxTokensPerTurn: 16384,
                    turnCap: settings.KeepTurns + 1,
                    wallClockCap: TimeSpan.FromMinutes(10),
                    logger: appHost.Services.GetRequiredService<ILogger<ModelAgentRunner>>(),
                    loopAssist: LoopAssistOptions.Off);
                // This deviates from the normal turn flow on purpose: the replay only measures the decision turn, so the stream
                // is cut as soon as the member name is written. Set ROSLYNSENTINEL_MODELEVAL_REPLAY_EARLY_STOP=0 to get whole turns.
                using var sampleCts = earlyStop is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (earlyStop is not null && sampleCts is not null)
                {
                    earlyStop.Begin(sampleCts);
                }

                ReplayRow row;
                bool measuredSample;
                try
                {
                    var result = await runner.RunAsync(systemPrompt, userPrompt, sampleDirectory, sampleCts?.Token ?? cancellationToken, seedTurns: seed);
                    stopwatch.Stop();

                    var liveTurn = result.Transcript.Turns.FirstOrDefault(t => t.TurnNumber > settings.KeepTurns);
                    row = BuildRow(sampleIndex, stopwatch.Elapsed, literalCase, liveTurn, declared, result.StopReason.ToString());
                    measuredSample = liveTurn is not null;
                }
                catch (OperationCanceledException) when (earlyStop is not null && earlyStop.Fired && !cancellationToken.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    row = BuildEarlyStopRow(sampleIndex, stopwatch.Elapsed, literalCase, earlyStop, declared);
                    measuredSample = true;
                }
                finally
                {
                    earlyStop?.End();
                }

                if (measuredSample)
                {
                    measured++;
                    if (row.TypoInTurn)
                    {
                        withTypo++;
                    }
                }

                File.AppendAllText(csvPath, row.ToCsvLine() + "\r\n");
                TestContext.Out.WriteLine($"Sample {sampleIndex}: {row.ToCsvLine()}");
            }
        }
        finally
        {
            TestContext.Out.WriteLine($"{withTypo} of {measured} measured sample(s) wrote a ShippingKinds member that does not exist (literal case {literalCase}).");
            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);
        }
    }

    /// <summary>Early stop is on unless ROSLYNSENTINEL_MODELEVAL_REPLAY_EARLY_STOP is 0, false or off.</summary>
    private static bool ReadEarlyStop()
    {
        var raw = Environment.GetEnvironmentVariable(EarlyStopVariable)?.Trim();
        return !(string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Scores a sample that was cut at the decision: there is no transcript turn, so the streamed text is the evidence.</summary>
    private static ReplayRow BuildEarlyStopRow(
        int sampleIndex, TimeSpan elapsed, HistoryVariant literalCase, EarlyStopLoggerProvider earlyStop, HashSet<string> declared)
    {
        var unknown = Misspellings(earlyStop.ReasoningText, declared)
            .Concat(Misspellings(earlyStop.ContentText, declared))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var inReasoning = earlyStop.FiredInReasoning;
        return new ReplayRow(sampleIndex, elapsed, literalCase, unknown, inReasoning, !inReasoning, false, "", "EarlyStopAtDecision");
    }

    private static HistoryVariant ReadLiteralCase()
    {
        var raw = Environment.GetEnvironmentVariable(LiteralCaseVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HistoryVariant.Original;
        }

        if (!Enum.TryParse<HistoryVariant>(raw.Trim(), ignoreCase: true, out var value))
        {
            throw new ArgumentException($"{LiteralCaseVariable} must be Original or Capitalized, got '{raw}'.");
        }

        return value;
    }

    /// <summary>Member names of ShippingKinds: declarations in the kept ReadFile result of ShippingKinds.cs, plus a fallback list.</summary>
    private static HashSet<string> DeclaredNames(IReadOnlyList<AgentTranscriptTurn> seed)
    {
        var names = new HashSet<string>(FallbackMemberNames, StringComparer.Ordinal);
        foreach (var call in seed.SelectMany(t => t.ToolCalls))
        {
            if (call.ToolName == "ReadFile" && call.ResultJson.Contains("class ShippingKinds", StringComparison.Ordinal))
            {
                foreach (Match match in DeclarationRegex.Matches(call.ResultJson))
                {
                    names.Add(match.Groups[1].Value);
                }
            }
        }

        return names;
    }

    private static ReplayRow BuildRow(
        int sampleIndex, TimeSpan elapsed, HistoryVariant literalCase, AgentTranscriptTurn? liveTurn, HashSet<string> declared, string stopReason)
    {
        if (liveTurn is null)
        {
            return new ReplayRow(sampleIndex, elapsed, literalCase, [], false, false, false, "", stopReason + " (no live turn)");
        }

        var reasoning = Misspellings(liveTurn.ModelMessage.ReasoningContent, declared);
        var content = Misspellings(liveTurn.ModelMessage.Content, declared);
        var calls = liveTurn.ModelMessage.ToolCalls.SelectMany(c => Misspellings(c.ArgumentsJson, declared)).ToList();
        var unknown = reasoning.Concat(content).Concat(calls).Distinct(StringComparer.Ordinal).ToList();
        var firstTool = liveTurn.ModelMessage.ToolCalls.FirstOrDefault()?.Name ?? "";
        return new ReplayRow(sampleIndex, elapsed, literalCase, unknown, reasoning.Count > 0, content.Count > 0, calls.Count > 0, firstTool, stopReason);
    }

    private static List<string> Misspellings(string? text, HashSet<string> declared)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return found;
        }

        foreach (Match match in MemberUseRegex.Matches(text))
        {
            var name = match.Groups[1].Value;

            // ShippingKinds.cs is the file name and short names are prose placeholders (ShippingKinds.X).
            if (name.Length < 3 || name == "cs" || PlaceholderNames.Contains(name, StringComparer.Ordinal) || declared.Contains(name))
            {
                continue;
            }

            found.Add(name);
        }

        return found;
    }

    private enum HistoryVariant
    {
        Original,
        Capitalized,
        QualifiedResult,
        ExampleOvernight,
    }

    private sealed record ReplayRow(
        int SampleIndex,
        TimeSpan Elapsed,
        HistoryVariant LiteralCase,
        IReadOnlyList<string> UnknownNames,
        bool InReasoning,
        bool InContent,
        bool InCalls,
        string FirstToolName,
        string StopReason)
    {
        public bool TypoInTurn => UnknownNames.Count > 0;

        public string ToCsvLine() => string.Join(
            ',',
            SampleIndex,
            Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
            LiteralCase,
            TypoInTurn ? "true" : "false",
            string.Join(';', UnknownNames),
            InReasoning ? "true" : "false",
            InContent ? "true" : "false",
            InCalls ? "true" : "false",
            FirstToolName,
            StopReason);
    }

    /// <summary>
    /// Reads the LM Studio reasoning and content deltas from the logger ("LM Studio reasoning: {Delta}" and "LM Studio streaming: {Delta}")
    /// and cancels the current sample once the accumulated text of a channel contains a ShippingKinds.O... member. Deltas arrive on an
    /// HttpClient stream thread, so every buffer access is locked.
    /// </summary>
    private sealed class EarlyStopLoggerProvider : ILoggerProvider
    {
        private static readonly Regex StopRegex = new(
            @"ShippingKinds\.O\w*[^\w]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _reasoning = new();
        private readonly System.Text.StringBuilder _content = new();
        private readonly EarlyStopLogger _logger;
        private CancellationTokenSource? _target;
        private bool _fired;
        private bool _firedInReasoning;

        public EarlyStopLoggerProvider()
        {
            _logger = new EarlyStopLogger(this);
        }

        public bool Fired
        {
            get { lock (_gate) { return _fired; } }
        }

        public bool FiredInReasoning
        {
            get { lock (_gate) { return _firedInReasoning; } }
        }

        public string ReasoningText
        {
            get { lock (_gate) { return _reasoning.ToString(); } }
        }

        public string ContentText
        {
            get { lock (_gate) { return _content.ToString(); } }
        }

        /// <summary>Resets the buffers and makes <paramref name="cts"/> the source to cancel when the decision is written.</summary>
        public void Begin(CancellationTokenSource cts)
        {
            lock (_gate)
            {
                _reasoning.Clear();
                _content.Clear();
                _fired = false;
                _firedInReasoning = false;
                _target = cts;
            }
        }

        /// <summary>Stops watching; the buffers stay readable until the next Begin.</summary>
        public void End()
        {
            lock (_gate)
            {
                _target = null;
            }
        }

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose()
        {
        }

        private void Append(bool reasoning, string delta)
        {
            lock (_gate)
            {
                var target = _target;
                if (target is null || _fired)
                {
                    return;
                }

                var buffer = reasoning ? _reasoning : _content;
                buffer.Append(delta);
                if (!StopRegex.IsMatch(buffer.ToString()))
                {
                    return;
                }

                _fired = true;
                _firedInReasoning = reasoning;
                target.Cancel();
            }
        }

        private sealed class EarlyStopLogger : ILogger
        {
            private readonly EarlyStopLoggerProvider _owner;

            public EarlyStopLogger(EarlyStopLoggerProvider owner)
            {
                _owner = owner;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                string? format = null;
                string? delta = null;
                foreach (var pair in values)
                {
                    if (pair.Key == "{OriginalFormat}")
                    {
                        format = pair.Value as string;
                    }
                    else if (pair.Key == "Delta")
                    {
                        delta = pair.Value?.ToString();
                    }
                }

                if (format is null || delta is null)
                {
                    return;
                }

                if (format.StartsWith("LM Studio reasoning:", StringComparison.Ordinal))
                {
                    _owner.Append(true, delta);
                }
                else if (format.StartsWith("LM Studio streaming:", StringComparison.Ordinal))
                {
                    _owner.Append(false, delta);
                }
            }
        }
    }

    /// <summary>Offers the tools the source run offered (rebuilt from its header) and answers every call with a placeholder.</summary>
    private sealed class ReplayToolHost : IAgentToolHost
    {
        private readonly ResumeSource _source;

        public ReplayToolHost(ResumeSource source)
        {
            _source = source;
        }

        public Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
        {
            var descriptions = _source.ToolDescriptions;
            var schemas = _source.ToolSchemas;
            if (descriptions is null || schemas is null)
            {
                throw new InvalidOperationException(
                    "The source transcript has no ToolDescriptions/ToolSchemas in its header (it predates their recording), so the tool list cannot be rebuilt. Pick a newer transcript.");
            }

            var tools = new List<AgentToolDefinition>(_source.Tools.Count);
            foreach (var name in _source.Tools)
            {
                if (!descriptions.TryGetValue(name, out var description) || !schemas.TryGetValue(name, out var schema))
                {
                    throw new InvalidOperationException($"The source transcript lists tool '{name}' but its header has no description or schema for it.");
                }

                tools.Add(new AgentToolDefinition { Name = name, Description = description, ParametersSchema = schema });
            }

            return Task.FromResult<IReadOnlyList<AgentToolDefinition>>(tools);
        }

        public Task<CallToolResult> CallToolAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            Task.FromResult(new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = PlaceholderText }],
            });
    }
}

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
/// It asserts nothing about the model: one results.csv row per sample plus a per-sample transcript.json that
/// scripts/Get-ShippingKindTypoCounts.ps1 reads (the decision turn is found the same way as for a full run).
/// </summary>
[TestFixture]
public class ShippingKindReplayTests
{
    /// <summary>Original (default) keeps the history as recorded; Capitalized capitalizes the quoted kind values in it.</summary>
    public const string LiteralCaseVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_LITERAL_CASE";

    private const string CsvHeader = "sampleIndex,seconds,literalCase,typoInTurn,unknownNames,inReasoning,inContent,inCalls,firstToolName,stopReason";

    /// <summary>What a live tool call is answered with: a neutral success that names nothing from the transcript.</summary>
    private const string PlaceholderText = "{\"isError\":false,\"statusMessage\":\"transcript replay: tool calls are not executed here\"}";

    private static readonly string[] FallbackMemberNames =
        ["Standard", "Express", "Overnight", "Pickup", "SameDay", "IsKnown", "IsExpedited", "IsPickup", "All", "Normalize"];

    // Same names the typo script ignores: placeholders in prose, not misspellings.
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
        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "results.csv");
        File.WriteAllText(csvPath, CsvHeader + "\r\n");

        var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        hostBuilder.Services.AddLmStudioAgentClient(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600));
        });
        hostBuilder.Logging.AddProvider(new FlushingFileLoggerProvider(Path.Combine(runDirectory, "agent.log")));
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
                var result = await runner.RunAsync(systemPrompt, userPrompt, sampleDirectory, cancellationToken, seedTurns: seed);
                stopwatch.Stop();

                var liveTurn = result.Transcript.Turns.FirstOrDefault(t => t.TurnNumber > settings.KeepTurns);
                var row = BuildRow(sampleIndex, stopwatch.Elapsed, literalCase, liveTurn, declared, result.StopReason.ToString());
                if (liveTurn is not null)
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

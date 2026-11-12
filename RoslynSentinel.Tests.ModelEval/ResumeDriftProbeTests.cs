using System.Text.Json;

using ModelContextProtocol.Client;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// No-model diagnostic: replays the resume capture named by <see cref="ResumeScenarios.StaleCallerEditAfterRename"/> on a
/// fresh fixture, with the same setup as RunResumeSampleAsync but without the language model, and prints where the
/// recorded schemas and replayed results differ. It prints for diagnosis and asserts nothing about drift.
/// </summary>
[TestFixture]
public class ResumeDriftProbeTests
{
    private const int SchemaWindowRadius = 80;
    private const int ResultWindowRadius = 100;

    [Test]
    [Explicit("No model needed: replays the resume capture on a fresh fixture and prints schema and result drift for diagnosis")]
    public async Task Probe_ReplaysTheCaptureAndPrintsDrift()
    {
        var printed = new List<string>();
        void Print(string line)
        {
            TestContext.Out.WriteLine(line);
            printed.Add(line);
        }

        try
        {
            var scenario = ResumeScenarios.StaleCallerEditAfterRename;
            var capturePath = ResumeScenarios.CapturePath(scenario.ResumeTranscript!);
            if (!File.Exists(capturePath))
            {
                Assert.Ignore($"Resume capture not found: '{capturePath}'.");
            }

            var cancellationToken = TestContext.CurrentContext.CancellationToken;
            var source = TranscriptResume.Load(capturePath);
            var kept = TranscriptResume.KeepTurns(source, scenario.ResumeKeepTurns);

            var sampleDirectory = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "model-eval",
                "ResumeDriftProbe",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(sampleDirectory);

            await using var environment = await ScenarioAgentTests.ScenarioEnvironment.CreateAsync(scenario, sampleDirectory, cancellationToken);

            var liveTools = await environment.Client.ListToolsAsync(cancellationToken: cancellationToken);
            var liveSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var liveDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tool in liveTools)
            {
                if (tool.JsonSchema.ValueKind != JsonValueKind.Undefined)
                {
                    liveSchemas[tool.Name] = tool.JsonSchema;
                }

                liveDescriptions[tool.Name] = tool.Description ?? "";
            }

            var (schemaReason, schemaDrift) = ResumeScenarioGate.CheckSchemas(source, kept, liveSchemas, liveDescriptions);

            var seed = TranscriptResume.RewriteTurns(kept, environment.SolutionDirectory);
            var replayed = await TranscriptResume.ReplayKeptCallsDetailedAsync(
                seed,
                (tool, arguments, token) => TranscriptReplayHelper.ReplayToolCallAsync(environment.Client, tool, arguments, token),
                cancellationToken,
                Print);
            var (replayReason, resultDrift) = ResumeScenarioGate.CheckReplay(replayed);

            var calledTools = kept
                .SelectMany(turn => turn.ToolCalls)
                .Select(call => call.ToolName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Print($"capture commit: {source.CommitHash ?? "(none)"}");
            Print($"kept turns: {kept.Count}");
            Print($"tools called in kept turns: {string.Join(", ", calledTools)}");
            Print($"CheckSchemas: reason='{schemaReason}' schemaDrift={schemaDrift}");
            Print($"CheckReplay: reason='{replayReason}' resultDrift={resultDrift}");

            Print("Schema detail:");
            foreach (var tool in calledTools)
            {
                var recordedDescription = source.ToolDescriptions?.GetValueOrDefault(tool) ?? "";
                var liveDescription = liveDescriptions.GetValueOrDefault(tool) ?? "";
                var descriptionEqual = string.Equals(recordedDescription.Trim(), liveDescription.Trim(), StringComparison.Ordinal);

                var recordedSchema = RawSchema(source.ToolSchemas, tool);
                var liveSchema = RawSchema(liveSchemas, tool);
                var schemaEqual = recordedSchema is not null && liveSchema is not null
                    && string.Equals(recordedSchema, liveSchema, StringComparison.Ordinal);

                Print(
                    $"{tool}: description equal={descriptionEqual}; schema equal={schemaEqual} " +
                    $"(recorded length {recordedSchema?.Length ?? -1}, live length {liveSchema?.Length ?? -1})");

                if (recordedSchema is not null && liveSchema is not null && !schemaEqual)
                {
                    var index = FirstDifference(recordedSchema, liveSchema);
                    Print($"  first difference at index {index}");
                    Print($"  recorded: {Window(recordedSchema, index, SchemaWindowRadius)}");
                    Print($"  live: {Window(liveSchema, index, SchemaWindowRadius)}");
                }
            }

            Print("Result detail:");
            var differing = 0;
            foreach (var call in replayed)
            {
                var recorded = TranscriptResume.NormalizeResultText(call.RecordedResultText);
                var live = TranscriptResume.NormalizeResultText(call.ReplayedResultText);
                if (string.Equals(recorded, live, StringComparison.Ordinal))
                {
                    continue;
                }

                differing++;
                var index = FirstDifference(recorded, live);
                Print($"turn {call.TurnNumber} {call.ToolName}: first difference at index {index}");
                Print($"recorded: {Window(recorded, index, ResultWindowRadius)}");
                Print($"replayed: {Window(live, index, ResultWindowRadius)}");
            }

            Print($"result drift: {differing} of {replayed.Count} replayed calls differ after normalization");
            Assert.That(replayed.Count, Is.GreaterThan(0), "The probe replayed no calls; the capture has no kept tool calls.");
        }
        finally
        {
            var probeDirectory = Path.Combine(Path.GetTempPath(), "rs-drift-probe");
            Directory.CreateDirectory(probeDirectory);
            var probePath = Path.Combine(probeDirectory, "drift-probe.txt");
            Print(probePath);
            File.WriteAllLines(probePath, printed, new System.Text.UTF8Encoding(false));
        }
    }

    private static string? RawSchema(IReadOnlyDictionary<string, JsonElement>? schemas, string tool) =>
        schemas is not null && schemas.TryGetValue(tool, out var element) ? element.GetRawText() : null;

    /// <summary>
    /// Index of the first position where the two strings differ. When one is a prefix of the other, the length of the
    /// shorter one. Returns -1 only when the strings are equal.
    /// </summary>
    private static int FirstDifference(string a, string b)
    {
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : shared;
    }

    /// <summary>
    /// Up to <paramref name="radius"/> characters on each side of <paramref name="index"/>, with CR and LF shown as a
    /// visible \n so the window stays on one line.
    /// </summary>
    private static string Window(string text, int index, int radius)
    {
        var start = Math.Min(Math.Max(0, index - radius), text.Length);
        var length = Math.Min(2 * radius, text.Length - start);
        return text.Substring(start, length).Replace("\r", "\\r").Replace("\n", "\\n");
    }
}

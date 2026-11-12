using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// A saved transcript.json read back for resuming. <see cref="AgentTranscript"/> cannot be used for this: its Turns
/// list is get-only, so System.Text.Json silently leaves it empty.
/// </summary>
public sealed class ResumeSource
{
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";

    /// <summary>Tools the original run offered; empty for a transcript written before that field existed.</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>Commit the run was captured at, recorded by the runner; null for transcripts written before the field existed.</summary>
    public string? CommitHash
    {
        get; set;
    }

    /// <summary>Tool-level description text per tool, recorded by the runner; null for transcripts written before the field existed.</summary>
    public Dictionary<string, string>? ToolDescriptions
    {
        get; set;
    }

    /// <summary>Full JSON schema per tool, recorded by the runner; null for transcripts written before the field existed.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? ToolSchemas
    {
        get; set;
    }

    public List<AgentTranscriptTurn> Turns { get; set; } = [];
}

/// <summary>The four ROSLYNSENTINEL_MODELEVAL_RESUME_* knobs, validated.</summary>
/// <param name="TranscriptPath">transcript.json or the folder holding it; null when unset.</param>
/// <param name="KeepTurns">Turns 1..K of the source kept as history.</param>
/// <param name="Samples">How many live resamples to run.</param>
/// <param name="ContinueTurns">Live turns each sample may run after the resume point.</param>
public sealed record ResumeSettings(string? TranscriptPath, int KeepTurns, int Samples, int ContinueTurns)
{
    public const string TranscriptVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_TRANSCRIPT";
    public const string KeepTurnsVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_KEEP_TURNS";
    public const string SamplesVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_SAMPLES";
    public const string ContinueTurnsVariable = "ROSLYNSENTINEL_MODELEVAL_RESUME_CONTINUE_TURNS";

    public const int DefaultKeepTurns = 3;
    public const int DefaultSamples = 10;
    public const int DefaultContinueTurns = 3;

    /// <param name="readVariable">Environment reader; defaults to the process environment (a seam for tests).</param>
    /// <exception cref="ArgumentException">A variable is set but is not a whole number of at least 1.</exception>
    public static ResumeSettings FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= name => Environment.GetEnvironmentVariable(name);
        var path = readVariable(TranscriptVariable);
        return new ResumeSettings(
            string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"'),
            ReadPositiveInt(readVariable, KeepTurnsVariable, DefaultKeepTurns),
            ReadPositiveInt(readVariable, SamplesVariable, DefaultSamples),
            ReadPositiveInt(readVariable, ContinueTurnsVariable, DefaultContinueTurns));
    }

    private static int ReadPositiveInt(Func<string, string?> readVariable, string name, int fallback)
    {
        var raw = readVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw.Trim(), out var value) || value < 1)
        {
            throw new ArgumentException($"{name} must be a whole number of at least 1, got '{raw}'.");
        }

        return value;
    }
}

/// <summary>
/// One measured sample of a resumed run, one results.csv row. The first seven columns are the measurement; the rest
/// say how the sample ended and what it called.
/// </summary>
/// <param name="LoopAssist">Loop assist was on for the sample.</param>
/// <param name="Role">Injection role ("user"/"assistant"); "n/a" when loop assist was off.</param>
/// <param name="FirstNewToolName">Tool of the first call the model made after the resume point; "" when it made none.</param>
/// <param name="FirstCallIdenticalToRejected">That first call repeats a rejected call from the kept turns (same tool, same arguments).</param>
/// <param name="ReachedRenameSymbolWithinTurns">Any new call within the continue-turn budget was RenameSymbol.</param>
/// <param name="AssistCount">The transcript's AssistCount; includes assists fired while seeding the kept turns.</param>
/// <param name="StopReason">AgentStopReason name, or "HarnessError".</param>
/// <param name="NewCalls">Every new call as Tool(ok) or Tool(rejected), joined by ';'.</param>
/// <param name="HarnessError">Single-line error text when the sample failed to run at all; "" otherwise.</param>
public sealed record ResumeSampleRow(
    int SampleIndex,
    bool LoopAssist,
    string Role,
    string FirstNewToolName,
    bool FirstCallIdenticalToRejected,
    bool ReachedRenameSymbolWithinTurns,
    int AssistCount,
    string StopReason,
    string NewCalls,
    string HarnessError)
{
    public const string CsvHeader =
        "sampleIndex,loopAssist,role,firstNewToolName,firstCallIdenticalToRejected,reachedRenameSymbolWithinTurns,assistCount,stopReason,newCalls,harnessError";

    public bool IsHarnessError => HarnessError.Length > 0;

    /// <summary>One CSV line (no line terminator); a field with a comma, quote or line break is quoted.</summary>
    public string ToCsvLine() => string.Join(
        ',',
        SampleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        LoopAssist ? "on" : "off",
        Csv(Role),
        Csv(FirstNewToolName),
        Bool(FirstCallIdenticalToRejected),
        Bool(ReachedRenameSymbolWithinTurns),
        AssistCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Csv(StopReason),
        Csv(NewCalls),
        Csv(HarnessError));

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}

/// <summary>
/// Pure helpers for the "resume a model-eval run from a saved transcript" mode: load a transcript, keep turns 1..K,
/// point every fixture path in it at the fresh fixture, re-execute the kept calls (so file state matches), and turn a
/// finished sample into a results.csv row. The live test (<c>TranscriptResumeAgentTests</c>) wires them to a real
/// model; these have no model or server dependency, so they are unit-testable.
/// </summary>
public static class TranscriptResume
{
    // The fixture folder is RoslynSentinelTests_<guid> under the temp dir. The same path shows up three ways in one
    // transcript: backslashes in the prompt, forward slashes in tool arguments, and JSON-escaped backslashes inside
    // tool results, so the match keeps its own style when it is replaced.
    private static readonly Regex FixtureDirectoryRegex = new(
        @"[A-Za-z]:(?:\\\\|\\|/)[^""\r\n<>|`]*?RoslynSentinelTests_[0-9a-fA-F-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The volatile keys were found by surveying recorded ReplaceSnippet/RenameSymbol/ReadFile/Build results of 10-chain runs
    // (toolCallId, changeId, resultId, duration, sizeBytes/sizeChars, workspaceVersion, fixture path); extend the list when
    // CountResultDrift flags a field that is not a wording change.
    // Ids embedded inside strings (the large-result file name, the changeId in a prose hint and the resultId in a paging hint) are masked by their own
    // regexes in NormalizeResultText, not by this key list.
    // TODO: a field this survey missed will show up as drift on every sample; when that happens, add its key to this list (or, if it sits inside a string, a narrow regex in NormalizeResultText).
    private static readonly Regex VolatileJsonValueRegex = new(
        @"""(toolCallId|changeId|resultId|duration|durationMs|elapsed|elapsedMs|sizeBytes|sizeChars|workspaceVersion)""\s*:\s*(""(?:[^""\\]|\\.)*""|-?[0-9.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LargeResultFileNameRegex = new(
        @"largeresult_[0-9]{8}T[0-9]{6}Z_[0-9a-fA-F]{32}\.json",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChangeIdProseRegex = new(
        @"changeId:\s*(?:\\""|"")[0-9a-fA-F]{6,}(?:\\""|"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ResultIdProseRegex = new(
        @"resultId:\s*(?:\\""|"")[0-9a-fA-F]{32}(?:\\""|"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsoTimestampRegex = new(
    @"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})?",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeSpanTextRegex = new(
    @"\b[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3,7}\b",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The transcript.json file for a path that is either that file or the folder holding it.</summary>
    public static string ResolveTranscriptPath(string pathOrFolder)
    {
        var path = Directory.Exists(pathOrFolder) ? Path.Combine(pathOrFolder, "transcript.json") : pathOrFolder;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Transcript not found: '{path}' (from '{pathOrFolder}'). Pass transcript.json or the run folder holding it.");
        }

        return path;
    }

    public static ResumeSource Load(string transcriptJsonPath)
    {
        var json = File.ReadAllText(transcriptJsonPath);
        return JsonSerializer.Deserialize<ResumeSource>(json)
            ?? throw new InvalidOperationException($"Transcript at '{transcriptJsonPath}' deserialized to null.");
    }

    /// <summary>Turns 1..<paramref name="keepTurns"/> of the source, in order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The source has fewer than <paramref name="keepTurns"/> turns, or it is below 1.</exception>
    public static IReadOnlyList<AgentTranscriptTurn> KeepTurns(ResumeSource source, int keepTurns)
    {
        if (keepTurns < 1 || keepTurns > source.Turns.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keepTurns), keepTurns,
                $"The transcript has {source.Turns.Count} turn(s); keep between 1 and {source.Turns.Count}.");
        }

        return source.Turns.Take(keepTurns).ToList();
    }

    /// <summary>
    /// Replaces every original fixture directory in <paramref name="text"/> with <paramref name="newDirectory"/>,
    /// keeping the style (backslash, forward slash, JSON-escaped backslash) each occurrence had.
    /// </summary>
    public static string? RewriteFixtureDirectory(string? text, string newDirectory)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return FixtureDirectoryRegex.Replace(text, match =>
            match.Value.Contains("\\\\", StringComparison.Ordinal) ? newDirectory.Replace("\\", "\\\\")
            : match.Value.Contains('\\') ? newDirectory
            : newDirectory.Replace('\\', '/'));
    }

    /// <summary>
    /// Masks what always differs between two runs of the same call: the fixture directory, volatile id, timing and size
    /// values, ids inside strings, ISO-8601 timestamps and .NET TimeSpan text. Null or empty gives "".
    /// </summary>
    public static string NormalizeResultText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = RewriteFixtureDirectory(text, "<FIXTURE>") ?? string.Empty;
        normalized = VolatileJsonValueRegex.Replace(normalized, "\"$1\":\"<v>\"");
        normalized = LargeResultFileNameRegex.Replace(normalized, "largeresult_<id>.json");
        normalized = ChangeIdProseRegex.Replace(normalized, "changeId: <id>");
        normalized = ResultIdProseRegex.Replace(normalized, "resultId: <id>");
        normalized = IsoTimestampRegex.Replace(normalized, "<time>");
        return TimeSpanTextRegex.Replace(normalized, "<duration>");
    }

    /// <summary>
    /// Copies of <paramref name="turns"/> with the fixture directory rewritten in the model's reasoning/content, each
    /// call's arguments and each result, so the model sees a history that matches the fresh fixture it will edit.
    /// </summary>
    public static IReadOnlyList<AgentTranscriptTurn> RewriteTurns(IReadOnlyList<AgentTranscriptTurn> turns, string newDirectory) =>
        turns.Select(turn => new AgentTranscriptTurn
        {
            TurnNumber = turn.TurnNumber,
            ModelLatency = turn.ModelLatency,
            StartedAt = turn.StartedAt,
            CompletedAt = turn.CompletedAt,
            ModelMessage = new AgentChatMessage
            {
                Role = turn.ModelMessage.Role,
                Content = RewriteFixtureDirectory(turn.ModelMessage.Content, newDirectory),
                ReasoningContent = RewriteFixtureDirectory(turn.ModelMessage.ReasoningContent, newDirectory),
                ToolCallId = turn.ModelMessage.ToolCallId,
                TotalTokens = turn.ModelMessage.TotalTokens,
                ToolCalls = turn.ModelMessage.ToolCalls
                    .Select(call => new AgentToolCall
                    {
                        Id = call.Id,
                        Name = call.Name,
                        ArgumentsJson = RewriteFixtureDirectory(call.ArgumentsJson, newDirectory)!,
                    })
                    .ToList(),
            },
            ToolCalls = turn.ToolCalls
                .Select(call => new AgentToolCallRecord
                {
                    ToolName = call.ToolName,
                    ArgumentsJson = RewriteFixtureDirectory(call.ArgumentsJson, newDirectory)!,
                    ResultJson = RewriteFixtureDirectory(call.ResultJson, newDirectory)!,
                    IsError = call.IsError,
                    Latency = call.Latency,
                    StartedAt = call.StartedAt,
                    CompletedAt = call.CompletedAt,
                    ToolCallId = call.ToolCallId,
                })
                .ToList(),
        }).ToList();

    /// <summary>
    /// A turn whose call was authored by the harness, not the model: the call itself is authored by the harness, only
    /// its <paramref name="resultText"/> is real (executed on the fixture). Used to seed a failed last call.
    /// </summary>
    public static AgentTranscriptTurn BuildAuthoredFaultTurn(int turnNumber, string toolName, string argumentsJson, string resultText, bool isError)
    {
        var callId = "authored-fault-" + turnNumber;
        var now = DateTimeOffset.UtcNow;
        return new AgentTranscriptTurn
        {
            TurnNumber = turnNumber,
            ModelLatency = TimeSpan.Zero,
            StartedAt = now,
            CompletedAt = now,
            ModelMessage = new AgentChatMessage
            {
                Role = "assistant",
                Content = "",
                ReasoningContent = null,
                ToolCalls = [new AgentToolCall { Id = callId, Name = toolName, ArgumentsJson = argumentsJson }],
            },
            ToolCalls =
            [
                new AgentToolCallRecord
                {
                    ToolName = toolName,
                    ArgumentsJson = argumentsJson,
                    ResultJson = resultText,
                    IsError = isError,
                    Latency = TimeSpan.Zero,
                    StartedAt = now,
                    CompletedAt = now,
                    ToolCallId = callId,
                },
            ],
        };
    }

    // TODO delegate to ReplayKeptCallsDetailedAsync once TranscriptResumeAgentTests moves to the detailed result.
    /// <summary>
    /// Re-executes every tool call of <paramref name="turns"/>, turn by turn and in recorded order, through
    /// <paramref name="callTool"/> (tool name, arguments JSON) so the fresh fixture's files (and the server's own
    /// repeated-rejection memory) reach the state they had at the resume point. The recorded results, not the replayed
    /// ones, are what the model is shown; this only reports how many replayed IsError flags differ from the record.
    /// </summary>
    public static async Task<int> ReplayKeptCallsAsync(
        IReadOnlyList<AgentTranscriptTurn> turns,
        Func<string, string, CancellationToken, Task<(string ResultText, bool IsError)>> callTool,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        var mismatches = 0;
        foreach (var turn in turns)
        {
            foreach (var recorded in turn.ToolCalls)
            {
                var (_, replayedIsError) = await callTool(recorded.ToolName, recorded.ArgumentsJson, cancellationToken);
                if (replayedIsError != recorded.IsError)
                {
                    mismatches++;
                    log?.Invoke(
                        $"*** MISMATCH at turn {turn.TurnNumber} {recorded.ToolName}: recorded IsError={recorded.IsError}, " +
                        $"replay IsError={replayedIsError}. Fixture state may differ from the original run from here on.");
                }
                else
                {
                    log?.Invoke($"Replayed turn {turn.TurnNumber} {recorded.ToolName} (IsError={replayedIsError}, as recorded).");
                }
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Re-executes every tool call of <paramref name="turns"/> like <see cref="ReplayKeptCallsAsync"/> and returns one
    /// <see cref="ReplayedCall"/> per recorded call, with the recorded and replayed result text and IsError flags.
    /// This is the gate input: a scenario with any Mismatch is Inconclusive (step 8b). The older
    /// <see cref="ReplayKeptCallsAsync"/> stays for the existing resume test and only counts.
    /// </summary>
    public static async Task<IReadOnlyList<ReplayedCall>> ReplayKeptCallsDetailedAsync(
        IReadOnlyList<AgentTranscriptTurn> turns,
        Func<string, string, CancellationToken, Task<(string ResultText, bool IsError)>> callTool,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        var replayed = new List<ReplayedCall>();
        foreach (var turn in turns)
        {
            foreach (var recorded in turn.ToolCalls)
            {
                var (replayedText, replayedIsError) = await callTool(recorded.ToolName, recorded.ArgumentsJson, cancellationToken);
                replayed.Add(new ReplayedCall(
                    turn.TurnNumber,
                    recorded.ToolName,
                    recorded.IsError,
                    replayedIsError,
                    recorded.ResultJson,
                    replayedText));
                if (replayedIsError != recorded.IsError)
                {
                    log?.Invoke(
                        $"*** MISMATCH at turn {turn.TurnNumber} {recorded.ToolName}: recorded IsError={recorded.IsError}, " +
                        $"replay IsError={replayedIsError}. Fixture state may differ from the original run from here on.");
                }
                else
                {
                    log?.Invoke($"Replayed turn {turn.TurnNumber} {recorded.ToolName} (IsError={replayedIsError}, as recorded).");
                }
            }
        }

        return replayed;
    }

    /// <summary>
    /// Null when no replayed call differs from the recording; otherwise a one-line reason naming the first mismatch.
    /// </summary>
    public static string? ReplayMismatchReason(IReadOnlyList<ReplayedCall> replayed)
    {
        var first = replayed.FirstOrDefault(call => call.Mismatch);
        if (first is null)
        {
            return null;
        }

        int count = replayed.Count(call => call.Mismatch);
        return $"Replay mismatch: {count} of {replayed.Count} replayed calls differ from the recording; " +
            $"first at turn {first.TurnNumber} {first.ToolName} (recorded IsError={first.RecordedIsError}, " +
            $"replayed IsError={first.ReplayedIsError}).";
    }

    /// <summary>
    /// Counts the replayed calls whose result text still differs from the saved one after <see cref="NormalizeResultText"/>.
    /// Counted into the CSV <c>resultDrift</c> column.
    /// </summary>
    public static int CountResultDrift(IReadOnlyList<ReplayedCall> replayed)
    {
        return replayed.Count(call =>
            !string.Equals(NormalizeResultText(call.RecordedResultText), NormalizeResultText(call.ReplayedResultText), StringComparison.Ordinal));
    }

    /// <summary>
    /// Measures a finished sample. <paramref name="turns"/> is the run transcript's full turn list: turns up to
    /// <paramref name="keepTurns"/> are the seeded history, later ones are the live continuation.
    /// </summary>
    public static ResumeSampleRow BuildRow(
        int sampleIndex,
        LoopAssistOptions assist,
        int keepTurns,
        int continueTurns,
        IReadOnlyList<AgentTranscriptTurn> turns,
        int assistCount,
        string stopReason)
    {
        var rejectedSeeded = turns
            .Where(t => t.TurnNumber <= keepTurns)
            .SelectMany(t => t.ToolCalls)
            .Where(c => c.IsError)
            .ToList();
        var newCalls = turns
            .Where(t => t.TurnNumber > keepTurns && t.TurnNumber <= keepTurns + continueTurns)
            .SelectMany(t => t.ToolCalls)
            .ToList();
        var first = newCalls.FirstOrDefault();

        return new ResumeSampleRow(
            sampleIndex,
            assist.Enabled,
            assist.Enabled ? assist.InjectionRole : "n/a",
            first?.ToolName ?? "",
            first is not null && rejectedSeeded.Any(r => SameCall(r, first)),
            newCalls.Any(c => c.ToolName == "RenameSymbol"),
            assistCount,
            stopReason,
            string.Join(';', newCalls.Select(c => $"{c.ToolName}({(c.IsError ? "rejected" : "ok")})")),
            "");
    }

    /// <summary>The row for a sample that threw before or while running (a harness error, not a model behaviour).</summary>
    public static ResumeSampleRow BuildHarnessErrorRow(int sampleIndex, LoopAssistOptions assist, string message)
    {
        var singleLine = Regex.Replace(message, @"\s+", " ").Trim();
        if (singleLine.Length > 300)
        {
            singleLine = singleLine[..300] + "...";
        }

        return new ResumeSampleRow(
            sampleIndex, assist.Enabled, assist.Enabled ? assist.InjectionRole : "n/a",
            "", false, false, 0, "HarnessError", "", singleLine.Length == 0 ? "unknown error" : singleLine);
    }

    /// <summary>Appends <paramref name="row"/> to <paramref name="csvPath"/>, writing the header first when the file is new.</summary>
    public static void AppendRow(string csvPath, ResumeSampleRow row)
    {
        var directory = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = File.Exists(csvPath)
            ? new[] { row.ToCsvLine() }
            : new[] { ResumeSampleRow.CsvHeader, row.ToCsvLine() };
        File.AppendAllLines(csvPath, lines);
    }

    private static bool SameCall(AgentToolCallRecord a, AgentToolCallRecord b)
    {
        if (a.ToolName != b.ToolName)
        {
            return false;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a.ArgumentsJson), JsonNode.Parse(b.ArgumentsJson));
        }
        catch (JsonException)
        {
            return a.ArgumentsJson == b.ArgumentsJson;
        }
    }
}
/// <summary>
/// One kept tool call replayed through the fresh fixture: the recorded result beside what the server returned now.
/// <see cref="Mismatch"/> is true when the replayed IsError flag differs from the recording.
/// </summary>
/// <param name="TurnNumber">Turn the recorded call belongs to.</param>
/// <param name="ToolName">Recorded tool name.</param>
/// <param name="RecordedIsError">IsError flag in the saved transcript.</param>
/// <param name="ReplayedIsError">IsError flag returned by the replayed call.</param>
/// <param name="RecordedResultText">Recorded result JSON from the saved transcript.</param>
/// <param name="ReplayedResultText">Result text returned by the replayed call.</param>
public sealed record ReplayedCall(int TurnNumber, string ToolName, bool RecordedIsError, bool ReplayedIsError, string RecordedResultText, string ReplayedResultText)
{
    /// <summary>True when the replayed IsError flag differs from the recorded one.</summary>
    public bool Mismatch => RecordedIsError != ReplayedIsError;
}

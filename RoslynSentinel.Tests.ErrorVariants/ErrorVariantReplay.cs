using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// The error-variant replay pipeline (plan Step 9). For every source transcript x variant x sample: cut the transcript at its
/// first error turn and splice the variant text in (<see cref="ResultOverride.Apply"/>), seed a <see cref="ModelAgentRunner"/>
/// whose tool host is a <see cref="TranscriptToolHost"/>, let the live model take <see cref="ErrorVariantReplayOptions.ContinueTurns"/>
/// turns, classify the first new turn (<see cref="NextTurnDecision"/>) and append a <see cref="VariantSampleRow"/> to
/// variants.csv in the run directory. Each sample also leaves the runner's own transcript.json.
/// A static service with no state of its own. The model host is a parameter (<see cref="HttpMessageHandler"/>) so a stub can stand in for LM Studio.
/// </summary>
/// <remarks>
/// Reads the model host, model key and sampling from <see cref="LlmOptions"/> (call <see cref="LlmOptions.Configure"/> first).
/// A problem with one cell (unreadable transcript, settings mismatch, model host down, runner failure) becomes a harness-error row
/// with no exception; only a bad configuration (a source or variant spec that resolves to nothing) throws, before any row is written.
/// </remarks>
public static class ErrorVariantReplay
{
    /// <summary>Row value of DecisionClass (and StopReason) when a cell produced no classification.</summary>
    public const string HarnessErrorClass = "HarnessError";

    /// <summary>The live values of the repeated-failure breaker and per-turn token budget (see the Step 9 spec).</summary>
    private const int RepeatedFailureLimit = 10;

    private const int MaxTokensPerTurn = 16384;

    // A fixture directory as the harness writes it into a prompt: an absolute path ending in RoslynSentinelTests_<guid>.
    private static readonly Regex FixtureDirectory = new(
        @"[A-Za-z]:[\\/][^\r\n`""'<>|*?]*?RoslynSentinelTests_[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs the replay and returns the rows it appended to <paramref name="runDirectory"/>/variants.csv, in order.
    /// </summary>
    /// <param name="handler">Transport to the model host: a real handler, or a stub in tests. Not disposed here.</param>
    /// <param name="runDirectory">Receives variants.csv and one folder per sample; created when missing. Its leaf name is the run stamp in every row.</param>
    /// <param name="onRow">Called after each row is appended; null for none.</param>
    /// <exception cref="InvalidOperationException">A source or variant spec resolves to nothing, or is ambiguous.</exception>
    /// <exception cref="FileNotFoundException">A source path or variant file does not exist.</exception>
    public static async Task<IReadOnlyList<VariantSampleRow>> RunAsync(
        ErrorVariantReplayOptions options,
        HttpMessageHandler handler,
        string runDirectory,
        CancellationToken cancellationToken,
        Action<VariantSampleRow>? onRow = null)
    {
        var sources = ResolveSources(options.Sources);
        var variants = ResolveVariants(options.Variants, options.VariantsRoot ?? Path.Combine(AppContext.BaseDirectory, "ErrorVariants"));

        Directory.CreateDirectory(runDirectory);
        var csvPath = Path.Combine(runDirectory, "variants.csv");
        var runStamp = Path.GetFileName(runDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var modelKey = LlmOptions.Model;
        var temperature = LlmOptions.Temperature;
        var topP = LlmOptions.TopP;

        // disposeHandler false: the caller owns the handler (and a test may inspect it afterwards).
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(LlmOptions.TimeoutSeconds * 4, 600)),
        };

        var modelError = await CheckModelLoadedAsync(http, ModelsUrl(LlmOptions.BaseUrl), modelKey, cancellationToken);

        // Built only when the check passed, so a down host or a missing model never sees a model request.
        var llm = modelError is null
            ? new LmStudioAgentClient(http, modelKey!, NullLogger<LmStudioAgentClient>.Instance)
            : null;

        // Cut turn 0: no seed, the model generates every turn live (see RunRegeneratedCellAsync).
        var regenerate = options.CutTurn == ErrorVariantReplayOptions.RegenerateCutTurn;

        var rows = new List<VariantSampleRow>();
        foreach (var source in sources)
        {
            AgentTranscript? transcript = null;
            string? loadError = null;
            try
            {
                transcript = SavedTranscript.Load(source.TranscriptPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loadError = $"Could not load the source transcript '{source.TranscriptPath}': {ex.Message}";
            }

            var (settingsState, settingsDetail) = transcript is null
                ? ("unknown", null)
                : CompareSourceSettings(transcript, modelKey, temperature, topP);

            foreach (var variant in variants)
            {
                for (var sampleIndex = 1; sampleIndex <= options.Samples; sampleIndex++)
                {
                    var template = new VariantSampleRow
                    {
                        RunStamp = runStamp,
                        ModelKey = modelKey ?? string.Empty,
                        Temperature = Format(temperature),
                        TopP = Format(topP),
                        MaxTokens = MaxTokensPerTurn,
                        ContinueTurns = options.ContinueTurns,
                        VariantId = variant.Id,
                        VariantHash = variant.Hash,
                        ErrorCode = variant.ErrorCode,
                        SourceRunFolder = source.RunFolder,
                        SourceSample = source.Sample,
                        SourceCommitHash = transcript?.CommitHash ?? string.Empty,
                        CutTurn = 0,
                        RecordedMessageHash = string.Empty,
                        SampleIndex = sampleIndex,
                        DecisionClass = HarnessErrorClass,
                        FirstTool = string.Empty,
                        CallCount = 0,
                        BlockedAnywhere = false,
                        StopTurn = null,
                        StopReason = HarnessErrorClass,
                        LatencyMs = 0,
                        HarnessError = string.Empty,
                        SourceSettings = settingsState,
                        Mode = regenerate ? VariantSampleRow.RegeneratedMode : VariantSampleRow.SeededMode,
                    };

                    VariantSampleRow row;
                    if (loadError is not null)
                    {
                        row = template with { HarnessError = loadError };
                    }
                    else if (settingsDetail is not null)
                    {
                        row = template with { HarnessError = settingsDetail };
                    }
                    else if (llm is null)
                    {
                        row = template with { HarnessError = "Model check failed, no model request was made: " + modelError };
                    }
                    else
                    {
                        var sampleDirectory = Path.Combine(
                            runDirectory,
                            Sanitize(variant.ErrorCode + "-" + variant.Id),
                            Sanitize(Path.GetFileName(source.RunFolder) + "_" + source.Sample),
                            $"sample-{sampleIndex:D2}");
                        row = regenerate
                            ? await RunRegeneratedCellAsync(template, transcript!, variant, sampleDirectory, llm, options, cancellationToken)
                            : await RunCellAsync(template, transcript!, variant, sampleDirectory, llm, options, cancellationToken);
                    }

                    VariantSampleRow.Append(csvPath, row);
                    rows.Add(row);
                    onRow?.Invoke(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// One sample: override, seed, run, classify. Never throws except for cancellation; a failure becomes the row's HarnessError.
    /// </summary>
    private static async Task<VariantSampleRow> RunCellAsync(
        VariantSampleRow template,
        AgentTranscript transcript,
        ErrorVariant variant,
        string sampleDirectory,
        LmStudioAgentClient llm,
        ErrorVariantReplayOptions options,
        CancellationToken cancellationToken)
    {
        var row = template;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var solutionDirectory = ResolveSolutionDirectory(transcript, variant);
            var (seed, recordedMessageHash) = ResultOverride.Apply(transcript, variant, options.CutTurn, solutionDirectory);
            var cut = seed.Count;
            row = row with { CutTurn = cut, RecordedMessageHash = recordedMessageHash };

            var host = new TranscriptToolHost(transcript, cut, variant, solutionDirectory);

            // turnCap counts the seeded turns, so the live loop gets exactly ContinueTurns more.
            // Loop assist stays off: the replay measures the model's reaction to the text, not to an injected hint.
            var runner = new ModelAgentRunner(
                llm, host,
                repeatedFailureLimit: RepeatedFailureLimit,
                maxTokensPerTurn: MaxTokensPerTurn,
                turnCap: cut + options.ContinueTurns,
                wallClockCap: TimeSpan.FromMinutes(10),
                logger: NullLogger<ModelAgentRunner>.Instance,
                loopAssist: LoopAssistOptions.Off);

            Directory.CreateDirectory(sampleDirectory);
            var result = await runner.RunAsync(
                transcript.SystemPrompt,
                transcript.UserPrompt,
                sampleDirectory,
                cancellationToken,
                seedTurns: seed,
                targetReached: _ => Task.FromResult(host.OffScript));

            var newTurns = result.Transcript.Turns.Skip(cut).ToList();
            if (newTurns.Count == 0)
            {
                return row with
                {
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    HarnessError = $"The run produced no turn after the cut (turn {cut}); it stopped with {result.StopReason}.",
                };
            }

            var decision = NextTurnDecision.Classify(newTurns, options.AbsentTargets, result.StopReason, host.OffScriptTool);
            return row with
            {
                DecisionClass = decision.DecisionClass,
                FirstTool = decision.FirstTool ?? string.Empty,
                CallCount = decision.CallCount,
                BlockedAnywhere = decision.BlockedAnywhere,
                StopTurn = decision.StopTurn,
                StopReason = decision.StopReason,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = SumOrNull(newTurns, turn => turn.ModelMessage.InputTokens),
                OutputTokens = SumOrNull(newTurns, turn => turn.ModelMessage.OutputTokens),
                HarnessError = string.Empty,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return row with
            {
                LatencyMs = stopwatch.ElapsedMilliseconds,
                HarnessError = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// One regenerated sample (cut turn 0): no seed, the model generates every turn live from the source's prompts and tools, and the
    /// host scripts ALL recorded turns with the source's first error call carrying the variant text
    /// (<see cref="TranscriptToolHost.ScriptedInFull"/>). turnCap is E + ContinueTurns, E being the source's first error turn.
    /// The row is a next-turn sample only when the model's call was served as the overridden error call in a turn of 1..E, before any
    /// off-script call; then the first live turn AFTER that turn is classified. Otherwise DecisionClass is
    /// <c>RegenMiss:&lt;first off-script tool or none&gt;</c> and nothing is classified. Never throws except for cancellation.
    /// </summary>
    /// <remarks>
    /// The turn the overridden call was served in comes from the host's call index (<see cref="TranscriptToolHost.OverriddenServedCallIndex"/>)
    /// mapped onto the run's recorded calls; the row's CutTurn is E (nothing is cut) and its token columns sum every live turn.
    /// </remarks>
    private static async Task<VariantSampleRow> RunRegeneratedCellAsync(
        VariantSampleRow template,
        AgentTranscript transcript,
        ErrorVariant variant,
        string sampleDirectory,
        LmStudioAgentClient llm,
        ErrorVariantReplayOptions options,
        CancellationToken cancellationToken)
    {
        var row = template;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var solutionDirectory = ResolveSolutionDirectory(transcript, variant);
            var errorTurn = ResultOverride.FirstErrorTurn(transcript);
            var recordedMessage = ResultOverride.FindErrorCall(transcript.Turns[errorTurn - 1], errorTurn).Message;
            var host = TranscriptToolHost.ScriptedInFull(transcript, variant, solutionDirectory);
            row = row with { CutTurn = errorTurn, RecordedMessageHash = ErrorVariantLoader.HashOf(recordedMessage) };

            var runner = new ModelAgentRunner(
                llm, host,
                repeatedFailureLimit: RepeatedFailureLimit,
                maxTokensPerTurn: MaxTokensPerTurn,
                turnCap: errorTurn + options.ContinueTurns,
                wallClockCap: TimeSpan.FromMinutes(10),
                logger: NullLogger<ModelAgentRunner>.Instance,
                loopAssist: LoopAssistOptions.Off);

            Directory.CreateDirectory(sampleDirectory);
            var result = await runner.RunAsync(
                transcript.SystemPrompt,
                transcript.UserPrompt,
                sampleDirectory,
                cancellationToken,
                targetReached: _ => Task.FromResult(host.OffScript));

            var turns = result.Transcript.Turns;
            var servedIndex = host.OverriddenServedCallIndex;
            var servedTurn = servedIndex is { } index ? TurnOfHostCall(turns, index) : null;
            var servedBeforeOffScript = host.OffScriptCallIndex is not { } offIndex || servedIndex < offIndex;
            var inputTokens = SumOrNull(turns, turn => turn.ModelMessage.InputTokens);
            var outputTokens = SumOrNull(turns, turn => turn.ModelMessage.OutputTokens);

            if (servedTurn is not { } regenTurn || regenTurn > errorTurn || !servedBeforeOffScript)
            {
                var offScriptTool = host.OffScriptTool;
                return row with
                {
                    DecisionClass = "RegenMiss:" + (offScriptTool ?? "none"),
                    StopTurn = turns.Count == 0 ? null : turns[^1].TurnNumber,
                    StopReason = offScriptTool is not null ? "OffScript:" + offScriptTool : result.StopReason.ToString(),
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    HarnessError = string.Empty,
                };
            }

            row = row with { RegenTurn = regenTurn };
            var newTurns = turns.Where(turn => turn.TurnNumber > regenTurn).ToList();
            if (newTurns.Count == 0)
            {
                return row with
                {
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    HarnessError = $"The run produced no turn after the regenerated error (served in turn {regenTurn}); it stopped with {result.StopReason}.",
                };
            }

            var decision = NextTurnDecision.Classify(newTurns, options.AbsentTargets, result.StopReason, host.OffScriptTool);
            return row with
            {
                DecisionClass = decision.DecisionClass,
                FirstTool = decision.FirstTool ?? string.Empty,
                CallCount = decision.CallCount,
                BlockedAnywhere = decision.BlockedAnywhere,
                StopTurn = decision.StopTurn,
                StopReason = decision.StopReason,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                HarnessError = string.Empty,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return row with
            {
                LatencyMs = stopwatch.ElapsedMilliseconds,
                HarnessError = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // The runner answers a call whose arguments are not valid JSON itself, without asking the host; such a call does not count as a host call.
    // TODO couples to the error text ModelAgentRunner.ExecuteToolCallAsync writes for malformed arguments; expose it as a constant there if it ever changes.
    private const string MalformedArgumentsResultPrefix = "{\"isError\":true,\"error\":\"Malformed tool-call arguments JSON";

    /// <summary>The turn number of the call the host received at zero-based position <paramref name="hostCallIndex"/>, or null when the run has fewer host calls.</summary>
    private static int? TurnOfHostCall(IReadOnlyList<AgentTranscriptTurn> turns, int hostCallIndex)
    {
        var seen = 0;
        foreach (var turn in turns)
        {
            foreach (var call in turn.ToolCalls)
            {
                if (call.ResultJson.StartsWith(MalformedArgumentsResultPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen++ == hostCallIndex)
                {
                    return turn.TurnNumber;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks LM Studio's native model list once: the configured model key must be listed and have a loaded instance.
    /// Returns null when it does, otherwise a fact-only description of what was found. Never throws (except cancellation).
    /// </summary>
    internal static async Task<string?> CheckModelLoadedAsync(HttpClient http, string modelsUrl, string? modelKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return "no model key is configured (ROSLYNSENTINEL_LLM_MODEL or --llm-model).";
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.GetAsync(modelsUrl, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return $"GET {modelsUrl} answered {(int)response.StatusCode} {response.StatusCode}.";
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return $"GET {modelsUrl} answered 200 but the body has no 'models' array, so it is not LM Studio's /api/v1/models.";
            }

            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object
                    || !model.TryGetProperty("key", out var key)
                    || !string.Equals(key.GetString(), modelKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var loaded = model.TryGetProperty("loaded_instances", out var instances)
                    && instances.ValueKind == JsonValueKind.Array
                    && instances.GetArrayLength() > 0;
                return loaded ? null : $"model '{modelKey}' is known to the host but has no loaded instance.";
            }

            return $"model '{modelKey}' is not in the host's model list ({models.GetArrayLength()} model(s) listed).";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return $"could not reach {modelsUrl}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Compares the source header's recorded model key, temperature and top-p with this run's.
    /// State is "unknown" when the header records no model key (a transcript written before the fields existed), "matched" when all three
    /// agree (null equals null: the server default), and "mismatch" otherwise, with Detail naming the differences.
    /// </summary>
    internal static (string State, string? Detail) CompareSourceSettings(AgentTranscript transcript, string? modelKey, double? temperature, double? topP)
    {
        if (transcript.ModelKey is null)
        {
            return ("unknown", null);
        }

        var differences = new List<string>();
        if (!string.Equals(transcript.ModelKey, modelKey, StringComparison.Ordinal))
        {
            differences.Add($"model key '{transcript.ModelKey}' recorded vs '{modelKey}' now");
        }

        if (transcript.Temperature != temperature)
        {
            differences.Add($"temperature {Format(transcript.Temperature)} recorded vs {Format(temperature)} now");
        }

        if (transcript.TopP != topP)
        {
            differences.Add($"top-p {Format(transcript.TopP)} recorded vs {Format(topP)} now");
        }

        return differences.Count == 0
            ? ("matched", null)
            : ("mismatch", "Refused, no model request was made: the source transcript was recorded under different settings (" + string.Join("; ", differences) + ").");
    }

    /// <summary>The absolute-path tuple for each source: transcript.json path, the run folder holding the sample, and the sample name.</summary>
    internal static IReadOnlyList<(string TranscriptPath, string RunFolder, string Sample)> ResolveSources(IEnumerable<string> specs)
    {
        var found = new List<(string TranscriptPath, string RunFolder, string Sample)>();
        foreach (var spec in specs)
        {
            var full = Path.GetFullPath(spec);
            if (File.Exists(full))
            {
                found.Add(DescribeTranscript(full));
                continue;
            }

            if (!Directory.Exists(full))
            {
                throw new FileNotFoundException($"Source '{spec}' is neither a transcript file nor a folder (resolved to '{full}').", full);
            }

            var direct = Path.Combine(full, "transcript.json");
            if (File.Exists(direct))
            {
                found.Add(DescribeTranscript(direct));
                continue;
            }

            var children = Directory.GetDirectories(full, "sample-*")
                .Select(dir => Path.Combine(dir, "transcript.json"))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (children.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Source folder '{full}' has no transcript.json and no sample-NN/transcript.json children. Pass a run folder, a sample folder or a transcript.json path.");
            }

            found.AddRange(children.Select(DescribeTranscript));
        }

        return found;
    }

    private static (string TranscriptPath, string RunFolder, string Sample) DescribeTranscript(string transcriptPath)
    {
        var directory = Path.GetDirectoryName(transcriptPath)!;
        if (string.Equals(Path.GetFileName(transcriptPath), "transcript.json", StringComparison.OrdinalIgnoreCase))
        {
            // <run folder>\sample-NN\transcript.json: the folder holding the transcript is the sample.
            return (transcriptPath, Path.GetDirectoryName(directory) ?? directory, Path.GetFileName(directory));
        }

        return (transcriptPath, directory, Path.GetFileNameWithoutExtension(transcriptPath));
    }

    /// <summary>
    /// Loads the variants named by <paramref name="specs"/> from <paramref name="variantsRoot"/>/&lt;errorCode&gt;/&lt;id&gt;.md:
    /// "&lt;errorCode&gt;/&lt;id&gt;", "&lt;errorCode&gt;/*" for every variant of the folder, or a bare id found in exactly one errorCode folder.
    /// </summary>
    internal static IReadOnlyList<ErrorVariant> ResolveVariants(IEnumerable<string> specs, string variantsRoot)
    {
        var variants = new List<ErrorVariant>();
        foreach (var spec in specs)
        {
            var separator = spec.IndexOfAny(['/', '\\']);
            if (separator < 0)
            {
                if (spec == "*")
                {
                    throw new InvalidOperationException(
                        $"Variant '*' needs an errorCode folder: use '<errorCode>/*' (folders under '{variantsRoot}': {ListFolders(variantsRoot)}).");
                }

                var matches = Directory.Exists(variantsRoot)
                    ? Directory.GetDirectories(variantsRoot)
                        .Select(dir => Path.Combine(dir, spec + ".md"))
                        .Where(File.Exists)
                        .ToList()
                    : [];
                if (matches.Count != 1)
                {
                    throw new InvalidOperationException(
                        matches.Count == 0
                            ? $"Variant id '{spec}' was not found in any errorCode folder under '{variantsRoot}' (folders: {ListFolders(variantsRoot)}). Use '<errorCode>/<id>'."
                            : $"Variant id '{spec}' exists in {matches.Count} errorCode folders; use '<errorCode>/<id>'.");
                }

                AddOnce(variants, ErrorVariantLoader.LoadFile(matches[0]));
                continue;
            }

            var errorCode = spec[..separator];
            var id = spec[(separator + 1)..];
            var folder = Path.Combine(variantsRoot, errorCode);
            if (!Directory.Exists(folder))
            {
                throw new InvalidOperationException(
                    $"Variant spec '{spec}': there is no errorCode folder '{errorCode}' under '{variantsRoot}' (folders: {ListFolders(variantsRoot)}).");
            }

            if (id == "*")
            {
                var files = Directory.GetFiles(folder, "*.md").Order(StringComparer.Ordinal).ToList();
                if (files.Count == 0)
                {
                    throw new InvalidOperationException($"Variant spec '{spec}': folder '{folder}' has no .md variant files.");
                }

                foreach (var file in files)
                {
                    AddOnce(variants, ErrorVariantLoader.LoadFile(file));
                }

                continue;
            }

            var path = Path.Combine(folder, id + ".md");
            if (!File.Exists(path))
            {
                var ids = Directory.GetFiles(folder, "*.md").Select(file => Path.GetFileNameWithoutExtension(file)).Order(StringComparer.Ordinal);
                throw new FileNotFoundException(
                    $"Variant '{id}' not found at '{path}'. Ids in '{errorCode}': {string.Join(", ", ids)}.", path);
            }

            AddOnce(variants, ErrorVariantLoader.LoadFile(path));
        }

        return variants;
    }

    private static void AddOnce(List<ErrorVariant> variants, ErrorVariant variant)
    {
        if (!variants.Any(existing => existing.ErrorCode == variant.ErrorCode && existing.Id == variant.Id))
        {
            variants.Add(variant);
        }
    }

    private static string ListFolders(string variantsRoot) =>
        Directory.Exists(variantsRoot)
            ? string.Join(", ", Directory.GetDirectories(variantsRoot).Select(dir => Path.GetFileName(dir)).Order(StringComparer.Ordinal))
            : "none, the folder does not exist";

    /// <summary>
    /// The fixture directory to put in the variant's {SOLUTION} token, read from the source transcript's prompts;
    /// null when the variant does not use the token.
    /// </summary>
    /// <exception cref="InvalidOperationException">The variant uses the token and no fixture directory is in the prompts.</exception>
    private static string? ResolveSolutionDirectory(AgentTranscript transcript, ErrorVariant variant)
    {
        if (!variant.Message.Contains(ResultOverride.SolutionToken, StringComparison.Ordinal)
            && variant.Trailing?.Contains(ResultOverride.SolutionToken, StringComparison.Ordinal) != true)
        {
            return null;
        }

        var match = FixtureDirectory.Match(transcript.UserPrompt);
        if (!match.Success)
        {
            match = FixtureDirectory.Match(transcript.SystemPrompt);
        }

        return match.Success
            ? match.Value
            : throw new InvalidOperationException(
                $"Variant '{variant.Id}' uses {ResultOverride.SolutionToken} but neither prompt of the source transcript names a RoslynSentinelTests_<guid> fixture directory.");
    }

    /// <summary>LM Studio's native model list for a /v1 base URL, as LmStudioAgentClient derives it.</summary>
    private static string ModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var root = trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed[..^"/v1".Length] : trimmed;
        return root + "/api/v1/models";
    }

    /// <summary>Invariant numeric text, or "default" when nothing was sent.</summary>
    private static string Format(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "default";

    private static long? SumOrNull(IEnumerable<AgentTranscriptTurn> turns, Func<AgentTranscriptTurn, int?> select)
    {
        var reported = turns.Select(select).Where(value => value.HasValue).Select(value => (long)value!.Value).ToList();
        return reported.Count == 0 ? null : reported.Sum();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
}

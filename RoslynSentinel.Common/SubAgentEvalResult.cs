using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Common;

/// <summary>
/// Structured outcome of one SubAgentEval call: did the dispatched model converge, and does what it
/// left in its worktree build and pass tests. Deliberately flat (counts, not the full Build/RunTest
/// payloads) so the result stays small; the full detail is in the transcript at
/// <see cref="TranscriptPath"/>.
/// </summary>
public sealed record SubAgentEvalResult
{
    /// <summary>True only when the model stopped on its own within the caps. Says nothing about whether the task was done correctly.</summary>
    public required bool Converged { get; init; }

    /// <summary>The <see cref="AgentStopReason"/> the loop ended with, as text.</summary>
    public required string StopReason { get; init; }

    public required int TurnCount { get; init; }

    /// <summary>Whether the worktree compiled after the run. False when the build result could not be read (see <see cref="Notes"/>).</summary>
    public required bool BuildSucceeded { get; init; }

    public required int BuildErrorCount { get; init; }

    /// <summary>Zero, with a <see cref="Notes"/> entry, when tests were skipped because the build failed.</summary>
    public required int TestPassedCount { get; init; }

    public required int TestFailedCount { get; init; }

    /// <summary>Every repo-relative path the model modified, deleted or added in its worktree.</summary>
    public required IReadOnlyList<string> TouchedPaths { get; init; }

    /// <summary>The model's last message, when the run did not converge. Null for a clean convergence.</summary>
    public string? FailureExcerpt { get; init; }

    /// <summary>The run's transcript.json, for a follow-up read. It outlives the (deleted) worktree.</summary>
    public required string TranscriptPath { get; init; }

    /// <summary>The tool+error signature the repeated-failure breaker tripped on, when it did.</summary>
    public string? RepeatedFailureSignature { get; init; }

    /// <summary>Caveats about how to read this result (a skipped test run, an unreadable build result).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Longest <see cref="FailureExcerpt"/> kept.</summary>
    public const int MaxFailureExcerptChars = 1500;

    /// <summary>
    /// Assembles the result from the agent run and the child server's post-run snapshots.
    /// </summary>
    /// <param name="run">The finished agent run.</param>
    /// <param name="buildResultJson">Raw text of the child server's <c>Build</c> tool result, or null if it was not obtained.</param>
    /// <param name="testResultJson">Raw text of the child server's <c>RunTest</c> tool result, or null when tests were not run.</param>
    /// <param name="touchedPaths">The worktree's dirty paths (<c>GitWorktreeManager.GetDirtyPaths</c>).</param>
    public static SubAgentEvalResult Build(
        AgentRunResult run, string? buildResultJson, string? testResultJson, IReadOnlyList<string> touchedPaths)
    {
        var notes = new List<string>();

        var buildSucceeded = false;
        var buildErrorCount = 0;
        if (buildResultJson is null)
        {
            notes.Add("The post-run build was not obtained from the child server; BuildSucceeded is false by default.");
        }
        else if (TryReadSuccessData(buildResultJson, out var buildData))
        {
            var outcome = ReadString(buildData, "outcome");
            buildSucceeded = string.Equals(outcome, "Succeeded", StringComparison.OrdinalIgnoreCase);
            buildErrorCount = ReadInt(buildData, "errorCount") ?? (buildSucceeded ? 0 : 1);
        }
        else
        {
            notes.Add(
                "The post-run build result could not be read (it was an error or was too large to return inline); " +
                "BuildSucceeded is false by default. See the child server's agent.log beside the transcript.");
        }

        var testsPassed = 0;
        var testsFailed = 0;
        if (testResultJson is null)
        {
            notes.Add("Tests were not run" + (buildSucceeded ? "." : " because the build did not succeed."));
        }
        else if (TryReadSuccessData(testResultJson, out var testData))
        {
            testsPassed = ReadInt(testData, "passedCount") ?? 0;
            testsFailed = ReadInt(testData, "failedCount") ?? 0;
        }
        else
        {
            notes.Add("The post-run test result could not be read (it was an error or timed out); test counts are zero.");
        }

        string? failureExcerpt = null;
        if (!run.Converged)
        {
            var lastContent = run.Transcript.Turns.Count > 0
                ? run.Transcript.Turns[^1].ModelMessage.Content ?? ""
                : "";
            failureExcerpt = lastContent.Length <= MaxFailureExcerptChars
                ? lastContent
                : lastContent[..MaxFailureExcerptChars] + "...";
        }

        return new SubAgentEvalResult
        {
            Converged = run.Converged,
            StopReason = run.StopReason.ToString(),
            TurnCount = run.TurnCount,
            BuildSucceeded = buildSucceeded,
            BuildErrorCount = buildErrorCount,
            TestPassedCount = testsPassed,
            TestFailedCount = testsFailed,
            TouchedPaths = touchedPaths,
            FailureExcerpt = failureExcerpt,
            TranscriptPath = run.TranscriptPath,
            RepeatedFailureSignature = run.RepeatedFailure?.Signature,
            Notes = notes,
        };
    }

    /// <summary>
    /// A child tool result over the inline-size threshold (a full-solution Build or RunTest easily
    /// is) arrives as a pointer, not data: either <c>largeResult.filePath</c> (a typed result) or
    /// <c>offloaded</c> + <c>resultId</c> (the generic backstop). The child wrote the payload to a
    /// <c>.roslynsentinel/largeresults</c> file inside its own worktree; this reads that file and
    /// returns an envelope text with an inline <c>successData</c>, so <see cref="Build"/> can parse it
    /// exactly as it parses an inline result. Returns <paramref name="toolText"/> unchanged when it is
    /// not a pointer, or when the file is missing, unreadable, or outside <paramref name="allowedRoot"/>
    /// (the child worktree) - the caller then reports the result as unreadable rather than guessing.
    /// </summary>
    /// <param name="toolText">Raw text of the child tool result, or null.</param>
    /// <param name="allowedRoot">The child worktree; a pointer to any path outside it is not followed.</param>
    public static string? ResolveOffloadedResult(string? toolText, string allowedRoot)
    {
        if (toolText is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(toolText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return toolText;
            }

            string? file = null;
            if (TryGetPropertyIgnoreCase(root, "largeResult", out var large) && large.ValueKind == JsonValueKind.Object)
            {
                file = ReadString(large, "filePath");
            }
            else if (TryGetPropertyIgnoreCase(root, "offloaded", out var offloaded) && offloaded.ValueKind == JsonValueKind.True)
            {
                var resultId = ReadString(root, "resultId");
                if (resultId is not null && resultId.All(char.IsAsciiLetterOrDigit))
                {
                    var directory = Path.Combine(allowedRoot, ".roslynsentinel", "largeresults");
                    if (Directory.Exists(directory))
                    {
                        file = Directory.EnumerateFiles(directory, $"largeresult_*_{resultId}.json").FirstOrDefault();
                    }
                }
            }

            if (file is null)
            {
                return toolText;
            }

            var fullPath = Path.GetFullPath(file);
            var rootPrefix = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                return toolText;
            }

            using var fileDocument = JsonDocument.Parse(File.ReadAllText(fullPath));
            if (!TryGetPropertyIgnoreCase(fileDocument.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return toolText;
            }

            // The generic backstop stores the whole envelope; a typed large result stores the payload alone.
            return TryGetPropertyIgnoreCase(data, "successData", out _)
                ? data.GetRawText()
                : "{\"successData\":" + data.GetRawText() + "}";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return toolText;
        }
    }

    /// <summary>
    /// Finds the envelope's inline <c>successData</c>. False for a non-envelope, an envelope with no
    /// inline data (an offloaded <c>largeResult</c>), or an envelope reporting failure with no data.
    /// </summary>
    private static bool TryReadSuccessData(string envelopeJson, out JsonElement successData)
    {
        successData = default;
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(document.RootElement, "successData", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                // The document is disposed on return; clone so the caller can keep reading it.
                successData = data.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON at all; falls through to false.
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        TryGetPropertyIgnoreCase(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

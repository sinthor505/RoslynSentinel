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

using System.Text.Json;

using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Pure gates for a resume scenario (types 2 and 4): whether the live server still offers the tools the kept turns
/// called, and whether replaying those turns still matches the recording. No I/O.
/// </summary>
public static class ResumeScenarioGate
{
    /// <summary>
    /// Compares only the tools called in <paramref name="keptTurns"/> against the schemas the transcript recorded.
    /// A non-empty InconclusiveReason marks the sample Inconclusive; SchemaDrift (description-only change) goes to the
    /// CSV column and does not affect the verdict.
    /// </summary>
    public static (string InconclusiveReason, bool SchemaDrift) CheckSchemas(
        ResumeSource source,
        IReadOnlyList<AgentTranscriptTurn> keptTurns,
        IReadOnlyDictionary<string, JsonElement> liveSchemas,
        IReadOnlyDictionary<string, string> liveDescriptions)
    {
        var calledTools = keptTurns
            .SelectMany(turn => turn.ToolCalls)
            .Select(call => call.ToolName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var (kind, detail) = ToolSchemaComparer.Compare(
            calledTools,
            source.ToolSchemas,
            source.ToolDescriptions,
            liveSchemas,
            liveDescriptions,
            source.CommitHash);

        return kind switch
        {
            SchemaDifferenceKind.Structural => ("SchemaChanged: " + detail, false),
            SchemaDifferenceKind.Missing => ("SchemaNotRecorded: " + detail, false),
            SchemaDifferenceKind.DescriptionOnly => ("", true),
            _ => ("", false),
        };
    }

    /// <summary>
    /// A non-empty InconclusiveReason (an IsError mismatch during replay) marks the sample Inconclusive. ResultDrift
    /// counts replayed results whose wording differs from the recording and goes to the CSV column.
    /// </summary>
    public static (string InconclusiveReason, int ResultDrift) CheckReplay(IReadOnlyList<ReplayedCall> replayed)
    {
        return (TranscriptResume.ReplayMismatchReason(replayed) ?? "", TranscriptResume.CountResultDrift(replayed));
    }
}

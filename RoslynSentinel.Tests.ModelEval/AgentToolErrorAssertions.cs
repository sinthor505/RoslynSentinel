using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Coarse class of one failed tool call; see <see cref="AgentToolErrorAssertions.ClassifyError"/>.</summary>
public enum ToolErrorClass
{
    /// <summary>The call was not an error.</summary>
    None,
    /// <summary>A compile-gate rejection whose next step names the right tool.</summary>
    GuardrailRedirect,
    /// <summary>A compile-gate rejection that is not a guardrail redirect.</summary>
    CompileGateRejection,
    /// <summary>The target was not found.</summary>
    NotFound,
    /// <summary>The arguments were rejected as invalid.</summary>
    InvalidArgument,
    /// <summary>The tool raised an exception.</summary>
    Exception,
    /// <summary>An error code not covered by the classes above.</summary>
    OtherCode,
    /// <summary>An error envelope that carries no error code.</summary>
    NoCode,
}

/// <summary>
/// Shared by every agent test that caps failed tool calls (<see cref="WholeFileRewriteAgentTests"/>
/// via its shared AssertFixApplied, <see cref="PlanImplementVerifyAgentTests"/>,
/// <see cref="PlanThenExecuteAgentTests"/>). A flat total-error-count cap can't distinguish a model
/// that tried 3 different tools once each while exploring (probably fine) from one that hit the
/// same tool 3 times in a row (thrashing -> see the CS0103/using-directive retry loop documented in
/// docs/current/project_directive_error_messages_wiggle_room_theory.md, 6 failed calls all on the
/// same root cause). Asserting per-tool, not just in total, makes that distinction visible directly
/// in the failure message instead of requiring a manual transcript read.
/// </summary>
public static class AgentToolErrorAssertions
{
    /// <param name="ByTool">Failed calls per tool, excluding guardrail redirects.</param>
    /// <param name="GuardrailRedirects">Failed calls that were a compile-gate redirect to the right tool.</param>
    public sealed record ToolErrorSummary(int TotalErrors, IReadOnlyList<(string ToolName, int Count)> ByTool, int GuardrailRedirects = 0)
    {
        public int MaxPerTool => ByTool.Count == 0 ? 0 : ByTool.Max(t => t.Count);

        public override string ToString() =>
            (ByTool.Count == 0 ? "none" : string.Join(", ", ByTool.Select(t => $"{t.ToolName}={t.Count}")))
            + (GuardrailRedirects > 0 ? $"; guardrail redirects (not in per-tool counts)={GuardrailRedirects}" : "");
    }

    // A compile-gate rejection whose next step names the right tool (EditImpactAnalyzer.NextStepFor, e.g.
    // "This edit renames X to Y and 1 file(s) still use X: use RenameSymbol(...)") is the guardrail working:
    // the model was redirected, not thrashing. Run 108 of the 2026-11-10 batch did all ten steps and failed
    // only because such redirects counted per tool. They still count toward the total.
    private static readonly System.Text.RegularExpressions.Regex GuardrailRedirect = new(
        @"This edit (renames|changes the signature of|makes|removes) .+? and \d+ file\(s\) still use",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static ToolErrorSummary Summarize(AgentRunResult result)
    {
        var errorTools = result.Transcript.Turns.SelectMany(t => t.ToolCalls).Where(tc => tc.IsError).ToList();
        var redirects = errorTools.Count(tc => tc.ResultJson is { } r && GuardrailRedirect.IsMatch(r));
        var byTool = errorTools
            .Where(tc => !(tc.ResultJson is { } r && GuardrailRedirect.IsMatch(r)))
            .GroupBy(tc => tc.ToolName)
            .Select(g => (ToolName: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();
        return new ToolErrorSummary(errorTools.Count, byTool, redirects);
    }

    // TODO ModelAgentRunner.TryReadStringProperty (private) walks to depth 3 and prefers a top-level errorCode, so it can pick up an errorCode typed inside echoed call arguments; ErrorCodeOf reads only errorData.errorCode then a top-level errorCode, on purpose. Merging them needs a decision on which depth/precedence contract wins (the runner's failure-signature use may want ErrorCodeOf's stricter read).
    /// <summary>
    /// Reads the error code nested under errorData, the same value ModelAgentRunner.BuildFailureSignature keys on.
    /// Falls back to a top-level errorCode only; never walks deeper, because the envelope echoes the call arguments.
    /// Returns null when the call was not an error, the result is blank or unparseable, or it carries no code.
    /// </summary>
    public static string? ErrorCodeOf(AgentToolCallRecord call)
    {
        if (!call.IsError || string.IsNullOrWhiteSpace(call.ResultJson))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(call.ResultJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("errorData", out var errorData) && errorData.ValueKind == System.Text.Json.JsonValueKind.Object
                && errorData.TryGetProperty("errorCode", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return nested.GetString();
            }

            if (root.TryGetProperty("errorCode", out var topLevel) && topLevel.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return topLevel.GetString();
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classifies one failed call by its error code. A ValidationFailed whose text matches
    /// <see cref="GuardrailRedirect"/> is a GuardrailRedirect (the redirect refines the code's class), any other
    /// ValidationFailed is a CompileGateRejection, and an IsError call with no code is NoCode.
    /// </summary>
    public static ToolErrorClass ClassifyError(AgentToolCallRecord call)
    {
        if (!call.IsError)
        {
            return ToolErrorClass.None;
        }

        var code = ErrorCodeOf(call);
        return code switch
        {
            null => ToolErrorClass.NoCode,
            "ValidationFailed" when call.ResultJson is { } r && GuardrailRedirect.IsMatch(r) => ToolErrorClass.GuardrailRedirect,
            "ValidationFailed" => ToolErrorClass.CompileGateRejection,
            "NotFound" => ToolErrorClass.NotFound,
            "InvalidArgument" => ToolErrorClass.InvalidArgument,
            "Exception" => ToolErrorClass.Exception,
            _ => ToolErrorClass.OtherCode,
        };
    }

    /// <summary>
    /// Asserts both a total cap (catches broad thrashing across many tools) and a per-tool cap
    /// (catches repeated failure on one tool -> the thrashing signature a flat total misses). Both
    /// default to 2, matching the "one mistake plus one guided self-correction retry" budget
    /// already established at every call site this replaces.
    /// </summary>
    public static void AssertWithinBudget(AgentRunResult result, int maxTotal = 2, int maxPerTool = 2)
    {
        var summary = Summarize(result);
        Assert.That(summary.TotalErrors, Is.LessThanOrEqualTo(maxTotal),
            $"Expected at most {maxTotal} failed tool calls total; {summary.TotalErrors} occurred " +
            $"(by tool: {summary}). Transcript: {result.TranscriptPath}");
        Assert.That(summary.MaxPerTool, Is.LessThanOrEqualTo(maxPerTool),
            $"Expected at most {maxPerTool} failed calls to any single tool (more suggests thrashing " +
            $"on one root cause rather than a self-correction retry); by tool: {summary}. " +
            $"Transcript: {result.TranscriptPath}");
    }
}

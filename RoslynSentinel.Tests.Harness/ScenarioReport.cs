namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Parses the Report block of a final message: lines after the DONE:/BLOCKED: line, form "label: value".</summary>
public static class ScenarioReport
{
    /// <summary>Label to value (labels case-insensitive, first occurrence wins); empty when there is no report.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? finalMessage)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }
}

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// One scenario task as authoring input to a prompt renderer. Renders in the fixed shape
/// Goal / Files / Conditions / If a condition fails / Done when / Report.
/// </summary>
public sealed record ScenarioTask(string Goal, string Files, string IfFails, string DoneWhen, string? Conditions = null, string? Report = null)
{
    /// <summary>Renders the task in the fixed shape Goal/Files/Conditions/If a condition fails/Done when/Report.</summary>
    public string Render(bool repeatEndings = false)
    {
        static string Normalise(string value) => value.Replace("\r\n", "\n").Trim();

        static string Required(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{name} is required and must not be blank.", name);
            }

            return Normalise(value);
        }

        var lines = new List<string>
{
    $"Goal: {Required(Goal, nameof(Goal))}",
    $"Files: {Required(Files, nameof(Files))}",
};
        if (!string.IsNullOrWhiteSpace(Conditions))
        {
            lines.Add($"Conditions: {Normalise(Conditions)}");
        }

        lines.Add($"If a condition fails: {Required(IfFails, nameof(IfFails))}");
        lines.Add($"Done when: {Required(DoneWhen, nameof(DoneWhen))}");
        if (!string.IsNullOrWhiteSpace(Report))
        {
            lines.Add($"Report: {Normalise(Report)}");
        }

        var text = string.Join("\n", lines);
        if (!repeatEndings)
        {
            return text;
        }

        return text + "\n\n" + AgentSystemPrompts.CodingAgentContractEndings.Replace("\r\n", "\n").Trim();
    }
}

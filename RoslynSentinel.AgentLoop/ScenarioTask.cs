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
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}

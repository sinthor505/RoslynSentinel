using System;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

// TODO error-policy arm D: temporary A/B switch; remove after Step 11c (ship the line in CodingAgent if D wins)
/// <summary>
/// ModelEval-only. When ROSLYNSENTINEL_EVAL_EXIT_LINE=1 the line is appended to the CodingAgent system prompt of scenario runs;
/// the saved system prompt in each transcript carries the line, and the stamp suffix marks the errorPolicyMode column.
/// </summary>
public static class EvalExitLine
{
    public const string EnvironmentVariable = "ROSLYNSENTINEL_EVAL_EXIT_LINE";

    public const string Line = "A step may not be doable as written (a named file or symbol may not exist). Reporting that is a valid outcome: begin your reply with `BLOCKED:` and name what is missing.";

    public const string StampSuffix = "+exitline";

    public static bool IsEnabled() => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    public static string Apply(string prompt, bool enabled)
    {
        if (!enabled)
        {
            return prompt;
        }

        return prompt.TrimEnd() + "\n\n" + Line;
    }

    public static string ComposeScenarioPrompt(string codingAgentPrompt) =>
        Apply(ErrorPolicyWiring.ComposePrompt(codingAgentPrompt), IsEnabled());

    public static string StampMode(string errorPolicyMode, bool enabled) =>
        enabled ? errorPolicyMode + StampSuffix : errorPolicyMode;
}

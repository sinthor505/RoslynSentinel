using RoslynSentinel.Common;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Applies the startup error-policy options (<see cref="ErrorPolicyOptions"/>) to the coding-agent prompt and to
/// the child server arguments that PlanStepRunner forwards. The explicit-argument overloads let tests avoid the
/// static startup state.
/// </summary>
public static class ErrorPolicyWiring
{
    /// <summary>Composes <paramref name="prompt"/> with the policy named by the startup options.</summary>
    public static string ComposePrompt(string prompt) =>
        ComposePrompt(prompt, ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath);

    /// <summary>
    /// Returns <paramref name="prompt"/> unchanged when no policy file is set and the mode is Off. Mode Pointer or
    /// Inline without a policy file throws. A policy file set in any mode, including Off (the prompt block is the
    /// baseline arm), appends the "## Error handling" block read from that file.
    /// </summary>
    public static string ComposePrompt(string prompt, ErrorPolicyMode mode, string? policyFilePath)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(policyFilePath))
        {
            if (mode == ErrorPolicyMode.Off)
            {
                return prompt;
            }

            // hint-ok: configuration message, not a tool-call hint
            throw new InvalidOperationException(
                $"Error-policy mode {mode} needs a policy file: set --error-policy-file or ROSLYNSENTINEL_ERROR_POLICY_FILE, or set the mode to Off.");
        }

        return AgentSystemPrompts.WithErrorHandling(prompt, ErrorPolicyDocument.Load(policyFilePath));
    }

    /// <summary>Child-server arguments for the startup error-policy options.</summary>
    public static IReadOnlyList<string> ChildServerArgs() =>
        ChildServerArgs(ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath);

    /// <summary>
    /// Arguments that forward the policy to a child server: <c>--error-policy-mode=</c> (lowercase) when the mode is
    /// not Off, then <c>--error-policy-file=</c> when the path is non-blank. Empty when neither applies.
    /// </summary>
    public static IReadOnlyList<string> ChildServerArgs(ErrorPolicyMode mode, string? policyFilePath)
    {
        var args = new List<string>();
        if (mode != ErrorPolicyMode.Off)
        {
            args.Add("--error-policy-mode=" + mode.ToString().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(policyFilePath))
        {
            args.Add("--error-policy-file=" + policyFilePath);
        }

        return args;
    }
}

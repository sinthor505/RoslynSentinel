using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>The edit arms of the edit-tool comparison; see <see cref="EditArmToolSurface"/>.</summary>
public enum EditArm
{
    /// <summary>Variable unset: the unchanged surface, offering both ReplaceSnippet and ReplaceRange.</summary>
    Default,

    /// <summary>The model sees ReplaceSnippet as its only edit tool.</summary>
    ReplaceSnippet,

    /// <summary>The model sees ReplaceRange as its only edit tool.</summary>
    ReplaceRange,

    /// <summary>The model sees EditSnippets (batch-only) as its only edit tool.</summary>
    EditSnippets,
}

/// <summary>
/// The shared edit-arm switch for the model-eval fixtures that compare edit tools (todo-punchlist #12). The environment
/// variable <see cref="EnvVar"/> selects one of three arms in which the model sees exactly one edit tool
/// (ReplaceSnippet, ReplaceRange or EditSnippets); unset keeps the surface unchanged, so every other run is unaffected.
/// An unknown value throws, so a typo cannot silently run the wrong arm.
/// </summary>
public static class EditArmToolSurface
{
    /// <summary>Environment variable that selects the edit arm.</summary>
    public const string EnvVar = "ROSLYNSENTINEL_MODELEVAL_EDIT_ARM";

    private const string EditSnippetsMode = "EditSnippetsExperiment";

    /// <summary>Unset, empty or whitespace gives <see cref="EditArm.Default"/>; the three arm names (trimmed, case-insensitive) give that arm; anything else throws.</summary>
    public static EditArm Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EditArm.Default;
        }

        var trimmed = value.Trim();
        foreach (var arm in new[] { EditArm.ReplaceSnippet, EditArm.ReplaceRange, EditArm.EditSnippets })
        {
            if (string.Equals(trimmed, arm.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return arm;
            }
        }

        throw new ArgumentException(
            $"Unknown {EnvVar} value '{value}'. Valid values: ReplaceSnippet, ReplaceRange, EditSnippets (or leave it unset for the default surface).",
            nameof(value));
    }

    /// <summary>The tool names the arm adds to the hide set. Fresh set per call.</summary>
    public static HashSet<string> ArmHiddenTools(EditArm arm) =>
        arm switch
        {
            EditArm.ReplaceSnippet => new HashSet<string>(["ReplaceRange"], StringComparer.OrdinalIgnoreCase),
            EditArm.ReplaceRange => new HashSet<string>(["ReplaceSnippet"], StringComparer.OrdinalIgnoreCase),
            EditArm.EditSnippets => new HashSet<string>(["ReplaceSnippet", "ReplaceRange"], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>The Advanced-surface modes for the arm: the chain modes, plus the EditSnippets experiment mode for that arm.</summary>
    public static HashSet<string> NewAdvancedModes(EditArm arm)
    {
        var modes = ChainEvalToolSurface.NewModes();
        if (arm == EditArm.EditSnippets)
        {
            modes.Add(EditSnippetsMode);
        }

        return modes;
    }

    /// <summary>
    /// The Advanced-surface hide set for the arm and an explicit toolset value (no environment read, so tests do not
    /// mutate the environment). Under lean, EditSnippets is not on the allow list, so the EditSnippets arm passes it as
    /// extraAllowed.
    /// </summary>
    public static HashSet<string> NewAdvancedHiddenTools(EditArm arm, string? toolsetValue)
    {
        var hidden = ChainEvalToolSurface.NewHiddenTools(toolsetValue, arm == EditArm.EditSnippets ? ["EditSnippets"] : null);
        hidden.UnionWith(ArmHiddenTools(arm));
        return hidden;
    }

    /// <summary>
    /// The Advanced tool registration for a <c>configureToolSurface</c> callback: reads <see cref="EnvVar"/> and
    /// <see cref="ChainEvalToolSurface.ToolsetEnvVar"/>, logs the arm, and registers the surface. With the Default arm the
    /// result equals the default registration of <c>ModelEvalMcpEnvironment</c>.
    /// </summary>
    public static void RegisterAdvanced(IMcpServerBuilder mcpBuilder, IServiceCollection services)
    {
        var arm = Parse(Environment.GetEnvironmentVariable(EnvVar));
        LogArm(arm);

        // NewHiddenTools(extraAllowed) reads ChainEvalToolSurface.ToolsetEnvVar and logs the "Tool surface:" line.
        var hidden = ChainEvalToolSurface.NewHiddenTools(arm == EditArm.EditSnippets ? ["EditSnippets"] : null);
        hidden.UnionWith(ArmHiddenTools(arm));
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, NewAdvancedModes(arm), hideTools: hidden);
    }

    /// <summary>The Basic-surface modes for the arm: the Basic modes, plus the EditSnippets experiment mode for that arm.</summary>
    public static HashSet<string> NewBasicModes(EditArm arm)
    {
        var modes = ModelEvalToolSurfaces.NewBasicModes();
        if (arm == EditArm.EditSnippets)
        {
            modes.Add(EditSnippetsMode);
        }

        return modes;
    }

    /// <summary>
    /// The Basic tool registration for a <c>configureToolSurface</c> callback: reads <see cref="EnvVar"/>, logs the arm, and
    /// registers the surface. With the Default arm the result equals <see cref="ModelEvalToolSurfaces.Basic"/>. The Basic
    /// surface registers both ReplaceRange (WorkspaceTools, mode Workspace) and EditSnippets (mode EditSnippetsExperiment),
    /// so all three arms are supported.
    /// </summary>
    public static void RegisterBasic(IMcpServerBuilder mcpBuilder, IServiceCollection services)
    {
        var arm = Parse(Environment.GetEnvironmentVariable(EnvVar));
        LogArm(arm);

        if (arm == EditArm.Default)
        {
            ModelEvalToolSurfaces.Basic(mcpBuilder, services);
            return;
        }

        mcpBuilder.AddRoslynSentinelToolsBasic(services, NewBasicModes(arm), hideTools: ArmHiddenTools(arm));
    }

    private static void LogArm(EditArm arm) =>
        TestContext.Out.WriteLine(
            arm == EditArm.Default
                ? "Edit arm: Default (lean surface unchanged)"
                : $"Edit arm: {arm}");
}

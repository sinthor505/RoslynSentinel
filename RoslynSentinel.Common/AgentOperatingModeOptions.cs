namespace RoslynSentinel.Common;

/// <summary>
/// Operating mode of the agent workflow. Not the same as <see cref="OperatingMode"/> (Production/Testing, from --testing).
/// </summary>
public enum AgentOperatingMode
{
    /// <summary>Decide from the first tool call of the process, then never change.</summary>
    Auto,

    /// <summary>The scoped operation ledger is available, and one agent is expected to edit.</summary>
    SingleAgent,

    /// <summary>Several agents edit at once; multi-step edits through the scoped operation ledger are refused.</summary>
    MultiAgent,
}

/// <summary>
/// Startup switch for the operating mode: <c>--operating-mode=auto|single-agent|multi-agent</c> first, then the
/// <c>ROSLYNSENTINEL_OPERATING_MODE</c> environment variable. Default is <see cref="AgentOperatingMode.Auto"/>.
/// The mode must not change mid-session, so auto latches on the first tool call and is never re-evaluated.
/// Static, so tests must reset it with <see cref="Configure"/>.
/// </summary>
[StartupSettingGroup("Agent operating mode", PostConfigure = "ResetLatch")]
public static class AgentOperatingModeOptions
{
    public static readonly StartupSetting<AgentOperatingMode> ConfiguredSetting = StartupSetting.Enum<AgentOperatingMode>("--operating-mode", "ROSLYNSENTINEL_OPERATING_MODE", AgentOperatingMode.Auto, "Agent operating mode: auto, single-agent or multi-agent.", SettingScope.Server);

    private const int Unresolved = 0;
    private const int LatchedSingleAgent = 1;
    private const int LatchedMultiAgent = 2;

    private static int _latched;

    public static AgentOperatingMode Configured => ConfiguredSetting.Value;

    /// <summary>The configured mode when it is not Auto; otherwise the latched mode, or Auto when not latched yet.</summary>
    public static AgentOperatingMode Effective
    {
        get
        {
            if (Configured != AgentOperatingMode.Auto)
            {
                return Configured;
            }

            return Volatile.Read(ref _latched) switch
            {
                LatchedSingleAgent => AgentOperatingMode.SingleAgent,
                LatchedMultiAgent => AgentOperatingMode.MultiAgent,
                _ => AgentOperatingMode.Auto,
            };
        }
    }

    /// <summary>Human-readable origin of <see cref="Effective"/>.</summary>
    public static string Source
    {
        get
        {
            if (Configured != AgentOperatingMode.Auto)
            {
                return "startup arg or env var";
            }

            return Volatile.Read(ref _latched) switch
            {
                LatchedMultiAgent => "auto: first call carried an agent id",
                LatchedSingleAgent => "auto: first call had no agent id",
                _ => "auto: no tool call yet",
            };
        }
    }

    /// <summary>
    /// Resolves the mode on the first tool call. A configured mode is returned without touching the latch; otherwise the
    /// first caller latches the mode and concurrent first calls all get the winner's value.
    /// </summary>
    public static AgentOperatingMode ResolveOnce(bool hasAgentId)
    {
        if (Configured != AgentOperatingMode.Auto)
        {
            return Configured;
        }

        Interlocked.CompareExchange(ref _latched, hasAgentId ? LatchedMultiAgent : LatchedSingleAgent, Unresolved);
        return Effective;
    }

    /// <summary>Call once at process startup; also resets the latch, which is how tests reset.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(AgentOperatingModeOptions), args);
    }

    /// <summary>"single-agent" or "multi-agent" (case-insensitive, whitespace-trimmed) map to the matching mode; "auto", null, empty or anything else maps to Auto and never throws (the registry reports an unrecognised value as invalid).</summary>
    public static AgentOperatingMode ParseMode(string? raw)
    {
        return ConfiguredSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }

    private static IReadOnlyList<ConfigurationProblem> ResetLatch()
    {
        Volatile.Write(ref _latched, Unresolved);
        return Array.Empty<ConfigurationProblem>();
    }
}

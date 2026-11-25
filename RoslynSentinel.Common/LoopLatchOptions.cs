namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch and constants for the server loop latch (<see cref="ToolLoopLatch"/>; proposal_loop_latch.md).
/// Populated once at startup via <see cref="Configure"/>: <c>--loop-latch</c> first, then the
/// <c>ROSLYNSENTINEL_LOOP_LATCH</c> environment variable. Off by default: with the flag off the latch filter is a pure
/// pass-through and the server behaves exactly as before. The bare flag (<c>--loop-latch</c>) and <c>--loop-latch=true</c>
/// or the literals <c>1</c>, <c>on</c>, <c>yes</c> turn it on; <c>false</c>, <c>0</c>, <c>off</c> and <c>no</c> leave it off.
/// Any other value is reported as an invalid setting and leaves it off.
/// </summary>
[StartupSettingGroup("Loop latch")]
public static class LoopLatchOptions
{
    public static readonly StartupSetting<bool> EnabledSetting = StartupSetting.Bool("--loop-latch", "ROSLYNSENTINEL_LOOP_LATCH", false, "Turns on the server loop latch that refuses repeated failing tool calls.", SettingScope.Server);
    /// <summary>The same tool failing the same way this many times, with no successful call of a different tool in between, latches it.</summary>
    public const int TripThreshold = 2;

    /// <summary>
    /// Harness side: after the first latch refusal of a tool, this many further calls of the same tool (each refused too) end the run
    /// with <c>AgentStopReason.LoopLatchIgnored</c>.
    /// </summary>
    public const int IgnoredRefusalLimit = 2;

    public static bool Enabled => EnabledSetting.Value;

    /// <summary>Parses --loop-latch (falling back to ROSLYNSENTINEL_LOOP_LATCH) into <see cref="Enabled"/>. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(LoopLatchOptions), args);
    }
}

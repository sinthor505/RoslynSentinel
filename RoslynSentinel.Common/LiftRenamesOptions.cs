namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for experimental rename lifting (proposal_semantic_edit_impact_diagnostics.md, "Second slice", step 1).
/// Populated once at startup via <see cref="Configure"/>: <c>--lift-renames</c> first, then the
/// <c>ROSLYNSENTINEL_LIFT_RENAMES</c> environment variable. Off by default: with the flag off a rejected ReplaceSnippet edit
/// behaves exactly as before (the first-slice redirect). The bare flag and <c>--lift-renames=true</c>, or the literals
/// <c>1</c>, <c>on</c>, <c>yes</c>, turn it on; <c>false</c>, <c>0</c>, <c>off</c> and <c>no</c> leave it off.
/// Any other value is reported as an invalid setting and leaves it off.
/// </summary>
[StartupSettingGroup("Rename lifting")]
public static class LiftRenamesOptions
{
    public static readonly StartupSetting<bool> EnabledSetting = StartupSetting.Bool("--lift-renames", "ROSLYNSENTINEL_LIFT_RENAMES", false, "Lifts rejected rename edits instead of redirecting to the first-slice path.", SettingScope.Server);

    // Deliberate deviation from the other *Options classes (plain static, set once at startup): the fixture tests need the flag
    // on in one test and off in the next without a process restart, and an AsyncLocal override keeps that safe under parallel
    // test runs, where a settable static would leak between fixtures.
    private static readonly AsyncLocal<bool?> Override = new();

    public static bool Enabled => Override.Value ?? EnabledSetting.Value;

    /// <summary>Parses --lift-renames (falling back to ROSLYNSENTINEL_LIFT_RENAMES) into <see cref="Enabled"/>. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(LiftRenamesOptions), args);
    }

    /// <summary>Test hook: forces <see cref="Enabled"/> for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable OverrideForCurrentContext(bool enabled)
    {
        var previous = Override.Value;
        Override.Value = enabled;
        return new OverrideScope(previous);
    }

    private sealed class OverrideScope(bool? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}

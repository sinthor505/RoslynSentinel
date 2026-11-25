namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the symbol member inventory of plan_error_policy_pointer (Step 11g, arm I): when on, a no-candidates symbol-not-found text for a current workspace
/// ends with the member names of the container the symbol was looked up in. The file-miss folder list is NOT behind this switch (it is always on).
/// Populated once at startup via <see cref="Configure"/>: <c>--notfound-inventory</c> first, then the <c>ROSLYNSENTINEL_NOTFOUND_INVENTORY</c> environment variable.
/// On by default: the bool literals true,false,1,0,on,off,yes,no (case-insensitive) are accepted; an unrecognised value keeps it on and is reported as an invalid setting.
/// </summary>
[StartupSettingGroup("Not-found inventory")]
public static class NotFoundInventoryOptions
{
    public static readonly StartupSetting<bool> EnabledSetting = StartupSetting.Bool("--notfound-inventory", EnvironmentVariable, true, "Append the container member names to symbol-not-found text.", SettingScope.Server);

    public const string EnvironmentVariable = "ROSLYNSENTINEL_NOTFOUND_INVENTORY";

    /// <summary>The suffix the ModelEval harness adds to the errorPolicyMode stamp when the switch is on.</summary>
    public const string StampSuffix = "+inventory";

    // Same deviation as LiftRenamesOptions: tests need the switch on in one test and off in the next without a process restart, and an
    // AsyncLocal override stays safe under parallel test runs, where a settable static would leak between fixtures.
    private static readonly AsyncLocal<bool?> Override = new();

    public static bool Enabled => Override.Value ?? EnabledSetting.Value;

    /// <summary>Parses --notfound-inventory (falling back to ROSLYNSENTINEL_NOTFOUND_INVENTORY) into <see cref="Enabled"/>. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(NotFoundInventoryOptions), args);
    }

    /// <summary>Unset or blank is on. The bool literals true,false,1,0,on,off,yes,no (case-insensitive) are accepted. An unrecognised value is on (the default) and is also reported as an invalid setting.</summary>
    public static bool ParseEnabled(string? raw)
    {
        return EnabledSetting.Read(StartupArgs.Empty, _ => raw).Value;
    }

    /// <summary>Test hook: forces <see cref="Enabled"/> for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable OverrideForCurrentContext(bool enabled)
    {
        var previous = Override.Value;
        Override.Value = enabled;
        return new OverrideScope(previous);
    }

    /// <summary>The errorPolicyMode stamp with the switch suffix when <paramref name="enabled"/>.</summary>
    public static string StampMode(string errorPolicyMode, bool enabled) =>
        enabled ? errorPolicyMode + StampSuffix : errorPolicyMode;

    private sealed class OverrideScope(bool? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}

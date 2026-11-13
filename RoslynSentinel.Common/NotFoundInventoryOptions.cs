namespace RoslynSentinel.Common;

// TODO plan_error_policy_pointer arm I: temporary A/B switch; resolve at plan close
/// <summary>
/// Startup switch for arm I of plan_error_policy_pointer (Step 11g): when on, a no-candidates not-found text for a current workspace
/// ends with a scoped inventory (the files of the folder a missing path names; the members of the container a missing symbol was
/// looked up in). Populated once at startup via <see cref="Configure"/>: <c>--notfound-inventory</c> first, then the
/// <c>ROSLYNSENTINEL_NOTFOUND_INVENTORY</c> environment variable. Only "1" and "true" (case-insensitive, trimmed) turn it on; anything
/// else, and unset, leaves it off, and the off text is exactly the arm H text.
/// </summary>
public static class NotFoundInventoryOptions
{
    public const string EnvironmentVariable = "ROSLYNSENTINEL_NOTFOUND_INVENTORY";

    /// <summary>The suffix the ModelEval harness adds to the errorPolicyMode stamp when the switch is on.</summary>
    public const string StampSuffix = "+inventory";

    private static bool _enabled;

    // Same deviation as LiftRenamesOptions: tests need the switch on in one test and off in the next without a process restart, and an
    // AsyncLocal override stays safe under parallel test runs, where a settable static would leak between fixtures.
    private static readonly AsyncLocal<bool?> Override = new();

    public static bool Enabled => Override.Value ?? _enabled;

    /// <summary>Parses --notfound-inventory (falling back to ROSLYNSENTINEL_NOTFOUND_INVENTORY) into <see cref="Enabled"/>. Call once at process startup.</summary>
    public static void Configure(string[] args)
    {
        var raw = LoopLatchOptions.GetFlagValue(args, "--notfound-inventory")
            ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        _enabled = ParseEnabled(raw);
    }

    /// <summary>True for "1" or "true" (case-insensitive, whitespace-trimmed); false for null, empty and anything else.</summary>
    public static bool ParseEnabled(string? raw)
    {
        var trimmed = raw?.Trim();
        return trimmed == "1" || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
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

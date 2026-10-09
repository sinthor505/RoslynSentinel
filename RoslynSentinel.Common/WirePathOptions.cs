namespace RoslynSentinel.Common;

/// <summary>
/// Kill switch for the wire-path relativizing response filter. Environment variable only: the value
/// is read on every access so tests can flip it at runtime. Default is enabled.
/// </summary>
public static class WirePathOptions
{
    /// <summary>Name of the environment variable that controls the feature.</summary>
    public const string EnvVarName = "ROSLYNSENTINEL_RELATIVE_PATHS";

    /// <summary>
    /// True unless <see cref="EnvVarName"/> is set to <c>0</c>, <c>false</c> or <c>off</c>
    /// (case-insensitive, surrounding whitespace ignored). Re-read from the environment on each access.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(EnvVarName)?.Trim();
            if (string.IsNullOrEmpty(raw))
                return true;

            return !(raw == "0"
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("off", StringComparison.OrdinalIgnoreCase));
        }
    }
}

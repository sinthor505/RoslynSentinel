namespace RoslynSentinel.Common;

/// <summary>
/// EVALUATION option, off by default and not meant for production use: startup switches for near-miss rejection hints.
/// Populated once at startup via <see cref="Configure"/>. Each option reads its command-line flag first, then its environment
/// variable. Both defaults match today's behaviour: <see cref="AutoCorrect"/> is false and <see cref="NameDisplay"/> is
/// <see cref="NearMissNameDisplay.Show"/>. Boolean parsing matches <see cref="LoopLatchOptions"/>: the bare flag and
/// <c>--near-miss-autocorrect=true</c>, <c>1</c>, <c>on</c> or <c>yes</c> turn it on; anything else leaves it off.
/// </summary>
public static class NearMissOptions
{
    private static bool _autoCorrect;
    private static NearMissNameDisplay _nameDisplay = NearMissNameDisplay.Show;

    // Deliberate deviation from the other *Options classes (plain static, set once at startup): the fixture tests need the flag
    // on in one test and off in the next without a process restart, and an AsyncLocal override keeps that safe under parallel
    // test runs, where a settable static would leak between fixtures.
    private static readonly AsyncLocal<bool?> AutoCorrectOverride = new();
    private static readonly AsyncLocal<NearMissNameDisplay?> NameDisplayOverride = new();

    /// <summary>
    /// EVALUATION option, off by default and not meant for production use. Enabled by <c>--near-miss-autocorrect</c> or
    /// <c>ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT</c>. Warning: an edit that uses a member before its declaration can get a
    /// silent change of meaning that still compiles, so do not treat correction as safe.
    /// </summary>
    public static bool AutoCorrect => AutoCorrectOverride.Value ?? _autoCorrect;

    /// <summary>
    /// EVALUATION option, <see cref="NearMissNameDisplay.Show"/> by default and not meant for production use. Set by
    /// <c>--near-miss-name-display</c> (value <c>show</c> or <c>hide</c>) or <c>ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY</c>.
    /// </summary>
    public static NearMissNameDisplay NameDisplay => NameDisplayOverride.Value ?? _nameDisplay;

    /// <summary>
    /// Parses --near-miss-autocorrect and --near-miss-name-display (each falling back to its environment variable) into
    /// <see cref="AutoCorrect"/> and <see cref="NameDisplay"/>. Call once at process startup, before DI is built.
    /// </summary>
    public static void Configure(string[] args)
    {
        var rawAutoCorrect = LoopLatchOptions.GetFlagValue(args, "--near-miss-autocorrect")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT");
        _autoCorrect = LoopLatchOptions.ParseEnabled(rawAutoCorrect);

        var rawNameDisplay = GetValueFlag(args, "--near-miss-name-display")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY");
        _nameDisplay = ParseNameDisplay(rawNameDisplay);
    }

    /// <summary>Test hook: forces <see cref="AutoCorrect"/> and <see cref="NameDisplay"/> for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable OverrideForCurrentContext(bool autoCorrect, NearMissNameDisplay nameDisplay)
    {
        var previousAutoCorrect = AutoCorrectOverride.Value;
        var previousNameDisplay = NameDisplayOverride.Value;
        AutoCorrectOverride.Value = autoCorrect;
        NameDisplayOverride.Value = nameDisplay;
        return new OverrideScope(previousAutoCorrect, previousNameDisplay);
    }

    /// <summary>"hide" (case-insensitive, whitespace-trimmed) gives <see cref="NearMissNameDisplay.Hide"/>; anything else, including null, gives <see cref="NearMissNameDisplay.Show"/>.</summary>
    private static NearMissNameDisplay ParseNameDisplay(string? raw)
    {
        return string.Equals(raw?.Trim(), "hide", StringComparison.OrdinalIgnoreCase)
            ? NearMissNameDisplay.Hide
            : NearMissNameDisplay.Show;
    }

    /// <summary>
    /// "--flag=value" gives the value; a bare "--flag" gives the next token, whatever it is. Null when the flag is absent or
    /// has no following token. Unlike <see cref="LoopLatchOptions.GetFlagValue"/>, a non-boolean value such as "hide" is kept.
    /// </summary>
    private static string? GetValueFlag(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1];
    }

    private sealed class OverrideScope(bool? previousAutoCorrect, NearMissNameDisplay? previousNameDisplay) : IDisposable
    {
        public void Dispose()
        {
            AutoCorrectOverride.Value = previousAutoCorrect;
            NameDisplayOverride.Value = previousNameDisplay;
        }
    }
}

/// <summary>
/// Display choice for the symbol name in a near-miss hint. Part of the EVALUATION option <see cref="NearMissOptions"/>:
/// off-by-default values only, not meant for production use.
/// </summary>
public enum NearMissNameDisplay
{
    /// <summary>The hint includes the suggested name. Default.</summary>
    Show,

    /// <summary>The hint omits the suggested name.</summary>
    Hide,
}

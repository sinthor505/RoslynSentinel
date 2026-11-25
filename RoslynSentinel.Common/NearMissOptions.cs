namespace RoslynSentinel.Common;

/// <summary>
/// EVALUATION option, off by default and not meant for production use: startup switches for near-miss rejection hints.
/// Populated once at startup via <see cref="Configure"/>. Each option reads its command-line flag first, then its environment
/// variable. Both defaults match today's behaviour: <see cref="AutoCorrect"/> is false and <see cref="NameDisplay"/> is
/// <see cref="NearMissNameDisplay.Show"/>. Boolean settings accept the bare flag, <c>true</c>, <c>1</c>, <c>on</c>, <c>yes</c>
/// (turn on) and <c>false</c>, <c>0</c>, <c>off</c>, <c>no</c> (leave off); any other value is reported as an invalid setting
/// and leaves the option off.
/// </summary>
[StartupSettingGroup("Near-miss hints")]
public static class NearMissOptions
{
    public static readonly StartupSetting<bool> AutoCorrectSetting = StartupSetting.Bool("--near-miss-autocorrect", "ROSLYNSENTINEL_NEAR_MISS_AUTOCORRECT", false, "EVALUATION: auto-correct near-miss rejections (unsafe; see AutoCorrect).", SettingScope.Server);

    public static readonly StartupSetting<NearMissNameDisplay> NameDisplaySetting = StartupSetting.Enum<NearMissNameDisplay>("--near-miss-name-display", "ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY", NearMissNameDisplay.Show, "EVALUATION: whether near-miss hints show the suggested name (show or hide).", SettingScope.Server);

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
    public static bool AutoCorrect => AutoCorrectOverride.Value ?? AutoCorrectSetting.Value;

    /// <summary>
    /// EVALUATION option, <see cref="NearMissNameDisplay.Show"/> by default and not meant for production use. Set by
    /// <c>--near-miss-name-display</c> (value <c>show</c> or <c>hide</c>) or <c>ROSLYNSENTINEL_NEAR_MISS_NAME_DISPLAY</c>.
    /// </summary>
    public static NearMissNameDisplay NameDisplay => NameDisplayOverride.Value ?? NameDisplaySetting.Value;

    /// <summary>
    /// Parses --near-miss-autocorrect and --near-miss-name-display (each falling back to its environment variable) into
    /// <see cref="AutoCorrect"/> and <see cref="NameDisplay"/>. Call once at process startup, before DI is built.
    /// </summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(NearMissOptions), args);
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

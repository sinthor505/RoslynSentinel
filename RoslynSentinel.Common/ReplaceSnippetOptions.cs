namespace RoslynSentinel.Common;

/// <summary>
/// Resolved size limits for <c>ReplaceSnippet</c>'s oldContent/newContent guard.
/// Populated once at startup via <see cref="Configure"/>: each setting prefers its
/// <c>--replace-snippet-*</c> command-line argument, falling back to the matching
/// <c>ROSLYNSENTINEL_REPLACE_SNIPPET_*</c> environment variable, then a built-in default.
/// Read-only after startup. Exists as a startup-tunable knob specifically so model-eval runs
/// can experiment with looser/tighter limits without a code change and rebuild.
/// </summary>
[StartupSettingGroup("ReplaceSnippet limits")]
public static class ReplaceSnippetOptions
{
    public static readonly StartupSetting<int> MaxOldContentLinesSetting = StartupSetting.Int("--replace-snippet-max-old-lines", "ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_OLD_LINES", 100, "Maximum lines in ReplaceSnippet oldContent (minimum 1).", SettingScope.Server, minValue: 1);

    public static readonly StartupSetting<int> MaxNewContentLinesSetting = StartupSetting.Int("--replace-snippet-max-new-lines", "ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_NEW_LINES", 100, "Maximum lines in ReplaceSnippet newContent (minimum 1).", SettingScope.Server, minValue: 1);

    public static readonly StartupSetting<int> MaxOldContentCharsSetting = StartupSetting.Int("--replace-snippet-max-old-chars", "ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_OLD_CHARS", 2000, "Maximum characters in ReplaceSnippet oldContent (minimum 1).", SettingScope.Server, minValue: 1);

    public static readonly StartupSetting<int> MaxNewContentCharsSetting = StartupSetting.Int("--replace-snippet-max-new-chars", "ROSLYNSENTINEL_REPLACE_SNIPPET_MAX_NEW_CHARS", 2000, "Maximum characters in ReplaceSnippet newContent (minimum 1).", SettingScope.Server, minValue: 1);

    /// <summary>Maximum entries in one ReplaceSnippet batchEdits call; the schema text and the batch refusal both read it.</summary>
    public const int MaxEditsPerBatch = 20;

    public static int MaxOldContentLines => MaxOldContentLinesSetting.Value;
    public static int MaxNewContentLines => MaxNewContentLinesSetting.Value;
    public static int MaxOldContentChars => MaxOldContentCharsSetting.Value;
    public static int MaxNewContentChars => MaxNewContentCharsSetting.Value;

    /// <summary>Parses --replace-snippet-* args (falling back to ROSLYNSENTINEL_REPLACE_SNIPPET_* env vars) into the static properties above. Call once at process startup, before DI is built.</summary>
    public static void Configure(string[] args)
    {
        StartupSettings.ConfigureGroup(typeof(ReplaceSnippetOptions), args);
    }
}

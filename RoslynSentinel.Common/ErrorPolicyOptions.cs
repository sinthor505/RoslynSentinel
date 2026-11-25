namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the error-policy mode: <c>--error-policy-mode=off|pointer|inline</c> first, then the
/// <c>ROSLYNSENTINEL_ERROR_POLICY_MODE</c> environment variable. Default is <see cref="ErrorPolicyMode.Off"/>.
/// The policy file comes from <c>--error-policy-file</c> first, then <c>ROSLYNSENTINEL_ERROR_POLICY_FILE</c>;
/// inline mode requires a readable file.
/// Populated once at startup via <see cref="Configure"/>; read-only after startup, but static,
/// so tests must reset it. Mode Off registers no ErrorPolicyFilter, so it changes nothing at runtime.
/// </summary>
[StartupSettingGroup("Error policy", PostConfigure = "ValidateCrossFields")]
public static class ErrorPolicyOptions
{
    // hint-ok: setting description for the configuration reference, not a tool-call hint
    public static readonly StartupSetting<ErrorPolicyMode> ModeSetting = StartupSetting.Enum<ErrorPolicyMode>("--error-policy-mode", "ROSLYNSENTINEL_ERROR_POLICY_MODE", ErrorPolicyMode.Off, "Error-policy mode for not-found guidance: Off, Pointer or Inline (Inline requires a readable policy file).", SettingScope.Server | SettingScope.PlanStepRunner);

    // hint-ok: setting description for the configuration reference, not a tool-call hint
    public static readonly StartupSetting<string?> FileSetting = StartupSetting.String("--error-policy-file", "ROSLYNSENTINEL_ERROR_POLICY_FILE", null, "Path of the error-policy file; Pointer mode points at it and Inline mode inlines its content.", SettingScope.Server | SettingScope.PlanStepRunner);

    public static ErrorPolicyMode Mode { get; private set; } = ErrorPolicyMode.Off;

    public static string? PolicyFilePath { get; private set; }

    /// <summary>
    /// Parses the mode and policy file (flags first, then environment variables) and assigns both properties.
    /// Validation runs first: a throw leaves the previous <see cref="Mode"/> and <see cref="PolicyFilePath"/> unchanged.
    /// </summary>
    public static void Configure(string[] args)
    {
        var parsed = StartupArgs.Parse(args);
        var modeRead = ModeSetting.Read(parsed, StartupSettings.EnvironmentReader);
        var fileRead = FileSetting.Read(parsed, StartupSettings.EnvironmentReader);

        if (modeRead.Problem is { } modeProblem)
        {
            throw ToArgumentException(modeProblem);
        }

        if (fileRead.Problem is { } fileProblem)
        {
            throw ToArgumentException(fileProblem);
        }

        if (modeRead.Value == ErrorPolicyMode.Inline)
        {
            ValidateReadableFile(fileRead.Value);
        }

        StartupSettings.ConfigureGroup(typeof(ErrorPolicyOptions), args);
        Mode = ModeSetting.Value;
        PolicyFilePath = FileSetting.Value;
    }

    /// <summary>PostConfigure hook for the error-policy group: Inline needs a readable policy file. On a problem Mode is Off (the registry resets the group); otherwise both properties are assigned from the settings.</summary>
    private static IReadOnlyList<ConfigurationProblem> ValidateCrossFields()
    {
        var mode = ModeSetting.Value;
        var filePath = FileSetting.Value;
        if (mode == ErrorPolicyMode.Inline)
        {
            try
            {
                ValidateReadableFile(filePath);
            }
            catch (ArgumentException ex)
            {
                Mode = ErrorPolicyMode.Off;
                PolicyFilePath = null;
                return [new ConfigurationProblem(ConfigurationProblemKind.PostConfigure, "ErrorPolicyOptions.Mode", ModeSetting.Flag, ModeSetting.EnvName, "inline", ex.Message)];
            }
        }

        Mode = mode;
        PolicyFilePath = filePath;
        return Array.Empty<ConfigurationProblem>();
    }

    private static ArgumentException ToArgumentException(ConfigurationProblem problem)
    {
        return problem.Kind == ConfigurationProblemKind.InvalidValue && problem.Flag == ModeSetting.Flag
            ? UnknownModeException(problem.RawValue)
            : new ArgumentException(StartupSettings.DescribeProblems([problem]));
    }

    private static ArgumentException UnknownModeException(string? raw)
    {
        // hint-ok: startup option validation message, not a tool-call hint
        return new ArgumentException(
            $"Unknown --error-policy-mode value '{raw}'. Expected Off, Pointer or Inline (set with --error-policy-mode or ROSLYNSENTINEL_ERROR_POLICY_MODE).");
    }

    /// <summary>
    /// "off", "pointer" or "inline" (case-insensitive, whitespace-trimmed) map to the matching mode; null, empty
    /// or whitespace maps to Off. Anything else throws <see cref="ArgumentException"/>. Inline does not check the
    /// policy file here; <see cref="Configure"/> does.
    /// </summary>
    public static ErrorPolicyMode ParseMode(string? raw)
    {
        var read = ModeSetting.Read(StartupArgs.Empty, _ => raw);
        if (read.Problem is not null)
        {
            throw UnknownModeException(read.Problem.RawValue);
        }

        return read.Value;
    }

    private static void ValidateReadableFile(string? filePath)
    {
        if (filePath is null)
        {
            throw new ArgumentException(
                "Inline error-policy mode requires a policy file: set --error-policy-file or ROSLYNSENTINEL_ERROR_POLICY_FILE to a readable file.");
        }

        try
        {
            using var stream = File.OpenRead(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                $"Inline error-policy mode cannot read the policy file '{filePath}' (from --error-policy-file or ROSLYNSENTINEL_ERROR_POLICY_FILE). Check that it exists and is a readable file.");
        }
    }
}

/// <summary>Error-policy mode for not-found guidance; see <see cref="ErrorPolicyOptions"/>.</summary>
public enum ErrorPolicyMode
{
    /// <summary>No error-policy filter; not-found responses are unchanged.</summary>
    Off,

    /// <summary>Not-found responses point at the policy file.</summary>
    Pointer,

    /// <summary>Not-found responses inline the policy file content.</summary>
    Inline,
}

/// <summary>Keys for the not-found situations an error policy can cover.</summary>
public enum ErrorPolicyKey
{
    FileNotFoundLookup,
    FileNotFoundEdit,
    SymbolNotFoundLookup,
    SymbolNotFoundEdit,
}

/// <summary>Wire names for <see cref="ErrorPolicyKey"/>; matching is exact and case-sensitive.</summary>
public static class ErrorPolicyKeys
{
    /// <summary>Every key, in declaration order.</summary>
    public static IReadOnlyList<ErrorPolicyKey> All { get; } = new[]
    {
        ErrorPolicyKey.FileNotFoundLookup,
        ErrorPolicyKey.FileNotFoundEdit,
        ErrorPolicyKey.SymbolNotFoundLookup,
        ErrorPolicyKey.SymbolNotFoundEdit,
    };

    public static string ToWireName(ErrorPolicyKey key) => key switch
    {
        ErrorPolicyKey.FileNotFoundLookup => "FileNotFound.Lookup",
        ErrorPolicyKey.FileNotFoundEdit => "FileNotFound.Edit",
        ErrorPolicyKey.SymbolNotFoundLookup => "SymbolNotFound.Lookup",
        ErrorPolicyKey.SymbolNotFoundEdit => "SymbolNotFound.Edit",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown error-policy key."),
    };

    /// <summary>Exact, case-sensitive match of a wire name with no trimming; null or anything else returns false.</summary>
    public static bool TryParseWireName(string? name, out ErrorPolicyKey key)
    {
        switch (name)
        {
            case "FileNotFound.Lookup":
                key = ErrorPolicyKey.FileNotFoundLookup;
                return true;
            case "FileNotFound.Edit":
                key = ErrorPolicyKey.FileNotFoundEdit;
                return true;
            case "SymbolNotFound.Lookup":
                key = ErrorPolicyKey.SymbolNotFoundLookup;
                return true;
            case "SymbolNotFound.Edit":
                key = ErrorPolicyKey.SymbolNotFoundEdit;
                return true;
            default:
                key = default;
                return false;
        }
    }
}

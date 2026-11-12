namespace RoslynSentinel.Common;

/// <summary>
/// Startup switch for the error-policy mode: <c>--error-policy-mode=off|pointer|inline</c> first, then the
/// <c>ROSLYNSENTINEL_ERROR_POLICY_MODE</c> environment variable. Default is <see cref="ErrorPolicyMode.Off"/>.
/// The policy file comes from <c>--error-policy-file</c> first, then <c>ROSLYNSENTINEL_ERROR_POLICY_FILE</c>;
/// inline mode requires a readable file.
/// Populated once at startup via <see cref="Configure"/>; read-only after startup, but static,
/// so tests must reset it. Mode Off registers no ErrorPolicyFilter, so it changes nothing at runtime.
/// </summary>
public static class ErrorPolicyOptions
{
    public static ErrorPolicyMode Mode { get; private set; } = ErrorPolicyMode.Off;

    public static string? PolicyFilePath { get; private set; }

    /// <summary>
    /// Parses the mode and policy file (flags first, then environment variables) and assigns both properties.
    /// Validation runs first: a throw leaves the previous <see cref="Mode"/> and <see cref="PolicyFilePath"/> unchanged.
    /// </summary>
    public static void Configure(string[] args)
    {
        var modeRaw = GetArgValue(args, "--error-policy-mode")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_MODE");
        var fileRaw = GetArgValue(args, "--error-policy-file")
            ?? Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_FILE");
        var filePath = string.IsNullOrWhiteSpace(fileRaw) ? null : fileRaw.Trim();

        var mode = ParseMode(modeRaw);
        if (mode == ErrorPolicyMode.Inline)
        {
            ValidateReadableFile(filePath);
        }

        Mode = mode;
        PolicyFilePath = filePath;
    }

    /// <summary>
    /// "off", "pointer" or "inline" (case-insensitive, whitespace-trimmed) map to the matching mode; null, empty
    /// or whitespace maps to Off. Anything else throws <see cref="ArgumentException"/>. Inline does not check the
    /// policy file here; <see cref="Configure"/> does.
    /// </summary>
    public static ErrorPolicyMode ParseMode(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ErrorPolicyMode.Off;
        }

        if (string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorPolicyMode.Off;
        }

        if (string.Equals(trimmed, "pointer", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorPolicyMode.Pointer;
        }

        if (string.Equals(trimmed, "inline", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorPolicyMode.Inline;
        }

        throw new ArgumentException(
            $"Unknown --error-policy-mode value '{raw}'. Expected Off, Pointer or Inline (set with --error-policy-mode or ROSLYNSENTINEL_ERROR_POLICY_MODE).");
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

    /// <summary>
    /// Reads a command-line flag's value, accepting "--flag=value", "--flag value", and a bare
    /// "--flag" (returned as "true"; a following token starting with "--" is not consumed as a value).
    /// </summary>
    private static string? GetArgValue(string[] args, string flag)
    {
        var inlinePrefix = flag + "=";
        var inline = args.FirstOrDefault(a => a.StartsWith(inlinePrefix, StringComparison.Ordinal));
        if (inline is not null)
        {
            return inline[inlinePrefix.Length..];
        }

        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : "true";
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

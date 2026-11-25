namespace RoslynSentinel.Common;

/// <summary>Finds the RoslynSentinel source checkout by its solution file.</summary>
[StartupSettingGroup("Repository roots")]
public static class RepoRootLocator
{
    public static readonly StartupSetting<string?> RepoRootSetting = StartupSetting.String(null, EnvironmentVariable, null, "Root of the RoslynSentinel source checkout; must hold RoslynSentinel.slnx.", SettingScope.Server | SettingScope.PlanStepRunner | SettingScope.AgentLoop | SettingScope.TestHarness, SettingKind.Live);

    public const string EnvironmentVariable = "ROSLYNSENTINEL_REPO_ROOT";
    public const string SolutionFileName = "RoslynSentinel.slnx";

    /// <summary>startDirectory null: the env var when it is rooted and holds RoslynSentinel.slnx, else the first
    /// ancestor of AppContext.BaseDirectory that holds it. startDirectory given: ONLY the walk-up from that
    /// directory, the env var is never consulted (RunStampTests.ReadWorkingTreeStamp_OutsideARepo_ReturnsNull,
    /// RunStampTests.cs:26, passes a temp folder and must get null while Step 5a exports the variable).
    /// Null when nothing matches. Never throws.</summary>
    public static string? TryFind(string? startDirectory = null)
    {
        try
        {
            if (startDirectory is not null)
            {
                return WalkUp(startDirectory);
            }

            return TryFromEnvironment() ?? WalkUp(AppContext.BaseDirectory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>TryFind, or throws DirectoryNotFoundException naming the env var and the start directory.</summary>
    public static string Find(string? startDirectory = null)
    {
        string? found = TryFind(startDirectory);
        if (found is not null)
        {
            return found;
        }

        string start = startDirectory ?? AppContext.BaseDirectory;
        throw new DirectoryNotFoundException(
            $"{SolutionFileName} not found from start directory '{start}'. Environment variable {EnvironmentVariable} is read only when no start directory is given.");
    }

    private static string? TryFromEnvironment()
    {
        string? raw = RepoRootSetting.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.Contains('%') || !Path.IsPathRooted(expanded))
        {
            return null;
        }

        string full = Trim(Path.GetFullPath(expanded));
        return File.Exists(Path.Combine(full, SolutionFileName)) ? full : null;
    }

    private static string? WalkUp(string startDirectory)
    {
        string? directory = Path.GetFullPath(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, SolutionFileName)))
            {
                return Trim(directory);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static string Trim(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string trimmed = fullPath;
        while (trimmed.Length > root.Length
            && (trimmed[^1] == Path.DirectorySeparatorChar || trimmed[^1] == Path.AltDirectorySeparatorChar))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}

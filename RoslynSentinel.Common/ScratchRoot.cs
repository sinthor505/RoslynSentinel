namespace RoslynSentinel.Common;

/// <summary>Resolves the out-of-repo folders that hold test, eval and run output.</summary>
public static class ScratchRoot
{
    public const string EnvironmentVariable = "ROSLYNSENTINEL_SCRATCH_ROOT";
    public const string ResultsRootEnvironmentVariable = "ROSLYNSENTINEL_RESULTS_ROOT";
    public const string DefaultFolderName = "RoslynSentinel-ScratchDir";

    /// <summary>Checks ROSLYNSENTINEL_SCRATCH_ROOT FIRST (expanded; must be absolute, else InvalidOperationException
    /// with the value and an example) and returns without touching RepoRootLocator when it is set; unset/blank ->
    /// Combine(parent of full(sourceRepoRoot ?? RepoRootLocator.Find()),
    /// DefaultFolderName); repo at a drive root (no parent) -> Combine(Path.GetTempPath(), DefaultFolderName).
    /// Result is a full path without a trailing separator.</summary>
    public static string Resolve(string? sourceRepoRoot = null)
    {
        string? configured = ReadAbsoluteEnvironment(EnvironmentVariable);
        if (configured is not null)
        {
            return configured;
        }

        string repoRoot = Trim(Path.GetFullPath(sourceRepoRoot ?? RepoRootLocator.Find()));
        string? parent = Path.GetDirectoryName(repoRoot);
        return parent is null
            ? Path.Combine(Path.GetTempPath(), DefaultFolderName)
            : Path.Combine(parent, DefaultFolderName);
    }

    /// <summary>ROSLYNSENTINEL_RESULTS_ROOT (absolute, else InvalidOperationException) else Combine(Resolve(), "ModelTestingResults").</summary>
    public static string ResultsRoot(string? sourceRepoRoot = null)
    {
        string? configured = ReadAbsoluteEnvironment(ResultsRootEnvironmentVariable);
        return configured ?? Path.Combine(Resolve(sourceRepoRoot), "ModelTestingResults");
    }

    /// <summary>Combine(Resolve(), "TestRuns").</summary>
    public static string TestRunsRoot(string? sourceRepoRoot = null)
    {
        return Path.Combine(Resolve(sourceRepoRoot), "TestRuns");
    }

    private static string? ReadAbsoluteEnvironment(string variable)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim();
        string expanded = Environment.ExpandEnvironmentVariables(value);
        if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded))
        {
            throw new InvalidOperationException(
                $"Environment variable {variable} is '{value}', which is not an absolute path. Set it to a fully qualified folder, for example C:\\scratch.");
        }

        return Trim(Path.GetFullPath(expanded));
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

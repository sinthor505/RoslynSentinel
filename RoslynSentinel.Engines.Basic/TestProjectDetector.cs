using System.Collections.Concurrent;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Detects whether a project is a test project. Results are cached per project file, keyed by
/// path and the file's last-write time, so an edited .csproj is re-read on the next check.</summary>
public static class TestProjectDetector
{
    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, bool IsTest)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, bool UsesNUnit)> NUnitCache =
    new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Is this a test project" -> checked via the project file referencing
    /// Microsoft.NET.Test.Sdk (the SDK package that actually makes `dotnet test` runnable), not via
    /// referencing nunit.framework: a project can pull in NUnit's assertion library transitively
    /// through a ProjectReference to a real test project (e.g. a benchmark/tool project referencing
    /// a Tests project for shared fixtures) without being a test project itself -> `dotnet test`
    /// against such a project fails outright rather than running zero tests.</summary>
    public static bool IsTestProject(Microsoft.CodeAnalysis.Project project)
    {
        var path = project.FilePath;
        if (path is null || !File.Exists(path))
            return false;

        var lastWriteUtc = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc)
            return cached.IsTest;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var isTest = text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
        Cache[path] = (lastWriteUtc, isTest);
        return isTest;
    }

    /// <summary>True when the project file at <paramref name="projectPath"/> references the NUnit test adapter (NUnit3TestAdapter),
    /// the adapter that understands the `TestCategory` filter property the fast RunTest suite uses. Another adapter (xunit)
    /// rejects an unknown filter property, so the fast suite leaves such projects' filter alone. Cached like <see cref="IsTestProject"/>.</summary>
    public static bool UsesNUnitAdapter(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
            return false;

        var lastWriteUtc = File.GetLastWriteTimeUtc(projectPath);
        if (NUnitCache.TryGetValue(projectPath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
            return cached.UsesNUnit;

        try
        {
            var usesNUnit = File.ReadAllText(projectPath).Contains("NUnit3TestAdapter", StringComparison.OrdinalIgnoreCase);
            NUnitCache[projectPath] = (lastWriteUtc, usesNUnit);
            return usesNUnit;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

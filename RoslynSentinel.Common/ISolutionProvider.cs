using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>Read-only access to the currently loaded solution and its on-disk location.</summary>
public interface ISolutionProvider
{
    /// <summary>Root directory used to resolve relative solution/project paths.</summary>
    string? BaseRepoDirectory
    {
        get;
        set;
    }
    /// <summary>The most recently loaded Roslyn solution, or null if none is loaded.</summary>
    Solution? CurrentSolution
    {
        get;
    }
    /// <summary>Number of projects in the current solution.</summary>
    int ProjectCount
    {
        get;
    }
    /// <summary>Path to the loaded .sln or .csproj, or null if none is loaded.</summary>
    string? SolutionPath
    {
        get;
        set;
    }
    /// <summary>Monotonically increasing version bumped on every workspace mutation.</summary>
    int WorkspaceVersion
    {
        get;
    }

    /// <summary>Returns the current in-memory solution. Roslyn's <see cref="Solution"/> is immutable, so callers can apply speculative edits (e.g. <c>WithDocumentText</c>) without affecting this instance or other callers.</summary>
    Task<Solution> GetCurrentSolutionAsync(CancellationToken cancellationToken);
    /// <summary>Lists solution-folder items (non-project files shown in Solution Explorer).</summary>
    List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems();
    /// <summary>Directory containing the loaded solution/project, or null if none is loaded.</summary>
    string? GetSolutionRoot();
    /// <summary>Resolves a wire-format relative path against the solution root into a validated FilePath.</summary>
    FilePath SetFilePath(string? filepath);
}

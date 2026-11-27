namespace RoslynSentinel.Git;

/// <summary>Why a Git repository-root lookup found no repository. The engine reports the cause; the tool adds the advice.</summary>
public enum GitRootFailure
{
    /// <summary>The lookup succeeded.</summary>
    None = 0,

    /// <summary>An explicit repoPath was given and no .git entry was found walking up from it.</summary>
    RepoPathNotARepository,

    /// <summary>A solution is loaded and its directory is not inside a git repository (no fallback to the server's own repository).</summary>
    SolutionDirectoryNotInRepository,

    /// <summary>No solution is loaded and walking up from the server's base and working directories found no .git entry.</summary>
    NoRepositoryFound
}

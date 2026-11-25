namespace RoslynSentinel.Common;

/// <summary>
/// Tells a caller whether the solution load it is waiting on is a background reload after a project-file write. Implemented by
/// <see cref="PersistentWorkspaceManager"/> only; deliberately NOT a member of <see cref="IWorkspaceManager"/> or
/// <see cref="ISolutionProvider"/>, so no fake and no existing interface changes. Callers reach it with an <c>is</c>/<c>as</c> check
/// (see <see cref="SolutionNotLoadedMessage.LoadWaitTimedOutFor"/>), the same pattern as <see cref="IEdgeGateLeaseSource"/>.
/// </summary>
public interface IBackgroundReloadSource
{
    /// <summary>True while at least one background reload is running.</summary>
    bool BackgroundReloadInProgress { get; }
}

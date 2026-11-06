namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Seam over <c>AppDomain.CurrentDomain.ProcessExit</c> so <see cref="ToolTelemetryFlushService"/> can be tested without
/// raising the real event (which would run every handler in the test process). Public because Server.Advanced has no
/// InternalsVisibleTo for its tests.
/// </summary>
public interface IProcessExitSource
{
    /// <summary>Raised when the process is exiting (for example after <c>Environment.Exit</c>).</summary>
    event EventHandler? ProcessExit;
}

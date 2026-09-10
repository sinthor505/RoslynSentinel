namespace RoslynSentinel.Common;

/// <summary>
/// Whether this server process is serving real work or an automated evaluation run.
/// </summary>
/// <remarks>
/// Deliberately NOT called just "mode": <c>--mode</c> already means tool activation
/// (<c>--mode=Workspace</c>, <c>--mode=Admin</c>, see <c>ToolClassRegistry</c>), so the CLI switch
/// for this is <c>--testing</c> and only the type carries the "OperatingMode" name.
/// </remarks>
public enum OperatingMode
{
    /// <summary>Normal operation. The default when <c>--testing</c> is absent.</summary>
    Production,

    /// <summary>
    /// An automated evaluation run (e.g. PlanStepRunner) executing against a worktree of a real
    /// repository. Only <c>ProjectDoc</c> branches on this — it reads out of <c>docs/testing/</c>
    /// instead of <c>docs/</c> so a test fixture and the production doc it mirrors can share a
    /// filename without one silently resolving to the other.
    /// </summary>
    Testing
}

/// <summary>
/// Process-wide host settings that aren't tool-activation (<c>--mode</c>/<c>--include-tools</c>)
/// and aren't refactoring-feature toggles (<see cref="SentinelConfiguration"/>).
/// </summary>
/// <remarks>
/// Carried through DI rather than as a static. A static would in fact be safe at runtime — a
/// server process only ever has one solution loaded, and the HTTP host registers everything as
/// singletons so concurrent /mcp clients share it — but the test assemblies run with
/// <c>[assembly: Parallelizable(ParallelScope.Fixtures)]</c>, and a mutable global read by one
/// fixture while another sets it produces order-dependent failures that only reproduce in full
/// runs. Injecting makes "production" and "testing" two objects instead of two writes to one
/// slot, and keeps this consistent with how every other host setting travels.
/// </remarks>
public class SentinelHostOptions
{
    public OperatingMode OperatingMode { get; init; } = OperatingMode.Production;
}

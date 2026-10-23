namespace RoslynSentinel.Engines.Basic;

/// <summary>How <c>McpServerControl(LaunchTestInstance)</c> obtains the server binary: Auto reuses the throwaway
/// build output when it is newer than every server source file and rebuilds otherwise; Always rebuilds; Skip
/// never builds and fails when there is no output.</summary>
public enum TestInstanceBuildMode
{
    Auto,
    Always,
    Skip
}

/// <summary>Where the launched server binary came from. The wire payload is rendered as JSON text by the
/// string-returning McpServerControl tool, so it carries no IOperationResult status.</summary>
/// <param name="OutputPath">Directory holding RoslynSentinel.Server.Advanced.dll.</param>
/// <param name="BuildTimeUtc">Write time of the server dll.</param>
/// <param name="GitSha">Short HEAD sha of the solution's repository; null when git is unavailable.</param>
/// <param name="IsDirty">True when server folders have uncommitted changes (the build may include them); null when unknown.</param>
/// <param name="Rebuilt">True when this launch ran a build; false when an existing output was reused.</param>
public sealed record TestInstanceBuildSource(string OutputPath, DateTime BuildTimeUtc, string? GitSha, bool? IsDirty, bool Rebuilt);

/// <summary>Files a test instance writes. Read them when a request fails or the instance misbehaves.</summary>
/// <param name="Stdout">Captured stdout of the child process.</param>
/// <param name="Stderr">Captured stderr of the child process.</param>
/// <param name="ServerLogDir">The child's own Serilog directory (inside its working directory).</param>
public sealed record TestInstanceLogPaths(string Stdout, string Stderr, string ServerLogDir);

/// <summary>Paste-ready PowerShell requests for driving a test instance out of band.</summary>
/// <param name="Note">One sentence: why raw requests are used and how to stop the instance.</param>
/// <param name="ToolsList">A tools/list request.</param>
/// <param name="ToolsCall">A tools/call request (McpServerStatus with a reason argument).</param>
public sealed record TestInstanceHowToCall(string Note, string ToolsList, string ToolsCall);

/// <summary>The McpServerControl(LaunchTestInstance) payload.</summary>
public sealed record LaunchedTestInstance(
    string Url,
    int Pid,
    string Mode,
    string SolutionPath,
    string WorkingDir,
    TestInstanceBuildSource BuildSource,
    TestInstanceLogPaths LogPaths,
    TestInstanceHowToCall HowToCall);

/// <summary>One row of ListTestInstances (and the element StopTestInstance reports).</summary>
/// <param name="IsAlive">False when the process has exited; such rows are reported once and then pruned.</param>
public sealed record RunningTestInstance(
    int Pid,
    string Url,
    string Mode,
    string SolutionPath,
    string WorkingDir,
    DateTime StartedUtc,
    double UptimeSeconds,
    string ExePath,
    bool IsAlive);

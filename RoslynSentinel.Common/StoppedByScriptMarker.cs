namespace RoslynSentinel.Common;

/// <summary>Captures whether THIS process's instance folder had a stopped-by-script marker at
/// startup (see ServerStartupHelpers.ReadAndConsumeStoppedByScriptMarker), so
/// SentinelServerStatusTools.McpServerStatus can tell an agent "the previous occupant of this
/// path was deliberately stopped by roslynsentinel-vscode-control.ps1" without any log-file
/// access - the marker itself is already consumed (read+deleted) by the time this is
/// constructed, so this is the only place that information survives to be queried live.</summary>
public record StoppedByScriptMarker(bool WasFound, string? Details);

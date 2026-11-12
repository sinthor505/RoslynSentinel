namespace RoslynSentinel.Common;

/// <summary>
/// A random id for this server process, fixed at first use. It differs for every process, including two servers started from the
/// same binary folder, which the telemetry instance id (<c>ToolTelemetry.InstanceId</c>) does not guarantee. Used to scope edit
/// effects (<see cref="EditEffect.ServerInstanceId"/>) for hosts that send no session id.
/// </summary>
public static class ServerInstanceIdentity
{
    /// <summary>12 hex characters of a v4 GUID.</summary>
    public static string Id { get; } = Guid.NewGuid().ToString("N")[..12];
}

using Microsoft.Extensions.Hosting;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Refreshes <see cref="ToolSurfaceController"/> once when the host starts. Hosted services start in
/// host.RunAsync(), which production (ServerStdio.Run, ServerHttp) and the test hosts
/// (McpToolsetControlTests.LiveServer) share, after ApplyStartupToolsets in both, so this is the first
/// point where startup tools and startup toolsets are both in place.
/// StopAsync does nothing.
/// </summary>
internal sealed class ToolSurfaceStartup(ToolSurfaceController surface) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        surface.Refresh("startup");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// MCP tools that augment or fix known bugs in the standard Microsoft roslyn-mcp server.
/// Each tool documents which standard tool it replaces and exactly what it fixes.
/// </summary>
[McpServerToolType]
public class SentinelAugmentTools
{
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelAugmentTools> _logger;

    public SentinelAugmentTools(
        MsToolAugmentEngine engine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelAugmentTools> logger)
    {
        _msToolAugmentEngine = engine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── 1. EncapsulateFieldSafe ───────────────────────────────────────────────







    // ── 6. FormatDocumentSafe ─────────────────────────────────────────────────


    // ── 12. ExtractMethodSafe ─────────────────────────────────────────────────



}

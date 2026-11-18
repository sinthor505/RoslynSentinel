using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Finds unresolved member names in a rejected edit that sit one close spelling away from a declared name on their receiver type,
/// and reports each as a <see cref="NearMissName"/>. Static and stateless, no MCP types.
/// </summary>
public static class NearMissDetector
{
    /// <summary>
    /// Detects near-miss names for the compiler diagnostics of a rejected edit. <paramref name="baseline"/> is the committed solution,
    /// <paramref name="changes"/> the candidate text per file, and <paramref name="diagnostics"/> the compile gate's diagnostics.
    /// </summary>
    public static Task<IReadOnlyList<NearMissName>> DetectAsync(Solution baseline, IReadOnlyDictionary<FilePathWrapper, string> changes, IReadOnlyList<DiagnosticInfo> diagnostics, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 2)
    }
}

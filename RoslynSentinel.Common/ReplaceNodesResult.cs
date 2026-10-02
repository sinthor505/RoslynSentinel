using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

/// <summary>
/// Result of <see cref="RoslynFormattingHelper.ReplaceNodesFormattedAsync"/>. A replacement whose old node could not
/// be located in the tree being edited (typically because an ancestor of it was replaced earlier in the same fold)
/// is NOT applied, and is listed in <see cref="UnlocatedNodes"/> instead of being skipped silently. A caller must
/// check <see cref="AllReplacementsApplied"/> before using <see cref="Text"/>: when it is false, <see cref="Text"/>
/// is missing those replacements and must not be written.
/// </summary>
/// <param name="Text">The formatted document text with every locatable replacement applied.</param>
/// <param name="UnlocatedNodes">The keys of the replacement map whose old node could not be located; empty when everything applied.</param>
public sealed record ReplaceNodesResult(string Text, IReadOnlyList<SyntaxNode> UnlocatedNodes)
{
    /// <summary>True when every requested replacement was applied.</summary>
    public bool AllReplacementsApplied => UnlocatedNodes.Count == 0;
}

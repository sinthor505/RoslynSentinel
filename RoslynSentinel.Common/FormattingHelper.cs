using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

internal class FormattingHelper
{
    // Added by AddTopLevelType (expected - used for diagnostics)
    /// <summary>
    /// Controls which side wins when <see cref="RefactoringEngine"/>'s shared "replace a node, then
    /// format" helper decides whose leading trivia (blank lines, doc comments, etc.) to keep on the
    /// replacement node.
    /// </summary>
    public enum TriviaEditIntent
    {
        /// <summary>
        /// Default: the original node's leading trivia always wins. Use this whenever the replacement
        /// node was synthesized fresh (e.g. via <c>WithModifiers</c> on a brand-new token list) and
        /// never carried the original doc comment/blank-line trivia in the first place — a freshly
        /// built token's own leading trivia (even a single zero-width elastic marker) must never be
        /// mistaken for a deliberate, caller-authored replacement.
        /// </summary>
        PreserveOld,

        /// <summary>
        /// The replacement node's leading trivia always wins, even if it looks empty or minimal. Use
        /// this only when the caller deliberately rewrote the node's leading trivia on purpose (e.g.
        /// <see cref="RefactoringEngine.RemoveSummaryCommentAsync"/> stripping a doc comment).
        /// </summary>
        ReplaceLeading,
    }

    /// <summary>
    /// Replaces <paramref name = "oldNode"/> with <paramref name = "newNode"/> and formats only the
    /// replaced node (via a tracking annotation), instead of the whole file. Prevents write-back
    /// paths from silently reformatting unrelated code and shifting line numbers below the edit.
    /// <paramref name = "oldNode"/>'s leading and trailing trivia (blank lines, doc comments, etc.
    /// anchored to its position in the file) is transplanted onto <paramref name = "newNode"/> by
    /// default (<paramref name = "triviaIntent"/> = <see cref="TriviaEditIntent.PreserveOld"/>), since
    /// a freshly synthesized replacement (e.g. via <c>WithModifiers</c> on a brand-new token list, or
    /// SyntaxFactory.ParseMemberDeclaration) has no knowledge of the blank lines or doc comment that
    /// belonged to the original node — and a freshly-built token's own "empty" leading trivia (even a
    /// single zero-width elastic marker) must never be mistaken for a deliberate replacement; a
    /// trivia-count/shape heuristic previously used here to detect "custom trivia" was exactly this
    /// mistake; see docs/current/CLOSED.md and RunTest ChangeAccessibility_PreservesLeadingDocComment.
    /// Pass <see cref="TriviaEditIntent.ReplaceLeading"/> when the caller deliberately rewrote
    /// <paramref name = "newNode"/>'s leading trivia on purpose (e.g. stripping a doc comment) — then
    /// <paramref name = "newNode"/>'s leading trivia wins unconditionally, even if it looks empty.
    /// </summary>
    private static async Task<string> ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld)
    {
        var annotation = new SyntaxAnnotation();

        var leadingTrivia = triviaIntent == TriviaEditIntent.ReplaceLeading ? newNode.GetLeadingTrivia() : oldNode.GetLeadingTrivia();
        var trailingTrivia = oldNode.GetTrailingTrivia();

        var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Removes <paramref name = "nodeToRemove"/> without reformatting any sibling's interior.
    /// KeepExteriorTrivia splices the removed node's leading trivia onto the token immediately
    /// before it (as trailing trivia) and its trailing trivia onto the token immediately after (as
    /// leading trivia) — which may belong to a sibling member (e.g. the next method) or to the
    /// container itself (e.g. its closing brace, when removing the first/last member). Both boundary
    /// tokens are restored to their exact pre-removal trivia afterwards, since the gap's own
    /// separation from whatever now precedes/follows it should be unchanged by the removal. This
    /// avoids the whole-container/whole-sibling formatting this helper used previously, which
    /// normalized untouched members' internal spacing as a side effect.
    /// </summary>
    private static async Task<string> RemoveNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, CancellationToken cancellationToken = default)
    {
        var tokenBefore = nodeToRemove.GetFirstToken().GetPreviousToken();
        var tokenAfter = nodeToRemove.GetLastToken().GetNextToken();
        var hasTokenBefore = tokenBefore != default;
        var hasTokenAfter = tokenAfter != default;
        var originalTrailingTrivia = hasTokenBefore ? tokenBefore.TrailingTrivia : default;
        var originalLeadingTrivia = hasTokenAfter ? tokenAfter.LeadingTrivia : default;

        var beforeAnnotation = hasTokenBefore ? new SyntaxAnnotation() : null;
        var afterAnnotation = hasTokenAfter ? new SyntaxAnnotation() : null;
        var annotatedRoot = root;
        if (beforeAnnotation != null)
            annotatedRoot = annotatedRoot.ReplaceToken(tokenBefore, tokenBefore.WithAdditionalAnnotations(beforeAnnotation));
        if (afterAnnotation != null)
        {
            var currentTokenAfter = annotatedRoot.DescendantTokens().Single(t => t.IsEquivalentTo(tokenAfter) && t.Span == tokenAfter.Span);
            annotatedRoot = annotatedRoot.ReplaceToken(currentTokenAfter, currentTokenAfter.WithAdditionalAnnotations(afterAnnotation));
        }

        var trackedNodeToRemove = annotatedRoot.DescendantNodesAndSelf().Single(n => n.IsEquivalentTo(nodeToRemove) && n.Span == nodeToRemove.Span);
        var newRoot = annotatedRoot.RemoveNode(trackedNodeToRemove, SyntaxRemoveOptions.KeepExteriorTrivia)!;

        if (beforeAnnotation != null)
        {
            var trackedTokenBefore = newRoot.GetAnnotatedTokens(beforeAnnotation).Single();
            newRoot = newRoot.ReplaceToken(trackedTokenBefore, trackedTokenBefore.WithTrailingTrivia(originalTrailingTrivia).WithoutAnnotations(beforeAnnotation));
        }
        if (afterAnnotation != null)
        {
            var trackedTokenAfter = newRoot.GetAnnotatedTokens(afterAnnotation).Single();
            newRoot = newRoot.ReplaceToken(trackedTokenAfter, trackedTokenAfter.WithLeadingTrivia(originalLeadingTrivia).WithoutAnnotations(afterAnnotation));
        }

        return (await document.WithSyntaxRoot(newRoot).GetTextAsync(cancellationToken)).ToString();
    }
}

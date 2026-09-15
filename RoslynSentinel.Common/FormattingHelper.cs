using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Common;

public static class FormattingHelper
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
        /// never carried the original doc comment/blank-line trivia in the first place -> a freshly
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
    /// belonged to the original node -> and a freshly-built token's own "empty" leading trivia (even a
    /// single zero-width elastic marker) must never be mistaken for a deliberate replacement; a
    /// trivia-count/shape heuristic previously used here to detect "custom trivia" was exactly this
    /// mistake; see docs/current/CLOSED.md and RunTest ChangeAccessibility_PreservesLeadingDocComment.
    /// Pass <see cref="TriviaEditIntent.ReplaceLeading"/> when the caller deliberately rewrote
    /// <paramref name = "newNode"/>'s leading trivia on purpose (e.g. stripping a doc comment) -> then
    /// <paramref name = "newNode"/>'s leading trivia wins unconditionally, even if it looks empty.
    /// </summary>
    public static async Task<string> ReplaceNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld, ILogger? logger = null)
    {
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

        var annotation = new SyntaxAnnotation();

        var leadingTrivia = triviaIntent == TriviaEditIntent.ReplaceLeading ? newNode.GetLeadingTrivia() : oldNode.GetLeadingTrivia();
        var trailingTrivia = oldNode.GetTrailingTrivia();

        var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        var normalizedText = EolUtilities.NormalizeEol(formattedText, dominantEol);

        // TODO(Phase 3): wire these into the real Finding channel once it exists instead of ILogger.
        if (logger != null)
        {
            var problems = CheckPostWriteInvariants(normalizedText, triviaIntent, oldNode);
            foreach (var problem in problems)
            {
                logger.LogWarning("ReplaceNodeFormattedAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    public static List<string> CheckPostWriteInvariants(string afterText, TriviaEditIntent triviaIntent, SyntaxNode? oldNode)
    {
        var problems = new List<string>();

        var hasCr = afterText.Contains('\r');
        var hasLfOnly = afterText.Replace("\r\n", "").Contains('\n');
        if (hasCr && hasLfOnly)
        {
            problems.Add("Result text mixes CRLF and bare LF line endings after EOL normalization.");
        }

        if (triviaIntent == TriviaEditIntent.PreserveOld && oldNode != null)
        {
            var oldLeadingTrivia = oldNode.GetLeadingTrivia().ToFullString();
            if (!string.IsNullOrWhiteSpace(oldLeadingTrivia) && !afterText.Contains(oldLeadingTrivia.Trim()))
            {
                problems.Add("triviaIntent was PreserveOld and the original node had non-whitespace leading trivia, but that trivia is not present in the result.");
            }
        }

        return problems;
    }


    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Batch form of <see cref="ReplaceNodeFormattedAsync"/>: replaces every old->new pair in
    /// <paramref name="replacements"/> against one evolving root and formats the whole set in a
    /// single <see cref="Formatter.FormatAsync"/> pass, instead of one call per pair. Needed because
    /// after the first <c>ReplaceNode</c> call, every other pending old node reference is stale -> the
    /// original node objects are no longer part of the tree being edited. <see cref="SyntaxNode.TrackNodes"/>/
    /// <see cref="SyntaxNode.GetCurrentNode{TNode}"/> re-locates each tracked old node against the
    /// current root before building its replacement, the same idiom already used in
    /// <c>AdvancedRefactoringEngine</c> for chained method-body rewrites. Each old node's leading/
    /// trailing trivia is preserved onto its replacement by default, same as the single-pair overload.
    /// </summary>
    public static async Task<string> ReplaceNodesFormattedAsync(Document document, SyntaxNode root, Dictionary<SyntaxNode, SyntaxNode> replacements, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld)
    {
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

        var trackedRoot = root.TrackNodes(replacements.Keys);
        var annotation = new SyntaxAnnotation();

        var currentRoot = trackedRoot;
        foreach (var (oldNode, newNode) in replacements)
        {
            var currentOldNode = currentRoot.GetCurrentNode(oldNode);
            if (currentOldNode == null)
            {
                continue;
            }

            var leadingTrivia = triviaIntent == TriviaEditIntent.ReplaceLeading ? newNode.GetLeadingTrivia() : currentOldNode.GetLeadingTrivia();
            var trailingTrivia = currentOldNode.GetTrailingTrivia();
            var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(annotation);
            currentRoot = currentRoot.ReplaceNode(currentOldNode, annotatedNewNode);
        }

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(currentRoot), annotation, cancellationToken: cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        return EolUtilities.NormalizeEol(formattedText, dominantEol);
    }


    /// <summary>
    /// Removes <paramref name = "nodeToRemove"/> without reformatting any sibling's interior.
    /// KeepExteriorTrivia splices the removed node's leading trivia onto the token immediately
    /// before it (as trailing trivia) and its trailing trivia onto the token immediately after (as
    /// leading trivia) -> which may belong to a sibling member (e.g. the next method) or to the
    /// container itself (e.g. its closing brace, when removing the first/last member). Both boundary
    /// tokens are restored to their exact pre-removal trivia afterwards, since the gap's own
    /// separation from whatever now precedes/follows it should be unchanged by the removal. This
    /// avoids the whole-container/whole-sibling formatting this helper used previously, which
    /// normalized untouched members' internal spacing as a side effect.
    /// </summary>
    public static async Task<string> RemoveNodeFormattedAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, CancellationToken cancellationToken = default, ILogger? logger = null)
    {
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

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

        var resultText = (await document.WithSyntaxRoot(newRoot).GetTextAsync(cancellationToken)).ToString();
        var normalizedText = EolUtilities.NormalizeEol(resultText, dominantEol);

        // TODO(Phase 3): wire these into the real Finding channel once it exists instead of ILogger.
        if (logger != null)
        {
            var problems = CheckPostWriteInvariants(normalizedText, TriviaEditIntent.PreserveOld, null);
            foreach (var problem in problems)
            {
                logger.LogWarning("RemoveNodeFormattedAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Inserts <paramref name = "newMember"/> into <paramref name = "container"/>'s member list at
    /// <paramref name = "insertIndex"/> (the position it should occupy in the resulting list -> i.e.
    /// pass the old index of the following member, or <c>container.Members.Count</c> to append) and
    /// formats only the newly inserted member, instead of the whole container. Prevents
    /// AddMember/InsertMemberAfter/InsertMemberBefore from reformatting sibling members' interior
    /// spacing as a side effect of formatting the whole container span -> see
    /// docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md
    /// and the earlier 2026-09-07 finding of the same class (unrelated method signatures re-spaced).
    /// <paramref name = "newMember"/> is given an explicit blank-line separator (matching this repo's
    /// established one-blank-line-between-members convention) before itself when there is a preceding
    /// sibling, and before the following sibling (if any) when that sibling didn't already carry one ->
    /// this is computed explicitly rather than left to <see cref="Formatter"/>, because a
    /// freshly-parsed member's "empty" leading trivia is otherwise indistinguishable from a deliberate
    /// zero-blank-line request, and formatting the whole container to resolve that ambiguity is exactly
    /// what caused both symptoms this helper exists to avoid.
    /// </summary>
    public static async Task<string> InsertMemberFormattedAsync(Document document, SyntaxNode root, TypeDeclarationSyntax container, int insertIndex, MemberDeclarationSyntax newMember, CancellationToken cancellationToken = default)
    {
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

        var members = container.Members;
        var precedingMember = insertIndex > 0 ? members[insertIndex - 1] : null;
        var followingMember = insertIndex < members.Count ? members[insertIndex] : null;

        var indentTrivia = (precedingMember ?? followingMember)?.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)) ?? default;
        var indentText = indentTrivia != default ? indentTrivia.ToFullString() : "    ";

        var blankLineBefore = precedingMember != null
            ? new[] { SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indentText) }
            : new[] { SyntaxFactory.Whitespace(indentText) };
        var newMemberWithTrivia = newMember
            .WithLeadingTrivia(blankLineBefore.Concat(newMember.GetLeadingTrivia()))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        var annotation = new SyntaxAnnotation();
        var annotatedNewMember = newMemberWithTrivia.WithAdditionalAnnotations(annotation);

        var newMembersList = members.Insert(insertIndex, annotatedNewMember);

        // If a following sibling exists but its own leading trivia has no blank line (e.g. it used to
        // be the first member, right after the container's opening brace), give it one now -> the new
        // member is being inserted immediately before it, so it needs the same separator every other
        // pair of siblings in this container already has.
        if (followingMember != null)
        {
            var followingLeadingTrivia = followingMember.GetLeadingTrivia();
            var hasBlankLine = followingLeadingTrivia.Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) >= 2;
            if (!hasBlankLine)
            {
                var followingIndentTrivia = followingLeadingTrivia.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
                var followingIndentText = followingIndentTrivia != default ? followingIndentTrivia.ToFullString() : indentText;
                var nonWhitespaceLeading = followingLeadingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia));
                var newFollowingLeading = new SyntaxTriviaList(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed)
                    .AddRange(nonWhitespaceLeading)
                    .Add(SyntaxFactory.Whitespace(followingIndentText));
                var updatedFollowingMember = followingMember.WithLeadingTrivia(newFollowingLeading);
                newMembersList = newMembersList.Replace(newMembersList[insertIndex + 1], updatedFollowingMember);
            }
        }

        var newContainer = container.WithMembers(newMembersList);
        var newRoot = root.ReplaceNode(container, newContainer);

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        return EolUtilities.NormalizeEol(formattedText, dominantEol);
    }
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Pure pass-through to <see cref="SyntaxNodeExtensions.NormalizeWhitespace{TNode}"/> (via
    /// <see cref="SyntaxNode.NormalizeWhitespace"/>) with identical arguments and identical
    /// behavior -> this method exists purely as a single, greppable chokepoint for the whole-subtree
    /// form of whitespace normalization, not as a safety fix.
    /// <para>
    /// Unlike <see cref="ReplaceNodeFormattedAsync"/>, <see cref="RemoveNodeFormattedAsync"/>, and
    /// <see cref="InsertMemberFormattedAsync"/> -> which are narrowly scoped to just the node being
    /// edited via a tracking annotation, and are safe by construction regardless of what the caller
    /// passes -> this method's safety depends entirely on what <paramref name="node"/> is:
    /// </para>
    /// <list type="bullet">
    /// <item>Safe: a freshly synthesized node with no pre-existing siblings, e.g.
    /// <c>SyntaxFactory.MethodDeclaration(...).NormalizeWhitespace()</c> -> there is no untouched
    /// sibling content to disturb.</item>
    /// <item>Risky: an existing tree root or container (e.g. a whole file's <c>root</c>/<c>newRoot</c>
    /// or a <c>CompilationUnitSyntax</c>) that already contains untouched code -> normalizing it
    /// reformats every sibling's whitespace as a side effect, which is exactly the bug class fixed in
    /// <c>RefactoringEngine.Member(add)</c> by introducing <see cref="InsertMemberFormattedAsync"/>
    /// (see docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md).</item>
    /// </list>
    /// <para>
    /// For an edit-then-format use case on an existing tree, prefer
    /// <see cref="InsertMemberFormattedAsync"/>/<see cref="ReplaceNodeFormattedAsync"/>/
    /// <see cref="RemoveNodeFormattedAsync"/> instead of this method -> they scope the formatting
    /// annotation to just the changed node so siblings are left untouched. Reach for this method only
    /// when the node truly has no siblings to protect (a synthesized standalone node), or as a
    /// mechanical drop-in during centralization, per docs/current/proposal_batch_replacesnippet.md-adjacent
    /// audit work -> auditing/fixing individual call sites' scope is deliberately out of scope here.
    /// </para>
    /// </summary>
    public static SyntaxNode NormalizeWholeSubtreeWhitespace(SyntaxNode node, string indentation = "    ", string eol = "\n", bool elasticTrivia = true)
    {
        return node.NormalizeWhitespace(indentation, eol, elasticTrivia);
    }
}

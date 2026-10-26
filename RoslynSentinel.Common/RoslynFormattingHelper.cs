using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

public static class RoslynFormattingHelper
{
    /// <summary>
    /// Controls which side wins when <see cref="ReplaceNodeAndFormatReplacementAsync"/>'s shared "replace a node, then
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
        /// <c>BasicRefactoringEngine.RemoveSummaryCommentAsync</c> stripping a doc comment).
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
    /// <paramref name = "eol"/> is the line ending the replacement and every other changed line takes,
    /// instead of the dominant ending of <paramref name = "document"/>.
    /// </summary>
    public static async Task<string> ReplaceNodeAndFormatReplacementAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, string eol, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld, ILogger? logger = null)
    {
        var leadingTrivia = triviaIntent == TriviaEditIntent.ReplaceLeading ? newNode.GetLeadingTrivia() : oldNode.GetLeadingTrivia();
        var trailingTrivia = oldNode.GetTrailingTrivia();

        // Only the replacement is annotated, so FormatAnnotatedSpansAsync formats just it and keeps the terminator of
        // every line the edit did not change (a stray CRLF in an LF file survives).
        var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(Formatter.Annotation);
        var newRoot = root.ReplaceNode(oldNode, annotatedNewNode);
        var formattedDoc = await FormatAnnotatedSpansAsync(document, newRoot, eol, cancellationToken);
        var normalizedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();

        // TODO(Phase 3): wire these into the real Finding channel once it exists instead of ILogger.
        if (logger != null)
        {
            var problems = CheckPostWriteInvariants(normalizedText, triviaIntent, oldNode);
            foreach (var problem in problems)
            {
                logger.LogWarning("ReplaceNodeAndFormatReplacementAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
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
    [Obsolete("Use the eol overload")]
    public static async Task<string> ReplaceNodeAndFormatReplacementAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await ReplaceNodeAndFormatReplacementAsync(document, root, oldNode, newNode, eol, cancellationToken, triviaIntent, logger);
    }

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

    /// <summary>
    /// Batch form of <see cref="ReplaceNodeAndFormatReplacementAsync"/>: replaces every old->new pair in
    /// <paramref name="replacements"/> against one evolving root and formats the whole set in a
    /// single annotated-span pass (<see cref="FormatAnnotatedSpansAsync"/>), instead of one call per pair. Needed because
    /// after the first <c>ReplaceNode</c> call, every other pending old node reference is stale -> the
    /// original node objects are no longer part of the tree being edited. <see cref="SyntaxNode.TrackNodes"/>/
    /// <see cref="SyntaxNode.GetCurrentNode{TNode}"/> re-locates each tracked old node against the
    /// current root before building its replacement, the same idiom already used in
    /// <c>AdvancedRefactoringEngine</c> for chained method-body rewrites. Each old node's leading/
    /// trailing trivia is preserved onto its replacement by default, same as the single-pair overload.
    /// <para>
    /// A replacement whose old node cannot be located (for example because an ancestor of it was replaced earlier in the
    /// same fold, which takes the descendant's pending replacement with it) is not applied and is reported in
    /// <see cref="ReplaceNodesResult.UnlocatedNodes"/>. Callers must check <see cref="ReplaceNodesResult.AllReplacementsApplied"/>
    /// and refuse to write the text when it is false. Edits whose targets can nest should use text-span edits against
    /// one original snapshot instead (see <c>AttributeTextEditBuilder</c>).
    /// </para>
    /// <paramref name="eol"/> is the line ending every changed line takes, instead of the dominant ending of <paramref name="document"/>.
    /// </summary>
    public static async Task<ReplaceNodesResult> ReplaceNodesAndFormatReplacementsAsync(Document document, SyntaxNode root, Dictionary<SyntaxNode, SyntaxNode> replacements, string eol, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld)
    {
        var trackedRoot = root.TrackNodes(replacements.Keys);

        var currentRoot = trackedRoot;
        var unlocatedNodes = new List<SyntaxNode>();
        foreach (var (oldNode, newNode) in replacements)
        {
            var currentOldNode = currentRoot.GetCurrentNode(oldNode);
            if (currentOldNode == null)
            {
                // Never skip silently: an ancestor replaced earlier in this fold takes this node's replacement with it.
                // The caller must see this and refuse to write a result that is missing the edit.
                unlocatedNodes.Add(oldNode);
                continue;
            }

            var leadingTrivia = triviaIntent == TriviaEditIntent.ReplaceLeading ? newNode.GetLeadingTrivia() : currentOldNode.GetLeadingTrivia();
            var trailingTrivia = currentOldNode.GetTrailingTrivia();
            var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(Formatter.Annotation);
            currentRoot = currentRoot.ReplaceNode(currentOldNode, annotatedNewNode);
        }

        var formattedDoc = await FormatAnnotatedSpansAsync(document, currentRoot, eol, cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        return new ReplaceNodesResult(formattedText, unlocatedNodes);
    }

    /// <summary>
    /// Batch form of <see cref="ReplaceNodeAndFormatReplacementAsync"/>: replaces every old->new pair in
    /// <paramref name="replacements"/> against one evolving root and formats the whole set in a
    /// single annotated-span pass (<see cref="FormatAnnotatedSpansAsync"/>), instead of one call per pair. Needed because
    /// after the first <c>ReplaceNode</c> call, every other pending old node reference is stale -> the
    /// original node objects are no longer part of the tree being edited. <see cref="SyntaxNode.TrackNodes"/>/
    /// <see cref="SyntaxNode.GetCurrentNode{TNode}"/> re-locates each tracked old node against the
    /// current root before building its replacement, the same idiom already used in
    /// <c>AdvancedRefactoringEngine</c> for chained method-body rewrites. Each old node's leading/
    /// trailing trivia is preserved onto its replacement by default, same as the single-pair overload.
    /// <para>
    /// A replacement whose old node cannot be located (for example because an ancestor of it was replaced earlier in the
    /// same fold, which takes the descendant's pending replacement with it) is not applied and is reported in
    /// <see cref="ReplaceNodesResult.UnlocatedNodes"/>. Callers must check <see cref="ReplaceNodesResult.AllReplacementsApplied"/>
    /// and refuse to write the text when it is false. Edits whose targets can nest should use text-span edits against
    /// one original snapshot instead (see <c>AttributeTextEditBuilder</c>).
    /// </para>
    /// </summary>
    [Obsolete("Use the eol overload")]
    public static async Task<ReplaceNodesResult> ReplaceNodesAndFormatReplacementsAsync(Document document, SyntaxNode root, Dictionary<SyntaxNode, SyntaxNode> replacements, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld)
    {
        ArgumentNullException.ThrowIfNull(document);
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await ReplaceNodesAndFormatReplacementsAsync(document, root, replacements, eol, cancellationToken, triviaIntent);
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
    /// <param name="removeOptions">
    /// Passed to <c>SyntaxNode.RemoveNode</c>. The default preserves the historical behaviour. Note that the
    /// boundary-token trivia restore runs regardless of this value, so directive-keeping flags
    /// (<see cref="SyntaxRemoveOptions.KeepUnbalancedDirectives"/>, <see cref="SyntaxRemoveOptions.KeepDirectives"/>)
    /// do not survive it: a directive that sat in the removed node's leading trivia is dropped, which can orphan
    /// its matching <c>#endregion</c>/<c>#endif</c>. Not yet safe for callers that need directives kept (C4 slice C4a finding).
    /// </param>
    public static async Task<string> RemoveNodePreservingTriviaAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions removeOptions = SyntaxRemoveOptions.KeepExteriorTrivia, CancellationToken cancellationToken = default, ILogger? logger = null)
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
        var newRoot = annotatedRoot.RemoveNode(trackedNodeToRemove, removeOptions)!;

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
                logger.LogWarning("RemoveNodePreservingTriviaAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
    }

    /// <summary>
    /// Removes <paramref name="nodeToRemove"/> and formats only its former container (the nearest ancestor whose node
    /// identity survives the removal), instead of the whole file, and returns the full text in the line ending <paramref name="eol"/>.
    /// Unlike <see cref="RemoveNodePreservingTriviaAsync"/> there is no boundary-token
    /// trivia restore, so the directive-keeping flags of <paramref name="removeOptions"/>
    /// (<see cref="SyntaxRemoveOptions.KeepUnbalancedDirectives"/>, <see cref="SyntaxRemoveOptions.KeepDirectives"/>) are
    /// honoured: a directive that sat in the removed node's leading trivia stays. The container is annotated with
    /// <see cref="Formatter.Annotation"/> and formatted through <see cref="FormatAnnotatedTextAsync(Document, SyntaxNode, string, CancellationToken)"/>,
    /// so lines the removal did not change keep their terminator and every changed line takes <paramref name="eol"/>
    /// (the formatter's own NewLine option never leaks into the file).
    /// A node with no parent has no container, so the whole document is formatted through
    /// <see cref="FormatWholeDocumentAsync(Document, string, Microsoft.CodeAnalysis.Text.SourceText?, CancellationToken)"/> with <paramref name="document"/> as the line-ending reference.
    /// </summary>
    /// <param name="document">The document before the edit. Supplies the encoding and the formatting options.</param>
    /// <param name="root">The root that contains <paramref name="nodeToRemove"/>; it may already carry edits made to <paramref name="document"/>'s tree.</param>
    /// <param name="nodeToRemove">The node to remove, a node of <paramref name="root"/>.</param>
    /// <param name="removeOptions">Passed to <c>SyntaxNode.RemoveNode</c>.</param>
    /// <param name="eol">The line ending every changed line takes.</param>
    public static async Task<string> RemoveNodeAndFormatContainerAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions removeOptions, string eol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(nodeToRemove);

        var container = nodeToRemove.Parent;
        if (container == null)
        {
            var bareNewRoot = root.RemoveNode(nodeToRemove, removeOptions)!;
            var originalSourceText = await document.GetTextAsync(cancellationToken);
            var bareFormattedDoc = await FormatWholeDocumentAsync(document.WithSyntaxRoot(bareNewRoot), eol, originalSourceText, cancellationToken);
            return (await bareFormattedDoc.GetTextAsync(cancellationToken)).ToString();
        }

        // A private tracking annotation finds the surviving container; Formatter.Annotation marks it for formatting.
        var tracking = new SyntaxAnnotation();
        var annotatedRoot = root.ReplaceNode(container, container.WithAdditionalAnnotations(tracking, Formatter.Annotation));
        var annotatedContainer = annotatedRoot.GetAnnotatedNodes(tracking).Single();
        var trackedNodeToRemove = annotatedContainer.DescendantNodesAndSelf().Single(n => n.IsEquivalentTo(nodeToRemove) && n.Span == nodeToRemove.Span);
        var newRoot = annotatedRoot.RemoveNode(trackedNodeToRemove, removeOptions)!;
        return await FormatAnnotatedTextAsync(document, newRoot, eol, cancellationToken);
    }

    /// <summary>
    /// Removes <paramref name="nodeToRemove"/> and formats only its former container (the nearest ancestor whose node
    /// identity survives the removal), instead of the whole file, and returns the full text in the line ending of
    /// <paramref name="document"/>. Unlike <see cref="RemoveNodePreservingTriviaAsync"/> there is no boundary-token
    /// trivia restore, so the directive-keeping flags of <paramref name="removeOptions"/>
    /// (<see cref="SyntaxRemoveOptions.KeepUnbalancedDirectives"/>, <see cref="SyntaxRemoveOptions.KeepDirectives"/>) are
    /// honoured: a directive that sat in the removed node's leading trivia stays. The container is annotated with
    /// <see cref="Formatter.Annotation"/> and formatted through <see cref="FormatAnnotatedTextAsync(Document, SyntaxNode, CancellationToken)"/>,
    /// so lines the removal did not change keep their terminator and every changed line takes the dominant ending of
    /// <paramref name="document"/> (the formatter's own NewLine option never leaks into the file).
    /// A node with no parent has no container, so the whole document is formatted through
    /// <see cref="FormatWholeDocumentAsync"/> with <paramref name="document"/> as the line-ending reference.
    /// </summary>
    /// <param name="document">The document before the edit. Supplies the dominant line ending, the encoding and the formatting options.</param>
    /// <param name="root">The root that contains <paramref name="nodeToRemove"/>; it may already carry edits made to <paramref name="document"/>'s tree.</param>
    /// <param name="nodeToRemove">The node to remove, a node of <paramref name="root"/>.</param>
    /// <param name="removeOptions">Passed to <c>SyntaxNode.RemoveNode</c>.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<string> RemoveNodeAndFormatContainerAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, SyntaxRemoveOptions removeOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(nodeToRemove);
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await RemoveNodeAndFormatContainerAsync(document, root, nodeToRemove, removeOptions, eol, cancellationToken);
    }

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
    public static async Task<string> InsertMemberDeclarationAndFormatInsertionAsync(Document document, SyntaxNode root, TypeDeclarationSyntax container, int insertIndex, MemberDeclarationSyntax newMember, CancellationToken cancellationToken = default)
    {
        var members = container.Members;
        var precedingMember = insertIndex > 0 ? members[insertIndex - 1] : null;
        var followingMember = insertIndex < members.Count ? members[insertIndex] : null;

        var indentTrivia = (precedingMember ?? followingMember)?.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)) ?? default;
        var indentText = indentTrivia != default ? indentTrivia.ToFullString() : "    ";

        // Only one CRLF is needed here to produce a single blank line: precedingMember's own
        // trailing trivia (standard Roslyn attachment - a token's trailing trivia runs through the
        // end-of-line that terminates its own line) already supplies the line break that ends
        // precedingMember's line. Adding a second CRLF here on top of that one is what previously
        // produced two blank lines instead of one - see MemberInsertAfterEolTests.
        var blankLineBefore = precedingMember != null
            ? new[] { SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Whitespace(indentText) }
            : new[] { SyntaxFactory.Whitespace(indentText) };
        var newMemberWithTrivia = newMember
            .WithLeadingTrivia(blankLineBefore.Concat(newMember.GetLeadingTrivia()))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        var annotatedNewMember = newMemberWithTrivia.WithAdditionalAnnotations(Formatter.Annotation);

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
                // Only one CRLF here for the same reason as blankLineBefore above: newMemberWithTrivia's
                // trailing trivia (set above to a single CarriageReturnLineFeed) already supplies the line
                // break that ends the new member's own line, so this only needs to add the blank line
                // itself, not a second line-ending on top of it.
                var newFollowingLeading = new SyntaxTriviaList(SyntaxFactory.CarriageReturnLineFeed)
                    .AddRange(nonWhitespaceLeading)
                    .Add(SyntaxFactory.Whitespace(followingIndentText));
                var updatedFollowingMember = followingMember.WithLeadingTrivia(newFollowingLeading);
                newMembersList = newMembersList.Replace(newMembersList[insertIndex + 1], updatedFollowingMember);
            }
        }

        var newContainer = container.WithMembers(newMembersList);
        var newRoot = root.ReplaceNode(container, newContainer);

        var formattedDoc = await FormatAnnotatedSpansAsync(document, newRoot, cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// Formats only the nodes of <paramref name="rewrittenRoot"/> that carry <see cref="Formatter.Annotation"/>,
    /// then puts the result in the line ending of <paramref name="document"/> (the document as it was BEFORE the
    /// rewrite). This is the existing-root path of the whole-subtree-whitespace rule: the rewriter adds
    /// <see cref="Formatter.Annotation"/> to the nodes it created or changed and calls this instead of
    /// a whole-subtree <c>NormalizeWhitespace</c> or a whole-document format, so code the rewrite did not touch is not
    /// re-spaced (the one exception is the whitespace and line break immediately before an annotated node, which the
    /// formatter owns and may re-space) and lines the edit did not touch keep their text.
    /// <para>
    /// Only the annotated spans are formatted, but the formatter also rewrites the whitespace and line break
    /// directly in front of an annotated node. It uses its own NewLine option for that (the host default, CRLF on
    /// Windows, unless an .editorconfig says otherwise), which would put a bare CRLF into an LF document. Line
    /// endings are therefore normalized with <see cref="EolUtilities.NormalizeEolOfChangedLines"/> against the
    /// pre-rewrite text: a line that survives unchanged keeps its own terminator (so a stray CRLF in an LF file is
    /// left alone), and every line the rewrite or the formatter added or changed takes the document's dominant
    /// ending. For a single-ending document the result is that ending throughout.
    /// </para>
    /// <para>
    /// No annotation in <paramref name="rewrittenRoot"/> means nothing is formatted: the result is the rewritten
    /// text in the dominant ending. The returned document carries the full result text; callers read it with
    /// <c>GetTextAsync</c>. No formatting options are passed, so the formatter resolves them from the document
    /// (workspace options and any .editorconfig).
    /// </para>
    /// </summary>
    /// <param name="document">The document before the rewrite. Supplies the dominant line ending, the encoding and the pre-image for line matching.</param>
    /// <param name="rewrittenRoot">The root after the rewrite, with <see cref="Formatter.Annotation"/> on the changed nodes.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<Document> FormatAnnotatedSpansAsync(Document document, SyntaxNode rewrittenRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rewrittenRoot);

        var originalSourceText = await document.GetTextAsync(cancellationToken);
        return await FormatAnnotatedSpansAsync(document, rewrittenRoot, EolUtilities.DetectDominantEol(originalSourceText), cancellationToken);
    }

    /// <summary>
    /// Eol-explicit form of <see cref="FormatAnnotatedSpansAsync(Document, SyntaxNode, CancellationToken)"/>: <paramref name="eol"/> is the line ending every changed line takes, instead of the dominant ending of <paramref name="document"/>.
    /// </summary>
    public static async Task<Document> FormatAnnotatedSpansAsync(Document document, SyntaxNode rewrittenRoot, string eol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rewrittenRoot);

        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(rewrittenRoot), Formatter.Annotation, cancellationToken: cancellationToken);
        return await WithChangedLinesInDominantEolAsync(formattedDoc, originalSourceText, eol, cancellationToken);
    }

    /// <summary>
    /// <see cref="FormatAnnotatedSpansAsync"/> followed by <c>GetTextAsync</c>: formats only the nodes of
    /// <paramref name="rewrittenRoot"/> annotated with <see cref="Formatter.Annotation"/> and returns the full text. Lines the
    /// rewrite did not touch keep their text and terminator, and every changed line takes the dominant line ending of
    /// <paramref name="document"/> (the document before the rewrite).
    /// </summary>
    /// <param name="document">The document before the rewrite.</param>
    /// <param name="rewrittenRoot">The root after the rewrite, with <see cref="Formatter.Annotation"/> on the changed nodes.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<string> FormatAnnotatedTextAsync(Document document, SyntaxNode rewrittenRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rewrittenRoot);

        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await FormatAnnotatedTextAsync(document, rewrittenRoot, eol, cancellationToken);
    }

    /// <summary>
    /// Eol-explicit form of the <see cref="Document"/> overload of <c>FormatAnnotatedTextAsync</c>: <paramref name="eol"/> is the line ending every changed line takes.
    /// </summary>
    public static async Task<string> FormatAnnotatedTextAsync(Document document, SyntaxNode rewrittenRoot, string eol, CancellationToken cancellationToken = default)
    {
        var formattedDoc = await FormatAnnotatedSpansAsync(document, rewrittenRoot, eol, cancellationToken);
        return (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
    }

    /// <summary>
    /// The <see cref="Document"/> overload of this method for callers that hold only file text: <paramref name="originalText"/>
    /// is placed in a throwaway <see cref="AdhocWorkspace"/> document purely as the pre-image that
    /// <see cref="FormatAnnotatedSpansAsync"/> needs. That document has no project options and no .editorconfig, so the
    /// formatter uses the workspace defaults. Lines the rewrite did not touch keep their text and terminator, and every
    /// line it added or changed takes the original text's dominant line ending.
    /// </summary>
    /// <param name="originalText">The file text before the rewrite.</param>
    /// <param name="rewrittenRoot">The root after the rewrite, with <see cref="Formatter.Annotation"/> on the changed nodes.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<string> FormatAnnotatedTextAsync(Microsoft.CodeAnalysis.Text.SourceText originalText, SyntaxNode rewrittenRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalText);

        return await FormatAnnotatedTextAsync(originalText, rewrittenRoot, EolUtilities.DetectDominantEol(originalText), cancellationToken);
    }

    /// <summary>
    /// Eol-explicit form of the <see cref="Microsoft.CodeAnalysis.Text.SourceText"/> overload of <c>FormatAnnotatedTextAsync</c>: <paramref name="eol"/> is the line ending every changed line takes.
    /// </summary>
    public static async Task<string> FormatAnnotatedTextAsync(Microsoft.CodeAnalysis.Text.SourceText originalText, SyntaxNode rewrittenRoot, string eol, CancellationToken cancellationToken = default)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("AnnotatedTextFormat", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Source.cs", originalText);
        return await FormatAnnotatedTextAsync(document, rewrittenRoot, eol, cancellationToken);
    }

    /// <summary>
    /// Formats a newly built member (or global statement) that is returned as a standalone snippet, not inserted into
    /// <paramref name="document"/>, and returns its text without surrounding blank lines. The node is annotated with
    /// <see cref="Formatter.Annotation"/> and formatted through <see cref="FormatAnnotatedSpansAsync"/> inside a throwaway
    /// compilation unit. The pre-image handed to it carries only <paramref name="eol"/>, so every line of
    /// the snippet takes that ending and none can inherit a stray terminator by text-matching a line of the document.
    /// </summary>
    /// <param name="document">The document the snippet is meant for. Supplies the encoding, the checksum algorithm and the formatting options.</param>
    /// <param name="member">The newly built member or global statement.</param>
    /// <param name="eol">The line ending every line of the snippet takes.</param>
    public static async Task<string> FormatNewSnippetAsync(Document document, MemberDeclarationSyntax member, string eol, CancellationToken cancellationToken = default)
    {
        var documentText = await document.GetTextAsync(cancellationToken);
        var snippetPreImage = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(eol, documentText.Encoding, documentText.ChecksumAlgorithm));
        var snippetRoot = SyntaxFactory.CompilationUnit().AddMembers(member.WithAdditionalAnnotations(Formatter.Annotation));
        return (await FormatAnnotatedTextAsync(snippetPreImage, snippetRoot, eol, cancellationToken)).Trim();
    }

    /// <summary>
    /// Formats a newly built member (or global statement) that is returned as a standalone snippet, not inserted into
    /// <paramref name="document"/>, and returns its text without surrounding blank lines. The node is annotated with
    /// <see cref="Formatter.Annotation"/> and formatted through <see cref="FormatAnnotatedSpansAsync"/> inside a throwaway
    /// compilation unit. The pre-image handed to it carries only the document's dominant line ending, so every line of
    /// the snippet takes that ending and none can inherit a stray terminator by text-matching a line of the document.
    /// </summary>
    /// <param name="document">The document the snippet is meant for. Supplies the dominant line ending, the encoding and the formatting options.</param>
    /// <param name="member">The newly built member or global statement.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<string> FormatNewSnippetAsync(Document document, MemberDeclarationSyntax member, CancellationToken cancellationToken = default)
    {
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await FormatNewSnippetAsync(document, member, eol, cancellationToken);
    }

    /// <summary>
    /// Formats the WHOLE of <paramref name="document"/>: every token's spacing, indentation and line breaks, not just
    /// what an edit touched. "Whole" is in the name because it is the risky scope: it re-spaces unrelated code and can
    /// shift every line number below the first change. Call it only when the whole document really is being
    /// formatted (a format-document operation, or a file the tool just scaffolded and fully owns). To format what a
    /// rewrite changed, annotate the changed nodes with <see cref="Formatter.Annotation"/> and call
    /// <see cref="FormatAnnotatedSpansAsync"/>; to replace one node, use <see cref="ReplaceNodeAndFormatReplacementAsync"/>.
    /// This is the EOL-aware whole-document chokepoint for edit paths. A few allowlisted sites still call the formatter
    /// on a whole document or root directly, because whole-document formatting is their job (the format-document tools
    /// in <c>BasicRefactoringEngine</c> and <c>MsToolAugmentEngine</c>) or the result is only compared or logged
    /// (<c>PersistentWorkspaceManager.ApplyProposedChangesAsync</c>). <c>WhitespaceRatchetTests</c> fails on any other
    /// raw <c>Formatter.FormatAsync</c>, <c>Formatter.Format</c> or <c>NormalizeWhitespace</c> call in production code
    /// and on a higher raw-call count in an allowlisted member.
    /// <para>
    /// The result is put in the line ending of <paramref name="eolReference"/> when given, otherwise of
    /// <paramref name="document"/> as it is passed in (so a caller that builds the document with
    /// <c>WithSyntaxRoot</c> and passes no reference gets the dominant ending of that edited text, which a
    /// synthesized block of the other ending can flip; pass the pre-edit text as <paramref name="eolReference"/> to
    /// avoid that): lines that survive the format unchanged keep their own terminator, and every line the
    /// formatter added or changed takes the dominant ending (<see cref="EolUtilities.NormalizeEolOfChangedLines"/>).
    /// The formatter's own NewLine option (the host default, CRLF on Windows, unless an .editorconfig says otherwise)
    /// therefore never leaks into an LF document. No formatting options are passed, so the formatter resolves them
    /// from the document (workspace options and any .editorconfig). The returned document carries the full result
    /// text. The encoding and checksum algorithm of <paramref name="eolReference"/> are applied only when the text is
    /// rewritten (some changed line needed its ending corrected); when the formatted text already has the right
    /// endings the formatted document is returned as it is, with its own text and encoding.
    /// </para>
    /// </summary>
    /// <param name="document">The document to format (possibly already carrying an edited root).</param>
    /// <param name="eolReference">Optional pre-edit text of the document. When given it supplies the dominant line ending, the pre-image for the changed-lines comparison, the encoding and the checksum algorithm, so lines the edit or the formatter changed take the original document's ending even if the edited text is dominated by another one. Null means <paramref name="document"/>'s own text.</param>
    [Obsolete("Use the eol overload")]
    public static async Task<Document> FormatWholeDocumentAsync(Document document, Microsoft.CodeAnalysis.Text.SourceText? eolReference = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var originalSourceText = eolReference ?? await document.GetTextAsync(cancellationToken);
        return await FormatWholeDocumentAsync(document, EolUtilities.DetectDominantEol(originalSourceText), eolReference, cancellationToken);
    }

    /// <summary>
    /// Eol-explicit form of <see cref="FormatWholeDocumentAsync(Document, Microsoft.CodeAnalysis.Text.SourceText?, CancellationToken)"/>: <paramref name="eol"/> is the line ending every changed line takes, instead of the dominant ending of <paramref name="eolReference"/> (or of <paramref name="document"/>).
    /// </summary>
    public static async Task<Document> FormatWholeDocumentAsync(Document document, string eol, Microsoft.CodeAnalysis.Text.SourceText? eolReference = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var originalSourceText = eolReference ?? await document.GetTextAsync(cancellationToken);
        var formattedDoc = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
        return await WithChangedLinesInDominantEolAsync(formattedDoc, originalSourceText, eol, cancellationToken);
    }

    /// <summary>
    /// Shared tail of <see cref="FormatAnnotatedSpansAsync"/> and <see cref="FormatWholeDocumentAsync"/>: puts the
    /// formatted text in the dominant line ending of <paramref name="originalSourceText"/> without touching the
    /// terminator of any line that survived unchanged (<see cref="EolUtilities.NormalizeEolOfChangedLines"/>), and keeps
    /// the original encoding and checksum algorithm. Returns <paramref name="formattedDoc"/> itself when nothing changed.
    /// </summary>
    private static async Task<Document> WithChangedLinesInDominantEolAsync(Document formattedDoc, Microsoft.CodeAnalysis.Text.SourceText originalSourceText, string eol, CancellationToken cancellationToken)
    {
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        var normalizedText = EolUtilities.NormalizeEolOfChangedLines(originalSourceText.ToString(), formattedText, eol);
        if (normalizedText == formattedText)
        {
            return formattedDoc;
        }

        return formattedDoc.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(normalizedText, originalSourceText.Encoding, originalSourceText.ChecksumAlgorithm));
    }

    /// <summary>
    /// Formats a NEWLY BUILT <see cref="CompilationUnitSyntax"/> (the root of a file that does not exist yet) that embeds
    /// EXISTING code, and returns its text. It serves roots like the ones built by ExtractSuperclass,
    /// ExtractMembersToPartial and ConvertTupleToClass: the using directives, members or types copied from the source file
    /// carry the user's comments, blank lines and indentation, which <c>NormalizeWhitespace</c> would rewrite.
    /// <para>
    /// <paramref name="isNewNode"/> classifies the nodes of <paramref name="newRoot"/>: <c>true</c> for structure the
    /// caller synthesized (namespace, class, name, braces), <c>false</c> for a node copied from existing source. Only the
    /// DIRECT tokens of new nodes (keywords, identifiers, braces, semicolons) carry <see cref="Formatter.Annotation"/>
    /// and are formatted, via <see cref="FormatAnnotatedSpansAsync"/>. A node classified as existing is skipped with its
    /// whole subtree: its tokens and trivia (comments, blank lines, indentation, inner spacing) are left byte-exact. The
    /// root itself is always treated as new. The formatter still owns the whitespace and line break that sit directly in
    /// front of a new token, so a new brace between two existing members is placed correctly while the members are not
    /// re-spaced.
    /// </para>
    /// <para>
    /// Line endings follow <see cref="FormatAnnotatedSpansAsync"/>: every line the formatter added or changed takes
    /// <paramref name="eol"/>, and a line that survives unchanged keeps its own terminator. When the last token of the root is new, the result ends with a
    /// line break in <paramref name="eol"/>.
    /// </para>
    /// </summary>
    /// <param name="document">The document the embedded code came from. Supplies the pre-image for line matching and the formatting options.</param>
    /// <param name="newRoot">The newly built root, with the existing nodes already embedded.</param>
    /// <param name="isNewNode">True for a node the caller synthesized, false for a node copied from existing source (that node's whole subtree is left untouched).</param>
    /// <param name="eol">The line ending every changed line takes.</param>
    public static async Task<string> FormatNewRootKeepingTriviaAsync(Document document, CompilationUnitSyntax newRoot, Func<SyntaxNode, bool> isNewNode, string eol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newRoot);
        ArgumentNullException.ThrowIfNull(isNewNode);

        var newTokens = new List<SyntaxToken>();
        CollectNewStructureTokens(newRoot, isNewNode, newTokens);
        var annotatedRoot = newRoot.ReplaceTokens(newTokens, static (original, _) => original.WithAdditionalAnnotations(Formatter.Annotation));

        var formattedDoc = await FormatAnnotatedSpansAsync(document, annotatedRoot, eol, cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();

        var lastToken = newRoot.GetLastToken();
        var endsWithNewToken = lastToken != default && newTokens.Contains(lastToken);
        if (endsWithNewToken && formattedText.Length > 0 && formattedText[^1] != '\n' && formattedText[^1] != '\r')
        {
            formattedText += eol;
        }

        return formattedText;
    }

    /// <summary>
    /// Formats a NEWLY BUILT <see cref="CompilationUnitSyntax"/> (the root of a file that does not exist yet) that embeds
    /// EXISTING code, and returns its text. It serves roots like the ones built by ExtractSuperclass,
    /// ExtractMembersToPartial and ConvertTupleToClass: the using directives, members or types copied from the source file
    /// carry the user's comments, blank lines and indentation, which <c>NormalizeWhitespace</c> would rewrite.
    /// <para>
    /// <paramref name="isNewNode"/> classifies the nodes of <paramref name="newRoot"/>: <c>true</c> for structure the
    /// caller synthesized (namespace, class, name, braces), <c>false</c> for a node copied from existing source. Only the
    /// DIRECT tokens of new nodes (keywords, identifiers, braces, semicolons) carry <see cref="Formatter.Annotation"/>
    /// and are formatted, via <see cref="FormatAnnotatedSpansAsync"/>. A node classified as existing is skipped with its
    /// whole subtree: its tokens and trivia (comments, blank lines, indentation, inner spacing) are left byte-exact. The
    /// root itself is always treated as new. The formatter still owns the whitespace and line break that sit directly in
    /// front of a new token, so a new brace between two existing members is placed correctly while the members are not
    /// re-spaced.
    /// </para>
    /// <para>
    /// Line endings follow <see cref="FormatAnnotatedSpansAsync"/>: <paramref name="document"/> is the SOURCE document the
    /// existing code came from; a line that survives unchanged keeps its own terminator and every line the formatter added
    /// or changed takes that document's dominant ending. When the last token of the root is new, the result ends with a
    /// line break in that dominant ending.
    /// </para>
    /// </summary>
    /// <param name="document">The document the embedded code came from. Supplies the dominant line ending, the pre-image for line matching and the formatting options.</param>
    /// <param name="newRoot">The newly built root, with the existing nodes already embedded.</param>
    /// <param name="isNewNode">True for a node the caller synthesized, false for a node copied from existing source (that node's whole subtree is left untouched).</param>
    [Obsolete("Use the eol overload")]
    public static async Task<string> FormatNewRootKeepingTriviaAsync(Document document, CompilationUnitSyntax newRoot, Func<SyntaxNode, bool> isNewNode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newRoot);
        ArgumentNullException.ThrowIfNull(isNewNode);
        var eol = EolUtilities.DetectDominantEol(await document.GetTextAsync(cancellationToken));
        return await FormatNewRootKeepingTriviaAsync(document, newRoot, isNewNode, eol, cancellationToken);
    }

    /// <summary>
    /// Collects the direct tokens of every node <paramref name="isNewNode"/> accepts, depth first. A node it rejects is
    /// skipped with its whole subtree. Zero-width and end-of-file tokens are never collected: they carry no text and the
    /// end-of-file token's leading trivia belongs to whatever precedes it.
    /// </summary>
    private static void CollectNewStructureTokens(SyntaxNode node, Func<SyntaxNode, bool> isNewNode, List<SyntaxToken> collected)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var token = child.AsToken();
                if (!token.IsMissing && token.Span.Length > 0 && !token.IsKind(SyntaxKind.EndOfFileToken))
                {
                    collected.Add(token);
                }
            }
            else if (isNewNode(child.AsNode()!))
            {
                CollectNewStructureTokens(child.AsNode()!, isNewNode, collected);
            }
        }
    }

    /// <summary>
    /// Returns a parentless copy of <paramref name="node"/> (taken from existing source, or parsed from caller text
    /// inside a dummy container) with EVERY piece of trivia removed: whitespace, end-of-line, comments and directives.
    /// Use it to embed a reused or parsed part (for example a parameter list or return type) in a newly built node.
    /// Comments are dropped silently, so call this only where losing them is acceptable (for example signature parts
    /// reused in a generated interface member).
    /// </summary>
    public static T StripAllTrivia<T>(T node)
        where T : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(node);

        // WithoutTrivia drops the leading trivia of the first token and the trailing trivia of the last token, which
        // the descendant-trivia pass does not reach; the pass then removes the rest.
        var detached = node.WithoutTrivia();
        var removable = detached.DescendantTrivia(descendIntoTrivia: false).ToList();
        return removable.Count == 0 ? detached : detached.ReplaceTrivia(removable, (_, _) => default);
    }
}

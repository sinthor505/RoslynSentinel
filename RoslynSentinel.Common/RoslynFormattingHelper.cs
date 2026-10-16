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
    /// </summary>
    public static async Task<string> ReplaceNodeAndFormatReplacementAsync(Document document, SyntaxNode root, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld, ILogger? logger = null)
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
                logger.LogWarning("ReplaceNodeAndFormatReplacementAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
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
    /// single <see cref="Formatter.FormatAsync"/> pass, instead of one call per pair. Needed because
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
    public static async Task<ReplaceNodesResult> ReplaceNodesAndFormatReplacementsAsync(Document document, SyntaxNode root, Dictionary<SyntaxNode, SyntaxNode> replacements, CancellationToken cancellationToken = default, TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld)
    {
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

        var trackedRoot = root.TrackNodes(replacements.Keys);
        var annotation = new SyntaxAnnotation();

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
            var annotatedNewNode = newNode.WithLeadingTrivia(leadingTrivia).WithTrailingTrivia(trailingTrivia).WithAdditionalAnnotations(annotation);
            currentRoot = currentRoot.ReplaceNode(currentOldNode, annotatedNewNode);
        }

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(currentRoot), annotation, cancellationToken: cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        return new ReplaceNodesResult(EolUtilities.NormalizeEol(formattedText, dominantEol), unlocatedNodes);
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
    public static async Task<string> RemoveNodePreservingTriviaAsync(Document document, SyntaxNode root, SyntaxNode nodeToRemove, CancellationToken cancellationToken = default, ILogger? logger = null)
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
                logger.LogWarning("RemoveNodePreservingTriviaAsync post-write invariant violation: {Problem}", problem);
            }
        }

        return normalizedText;
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
        var originalSourceText = await document.GetTextAsync(cancellationToken);
        var dominantEol = EolUtilities.DetectDominantEol(originalSourceText);

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

        var formattedDoc = await Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, cancellationToken: cancellationToken);
        var formattedText = (await formattedDoc.GetTextAsync(cancellationToken)).ToString();
        return EolUtilities.NormalizeEol(formattedText, dominantEol);
    }

    /// <summary>
    /// Pure pass-through to <see cref="SyntaxNodeExtensions.NormalizeWhitespace{TNode}"/> (via
    /// <see cref="SyntaxNode.NormalizeWhitespace"/>) with identical arguments and identical
    /// behavior -> this method exists purely as a single, greppable chokepoint for the whole-subtree
    /// form of whitespace normalization, not as a safety fix.
    /// <para>
    /// Unlike <see cref="ReplaceNodeAndFormatReplacementAsync"/>, <see cref="RemoveNodePreservingTriviaAsync"/>, and
    /// <see cref="InsertMemberDeclarationAndFormatInsertionAsync"/> -> which are narrowly scoped to just the node being
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
    /// <c>MemberRefactoringEngine.AddMemberAsync</c> by introducing <see cref="InsertMemberDeclarationAndFormatInsertionAsync"/>
    /// (see docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md).</item>
    /// </list>
    /// <para>
    /// For an edit-then-format use case on an existing tree, prefer
    /// <see cref="InsertMemberDeclarationAndFormatInsertionAsync"/>/<see cref="ReplaceNodeAndFormatReplacementAsync"/>/
    /// <see cref="RemoveNodePreservingTriviaAsync"/> instead of this method -> they scope the formatting
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

    /// <summary>
    /// Rebuilds the canonical whitespace layout of a NEWLY BUILT member, statement or other child node
    /// (one made from scratch with <see cref="SyntaxFactory"/>, never part of a user file), using the
    /// caller's <paramref name="eol"/>. <c>NormalizeWhitespace</c> discards all existing formatting, so the
    /// node is first checked by <c>EnsureNodeIsNew</c> and an <see cref="ArgumentException"/> is thrown
    /// when it looks like existing code (or is a <see cref="CompilationUnitSyntax"/>, which belongs to
    /// <see cref="NormalizeNewRootWhitespace"/>). <paramref name="eol"/> has no default: pass the target
    /// file's or project's line ending.
    /// <para>
    /// Content-check exemption: a whitespace trivia that is exactly one space (what <c>SyntaxFactory.Space</c>
    /// and a parsed single-line snippet carry) is not counted as user formatting. End-of-line, comment and
    /// longer whitespace (indentation) trivia that is not elastic are still rejected.
    /// </para>
    /// </summary>
    public static T NormalizeNewChildWhitespace<T>(T node, string eol, string indentation = "    ", bool elasticTrivia = true)
        where T : SyntaxNode
    {
        ValidateEol(eol);
        ArgumentNullException.ThrowIfNull(node);
        if (node is CompilationUnitSyntax)
        {
            throw new ArgumentException(
                "NormalizeNewChildWhitespace does not accept a CompilationUnitSyntax; use NormalizeNewRootWhitespace for a file root built from scratch.",
                nameof(node));
        }

        EnsureNodeIsNew(node, nameof(node));
        return node.NormalizeWhitespace(indentation, eol, elasticTrivia);
    }

    /// <summary>
    /// Rebuilds the canonical whitespace layout of a NEWLY BUILT <see cref="CompilationUnitSyntax"/> (the
    /// root of a file that does not exist yet), using the caller's <paramref name="eol"/>. A compilation
    /// unit has the same CLR type whether it is new or the root of an existing file, so the node is
    /// checked by <c>EnsureNodeIsNew</c> and an <see cref="ArgumentException"/> is thrown when it looks
    /// like an existing file's root. <paramref name="eol"/> has no default: pass the target file's or
    /// project's line ending. The content check exempts single-space whitespace trivia, exactly as
    /// described on <see cref="NormalizeNewChildWhitespace{T}"/>.
    /// </summary>
    public static CompilationUnitSyntax NormalizeNewRootWhitespace(CompilationUnitSyntax root, string eol, string indentation = "    ", bool elasticTrivia = true)
    {
        ValidateEol(eol);
        EnsureNodeIsNew(root, nameof(root));
        return root.NormalizeWhitespace(indentation, eol, elasticTrivia);
    }

    /// <summary>
    /// Returns a parentless copy of <paramref name="node"/> (taken from existing source, or parsed from caller text
    /// inside a dummy container) with EVERY piece of trivia removed: whitespace, end-of-line, comments and directives.
    /// Use it to embed a reused or parsed part in a newly built node so the node passes the
    /// <see cref="NormalizeNewChildWhitespace{T}"/> guard; <c>NormalizeWhitespace</c> then rebuilds all spacing.
    /// Comments are dropped silently, so call this only where losing them is acceptable (for example signature parts
    /// reused in a generated interface member). To reject comments instead, use
    /// <see cref="StripFormattingTriviaForNormalize{T}"/>.
    /// </summary>
    public static T StripAllTriviaForNormalize<T>(T node)
        where T : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(node);
        return DetachForNormalize(node, keepComments: false);
    }

    /// <summary>
    /// Returns a parentless copy of <paramref name="node"/> with only its whitespace and end-of-line trivia removed.
    /// Every other trivia kind (comments, documentation comments, directives, disabled text, skipped tokens) is kept
    /// wherever it sits, including on the node's first and last token, so the
    /// <see cref="NormalizeNewChildWhitespace{T}"/> guard rejects it with an <see cref="ArgumentException"/> instead of it
    /// being dropped silently at the boundary yet kept (and rejected) in the middle. Use it for text a caller
    /// supplied, where there is no user formatting to preserve but a comment must not vanish unnoticed. To drop
    /// comments as well, use <see cref="StripAllTriviaForNormalize{T}"/>.
    /// </summary>
    public static T StripFormattingTriviaForNormalize<T>(T node)
        where T : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(node);
        return DetachForNormalize(node, keepComments: true);
    }

    /// <summary>
    /// Shared core of <see cref="StripAllTriviaForNormalize{T}"/> and <see cref="StripFormattingTriviaForNormalize{T}"/>.
    /// <c>WithoutTrivia</c> is applied first because it drops the leading trivia of the first token and the trailing
    /// trivia of the last token (which the descendant-trivia pass below does not reach), so when comments are kept the
    /// non-formatting boundary trivia is captured beforehand and put back on the copy. The copy is parentless as a side
    /// effect, but that is not the purpose: a node a caller embeds in a new member becomes part of that member's fresh
    /// root anyway. The strip exists to remove comments, newlines and indentation so the
    /// <see cref="NormalizeNewChildWhitespace{T}"/> guard accepts the result.
    /// </summary>
    private static T DetachForNormalize<T>(T node, bool keepComments)
        where T : SyntaxNode
    {
        static bool IsFormatting(SyntaxTrivia trivia) => trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);

        var keptLeading = keepComments
            ? node.GetFirstToken(includeZeroWidth: true).LeadingTrivia.Where(t => !IsFormatting(t)).ToList()
            : [];
        var keptTrailing = keepComments
            ? node.GetLastToken(includeZeroWidth: true).TrailingTrivia.Where(t => !IsFormatting(t)).ToList()
            : [];

        var detached = node.WithoutTrivia();
        if (keptLeading.Count > 0)
        {
            detached = detached.WithLeadingTrivia(keptLeading);
        }

        if (keptTrailing.Count > 0)
        {
            detached = detached.WithTrailingTrivia(keptTrailing);
        }

        var removable = detached.DescendantTrivia(descendIntoTrivia: false)
            .Where(t => !keepComments || IsFormatting(t))
            .ToList();
        return removable.Count == 0 ? detached : detached.ReplaceTrivia(removable, (_, _) => default);
    }

    /// <summary>
    /// Shared guard for <see cref="NormalizeNewChildWhitespace{T}"/> and <see cref="NormalizeNewRootWhitespace"/>.
    /// Rejects, in order: (2) a node attached to a tree (<see cref="SyntaxNode.Parent"/> is not null),
    /// (3) a node whose <see cref="SyntaxTree.FilePath"/> is non-empty (it came from a file), and
    /// (4) a subtree that carries user formatting (non-elastic whitespace other than a single space,
    /// end-of-line or comment trivia), because <c>NormalizeWhitespace</c> would discard it. Check 1 (a
    /// <see cref="CompilationUnitSyntax"/> passed to the child method) is made by the child method itself.
    /// </summary>
    private static void EnsureNodeIsNew(SyntaxNode node, string paramName)
    {
        ArgumentNullException.ThrowIfNull(node, paramName);

        const string ExistingCodeHint = "This looks like existing code; use ReplaceNodeAndFormatReplacementAsync, or annotate the new node with Formatter.Annotation and format that span.";

        // Check 2: attached to a tree, so it is not new.
        if (node.Parent is not null)
        {
            throw new ArgumentException(
                $"The node is attached to a syntax tree (its parent is a {node.Parent.Kind()}), so it is not a newly built node. {ExistingCodeHint}",
                paramName);
        }

        // Check 3: its tree came from a file. The file name is deliberately not echoed.
        var filePath = node.SyntaxTree.FilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException(
                $"The node belongs to a syntax tree that has a file path, so it is part of an existing file. {ExistingCodeHint}",
                paramName);
        }

        // Check 4: user formatting present.
        // Whitespace and end-of-line trivia: elastic trivia is what SyntaxFactory synthesizes, so it is fine,
        // and so is a whitespace trivia that is exactly one space. SyntaxFactory.Space and a parsed
        // single-line snippet produce it, and it is harmless because NormalizeWhitespace rebuilds inter-token
        // spacing anyway. This is the narrowest rule the public API allows: SyntaxFactory.ElasticWhitespace(text)
        // carries no elastic annotation and is indistinguishable from SyntaxFactory.Whitespace(text), so
        // multi-character whitespace (indentation) is still counted.
        // Every other trivia kind (comments, documentation comments, directives, disabled text, skipped tokens,
        // conflict markers) is always counted, whatever its annotations: the elastic skip must not hide it.
        // DescendantTrivia(descendIntoTrivia: false) yields a directive as one trivia, so an end-of-line that
        // lives inside a directive's end-of-directive token is covered by counting the directive itself.
        var formattingTriviaCount = 0;
        foreach (var trivia in node.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (trivia.HasAnnotation(SyntaxAnnotation.ElasticAnnotation))
                {
                    continue;
                }

                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) && trivia.ToString() == " ")
                {
                    continue;
                }

                formattingTriviaCount++;
                continue;
            }

            if (trivia.IsDirective
                || trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                || trivia.IsKind(SyntaxKind.SkippedTokensTrivia)
                || trivia.IsKind(SyntaxKind.ConflictMarkerTrivia))
            {
                formattingTriviaCount++;
            }
        }

        if (formattingTriviaCount > 0)
        {
            throw new ArgumentException(
                $"The node contains {formattingTriviaCount} piece(s) of non-elastic whitespace (other than a single space), end-of-line, comment or directive trivia, which is existing formatting that NormalizeWhitespace would discard. {ExistingCodeHint}",
                paramName);
        }
    }

    /// <summary>
    /// Shared guard for the public <c>NormalizeNew*Whitespace</c> methods: <paramref name="eol"/> must be
    /// exactly "\n" or "\r\n". Null throws <see cref="ArgumentNullException"/>; an empty or any other string
    /// throws <see cref="ArgumentException"/>. Both carry the parameter name <c>eol</c>.
    /// </summary>
    private static void ValidateEol(string eol)
    {
        ArgumentNullException.ThrowIfNull(eol, nameof(eol));
        if (eol != "\n" && eol != "\r\n")
        {
            throw new ArgumentException(
                "The end-of-line sequence must be exactly \"\\n\" or \"\\r\\n\"; pass the target file's or project's line ending.",
                nameof(eol));
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using ModelContextProtocol;

namespace RoslynSentinel.Advanced;

/// <summary>
/// Server-orchestrated commenting: seeds every member in scope with a sentinel
/// <c>[ContentHash("Comment", "00000000")]</c> if it has none, then generates
/// <c>/// &lt;summary&gt;</c> comments (via an LLM) for every member whose hash doesn't match its
/// current content. Progress is a queryable fact (the attributes on disk), not a claim carried in
/// an agent's own context — repeated calls resume for free.
/// </summary>
public class CommentingEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly RefactoringEngine _refactoringEngine;
    private readonly ILlmClient _llmClient;

    private const string CommentSystemPrompt =
        "You write a single-line XML documentation summary describing what the given C# member " +
        "does. Reply with ONLY the summary sentence itself — no XML tags, no markdown, no leading " +
        "'Summary:' label, no trailing period-only filler. Keep it under 25 words.";

    public CommentingEngine(ISolutionProvider workspaceManager, RefactoringEngine refactoringEngine, ILlmClient llmClient)
    {
        _workspaceManager = workspaceManager;
        _refactoringEngine = refactoringEngine;
        _llmClient = llmClient;
    }

    /// <summary>One member found while walking scope, with everything needed to seed/comment it.</summary>
    public sealed record MemberSite(
        FilePath FilePath,
        string ProjectName,
        string MemberName,
        string ContextSnippet,
        string? LineBefore,
        MemberDeclarationSyntax Node,
        string? ExistingHash);

    // ── Phase 1: seed ────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps every member in scope that has no <c>[ContentHash("Comment", ...)]</c> at all with
    /// the sentinel hash. Batched per-file (one evolving root, one write per file) so this is safe
    /// to run solution-wide in a single call.
    /// </summary>
    public async Task<(Dictionary<FilePath, string> Changes, int SeededCount, int AlreadyTaggedCount)> SeedContentHashesAsync(
        ToolScope scope, string? projectName, string? filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var documents = EnumerateScopedDocuments(solution, scope, projectName, filePath);

        var changes = new Dictionary<FilePath, string>();
        int seeded = 0;
        int alreadyTagged = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            bool fileChanged = false;
            var currentRoot = root;

            foreach (var member in currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsTaggableMember).ToList())
            {
                // Re-resolve the member on the possibly-already-rewritten root by identity span text,
                // since ReplaceNode invalidates earlier node references once the tree changes.
                var liveMember = FindEquivalentMember(currentRoot, member);
                if (liveMember == null)
                {
                    continue;
                }

                if (HasContentHashAttribute(liveMember, out _))
                {
                    alreadyTagged++;
                    continue;
                }

                var newAttr = BuildContentHashAttribute(ContentHashAttributeSource.SeedHash);
                var updatedMember = AddAttribute(liveMember, newAttr);
                currentRoot = currentRoot.ReplaceNode(liveMember, updatedMember);
                fileChanged = true;
                seeded++;
            }

            if (fileChanged)
            {
                var finalSource = currentRoot.NormalizeWhitespace().ToFullString();
                changes[document.FilePath] = finalSource;
            }
        }

        if (changes.Count > 0)
        {
            InjectAttributeClassIfMissing(solution, documents, changes);
        }

        return (changes, seeded, alreadyTagged);
    }

    // ── Phase 2: find stale work ────────────────────────────────────────────

    /// <summary>
    /// Finds every taggable member in scope whose <c>[ContentHash]</c> hash doesn't match its
    /// current content (including members with no tag at all — treated as maximally stale).
    /// Syntax-level only, mirrors <c>FindMigrationCandidatesAsync</c>'s shape.
    /// </summary>
    public async Task<List<MemberSite>> FindStaleMembersAsync(
        ToolScope scope, string? projectName, string? filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
        var documents = EnumerateScopedDocuments(solution, scope, projectName, filePath);

        var stale = new List<MemberSite>();

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath == null)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var sourceText = await document.GetTextAsync(cancellationToken);
            if (root == null || sourceText == null)
            {
                continue;
            }

            foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsTaggableMember))
            {
                var hasTag = HasContentHashAttribute(member, out var existingHash);
                var currentHash = ComputeStableContentHash(member);
                if (hasTag && existingHash == currentHash)
                {
                    continue; // up to date
                }

                var memberName = GetTaggableMemberName(member);
                if (memberName == null)
                {
                    continue;
                }

                var lineSpan = member.GetLocation().GetLineSpan();
                var lines = sourceText.Lines;
                var declLineIndex = lineSpan.StartLinePosition.Line;
                var declLineText = lines[declLineIndex].ToString();
                string? lineBefore = declLineIndex > 0 ? lines[declLineIndex - 1].ToString() : null;

                stale.Add(new MemberSite(
                    FilePath: document.FilePath,
                    ProjectName: document.Project.Name,
                    MemberName: memberName,
                    ContextSnippet: declLineText,
                    LineBefore: lineBefore,
                    Node: member,
                    ExistingHash: hasTag ? existingHash : null));
            }
        }

        return stale;
    }

    // ── Phase 2: comment one file's worth of stale members ──────────────────

    /// <summary>Per-member outcome from <see cref="CommentFileAsync"/>.</summary>
    public sealed record MemberCommentOutcome(MemberSite Site, bool Succeeded, string? FailureReason);

    /// <summary>
    /// Comments every stale member belonging to one file against a local, in-memory <see
    /// cref="Solution"/> fork (seeded once from <paramref name="baseSolution"/>): each member's
    /// LLM-generated summary is applied via <see cref="RefactoringEngine.AddSummaryCommentCoreAsync"/>
    /// against that fork directly, so member N+1 sees member N's comment even though nothing has
    /// been written to disk yet. Stamps <c>[ContentHash]</c> in the same pass as each member's
    /// comment so comment and hash never desync. Returns the final file text (or null if no member
    /// in <paramref name="fileMembers"/> succeeded) plus a per-member outcome list — the caller
    /// applies the returned text once, at file granularity, via <c>ApplyProposedChangesAsync</c>.
    ///
    /// Alternative considered: instead of re-invoking AddSummaryCommentCoreAsync per member against
    /// a re-synced local Solution fork, operate on one running SyntaxNode/text in memory for the
    /// whole file and never touch a Solution/Document at all between members. That avoids the
    /// per-member WithDocumentText/GetSyntaxRootAsync round-trip but bypasses AddSummaryComment's
    /// existing snippet-disambiguation machinery entirely, so it would need its own re-implementation
    /// of member re-location. Worth revisiting once BulkComment's real-world performance on
    /// multi-member files is measured — for now, correctness/reuse win over the extra round-trips.
    /// </summary>
    public async Task<(string? FinalText, List<MemberCommentOutcome> Outcomes)> CommentFileAsync(
        Solution baseSolution, FilePath filePath, IReadOnlyList<MemberSite> fileMembers, int maxTokens, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<MemberCommentOutcome>();
        var document = baseSolution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return (null, fileMembers.Select(m => new MemberCommentOutcome(m, false, "Document not found.")).ToList());
        }

        string? lastGoodText = null;

        foreach (var site in fileMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string summary;
            try
            {
                var memberText = site.Node.ToFullString().Trim();
                summary = await _llmClient.CompleteAsync(CommentSystemPrompt, memberText, maxTokens, cancellationToken);
            }
            catch (Exception ex)
            {
                outcomes.Add(new MemberCommentOutcome(site, false, $"LLM call failed: {ex.Message}"));
                continue;
            }

            var editResult = await _refactoringEngine.AddSummaryCommentCoreAsync(
                document, site.FilePath, site.MemberName, summary, site.ContextSnippet, site.LineBefore, null, cancellationToken);

            if (editResult.Outcome != EditOutcome.Modified || editResult.UpdatedText == null)
            {
                outcomes.Add(new MemberCommentOutcome(site, false, $"AddSummaryComment failed: {editResult.Outcome} — {editResult.Message}"));
                continue;
            }

            // Re-parse the updated text to compute the post-comment hash and stamp it in the same
            // pass, then re-sync the local document to this member's result so the next member's
            // AddSummaryCommentCoreAsync call (above) sees this one's comment already in place.
            var newTree = CSharpSyntaxTree.ParseText(editResult.UpdatedText);
            var newRoot = await newTree.GetRootAsync(cancellationToken);
            var newMember = FindEquivalentMemberByName(newRoot, site.Node, site.MemberName);
            if (newMember == null)
            {
                outcomes.Add(new MemberCommentOutcome(site, false, "Could not re-locate member after AddSummaryComment to stamp ContentHash."));
                continue;
            }

            // Hash the member's body as it stood before the doc-comment was added (the comment
            // itself isn't part of "content" for staleness purposes — only a body edit invalidates it).
            // Deliberately does NOT call NormalizeWhitespace() on the result: newMember's leading
            // trivia already holds the doc comment AddSummaryCommentCoreAsync just spliced in by hand
            // (see the matching warning at RefactoringEngine.AddSummaryCommentCoreAsync), and a
            // whole-tree normalize pass corrupts/drops that hand-built trivia. AddAttribute below
            // gives the new [ContentHash] attribute list its own indent/newline trivia explicitly
            // instead, so the tree never needs a blanket re-format. ComputeStableContentHash (not
            // ContentHasher.ComputeHash directly) strips both the old [ContentHash] attribute and any
            // doc-comment trivia before hashing, so this matches what FindStaleMembersAsync will
            // recompute later from the post-write member on disk — hashing site.Node as-is here would
            // bake in the old attribute's own (self-referential) hash value, which can never match.
            var contentHash = ComputeStableContentHash(site.Node);
            var newAttr = BuildContentHashAttribute(contentHash);
            var strippedMember = RemoveContentHashAttribute(newMember);
            var taggedMember = AddAttribute(strippedMember, newAttr);
            var finalRoot = newRoot.ReplaceNode(newMember, taggedMember);
            var finalText = finalRoot.ToFullString();

            document = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(finalText));
            lastGoodText = finalText;
            outcomes.Add(new MemberCommentOutcome(site, true, null));
        }

        return (lastGoodText, outcomes);
    }

    // ── Scope enumeration ────────────────────────────────────────────────────

    private static IReadOnlyList<Document> EnumerateScopedDocuments(Solution solution, ToolScope scope, string? projectName, string? filePath)
    {
        var projects = solution.Projects.AsEnumerable();
        if (scope == ToolScope.project || scope == ToolScope.file)
        {
            // file scope still needs a document lookup below; project filter only applies when a
            // projectName was actually supplied (file scope may omit it and rely on filePath alone).
            if (!string.IsNullOrEmpty(projectName))
            {
                projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            }
        }

        var documents = projects.SelectMany(p => p.Documents);

        if (scope == ToolScope.file && !string.IsNullOrEmpty(filePath))
        {
            var normalizedFilter = filePath.Replace('\\', '/');
            documents = documents.Where(d => d.FilePath != null &&
                d.FilePath.Replace('\\', '/').EndsWith(normalizedFilter, StringComparison.OrdinalIgnoreCase));
        }

        return documents.Where(d => d.FilePath != null && d.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            // Never walk into a generated attribute-carrier file itself.
            .Where(d => !Path.GetFileName(d.FilePath!).Equals($"{ContentHashAttributeSource.FullName}.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ── Syntax helpers ───────────────────────────────────────────────────────

    private static bool IsTaggableMember(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => true,
        ConstructorDeclarationSyntax => true,
        PropertyDeclarationSyntax => true,
        EnumDeclarationSyntax => true,
        _ => false,
    };

    private static string? GetTaggableMemberName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EnumDeclarationSyntax e => e.Identifier.Text,
        _ => null,
    };

    private static bool HasContentHashAttribute(MemberDeclarationSyntax member, out string? hash)
    {
        foreach (var attrList in member.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name != ContentHashAttributeSource.ShortName && name != ContentHashAttributeSource.FullName)
                {
                    continue;
                }

                var args = attr.ArgumentList?.Arguments.Where(a => a.NameEquals == null).ToList();
                var purposeArg = args?.ElementAtOrDefault(0);
                var purpose = (purposeArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
                if (purpose != nameof(ContentHashPurpose.Comment))
                {
                    continue;
                }

                var hashArg = args?.ElementAtOrDefault(1);
                hash = (hashArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
                return true;
            }
        }

        hash = null;
        return false;
    }

    private static AttributeSyntax BuildContentHashAttribute(string hash)
    {
        var arguments = new List<AttributeArgumentSyntax>
        {
            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(nameof(ContentHashPurpose.Comment)))),
            SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(hash))),
        };

        return SyntaxFactory.Attribute(
            SyntaxFactory.IdentifierName(ContentHashAttributeSource.ShortName),
            SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    /// <summary>Strips any existing <c>[ContentHash("Comment", ...)]</c> from <paramref name="member"/>, then appends <paramref name="newAttribute"/>.</summary>
    private static MemberDeclarationSyntax AddAttribute(MemberDeclarationSyntax member, AttributeSyntax newAttribute)
    {
        // Give the new attribute list the member's own indentation and a trailing newline explicitly
        // — callers of this helper render the result via ToFullString() with no NormalizeWhitespace()
        // pass to fix it up after the fact (see CommentFileAsync), so the trivia has to be right here.
        var indentTrivia = member.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var newAttrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(newAttribute))
            .WithLeadingTrivia(indentTrivia)
            .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
        return member.AddAttributeLists(newAttrList);
    }

    private static MemberDeclarationSyntax RemoveContentHashAttribute(MemberDeclarationSyntax member)
    {
        // member's leading trivia (indentation, and — critically — a doc comment AddSummaryCommentCoreAsync
        // may have just spliced in) is physically attached to the first token of the member's first
        // AttributeList, not to the member node itself, whenever one is already present. Dropping that
        // list wholesale (below, when it filters down to zero attributes) would silently discard that
        // trivia along with it, so it's captured up front and reattached to whatever becomes the new
        // first token afterward.
        var originalLeadingTrivia = member.GetLeadingTrivia();

        var strippedLists = SyntaxFactory.List(
            member.AttributeLists
                .Select(al =>
                {
                    var filtered = al.Attributes.Where(a =>
                    {
                        var n = a.Name.ToString();
                        if (n != ContentHashAttributeSource.ShortName && n != ContentHashAttributeSource.FullName)
                        {
                            return true;
                        }

                        var args = a.ArgumentList?.Arguments.Where(arg => arg.NameEquals == null).ToList();
                        var purpose = (args?.ElementAtOrDefault(0)?.Expression as LiteralExpressionSyntax)?.Token.ValueText;
                        return purpose != nameof(ContentHashPurpose.Comment); // keep — different purpose
                    }).ToList();

                    if (filtered.Count == al.Attributes.Count)
                    {
                        return al;
                    }

                    return filtered.Count == 0 ? null : al.WithAttributes(SyntaxFactory.SeparatedList(filtered));
                })
                .Where(al => al != null)
                .Select(al => al!));

        var strippedMember = member.WithAttributeLists(strippedLists);
        return strippedLists.Count == member.AttributeLists.Count
            ? strippedMember
            : strippedMember.WithLeadingTrivia(originalLeadingTrivia);
    }

    /// <summary>
    /// Hashes <paramref name="member"/>'s content for staleness comparison, excluding both the
    /// <c>[ContentHash]</c> attribute itself (its value is self-referential — including it would
    /// mean the hash never stabilizes) and any doc-comment trivia (per this engine's own contract,
    /// the comment is tracked *output*, not input — only a body edit should invalidate the hash).
    /// Must be used at every hash-compute site in this file so a hash stamped by
    /// <see cref="CommentFileAsync"/> can still match what <see cref="FindStaleMembersAsync"/>
    /// recomputes from the same member after the comment/attribute have landed on disk.
    /// </summary>
    private static string ComputeStableContentHash(MemberDeclarationSyntax member)
    {
        var stripped = RemoveContentHashAttribute(member);
        var withoutDocComment = StripDocCommentTrivia(stripped);
        return ContentHasher.ComputeHash(withoutDocComment);
    }

    /// <summary>Removes any <c>/// ...</c> single-line doc-comment trivia from <paramref name="member"/>'s leading trivia, keeping indentation/newlines intact.</summary>
    private static MemberDeclarationSyntax StripDocCommentTrivia(MemberDeclarationSyntax member)
    {
        var leadingTrivia = member.GetLeadingTrivia();
        var filtered = SyntaxFactory.TriviaList(leadingTrivia.Where(t =>
            !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
            !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)));

        return filtered.Count == leadingTrivia.Count ? member : member.WithLeadingTrivia(filtered);
    }

    /// <summary>Re-finds a member on a possibly-rewritten root by matching declaration kind + identifier text + original span start (best-effort, since ReplaceNode invalidates old node references).</summary>
    private static MemberDeclarationSyntax? FindEquivalentMember(SyntaxNode currentRoot, MemberDeclarationSyntax original)
    {
        var originalName = GetTaggableMemberName(original);
        if (originalName == null)
        {
            return null;
        }

        return currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(IsTaggableMember)
            .FirstOrDefault(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == originalName && m.SpanStart == original.SpanStart)
            // SpanStart can drift once earlier members in the same file were rewritten; fall back to
            // first same-kind/same-name match, which is safe because SeedContentHashesAsync only
            // reaches this fallback within a single member's own iteration (each member is looked up
            // once, immediately before it's rewritten).
            ?? currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(IsTaggableMember)
                .FirstOrDefault(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == originalName);
    }

    private static MemberDeclarationSyntax? FindEquivalentMemberByName(SyntaxNode root, MemberDeclarationSyntax original, string memberName)
    {
        return root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(IsTaggableMember)
            .FirstOrDefault(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == memberName);
    }

    // ── Attribute class injection ────────────────────────────────────────────

    private static void InjectAttributeClassIfMissing(Solution solution, IReadOnlyList<Document> scopedDocuments, Dictionary<FilePath, string> changes)
    {
        var alreadyDefined = solution.Projects
            .SelectMany(p => p.Documents)
            .Any(d => d.FilePath != null &&
                      Path.GetFileName(d.FilePath).Equals($"{ContentHashAttributeSource.FullName}.cs", StringComparison.OrdinalIgnoreCase));

        if (alreadyDefined)
        {
            return;
        }

        // Inject once per project touched by this batch — mirrors MigrationCandidate's per-project
        // (project-root) placement so SDK-style glob-includes pick it up without duplication.
        var touchedProjectDirs = scopedDocuments
            .Where(d => changes.ContainsKey(d.FilePath!))
            .Select(d => d.Project.FilePath)
            .Where(p => p != null)
            .Select(Path.GetDirectoryName)
            .Where(dir => dir != null)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var projectDir in touchedProjectDirs)
        {
            var attrPath = Path.Combine(projectDir!, $"{ContentHashAttributeSource.FullName}.cs");
            changes[attrPath] = ContentHashAttributeSource.BuildSource();
        }
    }
}

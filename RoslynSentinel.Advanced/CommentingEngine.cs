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

    // Each CompleteAsync call is a stateless HTTP round-trip to the (typically local) LLM server —
    // no shared state between calls — so a handful can run concurrently to keep a small local model
    // busier (a single forward pass batching multiple small inputs) without the KV-cache footprint
    // of true request batching. Default of 2 is deliberately conservative; raise it via --llm-parallelism
    // or ROSLYNSENTINEL_LLM_PARALLELISM if the host has VRAM/compute headroom for more concurrent
    // contexts (see LlmOptions). This only parallelizes the LLM calls themselves —
    // CommentFileAsync's edit-application loop stays strictly sequential, since each member's
    // AddSummaryCommentCoreAsync call must see the previous member's edit already applied to the document.
    private static int LlmParallelism => LlmOptions.Parallelism;

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
        string? ExistingHash,
        string? ContainingTypeName,
        int NameOrdinal);

    // ── Phase 1: seed ────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps every member in scope that has no <c>[ContentHash("Comment", ...)]</c> at all with
    /// the sentinel hash. Batched per-file (one evolving root, one write per file) so this is safe
    /// to run solution-wide in a single call. <c>UnresolvedProjects</c> lists any touched project
    /// whose <c>ContentHashAttribute</c> could not be injected/verified — files belonging to those
    /// projects are dropped from <c>Changes</c> rather than seeded with an attribute that can't
    /// compile.
    /// </summary>
    public async Task<(Dictionary<FilePath, string> Changes, int SeededCount, int AlreadyTaggedCount, List<string> UnresolvedProjects)> SeedContentHashesAsync(
        ToolScope scope, string? projectName, string? filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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

            // Track each original member's ordinal among same-kind/same-name siblings (e.g. the
            // 2nd of 3 "Equals" overloads) up front, from the untouched original list — this is what
            // lets FindEquivalentMember re-locate the *right* sibling below even after earlier
            // iterations have rewritten currentRoot and shifted every later member's SpanStart.
            var originalMembers = currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsTaggableMember).ToList();
            var seenNameCounts = new Dictionary<(Type Kind, string Name), int>();
            var memberOrdinals = new List<int>(originalMembers.Count);
            foreach (var m in originalMembers)
            {
                var name = GetTaggableMemberName(m);
                var key = (m.GetType(), name ?? "");
                seenNameCounts.TryGetValue(key, out var count);
                memberOrdinals.Add(count);
                seenNameCounts[key] = count + 1;
            }

            for (int i = 0; i < originalMembers.Count; i++)
            {
                var member = originalMembers[i];
                // Re-resolve the member on the possibly-already-rewritten root by identity span text
                // (falling back to same-kind/same-name/same-ordinal), since ReplaceNode invalidates
                // earlier node references once the tree changes.
                var liveMember = FindEquivalentMember(currentRoot, member, memberOrdinals[i]);
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

        var unresolvedProjectIds = changes.Count > 0
            ? await InjectAttributeClassIfMissingAsync(solution, documents, changes, cancellationToken)
            : [];

        var unresolvedProjectNames = new List<string>();
        if (unresolvedProjectIds.Count > 0)
        {
            var unresolvedIdSet = unresolvedProjectIds.ToHashSet();
            unresolvedProjectNames = unresolvedProjectIds.Select(id => solution.GetProject(id)!.Name).ToList();

            var droppedPaths = documents
                .Where(d => d.FilePath != null && unresolvedIdSet.Contains(d.Project.Id))
                .Select(d => (FilePath)d.FilePath!)
                .ToHashSet();

            foreach (var path in droppedPaths)
            {
                changes.Remove(path);
            }
        }

        return (changes, seeded, alreadyTagged, unresolvedProjectNames);
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
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
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

            // Ordinal among same-kind/same-name/same-containing-type siblings in this document's
            // original root (e.g. the 2nd of 3 "Equals" overloads on FilePath) — overloaded
            // methods/constructors share GetTaggableMemberName, so MemberName+ContainingTypeName
            // alone can't tell CommentFileAsync's post-edit re-lookup which physical sibling this
            // site refers to. See the matching note on FindEquivalentMember/FindEquivalentMemberByName.
            var nameOrdinals = new Dictionary<(Type Kind, string Name, string? ContainingType), int>();

            foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(IsTaggableMember))
            {
                var hasTag = HasContentHashAttribute(member, out var existingHash);
                var currentHash = ComputeStableContentHash(member);
                var memberContainingType = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                var ordinalKey = (member.GetType(), GetTaggableMemberName(member) ?? "", memberContainingType);
                nameOrdinals.TryGetValue(ordinalKey, out var thisOrdinal);
                nameOrdinals[ordinalKey] = thisOrdinal + 1;

                if (hasTag && existingHash == currentHash)
                {
                    continue; // up to date
                }

                var memberName = GetTaggableMemberName(member);
                if (memberName == null)
                {
                    continue;
                }

                // member.GetLocation() spans from any attribute lists onward, so when a member
                // already carries a [ContentHash] (or other) attribute, its "declaration line" would
                // land on the attribute line instead — identical text across every seeded-but-not-
                // yet-commented member in a file, which made ContextSnippet ambiguous downstream in
                // AddSummaryCommentCoreAsync's snippet resolver (confirmed: solution-wide run on
                // RoslynSentinel.Advanced hit "contextSnippet ambiguous (2+ candidates)" repeatedly on
                // files with multiple already-seeded members or duplicate property names across
                // sibling types). DeclarationStartToken skips past attribute lists to the member's own
                // first token (a modifier or the type/return-type keyword) so the snippet always
                // describes the actual signature line, which is what's unique.
                var declStartToken = member.AttributeLists.Count > 0
                    ? member.AttributeLists.Last().GetLastToken().GetNextToken()
                    : member.GetFirstToken();
                var lineSpan = declStartToken.GetLocation().GetLineSpan();
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
                    ExistingHash: hasTag ? existingHash : null,
                    ContainingTypeName: memberContainingType,
                    NameOrdinal: thisOrdinal));
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

        // Every site's member text comes from the shared, untouched original tree (site.Node), so
        // the LLM calls themselves have no cross-site dependency and can run concurrently — only the
        // edit-application loop below is inherently sequential (each AddSummaryCommentCoreAsync call
        // must see the previous member's edit already folded into `document`). Bounding with
        // LlmParallelism keeps a small local model's forward passes batched across a couple of
        // requests instead of firing every member in the file at once. Indexed by position (not by
        // MemberSite itself) since MemberSite is a record with structural equality over its Node —
        // two sites with textually-identical members would collide as dictionary keys.
        var summaries = await GetSummariesAsync(fileMembers, maxTokens, cancellationToken);

        string? lastGoodText = null;

        for (int siteIndex = 0; siteIndex < fileMembers.Count; siteIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var site = fileMembers[siteIndex];

            var (summary, llmError) = summaries[siteIndex];
            if (llmError != null)
            {
                outcomes.Add(new MemberCommentOutcome(site, false, $"LLM call failed: {llmError}"));
                continue;
            }

            var editResult = await _refactoringEngine.AddSummaryCommentCoreAsync(
                document, site.FilePath, site.MemberName, summary, site.ContextSnippet, site.LineBefore, null, site.ContainingTypeName, cancellationToken);

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
            var newMember = FindEquivalentMemberByName(newRoot, site.Node, site.MemberName, site.ContainingTypeName, site.NameOrdinal);
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

    // Runs CompleteAsync for every site with at most LlmParallelism in flight at once, preserving
    // fileMembers' original order in the result so CommentFileAsync's sequential apply loop can index
    // straight into it. A failed call is captured as an error string rather than thrown, matching the
    // per-member try/catch the sequential version used to do inline.
    private async Task<(string Summary, string? Error)[]> GetSummariesAsync(
        IReadOnlyList<MemberSite> fileMembers, int maxTokens, CancellationToken cancellationToken)
    {
        var results = new (string Summary, string? Error)[fileMembers.Count];
        using var throttle = new SemaphoreSlim(LlmParallelism);

        var tasks = fileMembers.Select(async (site, index) =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var memberText = site.Node.ToFullString().Trim();
                var summary = await _llmClient.CompleteAsync(CommentSystemPrompt, memberText, maxTokens, cancellationToken);
                results[index] = (summary, null);
            }
            catch (Exception ex)
            {
                results[index] = (string.Empty, ex.Message);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
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
            .Where(d => !IsOutsideEditableSourceTree(d.FilePath!))
            .ToList();
    }

    // Roslyn surfaces more than a project's own hand-written source as Documents: MSBuild-generated
    // files under obj/ (AssemblyInfo.cs, GlobalUsings.g.cs, AssemblyAttributes.cs — regenerated on
    // every build, so any [ContentHash]/comment written there is silently discarded) and, worse,
    // Compile items a NuGet package contributes from its own build/ folder outside the repo entirely
    // (confirmed live: seeding wrote a live [ContentHash] into the *shared, machine-wide* NuGet
    // package cache copy of Microsoft.NET.Test.Sdk.Program.cs, breaking every other solution on the
    // machine that restores that same package version until the cache entry was deleted and
    // re-restored). Both cases are compile items the project references but does not own — exclude
    // any document under an obj/bin folder or under the user's NuGet global-packages folder.
    private static readonly string[] NuGetPackagesRoots = new[]
        {
            Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => Path.GetFullPath(p!) + Path.DirectorySeparatorChar)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsOutsideEditableSourceTree(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        if (NuGetPackagesRoots.Any(root => normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var segments = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
    }

    // ── Syntax helpers ───────────────────────────────────────────────────────

    // ResolveMemberOrEnumMemberByNameOrSnippet (RefactoringEngine.cs) deliberately excludes
    // interface-declared members from its name-based lookup — an interface method/property is a
    // signature only, and callers targeting "Method X" almost always mean an implementation, not the
    // interface's own declaration. Seeding/staleness-scanning still followed plain syntax-kind
    // matching with no such exclusion, so every interface member got tagged [ContentHash] and then
    // permanently failed AddSummaryComment with "target not found" on every subsequent call —
    // confirmed live against ICircuitBreaker.cs/IRateLimiter.cs/ISolutionProvider.cs. Matching the
    // resolver's own exclusion here means BulkComment never queues work it cannot possibly complete.
    private static bool IsTaggableMember(MemberDeclarationSyntax member) => member.Parent is not InterfaceDeclarationSyntax && member switch
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

    /// <summary>
    /// Re-finds a member on a possibly-rewritten root by matching declaration kind + identifier text
    /// + original span start (best-effort, since ReplaceNode invalidates old node references). Falls
    /// back to <paramref name="nameOrdinal"/> — this member's 0-based position among same-kind/
    /// same-name siblings in the *original*, unrewritten root — rather than a bare first-match:
    /// overloaded methods/constructors share GetTaggableMemberName, so a name-only fallback always
    /// resolved to sibling #0 once SpanStart drifted, silently re-tagging (or skipping, once already
    /// tagged) the same overload on every later iteration and leaving other overloads never seeded at
    /// all. Confirmed live: FilePath.cs's 3 "Equals" overloads (object?/string?/FilePath) — only the
    /// first ever got a [ContentHash], the other two were never tagged across repeated solution-scope
    /// runs, then permanently failed AddSummaryComment with "contextSnippet ambiguous" downstream
    /// because there was nothing distinguishing an un-seeded candidate from an untagged one.
    /// </summary>
    private static MemberDeclarationSyntax? FindEquivalentMember(SyntaxNode currentRoot, MemberDeclarationSyntax original, int nameOrdinal)
    {
        var originalName = GetTaggableMemberName(original);
        if (originalName == null)
        {
            return null;
        }

        var bySpanStart = currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(IsTaggableMember)
            .FirstOrDefault(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == originalName && m.SpanStart == original.SpanStart);
        if (bySpanStart != null)
        {
            return bySpanStart;
        }

        var sameNameSiblings = currentRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(IsTaggableMember)
            .Where(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == originalName)
            .ToList();
        return nameOrdinal < sameNameSiblings.Count ? sameNameSiblings[nameOrdinal] : sameNameSiblings.FirstOrDefault();
    }

    // Sibling types can declare identically-named, identically-shaped members (e.g. two records each
    // with "public string CalleeMethod"), so a bare kind+name match here always picks the first
    // occurrence in the file regardless of which member AddSummaryCommentCoreAsync actually just
    // edited — confirmed live: the second of two identically-named properties got its real comment
    // written by AddSummaryCommentCoreAsync (which does disambiguate by containing type), but this
    // re-lookup then grabbed the *first* occurrence to stamp the ContentHash onto, silently
    // overwriting/misplacing that occurrence's trivia and leaving the actually-edited member
    // unhashed. containingTypeName narrows candidates the same way the resolver in RefactoringEngine
    // does, only falling back to the unnarrowed set if nothing matches (should not normally happen,
    // since it's the same hint AddSummaryCommentCoreAsync just used successfully).
    private static MemberDeclarationSyntax? FindEquivalentMemberByName(SyntaxNode root, MemberDeclarationSyntax original, string memberName, string? containingTypeName, int nameOrdinal)
    {
        var candidates = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(IsTaggableMember)
            .Where(m => m.GetType() == original.GetType() && GetTaggableMemberName(m) == memberName)
            .ToList();

        if (containingTypeName != null && candidates.Count > 1)
        {
            var narrowed = candidates.Where(c => c.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text == containingTypeName).ToList();
            if (narrowed.Count > 0)
            {
                candidates = narrowed;
            }
        }

        // Same-type overloads (e.g. FilePath's 3 "Equals" methods) all pass the containingTypeName
        // narrowing above identically, so nameOrdinal — this site's 0-based position among its
        // original same-kind/same-name/same-containing-type siblings — is the only remaining way to
        // pick the right physical sibling instead of always re-targeting the first one.
        return nameOrdinal < candidates.Count ? candidates[nameOrdinal] : candidates.FirstOrDefault();
    }

    // ── Attribute class injection ────────────────────────────────────────────

    /// <summary>
    /// Ensures every project touched by <paramref name="changes"/> can actually resolve the
    /// <c>ContentHashAttribute</c> type, injecting a fresh copy where it can't. Returns the
    /// directories of any touched project where injection was needed but could not be verified
    /// afterward (candidate compilation still doesn't resolve the type) — callers should treat
    /// this as fatal for those projects rather than seeding members that can't possibly compile.
    /// </summary>
    private static async Task<List<ProjectId>> InjectAttributeClassIfMissingAsync(
        Solution solution, IReadOnlyList<Document> scopedDocuments, Dictionary<FilePath, string> changes, CancellationToken cancellationToken)
    {
        // Per-project, not solution-wide: [ContentHash] is a plain type, invisible across project
        // boundaries without a reference, so each project that gets a member tagged needs its own
        // copy of the source file. A solution-wide "already defined somewhere" check let the first
        // touched project's copy silently starve every other project — confirmed live: a
        // solution-scope BulkComment run seeded/attempted members in RoslynSentinel.Basic (which has
        // no reference to RoslynSentinel.Advanced, where the file already existed) and every one of
        // those files failed apply validation with dozens of CS0246-style diagnostics (the attribute
        // type doesn't exist in that compilation) — 0 members actually committed in any such project.
        //
        // Detection is semantic (Compilation.GetTypeByMetadataName), not filename-based — a
        // filename match let RoslynSentinel.Common's own ContentHashAttributeGenerator.cs (the
        // *builder* of this source, previously misnamed ContentHashAttribute.cs) masquerade as an
        // already-present attribute in every project that references Common, silently starving all
        // of them the same way. See docs history: "BulkComment fails to apply any comments".
        var touchedProjectIds = scopedDocuments
            .Where(d => changes.ContainsKey(d.FilePath!))
            .Select(d => d.Project.Id)
            .Distinct()
            .ToList();

        var unresolved = new List<ProjectId>();

        foreach (var projectId in touchedProjectIds)
        {
            var project = solution.GetProject(projectId)!;
            var compilation = await project.GetCompilationAsync(cancellationToken);
            var hasAttribute = compilation?.GetTypeByMetadataName(ContentHashAttributeSource.FullName) is { } attrType
                                && IsAttributeType(attrType);
            if (hasAttribute)
            {
                continue;
            }

            var projectDir = project.FilePath == null ? null : Path.GetDirectoryName(project.FilePath);
            if (projectDir == null)
            {
                unresolved.Add(projectId);
                continue;
            }

            var attrPath = Path.Combine(projectDir, $"{ContentHashAttributeSource.FullName}.cs");
            var attrSource = ContentHashAttributeSource.BuildSource();

            // Verify the injected source actually resolves before trusting it — re-checking against
            // the edited candidate (not just trusting BuildSource() to be correct) is what catches a
            // future drift between the generator and what the compiler accepts, instead of finding
            // out only after seed-phase validation rejects hundreds of members at once.
            var candidateProject = project.AddDocument(Path.GetFileName(attrPath), attrSource, filePath: attrPath).Project;
            var candidateCompilation = await candidateProject.GetCompilationAsync(cancellationToken);
            var verified = candidateCompilation?.GetTypeByMetadataName(ContentHashAttributeSource.FullName) is { } injectedType
                            && IsAttributeType(injectedType);
            if (!verified)
            {
                unresolved.Add(projectId);
                continue;
            }

            changes[attrPath] = attrSource;
        }

        return unresolved;
    }

    private static bool IsAttributeType(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (baseType.ToDisplayString() == "System.Attribute")
            {
                return true;
            }
        }

        return false;
    }
}

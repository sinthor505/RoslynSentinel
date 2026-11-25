using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

using static RoslynSentinel.Engines.Basic.CopyAndObsoleteEdits;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// One rewrite pass over the callers of the types MoveFile's copyAndObsolete handling copied to a new namespace: every file that references one of the
/// moved top-level types is switched to the new namespace, so the old (now [Obsolete]) types are no longer used. Nothing is written and nothing is
/// compiled here: the pass returns the new text of each changed file, and the caller loop compiles the result and passes the files that gained errors
/// back with a forced <see cref="CallerRewriteMode.Qualify"/> (or reverts them) on the next pass.
/// Callers are found by symbol (<see cref="SymbolFinder.FindReferencesAsync(ISymbol, Solution, CancellationToken)"/> over the whole solution), so
/// cref, nameof, typeof, attribute, base-list, generic-argument and alias-directive references are all found the same way. Edits are text splices on the
/// original string, so every byte outside the edited spots (BOM, line endings, comments) is preserved. The moved types' own file, generated files and
/// files without a reference are never touched.
/// </summary>
public static class CopyAndObsoleteCallerRewrite
{
    private const char Bom = '﻿';

    // A cheap text probe so only files that can hold a global using directive are parsed when looking for one.
    private static readonly Regex GlobalUsingHint = new(@"\bglobal\s+using\b", RegexOptions.Compiled);

    /// <summary>
    /// Runs the pass for an accepted <paramref name="check"/> of <paramref name="source"/>; a refused check is returned as this call's refusal.
    /// See the explicit-argument overload for what is produced.
    /// </summary>
    public static Task<CallerRewriteResult> RewriteAsync(Solution solution, FilePathWrapper source, CopyAndObsoleteCheckResult check, IReadOnlyDictionary<FilePathWrapper, CallerRewriteMode>? forcedModes = null, IReadOnlyCollection<FilePathWrapper>? excludedPaths = null, CancellationToken cancellationToken = default)
    {
        if (check.Refusal is not null)
        {
            return Task.FromResult(CallerRewriteResult.Refused(check.Refusal));
        }

        if (check.OldNamespace is null || check.NewNamespace is null)
        {
            return Task.FromResult(CallerRewriteResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: the check of '{source.Absolute}' returned no namespaces, so the callers' new namespace is unknown. Options: run the check again, or use namespaceHandling keep to move the file as it is."));
        }

        return RewriteAsync(solution, source, check.OldNamespace, check.NewNamespace, check.Types.Select(type => type.Name).ToList(), forcedModes, excludedPaths, cancellationToken);
    }

    /// <summary>
    /// One pass: finds every reference to the top-level types named in <paramref name="typeNames"/> that <paramref name="source"/> declares (types that already
    /// carry [Obsolete] migrate like the rest), across all projects of <paramref name="solution"/>, and returns the rewritten text of each caller file.
    /// <paramref name="solution"/> is the baseline: the source file as it is today, with no copy of it yet.
    /// A reference that already names the old namespace (<c>OldNs.Type</c>, <c>global::OldNs.Type</c>, a partial qualification or an alias directive such as
    /// <c>using A = OldNs.Type;</c>) always has the namespace part replaced by <paramref name="newNamespace"/>; a using directive cannot supply the type there.
    /// Simple-name references follow the file's mode. <see cref="CallerRewriteMode.Using"/> (the default) adds <c>using &lt;NewNs&gt;;</c> sorted among the
    /// plain usings of the scope that held <c>using &lt;OldNs&gt;;</c> and removes the old one when nothing else in the file needs it. The default is
    /// <see cref="CallerRewriteMode.Qualify"/> instead when: a global using of the old namespace exists anywhere in the solution (a fact is recorded);
    /// the old namespace is the global namespace; the file declares code inside the old namespace or one nested in it (members of the enclosing
    /// namespace are found before using directives, so a using would keep binding the old type); the file uses other types of the old namespace, so the
    /// old using must stay; the old using appears in several scopes or not at all; or a type the file already uses shares its name with a type of
    /// the new namespace (the added using would make it ambiguous). Qualify writes <c>&lt;NewNs&gt;.</c> before each simple-name reference and leaves
    /// the usings alone. <paramref name="forcedModes"/> overrides the default per file (keyed by the file's path). Paths in
    /// <paramref name="excludedPaths"/>, the source file, generated files and documents without a path are never changed.
    /// Refusals (facts and options): empty new namespace; source not a document of the solution; none of the type names declared by the source.
    /// </summary>
    public static async Task<CallerRewriteResult> RewriteAsync(Solution solution, FilePathWrapper source, string oldNamespace, string newNamespace, IReadOnlyCollection<string> typeNames, IReadOnlyDictionary<FilePathWrapper, CallerRewriteMode>? forcedModes = null, IReadOnlyCollection<FilePathWrapper>? excludedPaths = null, CancellationToken cancellationToken = default)
    {
        if (newNamespace.Length == 0)
        {
            return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument,
                $"copyAndObsolete: the destination gives the global namespace, so callers of '{source.Absolute}' have no namespace to switch to. Options: pass a {MoveFileParameterNames.DestinationPath} in a folder that gives a named namespace, or use namespaceHandling keep to move the file as it is.");
        }

        var lookup = DocumentLookup.TryGetDocument(solution, source);
        if (!lookup.TryGetDocument(out var sourceDocument))
        {
            return CallerRewriteResult.Refused(lookup.ToResultError());
        }

        var facts = new List<string>();
        var symbols = await FindMovedTypesAsync(sourceDocument, typeNames, facts, cancellationToken);
        if (symbols.Count == 0)
        {
            return CallerRewriteResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"copyAndObsolete: '{source.Absolute}' declares none of the top-level types {string.Join(", ", typeNames.Select(name => $"'{name}'"))}, so no callers could be found. Options: run the check again on the current file, or use namespaceHandling keep to move the file as it is.");
        }

        var sitesByPath = await FindReferenceSitesAsync(solution, symbols, cancellationToken);
        var results = new List<CallerFileRewrite>();
        if (sitesByPath.Count == 0)
        {
            return new CallerRewriteResult(results, facts, null);
        }

        var globalUsingPath = oldNamespace.Length == 0 ? null : await FindGlobalUsingAsync(solution, oldNamespace, cancellationToken);
        if (globalUsingPath is not null)
        {
            facts.Add($"'{globalUsingPath}' has a global using of '{oldNamespace}', which stays; every caller is qualified with '{newNamespace}.' instead of getting a using directive.");
        }

        foreach (var (path, sites) in sitesByPath.OrderBy(entry => entry.Key, PathComparison.Comparer))
        {
            var wrapped = new FilePathWrapper(path);
            if (wrapped == source || (excludedPaths?.Contains(wrapped) ?? false))
            {
                continue;
            }

            CallerRewriteMode? forced = forcedModes is not null && forcedModes.TryGetValue(wrapped, out var mode) ? mode : null;
            var rewrite = await RewriteFileAsync(path, sites, oldNamespace, newNamespace, forced, globalUsingPath, facts, cancellationToken);
            if (rewrite is not null)
            {
                results.Add(rewrite);
            }
        }

        return new CallerRewriteResult(results, facts, null);
    }

    /// <summary>
    /// The set overload, for a namespace change: each <see cref="NamespaceMove"/> moves the top-level types of one file from its old namespace to its new one. The types
    /// are moved, not copied (the old ones are gone, so a using directive of the new namespace is safe even in a file that declares code inside the old namespace).
    /// Every file of the set is rewritten once for both roles. As the declaration owner its namespace declaration names the new namespace and it gets a using for the
    /// old-namespace types it reached implicitly, but none for a sibling that moves with it. As a caller its references to the moved types of any move of the set (its own
    /// types included) are switched as in the explicit-argument overload; a simple name that already reaches the type from the file's new namespace is left alone, and a
    /// namespace-qualified reference to a type of the set is rewritten, not refused. Callers outside the set are rewritten as in the explicit-argument overload, once per
    /// distinct (old, new) pair. Paths in <paramref name="excludedPaths"/> are left out of the caller role only (a listed set member still gets its own edits);
    /// <paramref name="forcedModes"/> works as in the explicit-argument overload; generated callers are never changed.
    /// All edits of a file are applied together. Identical edits are merged; two that overlap are not skipped, the pass refuses with one
    /// <see cref="EditOverlap"/> item per pair in <c>Refusal.StructuredDetail</c>. Afterwards a plain <c>using Old;</c> or <c>global using Old;</c> that bound before the
    /// move and binds to nothing now (the old namespace is gone) is reported in <see cref="CallerRewriteResult.DanglingUsings"/>: removed in the returned text, or left as it
    /// is as <see cref="DanglingUsingResolution.RefusedGlobal"/> when a global using cannot be removed safely (generated file, or shared with a project where it still binds).
    /// Refusals (facts and options): empty new namespace; a file listed twice; a file that is not a document of the solution or declares no top-level type in its old
    /// namespace; a file shape the namespace swap cannot edit; overlapping edits. A move whose old and new namespace are equal is skipped with a fact.
    /// </summary>
    public static async Task<CallerRewriteResult> RewriteAsync(Solution solution, IReadOnlyList<NamespaceMove> moves, IReadOnlyDictionary<FilePathWrapper, CallerRewriteMode>? forcedModes = null, IReadOnlyCollection<FilePathWrapper>? excludedPaths = null, CancellationToken cancellationToken = default)
    {
        var facts = new List<string>();
        var members = new Dictionary<FilePathWrapper, NamespaceMove>();
        foreach (var move in moves)
        {
            if (move.NewNamespace.Length == 0)
            {
                return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument,
                    $"The new namespace of '{move.FilePath.Absolute}' is the global namespace, so its callers have no namespace to switch to. Options: name a namespace, or leave the file out of the namespace change.");
            }

            if (string.Equals(move.OldNamespace, move.NewNamespace, StringComparison.Ordinal))
            {
                facts.Add($"'{move.FilePath.Absolute}' already declares '{move.NewNamespace}', so it was left unchanged.");
                continue;
            }

            if (!members.TryAdd(move.FilePath, move))
            {
                return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument,
                    $"'{move.FilePath.Absolute}' is listed in more than one namespace move. Options: list each file once.");
            }
        }

        if (members.Count == 0)
        {
            return new CallerRewriteResult([], facts, null, []);
        }

        // The moved types, grouped by (old, new) namespace pair: the callers of one pair share one using decision.
        var sourceDocuments = new Dictionary<FilePathWrapper, Document>();
        var symbolsByPair = new Dictionary<(string Old, string New), List<INamedTypeSymbol>>();
        foreach (var (memberPath, memberMove) in members.OrderBy(entry => entry.Key))
        {
            var lookup = DocumentLookup.TryGetDocument(solution, memberPath);
            if (!lookup.TryGetDocument(out var memberDocument))
            {
                return CallerRewriteResult.Refused(lookup.ToResultError());
            }

            sourceDocuments[memberPath] = memberDocument;
            var typeNames = await DeclaredTopLevelTypeNamesAsync(memberDocument, memberMove.OldNamespace, cancellationToken);
            var symbols = typeNames.Count == 0 ? new List<INamedTypeSymbol>() : await FindMovedTypesAsync(memberDocument, typeNames, facts, cancellationToken);
            if (symbols.Count == 0)
            {
                var described = memberMove.OldNamespace.Length == 0 ? "the global namespace" : $"'{memberMove.OldNamespace}'";
                return CallerRewriteResult.Refused(ToolErrorCode.PreconditionNotMet,
                    $"'{memberPath.Absolute}' declares no top-level type in {described}, so there is nothing to move and no callers could be found. Options: run the change again on the current file, or leave the file out of the namespace change.");
            }

            var pair = (memberMove.OldNamespace, memberMove.NewNamespace);
            if (!symbolsByPair.TryGetValue(pair, out var pairSymbols))
            {
                pairSymbols = [];
                symbolsByPair[pair] = pairSymbols;
            }

            pairSymbols.AddRange(symbols);
        }

        // Reference sites per file and pair; movedStarts holds, per file, where a reference to ANY moved type starts.
        var sitesByPath = new Dictionary<string, List<((string Old, string New) Pair, List<(Document Document, TextSpan Span, string TypeName)> Sites)>>(PathComparison.Comparer);
        var movedStarts = new Dictionary<string, HashSet<int>>(PathComparison.Comparer);
        foreach (var (pair, pairSymbols) in symbolsByPair.OrderBy(entry => entry.Key.Old, StringComparer.Ordinal).ThenBy(entry => entry.Key.New, StringComparer.Ordinal))
        {
            var found = await FindReferenceSitesAsync(solution, pairSymbols, cancellationToken);
            foreach (var (sitePath, sites) in found)
            {
                if (!sitesByPath.TryGetValue(sitePath, out var perPair))
                {
                    perPair = [];
                    sitesByPath[sitePath] = perPair;
                }

                perPair.Add((pair, sites));
                if (!movedStarts.TryGetValue(sitePath, out var starts))
                {
                    starts = [];
                    movedStarts[sitePath] = starts;
                }

                starts.UnionWith(sites.Select(site => site.Span.Start));
            }
        }

        // A global using of an old namespace makes the callers of that pair qualify (the sweep below decides what happens to the directive itself).
        var globalUsings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (sitesByPath.Count > 0)
        {
            foreach (var oldNamespace in symbolsByPair.Keys.Select(pair => pair.Old).Where(ns => ns.Length > 0).Distinct(StringComparer.Ordinal))
            {
                globalUsings[oldNamespace] = await FindGlobalUsingAsync(solution, oldNamespace, cancellationToken);
            }
        }

        var allPaths = sitesByPath.Keys
            .Concat(sourceDocuments.Values.Select(document => document.FilePath ?? string.Empty).Where(path => path.Length > 0))
            .Distinct(PathComparison.Comparer)
            .Order(PathComparison.Comparer)
            .ToList();

        var results = new Dictionary<FilePathWrapper, CallerFileRewrite>();
        var overlaps = new List<EditOverlap>();
        foreach (var path in allPaths)
        {
            var wrapped = new FilePathWrapper(path);
            var isMember = members.TryGetValue(wrapped, out var move);
            var callerRole = !(excludedPaths?.Contains(wrapped) ?? false);
            if (!isMember && !callerRole)
            {
                continue;
            }

            sitesByPath.TryGetValue(path, out var perPair);
            var document = isMember ? sourceDocuments[wrapped] : perPair![0].Sites[0].Document;
            var edits = new List<(int Position, int Length, string Text)>();
            string? plannedText = null;
            var qualified = false;
            var rewritten = 0;

            // Caller role: the references in this file to the moved types of every pair.
            if (callerRole && perPair is not null)
            {
                CallerRewriteMode? forced = forcedModes is not null && forcedModes.TryGetValue(wrapped, out var forcedMode) ? forcedMode : null;
                foreach (var (pair, sites) in perPair)
                {
                    var plan = await PlanFileEditsAsync(path, document, sites, pair.Old, pair.New, forced, globalUsings.GetValueOrDefault(pair.Old), facts, oldTypesGone: true, selfNewNamespace: move?.NewNamespace, allMovedStarts: movedStarts[path], cancellationToken);
                    if (plan is not { } planned)
                    {
                        break;
                    }

                    plannedText = planned.FileText;
                    edits.AddRange(planned.Edits);
                    qualified |= planned.Mode == CallerRewriteMode.Qualify;
                    rewritten += planned.Rewritten;
                }
            }

            if (!isMember && (plannedText is null || edits.Count == 0))
            {
                continue;
            }

            var text = plannedText ?? (await document.GetTextAsync(cancellationToken)).ToString();

            // Declaration-owner role: the namespace swap and the usings the move makes necessary.
            if (isMember)
            {
                var refusal = await AddMovedFileEditsAsync(document, move!, members.Keys, edits, cancellationToken);
                if (refusal is not null)
                {
                    return CallerRewriteResult.Refused(refusal);
                }
            }

            var newText = ApplyStrict(path, text, edits.Distinct().ToList(), overlaps);
            if (newText is null || (!isMember && string.Equals(newText, text, StringComparison.Ordinal)))
            {
                continue;
            }

            results[wrapped] = new CallerFileRewrite(wrapped, newText, qualified ? CallerRewriteMode.Qualify : CallerRewriteMode.Using, rewritten);
        }

        if (overlaps.Count > 0)
        {
            return CallerRewriteResult.Refused(OverlapRefusal(overlaps));
        }

        var dangling = await SweepDanglingUsingsAsync(solution, symbolsByPair.Keys.Select(pair => pair.Old), results, facts, cancellationToken);
        return new CallerRewriteResult(results.Values.OrderBy(file => file.FilePath).ToList(), facts, null, dangling);
    }

    // The names of the top-level types (and delegates) the document declares directly in the namespace.
    private static async Task<List<string>> DeclaredTopLevelTypeNamesAsync(Document document, string ns, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
        {
            return names;
        }

        foreach (var declaration in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            var name = NameOf(declaration);
            if (name.Length > 0 && string.Equals(DeclaredNamespace(declaration), ns, StringComparison.Ordinal) && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // After the rewrite: the plain "using Old;" / "global using Old;" directives that bound in the baseline and bind to nothing once the rewritten texts are in place.
    // A removable one is deleted from the file's text (the file joins <paramref name="files"/> when it was not there) and reported Removed; a global using that is in a
    // generated file, or that still binds in another project sharing the file, stays and is reported RefusedGlobal. A non-global one that cannot be removed is a fact.
    private static async Task<List<DanglingUsing>> SweepDanglingUsingsAsync(Solution baseline, IEnumerable<string> oldNamespaces, Dictionary<FilePathWrapper, CallerFileRewrite> files, List<string> facts, CancellationToken cancellationToken)
    {
        var dangling = new List<DanglingUsing>();
        var olds = oldNamespaces.Where(ns => ns.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (olds.Count == 0)
        {
            return dangling;
        }

        var rewrittenSolution = baseline;
        foreach (var file in files.Values)
        {
            foreach (var id in baseline.GetDocumentIdsWithFilePath(file.FilePath.Absolute))
            {
                rewrittenSolution = rewrittenSolution.WithDocumentText(id, SourceText.From(file.NewText));
            }
        }

        var probe = new Regex(@"\busing\s+(?:global::)?(?:" + string.Join("|", olds.Select(Regex.Escape)) + @")\s*;");
        var baselineHolds = new Dictionary<(ProjectId Project, string Namespace), bool>();

        async Task<bool> HeldBeforeAsync(ProjectId project, string ns)
        {
            if (!baselineHolds.TryGetValue((project, ns), out var holds))
            {
                var compilation = baseline.GetProject(project) is { } baselineProject ? await baselineProject.GetCompilationAsync(cancellationToken) : null;
                holds = compilation is not null && NamespaceExists(compilation, ns);
                baselineHolds[(project, ns)] = holds;
            }

            return holds;
        }

        // Group the copies of each file by path (first-seen spelling is the key), then visit the files in path order.
        var copiesByPath = new Dictionary<string, List<Document>>(PathComparison.Comparer);
        foreach (var project in rewrittenSolution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path)
                {
                    continue;
                }

                if (!copiesByPath.TryGetValue(path, out var sameFile))
                {
                    sameFile = new List<Document>();
                    copiesByPath[path] = sameFile;
                }

                sameFile.Add(document);
            }
        }

        var documents = copiesByPath.OrderBy(group => group.Key, PathComparison.Comparer);
        foreach (var group in documents)
        {
            var copies = group.Value;
            var text = (await copies[0].GetTextAsync(cancellationToken)).ToString();
            if (!probe.IsMatch(text) || await copies[0].GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
            {
                continue;
            }

            // A directive is unbound when it binds to nothing in the project of every copy of the file; it is mixed when it binds in some.
            HashSet<int>? unboundEverywhere = null;
            var unboundSomewhere = new HashSet<int>();
            foreach (var copy in copies)
            {
                var starts = await UnboundUsingStartsAsync(copy, olds, cancellationToken);
                unboundEverywhere = unboundEverywhere is null ? starts : unboundEverywhere.Intersect(starts).ToHashSet();
                unboundSomewhere.UnionWith(starts);
            }

            var removals = new List<(int Position, int Length, string Text)>();
            var bomOffset = text.Length > 0 && text[0] == Bom ? 1 : 0;
            var generated = IsGenerated(copies[0], root);
            foreach (var directive in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
            {
                var start = directive.SpanStart;
                if (!unboundSomewhere.Contains(start))
                {
                    continue;
                }

                var ns = UsingName(directive);
                var isGlobal = !directive.GlobalKeyword.IsKind(SyntaxKind.None);
                var everywhere = unboundEverywhere!.Contains(start);
                if (everywhere)
                {
                    var heldBefore = false;
                    foreach (var copy in copies)
                    {
                        heldBefore |= await HeldBeforeAsync(copy.Project.Id, ns);
                    }

                    if (!heldBefore)
                    {
                        continue;
                    }
                }

                if (isGlobal && (generated || !everywhere))
                {
                    dangling.Add(new DanglingUsing(group.Key, ns, DanglingUsingResolution.RefusedGlobal));
                }
                else if (generated || !everywhere)
                {
                    facts.Add($"'{group.Key}' has 'using {ns};', which no longer binds, but the file is {(generated ? "generated" : "shared with a project where the namespace still exists")}, so the directive was left in place.");
                }
                else
                {
                    removals.Add(RemoveDirective(text, bomOffset, directive));
                    dangling.Add(new DanglingUsing(group.Key, ns, DanglingUsingResolution.Removed));
                }
            }

            if (removals.Count > 0)
            {
                var wrapped = new FilePathWrapper(group.Key);
                files.TryGetValue(wrapped, out var existing);
                files[wrapped] = new CallerFileRewrite(wrapped, Apply(text, removals), existing?.Mode ?? CallerRewriteMode.Using, existing?.ReferencesRewritten ?? 0);
            }
        }

        return dangling;
    }

    // The span starts of the plain using directives of the document that name one of the namespaces and bind to nothing in the document's project.
    private static async Task<HashSet<int>> UnboundUsingStartsAsync(Document document, List<string> namespaces, CancellationToken cancellationToken)
    {
        var starts = new HashSet<int>();
        if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return starts;
        }

        foreach (var directive in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Name is { } name
                && namespaces.Contains(UsingName(directive), StringComparer.Ordinal)
                && model.GetSymbolInfo(name, cancellationToken).Symbol is null)
            {
                starts.Add(directive.SpanStart);
            }
        }

        return starts;
    }

    // True when the compilation has a namespace of this name (declared by source or reached through a reference).
    private static bool NamespaceExists(Compilation compilation, string ns)
    {
        INamespaceSymbol? scope = compilation.GlobalNamespace;
        foreach (var part in ns.Split('.'))
        {
            scope = scope?.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault();
        }

        return scope is not null;
    }

    // The symbols of the listed top-level types as declared in the source document; a listed name the document does not declare is a fact.
    private static async Task<List<INamedTypeSymbol>> FindMovedTypesAsync(Document sourceDocument, IReadOnlyCollection<string> typeNames, List<string> facts, CancellationToken cancellationToken)
    {
        var symbols = new List<INamedTypeSymbol>();
        if (await sourceDocument.GetSyntaxRootAsync(cancellationToken) is not { } root || await sourceDocument.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return symbols;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            var name = declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
                _ => string.Empty,
            };
            if (typeNames.Contains(name) && model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol)
            {
                symbols.Add(symbol);
                found.Add(name);
            }
        }

        foreach (var missing in typeNames.Where(name => !found.Contains(name)))
        {
            facts.Add($"The source document does not declare a top-level type '{missing}', so no callers were searched for it.");
        }

        return symbols;
    }

    // Reference locations grouped by file path; a file shared by several projects keeps one entry per document so the text can be compared.
    private static async Task<Dictionary<string, List<(Document Document, TextSpan Span, string TypeName)>>> FindReferenceSitesAsync(Solution solution, List<INamedTypeSymbol> symbols, CancellationToken cancellationToken)
    {
        var byPath = new Dictionary<string, List<(Document Document, TextSpan Span, string TypeName)>>(PathComparison.Comparer);
        foreach (var symbol in symbols)
        {
            var referenced = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
            foreach (var group in referenced)
            {
                // A reference that names the type is reported with the type; one that is an object creation can be reported with the constructor.
                var isTypeOrConstructor = SymbolEqualityComparer.Default.Equals(group.Definition, symbol)
                    || (group.Definition is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && SymbolEqualityComparer.Default.Equals(constructor.ContainingType, symbol));
                if (!isTypeOrConstructor)
                {
                    continue;
                }

                foreach (var location in group.Locations)
                {
                    // Uses through an alias name the alias, which keeps working; the alias directive itself is a normal reference.
                    if (location.Alias is not null || location.IsImplicit || location.Document.FilePath is not { Length: > 0 } path)
                    {
                        continue;
                    }

                    if (!byPath.TryGetValue(path, out var list))
                    {
                        list = [];
                        byPath[path] = list;
                    }

                    list.Add((location.Document, location.Location.SourceSpan, symbol.Name));
                }
            }
        }

        return byPath;
    }

    // The path of the first document that declares "global using <oldNamespace>;", or null.
    private static async Task<string?> FindGlobalUsingAsync(Solution solution, string oldNamespace, CancellationToken cancellationToken)
    {
        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            if (!GlobalUsingHint.IsMatch(text) || await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root)
            {
                continue;
            }

            if (root.Usings.Any(directive => !directive.GlobalKeyword.IsKind(SyntaxKind.None) && directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && string.Equals(UsingName(directive), oldNamespace, StringComparison.Ordinal)))
            {
                return document.FilePath ?? document.Name;
            }
        }

        return null;
    }

    // One caller file: classifies its references, picks the mode and splices the edits; null when the file is generated, unreadable or would not change.
    private static async Task<CallerFileRewrite?> RewriteFileAsync(string path, List<(Document Document, TextSpan Span, string TypeName)> sites, string oldNamespace, string newNamespace, CallerRewriteMode? forced, string? globalUsingPath, List<string> facts, CancellationToken cancellationToken)
    {
        var plan = await PlanFileEditsAsync(path, sites[0].Document, sites, oldNamespace, newNamespace, forced, globalUsingPath, facts, oldTypesGone: false, selfNewNamespace: null, allMovedStarts: null, cancellationToken);
        if (plan is not { } planned)
        {
            return null;
        }

        var newText = Apply(planned.FileText, planned.Edits);
        return string.Equals(newText, planned.FileText, StringComparison.Ordinal) ? null : new CallerFileRewrite(new FilePathWrapper(path), newText, planned.Mode, planned.Rewritten);
    }

    // One file's caller edits as positions in FileText; null when the file is generated or unreadable (a fact says so). Mode is the mode applied (the forced one when
    // given); Rewritten counts the references that now name the new namespace.
    // oldTypesGone: the types moved instead of being copied (a namespace change), so a using directive of the new namespace is safe even in a file that declares code
    // inside the old namespace (a copy leaves the old type there, found before any using). selfNewNamespace: the file itself moves and will declare that namespace, so a
    // simple name that already reaches a type of it (or of a namespace enclosing it) needs no edit. allMovedStarts: where every reference to a moved type of the whole set
    // starts in this file, so none of them counts as a use of the old namespace.
    private static async Task<(string FileText, List<(int Position, int Length, string Text)> Edits, CallerRewriteMode Mode, int Rewritten)?> PlanFileEditsAsync(string path, Document document, List<(Document Document, TextSpan Span, string TypeName)> sites, string oldNamespace, string newNamespace, CallerRewriteMode? forced, string? globalUsingPath, List<string> facts, bool oldTypesGone, string? selfNewNamespace, HashSet<int>? allMovedStarts, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            facts.Add($"'{path}' has no syntax tree or semantic model in the loaded solution, so its references were left unchanged.");
            return null;
        }

        if (IsGenerated(document, root))
        {
            facts.Add($"'{path}' is generated; its {sites.Count} reference(s) to the moved type(s) were left unchanged.");
            return null;
        }

        var sourceText = await document.GetTextAsync(cancellationToken);
        var text = sourceText.ToString();
        var bomOffset = text.Length > 0 && text[0] == Bom ? 1 : 0;
        var eol = DetectEol(text);

        // Reference names, de-duplicated; a document of the same path in another project only counts when it holds the same text.
        var names = new List<SimpleNameSyntax>();
        var referenceStarts = new HashSet<int>();
        foreach (var site in sites)
        {
            if (site.Document.Id != document.Id && !(await site.Document.GetTextAsync(cancellationToken)).ContentEquals(sourceText))
            {
                continue;
            }

            var token = root.FindToken(site.Span.Start, findInsideTrivia: true);
            if (token.IsKind(SyntaxKind.IdentifierToken) && token.SpanStart == site.Span.Start && token.Parent is SimpleNameSyntax name && name.Identifier == token
                && NameMatches(token.ValueText, site.TypeName) && referenceStarts.Add(name.SpanStart))
            {
                names.Add(name);
            }
        }

        var edits = new List<(int Position, int Length, string Text)>();
        var simple = new List<SimpleNameSyntax>();
        var rewritten = 0;
        foreach (var name in names)
        {
            if (TryGetQualifier(name, out var left))
            {
                if (!LeftIsOldNamespace(model, left, oldNamespace, cancellationToken))
                {
                    facts.Add($"'{path}': the qualification '{left}' of a reference to '{name.Identifier.ValueText}' does not name '{oldNamespace}', so that reference was left unchanged.");
                    continue;
                }

                var alias = left.DescendantNodesAndSelf().OfType<AliasQualifiedNameSyntax>().FirstOrDefault();
                edits.Add((left.SpanStart, left.Span.Length, alias is null ? newNamespace : alias.Alias.Identifier.ValueText + "::" + newNamespace));
                rewritten++;
            }
            else if (name.Parent is AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global" })
            {
                edits.Add((name.SpanStart, 0, newNamespace + "."));
                rewritten++;
            }
            else if (name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
            {
                // An alias or using static directive resolves its target without the other usings of its scope, so it is always written out.
                edits.Add((name.SpanStart, 0, newNamespace + "."));
                rewritten++;
            }
            else
            {
                simple.Add(name);
            }
        }

        if (selfNewNamespace is not null && (string.Equals(selfNewNamespace, newNamespace, StringComparison.Ordinal) || selfNewNamespace.StartsWith(newNamespace + ".", StringComparison.Ordinal)))
        {
            simple.Clear();
        }

        var oldUsings = root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Where(directive => IsPlain(directive) && string.Equals(UsingName(directive), oldNamespace, StringComparison.Ordinal))
            .ToList();

        var exclusionStarts = referenceStarts;
        if (allMovedStarts is not null)
        {
            exclusionStarts = new HashSet<int>(referenceStarts);
            exclusionStarts.UnionWith(allMovedStarts);
        }

        List<INamedTypeSymbol>? used = null;
        List<INamedTypeSymbol> Used() => used ??= CollectUsedTypes(model, root, exclusionStarts, cancellationToken);

        // TODO moved mode (oldTypesGone) qualifies on "global using of old" and "old using must stay", where adding a using of the new namespace beside the kept old one is safe (old types gone, conflicts checked below); live ChangeNamespace dry run over RoslynSentinel.Git qualified 98+ references this way. Also check globalUsingPath scoping: an Engines.Basic file cited Common's obj GlobalUsings.g.cs.
        string? WhyQualify()
        {
            if (globalUsingPath is not null)
            {
                return $"'{oldNamespace}' is imported by a global using in '{globalUsingPath}'";
            }

            if (oldNamespace.Length == 0)
            {
                return "types of the global namespace are found before any using directive";
            }

            // A file inside the old namespace needs no using of it; once the old types are gone from there, a using of the new namespace is safe, so the checks of
            // the old using (none found, several scopes, other types of the old namespace in use) do not apply to it.
            var insideOld = DeclaresWithin(root, oldNamespace);
            if (insideOld && !oldTypesGone)
            {
                return $"the file declares code inside '{oldNamespace}', where its own types are found before a using directive";
            }

            if (!insideOld)
            {
                if (oldUsings.Count == 0)
                {
                    return $"no 'using {oldNamespace};' directive was found, so how the simple names resolve is unknown";
                }

                if (oldUsings.Select(directive => directive.Parent).Distinct().Count() > 1)
                {
                    return $"'using {oldNamespace};' appears in more than one scope";
                }

                var needing = Used().Where(type => IsInNamespace(type, oldNamespace)).Select(type => type.Name).Distinct().Order(StringComparer.Ordinal).ToList();
                if (needing.Count > 0)
                {
                    return $"the file also uses {string.Join(", ", needing.Select(name => $"'{name}'"))} from '{oldNamespace}', so that using must stay";
                }
            }

            var conflicts = FindNameConflicts(model.Compilation, Used(), newNamespace);
            return conflicts.Count > 0
                ? $"{string.Join(", ", conflicts.Select(name => $"'{name}'"))} exists in '{newNamespace}' and in a namespace the file already imports, so an added using would make the name ambiguous"
                : null;
        }

        CallerRewriteMode applied;
        if (simple.Count == 0)
        {
            applied = forced ?? CallerRewriteMode.Using;
        }
        else if (forced is { } forcedMode)
        {
            applied = forcedMode;
        }
        else if (WhyQualify() is { } reason)
        {
            applied = CallerRewriteMode.Qualify;
            facts.Add($"'{path}': references are qualified with '{newNamespace}.' instead of getting a using directive, because {reason}.");
        }
        else
        {
            applied = CallerRewriteMode.Using;
        }

        if (simple.Count > 0 && applied == CallerRewriteMode.Qualify)
        {
            edits.AddRange(simple.Select(name => (name.SpanStart, 0, newNamespace + ".")));
        }
        else if (simple.Count > 0)
        {
            AddUsingEdits(edits, root, text, eol, bomOffset, oldUsings, newNamespace, removeOld: oldUsings.Count > 0 && !Used().Any(type => IsInNamespace(type, oldNamespace)));
        }

        rewritten += simple.Count;
        return (text, edits, applied, rewritten);
    }

    // Adds "using <newNamespace>;" (sorted among the plain usings of the scope that held the old using, else at the top of the file) and removes the old usings.
    private static void AddUsingEdits(List<(int Position, int Length, string Text)> edits, CompilationUnitSyntax root, string text, string eol, int bomOffset, List<UsingDirectiveSyntax> oldUsings, string newNamespace, bool removeOld)
    {
        var container = oldUsings.Select(directive => directive.Parent).FirstOrDefault() ?? root;
        var usings = UsingsOf(container);
        var alreadyThere = usings.Concat(root.Usings).Any(directive => IsPlain(directive) && string.Equals(UsingName(directive), newNamespace, StringComparison.Ordinal));
        var line = $"using {newNamespace};";

        if (!alreadyThere)
        {
            var plain = usings.Where(IsPlain).ToList();
            if (plain.FirstOrDefault(directive => CompareNamespaces(newNamespace, UsingName(directive)) < 0) is { } next)
            {
                edits.Add(InsertBefore(text, eol, bomOffset, next, line));
            }
            else if (plain.Count > 0)
            {
                edits.Add(InsertAfter(text, eol, bomOffset, plain[^1], line));
            }
            else if (usings.Count > 0)
            {
                edits.Add(InsertBefore(text, eol, bomOffset, usings[0], line));
            }
            else
            {
                // A file with no usings at all: after any header comment, directly above the first member's own comments.
                var anchor = Math.Max(AnchorPosition(root.Members[0]), bomOffset);
                edits.Add((anchor, 0, line + eol + eol));
            }
        }

        if (removeOld)
        {
            // Insertions were added first so an insertion at the start of a removed line is emitted before the removal.
            foreach (var directive in oldUsings)
            {
                edits.Add(RemoveDirective(text, bomOffset, directive));
            }
        }
    }

    // The edit that deletes a using directive with its line (only the directive itself when other code shares the line).
    private static (int Position, int Length, string Text) RemoveDirective(string text, int bomOffset, UsingDirectiveSyntax directive)
    {
        var start = LineStart(text, directive.SpanStart, bomOffset);
        if (!string.IsNullOrWhiteSpace(text[start..directive.SpanStart]))
        {
            start = directive.SpanStart;
        }

        return (start, directive.FullSpan.End - start, string.Empty);
    }

    private static (int Position, int Length, string Text) InsertBefore(string text, string eol, int bomOffset, UsingDirectiveSyntax directive, string line)
    {
        var start = LineStart(text, directive.SpanStart, bomOffset);
        var indent = text[start..directive.SpanStart];
        return string.IsNullOrWhiteSpace(indent) ? (start, 0, indent + line + eol) : (directive.SpanStart, 0, line + " ");
    }

    private static (int Position, int Length, string Text) InsertAfter(string text, string eol, int bomOffset, UsingDirectiveSyntax directive, string line)
    {
        var start = LineStart(text, directive.SpanStart, bomOffset);
        var indent = text[start..directive.SpanStart];
        indent = string.IsNullOrWhiteSpace(indent) ? indent : string.Empty;
        var end = directive.FullSpan.End;

        // The directive's trailing trivia normally ends with its line break; when it does not (end of file), start a new line first.
        return end > 0 && text[end - 1] == '\n' ? (end, 0, indent + line + eol) : (end, 0, eol + indent + line);
    }

    // The top-level types (and the types of their members) the file's simple names bind to, outside using directives and the moved-type references.
    private static List<INamedTypeSymbol> CollectUsedTypes(SemanticModel model, SyntaxNode root, HashSet<int> referenceStarts, CancellationToken cancellationToken)
    {
        var result = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var name in root.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
        {
            if (referenceStarts.Contains(name.SpanStart)
                || name is IdentifierNameSyntax { IsVar: true }
                || IsQualifiedRight(name)
                || name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null
                || (name.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>() is { } enclosing && enclosing.Name.Span.Contains(name.Span)))
            {
                continue;
            }

            // A name bound through a using alias (using F = Old.Foo;) names the alias, which needs no using of the old namespace.
            if (name is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier, cancellationToken) is not null)
            {
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (OwnerType(symbol) is { } type && seen.Add(type))
            {
                result.Add(type);
            }
        }

        // Extension methods reached without a name node (foreach GetEnumerator, await GetAwaiter, collection-initializer Add, Deconstruct): a file that
        // relies on 'using <OldNs>;' only for those keeps it, so Using mode does not drop it.
        foreach (var extensionClass in ImplicitExtensionUses.Collect(root, model, cancellationToken))
        {
            if (OwnerType(extensionClass) is { } type && seen.Add(type))
            {
                result.Add(type);
            }
        }

        return result;
    }

    // The top-level type a name reaches: the type itself, a constructor's type or an extension method's static class.
    private static INamedTypeSymbol? OwnerType(ISymbol? symbol)
    {
        var type = symbol switch
        {
            IMethodSymbol { ReducedFrom: { } extension } => extension.ContainingType,
            IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType,
            INamedTypeSymbol named => named,
            _ => null,
        };

        while (type?.ContainingType is { } outer)
        {
            type = outer;
        }

        return type?.OriginalDefinition;
    }

    private static bool IsInNamespace(INamedTypeSymbol type, string ns) => string.Equals(type.ContainingNamespace.ToDisplayString(), ns, StringComparison.Ordinal);

    // Names of used types that a different type of the new namespace shares, so "using <NewNs>;" would make the simple name ambiguous.
    private static List<string> FindNameConflicts(Compilation compilation, List<INamedTypeSymbol> used, string newNamespace)
    {
        INamespaceSymbol? scope = compilation.GlobalNamespace;
        foreach (var part in newNamespace.Split('.'))
        {
            scope = scope?.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault();
        }

        if (scope is null)
        {
            return [];
        }

        return used
            .Where(type => scope.GetTypeMembers(type.Name).Any(candidate => !SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, type)))
            .Select(type => type.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // True when the left side of a qualified reference names the old namespace (by its symbol; by its text when the left side does not bind).
    private static bool LeftIsOldNamespace(SemanticModel model, ExpressionSyntax left, string oldNamespace, CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(left, cancellationToken);
        if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is INamespaceSymbol ns)
        {
            return string.Equals(ns.ToDisplayString(), oldNamespace, StringComparison.Ordinal);
        }

        var written = StripGlobal(left.ToString());
        return string.Equals(written, oldNamespace, StringComparison.Ordinal) || oldNamespace.EndsWith("." + written, StringComparison.Ordinal);
    }

    // The qualification in front of a reference: "A.B" of A.B.Type in a type, expression or cref position.
    private static bool TryGetQualifier(SimpleNameSyntax name, out ExpressionSyntax left)
    {
        switch (name.Parent)
        {
            case QualifiedNameSyntax qualified when qualified.Right == name:
                left = qualified.Left;
                return true;
            case MemberAccessExpressionSyntax access when access.Name == name:
                left = access.Expression;
                return true;
            case NameMemberCrefSyntax { Parent: QualifiedCrefSyntax qualifiedCref } member when qualifiedCref.Member == member:
                left = qualifiedCref.Container;
                return true;
        }

        left = null!;
        return false;
    }

    private static bool IsQualifiedRight(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Right == name,
        MemberAccessExpressionSyntax access => access.Name == name,
        AliasQualifiedNameSyntax alias => alias.Name == name,
        NameMemberCrefSyntax { Parent: QualifiedCrefSyntax qualifiedCref } member => qualifiedCref.Member == member,
        MemberBindingExpressionSyntax => true,
        _ => false,
    };

    // The written identifier is the type's name, or its name without the Attribute suffix ([Foo] for FooAttribute).
    private static bool NameMatches(string written, string typeName)
        => string.Equals(written, typeName, StringComparison.Ordinal) || string.Equals(written + "Attribute", typeName, StringComparison.Ordinal);

    // True when the file declares a namespace that is the old namespace or nested in it.
    private static bool DeclaresWithin(CompilationUnitSyntax root, string ns)
    {
        foreach (var declaration in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseNamespaceDeclarationSyntax>())
        {
            var full = string.Join(".", declaration.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(declared => declared.Name.ToString()));
            if (string.Equals(full, ns, StringComparison.Ordinal) || full.StartsWith(ns + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenerated(Document document, SyntaxNode root)
    {
        if (document is SourceGeneratedDocument)
        {
            return true;
        }

        var fileName = Path.GetFileName(document.FilePath ?? string.Empty);
        if (new[] { ".g.cs", ".g.i.cs", ".generated.cs", ".designer.cs" }.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // The header comment before the first token: "<auto-generated" is what the compilers' own generators write.
        return root.GetFirstToken(includeZeroWidth: true).LeadingTrivia
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Any(trivia => trivia.ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) || trivia.ToString().Contains("<autogenerated", StringComparison.OrdinalIgnoreCase));
    }

    private static SyntaxList<UsingDirectiveSyntax> UsingsOf(SyntaxNode container) => container switch
    {
        CompilationUnitSyntax unit => unit.Usings,
        BaseNamespaceDeclarationSyntax declaration => declaration.Usings,
        _ => default,
    };

    private static bool IsPlain(UsingDirectiveSyntax directive)
        => directive.Alias is null && directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.GlobalKeyword.IsKind(SyntaxKind.None) && directive.Name is not null;

    private static string UsingName(UsingDirectiveSyntax directive) => StripGlobal(directive.Name?.ToString() ?? string.Empty);

    private static string StripGlobal(string name) => name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;

    // Start of the line holding the position, but never ahead of a leading BOM.
    private static int LineStart(string text, int position, int bomOffset)
    {
        var lineStart = position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
        return Math.Max(lineStart, bomOffset);
    }
}

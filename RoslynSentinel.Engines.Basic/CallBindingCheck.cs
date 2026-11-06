using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Verdict of <see cref="CallBindingCheck"/>.</summary>
public enum CallBindingCheckStatus
{
    /// <summary>Every captured call binds where the operation says it should.</summary>
    Clean = 0,

    /// <summary>At least one call binds somewhere the operation does not account for, or a site appeared, vanished or changed count.</summary>
    Discrepancies,

    /// <summary>The check could not run. Callers treat this as a refusal (fail closed), never as a pass.</summary>
    CheckFailed,
}

/// <summary>Why a <see cref="CallBindingDiscrepancy"/> was reported.</summary>
public enum CallBindingDiscrepancyKind
{
    /// <summary>The call binds to a different symbol than the operation expects.</summary>
    BindsElsewhere = 0,

    /// <summary>A member mentions the name a different number of times than before, so its sites cannot be paired one to one.</summary>
    OccurrenceCountMismatch,

    /// <summary>The member that held these occurrences, or the occurrences themselves, are gone from the trial solution.</summary>
    MissingInTrial,

    /// <summary>The trial solution has occurrences in a member that were not there before and that the operation did not declare as added.</summary>
    UnexpectedInTrial,
}

/// <summary>
/// What <see cref="CallBindingCheck.CaptureAsync"/> records. Pass the same scope for the original and the trial capture.
/// </summary>
/// <param name="AffectedNames">Identifier texts to capture in every document that mentions one: method names for a method
/// operation, the type name for constructor calls (captured under the name "new TypeName").</param>
/// <param name="AddedUsingDocumentPaths">Documents where the change adds a using directive. For these every invocation is
/// captured, because a new using can rebind a differently named extension method. Null or empty when none.</param>
public sealed record CallBindingScope(IReadOnlyCollection<string> AffectedNames, IReadOnlyCollection<string>? AddedUsingDocumentPaths = null);

/// <summary>
/// One captured occurrence. The site key is (ProjectName, EnclosingMemberId, Name, Occurrence): no line numbers, so it survives
/// receiver rewrites and line shifts. FilePath, Line and CallText are for messages only.
/// </summary>
/// <param name="ProjectName">Project the document compiles in (multi-targeted projects have one name per target).</param>
/// <param name="EnclosingMemberId">Doc-comment id of the enclosing member (a local function or lambda uses its enclosing member); a "~" display string when it has none.</param>
/// <param name="Name">The identifier text, or "new TypeName" for a constructor call.</param>
/// <param name="Occurrence">0-based index of this occurrence of Name within the member, in document order.</param>
/// <param name="FilePath">Document path (document name when the document has no path).</param>
/// <param name="Line">1-based line of the occurrence.</param>
/// <param name="CallText">The bound expression, single-line and shortened.</param>
/// <param name="BoundId">Doc-comment id of the bound symbol's original definition (the static definition for a reduced extension call); "~" display string when it has none; "candidates:" + sorted ids when there is no single symbol; "unbound" when nothing bound.</param>
/// <param name="BoundDisplay">Readable form of the bound symbol(s) for messages.</param>
public sealed record CallBindingObservation(string ProjectName, string EnclosingMemberId, string Name, int Occurrence, string FilePath, int Line, string CallText, string BoundId, string BoundDisplay);

/// <summary>The observations of one solution, plus any problem that stopped part of the capture (which makes the check fail closed).</summary>
/// <param name="Observations">Every captured occurrence.</param>
/// <param name="Problems">Short, path-free reasons the capture was incomplete; empty when it was complete.</param>
public sealed record CallBindingSnapshot(IReadOnlyList<CallBindingObservation> Observations, IReadOnlyList<string> Problems);

/// <summary>
/// What the operation is expected to change. Everything not listed is expected to stay exactly as it was: the same enclosing
/// member, bound to the same symbol. All ids are doc-comment ids as recorded in <see cref="CallBindingObservation"/>.
/// </summary>
public sealed record CallBindingExpectations
{
    /// <summary>No change expected anywhere.</summary>
    public static CallBindingExpectations Unchanged { get; } = new();

    /// <summary>Old enclosing-member id -> new id, for a member the operation moved or re-signed. Its own body and the sites inside it pair through this map.</summary>
    public IReadOnlyDictionary<string, string>? MemberIdMap { get; init; }

    /// <summary>
    /// Old bound-symbol id -> every id a site that bound there may bind to now. MoveMember: old id -> the moved member's new id
    /// (keep modes also list the old id, because the forwarder stays a legal target). Sites bound to ids not listed must keep them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? ExpectedTargetsByOldId { get; init; }

    /// <summary>
    /// Per-site override, given the ORIGINAL observation; returns the acceptable ids, or null to fall back to
    /// <see cref="ExpectedTargetsByOldId"/> and then "unchanged". ChangeSignature: a migrated site -> the new id, a site left on the wrapper -> the old id.
    /// </summary>
    public Func<CallBindingObservation, IReadOnlyCollection<string>?>? ExpectedTargetsForSite { get; init; }

    /// <summary>
    /// Ids (as they are in the trial solution) of members the operation added or generated: the forwarder, the wrapper, the moved
    /// member. Occurrences in them that have no counterpart before are not reported; in any other member a count change is.
    /// </summary>
    public IReadOnlyCollection<string>? AddedMemberIds { get; init; }
}

/// <summary>One call that does not bind where the operation says it should, or a site that cannot be paired.</summary>
/// <param name="Kind">Why it was reported.</param>
/// <param name="ProjectName">Project of the site.</param>
/// <param name="EnclosingMemberId">Enclosing member id in the trial solution (original solution for MissingInTrial).</param>
/// <param name="Name">The identifier text, or "new TypeName".</param>
/// <param name="FilePath">Document of the original text for BindsElsewhere and MissingInTrial, of the trial text for the other kinds.</param>
/// <param name="Line">1-based line in that text.</param>
/// <param name="CallText">The bound expression as it reads in the trial text, single-line and shortened.</param>
/// <param name="BindsNow">What the site binds to today (original solution).</param>
/// <param name="WouldBind">What it would bind to after the operation (trial solution).</param>
/// <param name="ExpectedIds">The ids the operation accounted for.</param>
/// <param name="Detail">Extra explanation for the non-binding kinds (counts); empty for BindsElsewhere.</param>
public sealed record CallBindingDiscrepancy(CallBindingDiscrepancyKind Kind, string ProjectName, string EnclosingMemberId, string Name, string FilePath, int Line, string CallText, string BindsNow, string WouldBind, IReadOnlyList<string> ExpectedIds, string Detail);

/// <summary>Result of <see cref="CallBindingCheck"/>.</summary>
/// <param name="Status">Clean, Discrepancies or CheckFailed.</param>
/// <param name="Discrepancies">Every discrepancy (not capped; the formatter caps the listing).</param>
/// <param name="SitesChecked">Number of occurrences captured in the original solution.</param>
/// <param name="FailureReason">For CheckFailed: a short, path-free reason; otherwise null.</param>
public sealed record CallBindingCheckResult(CallBindingCheckStatus Status, IReadOnlyList<CallBindingDiscrepancy> Discrepancies, int SitesChecked, string? FailureReason)
{
    /// <summary>Total number of discrepancies.</summary>
    public int TotalDiscrepancies => Discrepancies.Count;

    /// <summary>True when the operation must be refused: discrepancies were found or the check could not run.</summary>
    public bool IsRefusal => Status != CallBindingCheckStatus.Clean;

    internal static CallBindingCheckResult Ok(int sitesChecked) => new(CallBindingCheckStatus.Clean, Array.Empty<CallBindingDiscrepancy>(), sitesChecked, null);

    internal static CallBindingCheckResult Failed(string reason) => new(CallBindingCheckStatus.CheckFailed, Array.Empty<CallBindingDiscrepancy>(), 0, reason);
}

/// <summary>
/// Detects a refactoring that silently changes which method a call binds to while the code still compiles (for example moving
/// Save(int) next to Save(long): target.Save(1) bound Save(long) before and binds the moved Save(int) after). Capture every
/// occurrence of the affected names on the original solution and on a trial solution (the caller builds the trial with the planned
/// changes applied, before anything is written), then compare site by site; <see cref="CallBindingExpectations"/> says what is
/// expected to change. This detects; the repair of calls that WERE callers of a moved member stays with the operation itself.
/// <para>
/// Limits, not a proof of completeness: dynamic binding, reflection, and runtime dispatch changes where the bound symbol is
/// unchanged (virtual or interface dispatch) are not visible. Only documents that mention an affected name are examined (plus the
/// added-using documents, fully), so a target-typed new(...) or a base/this initializer whose type name is not written in the
/// document is missed. Captured symbols are compared by documentation-comment id, so two symbols with the same id collapse.
/// </para>
/// </summary>
public static class CallBindingCheck
{
    /// <summary>How many discrepancies <see cref="FormatRefusal"/> lists by default.</summary>
    public const int DefaultMaxListed = 10;

    private const string Unbound = "unbound";
    private const string CandidatesPrefix = "candidates:";
    private const string Absent = "(absent)";
    private const int MaxCallTextLength = 80;

    private static readonly SymbolDisplayFormat DisplayFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    /// <summary>
    /// Captures both solutions and compares them. Fails closed: an exception other than cancellation, a document without a syntax
    /// tree or semantic model, or a failing expectation callback returns a CheckFailed result instead of passing.
    /// </summary>
    public static async Task<CallBindingCheckResult> CheckAsync(Solution original, Solution trial, CallBindingScope scope, CallBindingExpectations? expectations = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var before = await CaptureAsync(original, scope, cancellationToken);
            var after = await CaptureAsync(trial, scope, cancellationToken);
            return Compare(before, after, expectations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type is echoed: its text can carry file paths.
            return CallBindingCheckResult.Failed($"the capture raised {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Records every occurrence of an affected name (and, in the added-using documents, every invocation) in the C# projects of
    /// <paramref name="solution"/>. Declarations and doc-comment crefs are not captured; nameof is. Incomplete capture is reported
    /// in <see cref="CallBindingSnapshot.Problems"/>; other failures throw (use <see cref="CheckAsync"/> to fail closed).
    /// </summary>
    public static async Task<CallBindingSnapshot> CaptureAsync(Solution solution, CallBindingScope scope, CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(scope.AffectedNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.Ordinal);
        var everyInvocationPaths = new HashSet<string>((scope.AddedUsingDocumentPaths ?? Array.Empty<string>()).Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var observations = new List<CallBindingObservation>();
        var problems = new List<string>();
        var counters = new Dictionary<(string Project, string Member, string Name), int>();

        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            foreach (var document in project.Documents.OrderBy(d => d.FilePath ?? d.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CaptureDocumentAsync(project, document, names, everyInvocationPaths, counters, observations, problems, cancellationToken);
            }
        }

        return new CallBindingSnapshot(observations, problems);
    }

    private static async Task CaptureDocumentAsync(Project project, Document document, HashSet<string> names, HashSet<string> everyInvocationPaths, Dictionary<(string Project, string Member, string Name), int> counters, List<CallBindingObservation> observations, List<string> problems, CancellationToken cancellationToken)
    {
        var everyInvocation = document.FilePath != null && everyInvocationPaths.Contains(NormalizePath(document.FilePath));

        // Cheap text filter first: only documents that mention an affected name (or are added-using documents) are examined.
        var mentionsName = false;
        if (names.Count > 0)
        {
            var text = (await document.GetTextAsync(cancellationToken)).ToString();
            mentionsName = names.Any(n => text.Contains(n, StringComparison.Ordinal));
        }

        if (!everyInvocation && !mentionsName)
        {
            return;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            problems.Add($"a document of project '{project.Name}' has no syntax tree");
            return;
        }

        var candidates = CollectCandidates(root, names, mentionsName, everyInvocation);
        if (candidates.Count == 0)
        {
            return;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is null)
        {
            problems.Add($"a document of project '{project.Name}' has no semantic model");
            return;
        }

        var documentPath = document.FilePath ?? document.Name;
        foreach (var (anchor, bindNode, knownName) in candidates)
        {
            var info = model.GetSymbolInfo(bindNode, cancellationToken);
            var name = knownName;
            if (name is null)
            {
                // Target-typed new(...) and base/this initializers: the type is only known once bound.
                var typeName = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault())?.ContainingType?.Name;
                if (typeName is null || !names.Contains(typeName))
                {
                    continue;
                }

                name = "new " + typeName;
            }

            var (boundId, boundDisplay) = Describe(info);
            var memberId = EnclosingMemberId(anchor, model, document.Name, cancellationToken);
            var counterKey = (Project: project.Name, Member: memberId, Name: name);
            var occurrence = counters.GetValueOrDefault(counterKey);
            counters[counterKey] = occurrence + 1;
            var line = anchor.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            observations.Add(new CallBindingObservation(project.Name, memberId, name, occurrence, documentPath, line, ShortCallText(bindNode), boundId, boundDisplay));
        }
    }

    /// <summary>
    /// The syntax nodes to bind, in document order. Anchor is where the line and the enclosing member come from; BindNode is what
    /// is bound (the whole invocation for a call, as MoveBindCheck does); Name is the key name, or null when only binding reveals it.
    /// </summary>
    private static List<(SyntaxNode Anchor, SyntaxNode BindNode, string? Name)> CollectCandidates(SyntaxNode root, HashSet<string> names, bool mentionsName, bool everyInvocation)
    {
        var candidates = new List<(SyntaxNode Anchor, SyntaxNode BindNode, string? Name)>();

        // DescendantNodes() does not enter trivia, so doc-comment crefs are never captured. Declarations are tokens, not names.
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case SimpleNameSyntax name when name.Parent is not (NameColonSyntax or NameEqualsSyntax):
                    var bindNode = BindNodeFor(name);
                    var affected = mentionsName && names.Contains(name.Identifier.ValueText);
                    if (affected || (everyInvocation && bindNode is InvocationExpressionSyntax))
                    {
                        candidates.Add((name, bindNode, name.Identifier.ValueText));
                    }

                    break;
                case ObjectCreationExpressionSyntax creation when mentionsName:
                    var typeName = RightmostName(creation.Type);
                    if (typeName != null && names.Contains(typeName))
                    {
                        candidates.Add((creation, creation, "new " + typeName));
                    }

                    break;
                case ImplicitObjectCreationExpressionSyntax implicitCreation when mentionsName:
                    candidates.Add((implicitCreation, implicitCreation, null));
                    break;
                case ConstructorInitializerSyntax initializer when mentionsName:
                    candidates.Add((initializer, initializer, null));
                    break;
                case PrimaryConstructorBaseTypeSyntax primaryBase when mentionsName:
                    // class D(int x) : B(x): the call is bound on the base-type node; like an initializer its type is only known once bound.
                    candidates.Add((primaryBase, primaryBase, null));
                    break;
            }
        }

        return candidates;
    }

    /// <summary>The expression to bind for a name: the member access or binding it ends, and the invocation that calls that.</summary>
    private static SyntaxNode BindNodeFor(SimpleNameSyntax name)
    {
        ExpressionSyntax bound = name;
        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
        {
            bound = memberAccess;
        }
        else if (name.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == name)
        {
            bound = memberBinding;
        }

        if (bound.Parent is InvocationExpressionSyntax invocation && invocation.Expression == bound)
        {
            return invocation;
        }

        return bound;
    }

    private static string? RightmostName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => RightmostName(qualified.Right),
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>The comparable id and the readable text of what a bind found: one symbol, sorted candidates, or nothing.</summary>
    private static (string Id, string Display) Describe(SymbolInfo info)
    {
        // A method group (nameof(Target.Save)) has no Symbol but one candidate: that single candidate is the bound symbol.
        var single = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
        if (single is { } symbol)
        {
            return (SymbolId(symbol), (SourceDefinition(symbol)).ToDisplayString(DisplayFormat));
        }

        if (info.CandidateSymbols.Length > 0)
        {
            var ids = info.CandidateSymbols.Select(SymbolId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var displays = info.CandidateSymbols.Select(c => SourceDefinition(c).ToDisplayString(DisplayFormat)).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal);
            return (CandidatesPrefix + string.Join("|", ids), "no single method (candidates: " + string.Join("; ", displays) + ")");
        }

        return (Unbound, "nothing (it does not bind)");
    }

    /// <summary>The static definition behind a reduced extension call, else the original definition of a constructed member.</summary>
    private static ISymbol SourceDefinition(ISymbol symbol) => ((symbol as IMethodSymbol)?.ReducedFrom ?? symbol).OriginalDefinition;

    private static string SymbolId(ISymbol symbol)
    {
        var definition = SourceDefinition(symbol);
        return definition.GetDocumentationCommentId() ?? $"~{definition.Kind}:{definition.ToDisplayString(DisplayFormat)}";
    }

    /// <summary>Doc-comment id of the member that holds the node; a local function or lambda is part of its enclosing member.</summary>
    private static string EnclosingMemberId(SyntaxNode anchor, SemanticModel model, string documentName, CancellationToken cancellationToken)
    {
        var member = anchor.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        ISymbol? symbol = null;
        if (member is BaseFieldDeclarationSyntax field)
        {
            var declarator = anchor.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => field.Declaration.Variables.Contains(v)) ?? field.Declaration.Variables.First();
            symbol = model.GetDeclaredSymbol(declarator, cancellationToken);
        }
        else if (member is not null and not GlobalStatementSyntax)
        {
            symbol = model.GetDeclaredSymbol(member, cancellationToken);
        }

        if (symbol is null)
        {
            return $"~{(member is null ? "compilation-unit" : member.Kind().ToString())}:{documentName}";
        }

        return symbol.GetDocumentationCommentId() ?? $"~{symbol.Kind}:{symbol.ToDisplayString(DisplayFormat)}";
    }

    private static string ShortCallText(SyntaxNode node)
    {
        var text = string.Join(" ", node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxCallTextLength ? text : text[..MaxCallTextLength] + "...";
    }

    /// <summary>
    /// Pairs the sites of <paramref name="original"/> with those of <paramref name="trial"/> by (project, enclosing member, name,
    /// occurrence) and reports every site whose binding differs from what <paramref name="expectations"/> allows. The enclosing
    /// member is translated through <see cref="CallBindingExpectations.MemberIdMap"/>. When a (project, member, name) group has a
    /// different occurrence count it is reported, not paired, unless the trial member is in
    /// <see cref="CallBindingExpectations.AddedMemberIds"/> and only has extra occurrences (then the extras are ignored). Fails
    /// closed on an incomplete snapshot or a throwing expectation callback.
    /// </summary>
    public static CallBindingCheckResult Compare(CallBindingSnapshot original, CallBindingSnapshot trial, CallBindingExpectations? expectations = null)
    {
        if (original.Problems.Count > 0 || trial.Problems.Count > 0)
        {
            return CallBindingCheckResult.Failed("the call bindings could not be captured completely (" + string.Join("; ", original.Problems.Concat(trial.Problems).Distinct(StringComparer.Ordinal).Take(3)) + ")");
        }

        try
        {
            return CompareCore(original, trial, expectations ?? CallBindingExpectations.Unchanged);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CallBindingCheckResult.Failed($"the comparison raised {ex.GetType().Name}");
        }
    }

    private static CallBindingCheckResult CompareCore(CallBindingSnapshot original, CallBindingSnapshot trial, CallBindingExpectations expectations)
    {
        var added = new HashSet<string>(expectations.AddedMemberIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var trialGroups = trial.Observations
            .GroupBy(o => (o.ProjectName, o.EnclosingMemberId, o.Name))
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Occurrence).ToList());
        var consumed = new HashSet<(string, string, string)>();
        var discrepancies = new List<CallBindingDiscrepancy>();

        foreach (var group in original.Observations.GroupBy(o => (o.ProjectName, o.EnclosingMemberId, o.Name)))
        {
            var before = group.OrderBy(o => o.Occurrence).ToList();
            var mappedMember = expectations.MemberIdMap is { } map && map.TryGetValue(group.Key.EnclosingMemberId, out var mapped) ? mapped : group.Key.EnclosingMemberId;
            var trialKey = (group.Key.ProjectName, mappedMember, group.Key.Name);
            consumed.Add(trialKey);

            if (!trialGroups.TryGetValue(trialKey, out var after))
            {
                var gone = before[0];
                discrepancies.Add(new CallBindingDiscrepancy(CallBindingDiscrepancyKind.MissingInTrial, gone.ProjectName, gone.EnclosingMemberId, gone.Name, gone.FilePath, gone.Line, gone.CallText, gone.BoundDisplay, Absent, new[] { gone.BoundId }, $"'{gone.Name}' appears {before.Count} time(s) here today and not at all afterwards"));
                continue;
            }

            if (before.Count != after.Count && (before.Count > after.Count || !added.Contains(mappedMember)))
            {
                var changed = after[0];
                discrepancies.Add(new CallBindingDiscrepancy(CallBindingDiscrepancyKind.OccurrenceCountMismatch, changed.ProjectName, changed.EnclosingMemberId, changed.Name, changed.FilePath, changed.Line, changed.CallText, before[0].BoundDisplay, changed.BoundDisplay, new[] { before[0].BoundId }, $"'{changed.Name}' appears {before.Count} time(s) in this member today and {after.Count} afterwards, so its sites cannot be compared one to one"));
                continue;
            }

            for (var i = 0; i < before.Count; i++)
            {
                var expected = ExpectedIdsFor(before[i], expectations);
                if (!expected.Contains(after[i].BoundId, StringComparer.Ordinal))
                {
                    var site = after[i];
                    // File and line are the ORIGINAL ones: nothing is written yet, so the trial's line numbers (shifted by the edit) mean nothing to the reader. The call text is how the call reads in the trial.
                    discrepancies.Add(new CallBindingDiscrepancy(CallBindingDiscrepancyKind.BindsElsewhere, site.ProjectName, site.EnclosingMemberId, site.Name, before[i].FilePath, before[i].Line, site.CallText, before[i].BoundDisplay, site.BoundDisplay, expected.ToList(), string.Empty));
                }
            }
        }

        foreach (var (key, extra) in trialGroups)
        {
            if (consumed.Contains(key) || added.Contains(key.EnclosingMemberId))
            {
                continue;
            }

            var appeared = extra[0];
            discrepancies.Add(new CallBindingDiscrepancy(CallBindingDiscrepancyKind.UnexpectedInTrial, appeared.ProjectName, appeared.EnclosingMemberId, appeared.Name, appeared.FilePath, appeared.Line, appeared.CallText, Absent, appeared.BoundDisplay, Array.Empty<string>(), $"'{appeared.Name}' appears {extra.Count} time(s) here afterwards and not at all today"));
        }

        if (discrepancies.Count == 0)
        {
            return CallBindingCheckResult.Ok(original.Observations.Count);
        }

        var ordered = discrepancies
            .OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        return new CallBindingCheckResult(CallBindingCheckStatus.Discrepancies, ordered, original.Observations.Count, null);
    }

    /// <summary>Per-site override first, then the old-id map, else the id the site has today.</summary>
    private static IReadOnlyCollection<string> ExpectedIdsFor(CallBindingObservation original, CallBindingExpectations expectations)
    {
        var perSite = expectations.ExpectedTargetsForSite?.Invoke(original);
        if (perSite is not null)
        {
            return perSite;
        }

        if (expectations.ExpectedTargetsByOldId is { } byOldId && byOldId.TryGetValue(original.BoundId, out var mapped))
        {
            return mapped;
        }

        return new[] { original.BoundId };
    }

    /// <summary>
    /// The refusal text for a non-clean <paramref name="result"/>; empty for a clean one. Lists at most
    /// <paramref name="maxListed"/> discrepancies and states the total. <paramref name="operation"/> completes "Cannot ...",
    /// for example "move 'Save' to 'Target'".
    /// </summary>
    /// <param name="bindsElsewhereFix">Optional fix text (no trailing period) for a call that would re-point to another method; null keeps the default text.</param>
    /// <param name="unmatchedFix">Optional fix text (no trailing period) for every other discrepancy kind; null keeps the default text.</param>
    public static string FormatRefusal(CallBindingCheckResult result, string operation, int maxListed = DefaultMaxListed, string? bindsElsewhereFix = null, string? unmatchedFix = null)
    {
        if (!result.IsRefusal)
        {
            return string.Empty;
        }

        if (result.Status == CallBindingCheckStatus.CheckFailed)
        {
            return $"Cannot {operation}: the call bindings could not be verified ({result.FailureReason}), and an unchecked change can silently re-point a call at a different method. Nothing was written.";
        }

        var shown = result.Discrepancies.Take(Math.Max(1, maxListed)).ToList();
        var entries = string.Join(" | ", shown.Select((d, index) => $"{index + 1}) {DescribeDiscrepancy(d, bindsElsewhereFix, unmatchedFix)}"));
        var more = result.TotalDiscrepancies > shown.Count ? $" ... {result.TotalDiscrepancies - shown.Count} more not shown." : string.Empty;
        var first = shown[0];
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var hint = ToolCallHint.Next("ReadFile", "to see the first site in context.", ("filePath", first.FilePath), ("startLine", Math.Max(1, first.Line - 3).ToString(culture)), ("endLine", (first.Line + 3).ToString(culture)));
        return $"Cannot {operation}: {result.TotalDiscrepancies} call site(s) would silently change which method they bind to (the code would still compile) or could not be matched against the original. Nothing was written. Showing {shown.Count} of {result.TotalDiscrepancies}: {entries}{more} {hint}";
    }

    private static string DescribeDiscrepancy(CallBindingDiscrepancy d, string? bindsElsewhereFix = null, string? unmatchedFix = null)
    {
        var where = $"{d.FilePath}:{d.Line} in {ReadableId(d.EnclosingMemberId)}";
        if (d.Kind == CallBindingDiscrepancyKind.BindsElsewhere)
        {
            var bindsFix = bindsElsewhereFix ?? $"keep the call on '{d.BindsNow}' (for an argument capture, cast the argument to that method's parameter type), use a different name for the new member, or leave the member where it is";
            return $"{where}: `{d.CallText}` binds to '{d.BindsNow}' today but would bind to '{d.WouldBind}'. Fix: {bindsFix}.";
        }

        var unmatchedText = unmatchedFix ?? "review that member by hand before retrying, or leave the member where it is";
        return $"{where}: {d.Detail}. Fix: {unmatchedText}.";
    }

    private static string ReadableId(string id) => id.Length > 2 && id[1] == ':' ? id[2..] : id.TrimStart('~');

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

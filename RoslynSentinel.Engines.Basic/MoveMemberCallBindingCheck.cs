using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// MoveMember's use of <see cref="CallBindingCheck"/>: builds the scope and the expectations from the moved symbols and the trial
/// solution (the original with the move's planned file texts applied, before anything is written), then compares every call of a
/// moved member's name before and after. Whatever the move rewrites (a receiver, a qualification) is expected to bind to the moved
/// member's new location; keep modes also accept the old location, where a forwarder stays; everything else must keep its binding.
/// <para>
/// Limits: a moved nested type is mapped member by member by replacing its qualified name (not verified against the trial). The
/// caller skips the check when the trial has compile errors (the write's compile gate reports those).
/// </para>
/// </summary>
public static class MoveMemberCallBindingCheck
{
    private static readonly SymbolDisplayFormat DisplayFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    /// <summary>
    /// Captures the bindings of the moved members' names in <paramref name="original"/> and <paramref name="trial"/> and compares them.
    /// Fails closed: anything that cannot be mapped or captured returns a CheckFailed result, never a pass.
    /// </summary>
    /// <param name="original">The committed solution before the move.</param>
    /// <param name="trial">The same solution with the move's file texts applied.</param>
    /// <param name="movedSymbols">The moved declarations as symbols of <paramref name="original"/> (one per method, property, field variable, event or nested type).</param>
    /// <param name="targetClassName">Name of the type that receives the members (an existing class, a new class or a base type).</param>
    /// <param name="changedPaths">Paths of the files the move changes or creates.</param>
    /// <param name="keepsOldMember">True for the keep modes: a forwarder stays at the old location, so it is also a legal binding target.</param>
    public static async Task<CallBindingCheckResult> CheckAsync(Solution original, Solution trial, IReadOnlyCollection<ISymbol> movedSymbols, string targetClassName, IReadOnlyCollection<string> changedPaths, bool keepsOldMember, CancellationToken cancellationToken = default)
    {
        try
        {
            var plan = await BuildPlanAsync(original, trial, movedSymbols, targetClassName, changedPaths, keepsOldMember, cancellationToken);
            if (plan.Failure != null)
            {
                return CallBindingCheckResult.Failed(plan.Failure);
            }

            var before = await CallBindingCheck.CaptureAsync(original, plan.Scope!, cancellationToken);
            var after = await CallBindingCheck.CaptureAsync(trial, plan.Scope!, cancellationToken);
            before = RemapProjects(before, plan.Expectations!.MemberIdMap!, plan.SourceProject, plan.TargetProject);
            return CallBindingCheck.Compare(before, after, plan.Expectations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type is echoed: its text can carry file paths.
            return CallBindingCheckResult.Failed($"the move check raised {ex.GetType().Name}");
        }
    }

    private static async Task<(string? Failure, CallBindingScope? Scope, CallBindingExpectations? Expectations, string? SourceProject, string? TargetProject)> BuildPlanAsync(Solution original, Solution trial, IReadOnlyCollection<ISymbol> movedSymbols, string targetClassName, IReadOnlyCollection<string> changedPaths, bool keepsOldMember, CancellationToken cancellationToken)
    {
        static (string? Failure, CallBindingScope? Scope, CallBindingExpectations? Expectations, string? SourceProject, string? TargetProject) Fail(string reason) => (reason, null, null, null, null);

        var (target, targetProject, targetFailure) = await FindTargetAsync(trial, targetClassName, changedPaths, cancellationToken);
        if (target is null)
        {
            return Fail(targetFailure!);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var oldToNew = new Dictionary<string, string>(StringComparer.Ordinal);
        string? sourceProject = null;
        foreach (var moved in movedSymbols.Select(s => s.OriginalDefinition))
        {
            var oldId = moved.GetDocumentationCommentId();
            if (oldId is null)
            {
                return Fail($"the moved member '{moved.Name}' has no documentation id");
            }

            sourceProject ??= moved.DeclaringSyntaxReferences.Select(r => original.GetDocument(r.SyntaxTree)?.Project.Name).FirstOrDefault(n => n != null);
            names.Add(moved.Name);
            if (moved is INamedTypeSymbol movedType)
            {
                var newType = target.GetTypeMembers(movedType.Name, movedType.Arity).FirstOrDefault();
                if (newType is null)
                {
                    return Fail($"the moved type '{moved.Name}' was not found in the target after the move");
                }

                MapTypeTree(movedType, newType, oldToNew);
                continue;
            }

            var newMember = FindMovedMember(target, moved);
            var newId = newMember?.OriginalDefinition.GetDocumentationCommentId();
            if (newId is null)
            {
                return Fail($"the moved member '{moved.Name}' could not be matched to a member of the target after the move");
            }

            oldToNew.TryAdd(oldId, newId);
        }

        if (names.Count == 0)
        {
            return Fail("no moved member names to check");
        }

        var expectations = new CallBindingExpectations
        {
            MemberIdMap = oldToNew,
            ExpectedTargetsByOldId = oldToNew.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)(keepsOldMember ? new[] { kv.Value, kv.Key } : new[] { kv.Value }), StringComparer.Ordinal),
            // The moved members at their new place; in keep modes also the forwarders that stay at the old ids.
            AddedMemberIds = oldToNew.Values.Concat(keepsOldMember ? oldToNew.Keys : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList(),
        };

        var addedUsingPaths = await AddedUsingPathsAsync(original, trial, changedPaths, cancellationToken);
        return (null, new CallBindingScope(names, addedUsingPaths), expectations, sourceProject, targetProject);
    }

    /// <summary>The single type named <paramref name="targetClassName"/> declared in the changed files of the trial, and the project it compiles in.</summary>
    private static async Task<(INamedTypeSymbol? Target, string? Project, string? Failure)> FindTargetAsync(Solution trial, string targetClassName, IReadOnlyCollection<string> changedPaths, CancellationToken cancellationToken)
    {
        var found = new List<INamedTypeSymbol>();
        string? project = null;
        foreach (var path in changedPaths)
        {
            foreach (var documentId in trial.GetDocumentIdsWithFilePath(path))
            {
                var document = trial.GetDocument(documentId);
                var root = document == null ? null : await document.GetSyntaxRootAsync(cancellationToken);
                var model = document == null ? null : await document.GetSemanticModelAsync(cancellationToken);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Identifier.Text == targetClassName))
                {
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol && !found.Any(f => SymbolEqualityComparer.Default.Equals(f, symbol)))
                    {
                        found.Add(symbol);
                        project = document!.Project.Name;
                    }
                }
            }
        }

        return found.Count == 1 ? (found[0], project, null) : (null, null, found.Count == 0 ? $"the target type '{targetClassName}' was not found in the files the move changes" : $"the target type '{targetClassName}' is declared more than once in the files the move changes");
    }

    /// <summary>The member of the target that is the moved member after the move: same name and kind, preferring the same parameter types.</summary>
    private static ISymbol? FindMovedMember(INamedTypeSymbol target, ISymbol moved)
    {
        var sameNameAndKind = target.GetMembers(moved.Name).Where(c => c.Kind == moved.Kind).ToList();
        var sameSignature = sameNameAndKind.Where(c => ParameterKey(c) == ParameterKey(moved)).ToList();
        if (sameSignature.Count == 1)
        {
            return sameSignature[0];
        }

        return sameSignature.Count == 0 && sameNameAndKind.Count == 1 ? sameNameAndKind[0] : null;
    }

    private static string ParameterKey(ISymbol symbol)
    {
        var parameters = symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => default,
        };
        var arity = symbol is IMethodSymbol m ? m.TypeParameters.Length : 0;
        return arity + "|" + (parameters.IsDefault ? string.Empty : string.Join(",", parameters.Select(p => p.RefKind + " " + p.Type.ToDisplayString(DisplayFormat))));
    }

    /// <summary>Maps a moved nested type and everything inside it by replacing the type's qualified name in each documentation id.</summary>
    private static void MapTypeTree(INamedTypeSymbol oldType, INamedTypeSymbol newType, Dictionary<string, string> map)
    {
        // TODO MoveMember call-binding check: the mapped ids of a moved nested type's members are not verified against the trial symbols (and no existing test moves a nested type with affected-name calls)
        var oldQualified = oldType.OriginalDefinition.GetDocumentationCommentId()?[2..];
        var newQualified = newType.OriginalDefinition.GetDocumentationCommentId()?[2..];
        if (oldQualified is null || newQualified is null)
        {
            return;
        }

        void Visit(ISymbol symbol)
        {
            var id = symbol.OriginalDefinition.GetDocumentationCommentId();
            if (id != null)
            {
                map.TryAdd(id, id.Replace(oldQualified, newQualified, StringComparison.Ordinal));
            }

            if (symbol is INamedTypeSymbol type)
            {
                foreach (var member in type.GetMembers())
                {
                    Visit(member);
                }
            }
        }

        Visit(oldType);
    }

    /// <summary>
    /// When the move adds a using directive to any changed file, every invocation in the changed files is captured (a new using can
    /// rebind a differently named extension method); otherwise none. Null when nothing is added.
    /// </summary>
    private static async Task<IReadOnlyCollection<string>?> AddedUsingPathsAsync(Solution original, Solution trial, IReadOnlyCollection<string> changedPaths, CancellationToken cancellationToken)
    {
        foreach (var path in changedPaths)
        {
            var trialDocumentId = trial.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            var trialRoot = trialDocumentId == null ? null : await trial.GetDocument(trialDocumentId)!.GetSyntaxRootAsync(cancellationToken);
            if (trialRoot is null)
            {
                continue;
            }

            var originalDocumentId = original.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            var originalRoot = originalDocumentId == null ? null : await original.GetDocument(originalDocumentId)!.GetSyntaxRootAsync(cancellationToken);
            var before = originalRoot == null ? new HashSet<string>(StringComparer.Ordinal) : UsingKeys(originalRoot);
            if (UsingKeys(trialRoot).Any(key => !before.Contains(key)))
            {
                return changedPaths.ToList();
            }
        }

        return null;
    }

    private static HashSet<string> UsingKeys(SyntaxNode root) => root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => string.Concat(u.ToString().Where(c => !char.IsWhiteSpace(c)))).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The site key includes the project, and a member moved into another project compiles there afterwards, so the original
    /// observations inside a moved member are renamed to the target project (keeping a target-framework suffix like "(net8.0)").
    /// </summary>
    private static CallBindingSnapshot RemapProjects(CallBindingSnapshot snapshot, IReadOnlyDictionary<string, string> movedMemberIds, string? sourceProject, string? targetProject)
    {
        if (sourceProject is null || targetProject is null)
        {
            return snapshot;
        }

        var sourceBase = BaseName(sourceProject);
        var targetBase = BaseName(targetProject);
        if (string.Equals(sourceBase, targetBase, StringComparison.Ordinal))
        {
            return snapshot;
        }

        var remapped = snapshot.Observations.Select(o =>
        {
            if (!movedMemberIds.ContainsKey(o.EnclosingMemberId) || !string.Equals(BaseName(o.ProjectName), sourceBase, StringComparison.Ordinal))
            {
                return o;
            }

            return o with { ProjectName = targetBase + o.ProjectName[sourceBase.Length..] };
        }).ToList();
        return new CallBindingSnapshot(remapped, snapshot.Problems);
    }

    private static string BaseName(string projectName)
    {
        var index = projectName.IndexOf('(', StringComparison.Ordinal);
        return index < 0 ? projectName : projectName[..index].TrimEnd();
    }
}

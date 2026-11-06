using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// ChangeSignature's use of <see cref="CallBindingCheck"/>: compares every call of the changed method's name (for a constructor, the
/// type name) before and after, on a trial solution the caller built from the planned file texts, before anything is written.
/// A call the operation migrates is expected on the NEW member; a call it leaves on the kept [Obsolete] wrapper (same documentation
/// id as the old member) is expected on the OLD id; with no wrapper every call that bound to the old member must end on the new
/// one; every other call must keep its binding. Migrated and left-on-wrapper are decided by the operation's own plan (the call
/// sites it rewrites and the ones it skips), matched to the captured calls by file and line: when one line holds both kinds, both
/// bindings are accepted for it. The wrapper's own forwarding call is generated code (an added member); the changed member's own
/// body pairs with its old self through the member id map.
/// <para>
/// Limits: ChangeSignature adds no using directive, so no added-using documents are passed. A call that is neither planned nor
/// skipped (no reference location for it) is treated as left alone. Checked per site by documentation id only.
/// </para>
/// </summary>
public static class ChangeSignatureCallBindingCheck
{
    // Mirrors the id format CallBindingCheck gives a bind that found several methods and no single one: prefix + sorted distinct ids joined by "|".
    private const string CandidatesPrefix = "candidates:";

    /// <summary>
    /// Captures the bindings of <paramref name="methodName"/> in <paramref name="original"/> and <paramref name="trial"/> and compares
    /// them. Fails closed: anything that cannot be mapped or captured returns a CheckFailed result, never a pass.
    /// </summary>
    /// <param name="original">The committed solution before the change.</param>
    /// <param name="trial">The same solution with the change's planned file texts applied.</param>
    /// <param name="changedMethod">The changed method or constructor as a symbol of <paramref name="original"/>.</param>
    /// <param name="methodName">The name the change targets (the type name for a constructor).</param>
    /// <param name="newParameterList">The new parameter list the change declares, used to find the new member in the trial.</param>
    /// <param name="declarationPath">Path of the file that declares the changed member.</param>
    /// <param name="keepsWrapper">True when the old signature stays as an [Obsolete] wrapper.</param>
    /// <param name="migratedSites">File and 1-based line of every call site the change rewrites.</param>
    /// <param name="leftOnWrapperSites">File and 1-based line of every call site the change leaves alone (skipped).</param>
    public static async Task<CallBindingCheckResult> CheckAsync(Solution original, Solution trial, IMethodSymbol changedMethod, string methodName, ParameterListSyntax newParameterList, string declarationPath, bool keepsWrapper, IReadOnlyCollection<(string FilePath, int Line)> migratedSites, IReadOnlyCollection<(string FilePath, int Line)> leftOnWrapperSites, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldId = changedMethod.OriginalDefinition.GetDocumentationCommentId();
            var containerId = changedMethod.ContainingType.OriginalDefinition.GetDocumentationCommentId();
            if (oldId is null || containerId is null)
            {
                return CallBindingCheckResult.Failed($"the changed member '{methodName}' has no documentation id");
            }

            var (newId, failure) = await FindNewIdAsync(trial, declarationPath, methodName, containerId, newParameterList, cancellationToken);
            if (newId is null)
            {
                return CallBindingCheckResult.Failed(failure!);
            }

            var migrated = new HashSet<string>(migratedSites.Select(s => SiteKey(s.FilePath, s.Line)), StringComparer.OrdinalIgnoreCase);
            var stayed = new HashSet<string>(leftOnWrapperSites.Select(s => SiteKey(s.FilePath, s.Line)), StringComparer.OrdinalIgnoreCase);
            var expectations = new CallBindingExpectations
            {
                MemberIdMap = new Dictionary<string, string>(StringComparer.Ordinal) { [oldId] = newId },
                ExpectedTargetsForSite = site => ExpectedTargets(site, oldId, newId, keepsWrapper, migrated, stayed),
                // Only the wrapper is generated; the new member's body is the old body, so it must pair one to one through the id map.
                AddedMemberIds = keepsWrapper ? new[] { oldId } : null,
            };
            return await CallBindingCheck.CheckAsync(original, trial, new CallBindingScope(new[] { methodName }), expectations, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type is echoed: its text can carry file paths.
            return CallBindingCheckResult.Failed($"the signature check raised {ex.GetType().Name}");
        }
    }

    /// <summary>The ids a call may bind to after the change, given the call as it bound before; null for a call that did not involve the changed member.</summary>
    private static IReadOnlyCollection<string>? ExpectedTargets(CallBindingObservation site, string oldId, string newId, bool keepsWrapper, HashSet<string> migrated, HashSet<string> stayed)
    {
        var isCandidateSet = site.BoundId.StartsWith(CandidatesPrefix, StringComparison.Ordinal);
        var before = isCandidateSet ? site.BoundId[CandidatesPrefix.Length..].Split('|') : new[] { site.BoundId };
        if (!before.Contains(oldId, StringComparer.Ordinal))
        {
            return null;
        }

        if (isCandidateSet)
        {
            // nameof(M) or another bind that sees every overload: the set loses the old id unless a wrapper keeps it, and gains the new one.
            return new[] { Candidates(before.Where(id => keepsWrapper || id != oldId).Append(newId)) };
        }

        if (!keepsWrapper)
        {
            return new[] { newId };
        }

        var key = SiteKey(site.FilePath, site.Line);
        var accepted = new List<string>();
        var isMigrated = migrated.Contains(key);
        if (isMigrated)
        {
            accepted.Add(newId);
        }

        if (stayed.Contains(key) || !isMigrated)
        {
            // Left on the wrapper (same id as the old member); a method group or nameof then sees both overloads.
            accepted.Add(oldId);
            accepted.Add(Candidates(new[] { oldId, newId }));
        }

        return accepted;
    }

    private static string Candidates(IEnumerable<string> ids) => CandidatesPrefix + string.Join("|", ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal));

    private static string SiteKey(string path, int line) => path.Replace('\\', '/') + "|" + line;

    /// <summary>
    /// The documentation id of the changed member as it is declared in the trial: the member of the same name in the same type, in the
    /// declaration file, whose parameter list is the new one (compared without whitespace; the parameter names are the fallback).
    /// </summary>
    private static async Task<(string? Id, string? Failure)> FindNewIdAsync(Solution trial, string declarationPath, string methodName, string containerId, ParameterListSyntax newParameterList, CancellationToken cancellationToken)
    {
        static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var wantedText = Compact(newParameterList.ToString());
        var wantedNames = newParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
        var byText = new List<string>();
        var byNames = new List<string>();
        foreach (var documentId in trial.GetDocumentIdsWithFilePath(declarationPath))
        {
            var document = trial.GetDocument(documentId);
            var root = document == null ? null : await document.GetSyntaxRootAsync(cancellationToken);
            var model = document == null ? null : await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || model is null)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var identifier = declaration switch
                {
                    MethodDeclarationSyntax method => method.Identifier.Text,
                    ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
                    _ => null,
                };
                if (identifier != methodName || model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol symbol)
                {
                    continue;
                }

                var id = symbol.OriginalDefinition.GetDocumentationCommentId();
                if (id is null || symbol.ContainingType.OriginalDefinition.GetDocumentationCommentId() != containerId)
                {
                    continue;
                }

                if (Compact(declaration.ParameterList.ToString()) == wantedText)
                {
                    byText.Add(id);
                }
                else if (declaration.ParameterList.Parameters.Select(p => p.Identifier.Text).SequenceEqual(wantedNames))
                {
                    byNames.Add(id);
                }
            }
        }

        var found = (byText.Count > 0 ? byText : byNames).Distinct(StringComparer.Ordinal).ToList();
        return found.Count == 1 ? (found[0], null) : (null, found.Count == 0 ? $"the changed member '{methodName}' was not found with its new parameter list after the change" : $"the changed member '{methodName}' matched more than one declaration after the change");
    }
}

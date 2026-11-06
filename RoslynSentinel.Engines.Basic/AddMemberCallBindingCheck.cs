using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Member addMember's (and insertAfter/insertBefore's) use of <see cref="CallBindingCheck"/>: a new method or property can capture a
/// call that bound elsewhere before (an overload that fits an argument better, an instance member that beats an extension method or
/// an outer member of the same name). The add introduces no moved id, so the expectation is plain: every existing site keeps its
/// binding; the new member and the occurrences in its own body are the only additions.
/// <para>
/// Cost control, in order: a member that is not a method or property is not checked; a name that appears in no document of the
/// solution cannot be called anywhere (the new member is not in the original), so the check is skipped; a name that is only
/// declared or mentioned, never bound, is cleared after one capture of the original; only then is the trial built and captured.
/// A trial whose new member has compile errors of its own is not compared (the write's compile gate reports those). Limits: those
/// of <see cref="CallBindingCheck"/>; constructors, operators, indexers, fields and nested types are not checked.
/// </para>
/// </summary>
public static class AddMemberCallBindingCheck
{
    private const string CandidatesPrefix = "candidates:";

    /// <summary>The name whose bindings an add of <paramref name="member"/> can change; null for a kind that is not checked.</summary>
    public static string? CheckedNameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// The cheap text pre-check: true when <paramref name="name"/> occurs in any C# document of <paramref name="solution"/>. When it
    /// is false no existing call can be captured by a new member of that name, and the add needs no further check.
    /// </summary>
    public static async Task<bool> NameAppearsAsync(Solution solution, string name, CancellationToken cancellationToken = default)
    {
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await document.GetTextAsync(cancellationToken);
                if (text.ToString().Contains(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The gate the add routes call after computing <paramref name="updatedText"/> and before returning it: throws
    /// <see cref="ToolInvalidArgumentException"/> (message ends "Nothing was written.") when the add would re-point an existing call
    /// or when the check cannot run; returns normally for a clean add and for every skip described on the class.
    /// </summary>
    /// <param name="solution">The committed solution the add is computed against.</param>
    /// <param name="document">The document that receives the member.</param>
    /// <param name="container">The type declaration, in <paramref name="document"/>'s original syntax tree, that receives the member.</param>
    /// <param name="newMember">The parsed member being added.</param>
    /// <param name="insertIndex">Index in <paramref name="container"/>'s members at which the member was inserted.</param>
    /// <param name="updatedText">The document text with the member added.</param>
    public static async Task RequireKeepsBindingsAsync(Solution solution, Document document, TypeDeclarationSyntax container, MemberDeclarationSyntax newMember, int insertIndex, string updatedText, CancellationToken cancellationToken = default)
    {
        var name = CheckedNameOf(newMember);
        if (name is null || !await NameAppearsAsync(solution, name, cancellationToken))
        {
            return;
        }

        var check = await CheckAsync(solution, document.Id, container, newMember, insertIndex, updatedText, cancellationToken);
        if (check.IsRefusal)
        {
            var kind = newMember is PropertyDeclarationSyntax ? "property" : "method";
            throw new ToolInvalidArgumentException(CallBindingCheck.FormatRefusal(check, $"add {kind} '{name}' to '{container.Identifier.ValueText}'"));
        }
    }

    /// <summary>
    /// Captures the bindings of the new member's name in <paramref name="original"/> and in a trial with <paramref name="updatedText"/>
    /// applied, and compares them. Fails closed: anything that cannot be located or captured returns a CheckFailed result.
    /// </summary>
    public static async Task<CallBindingCheckResult> CheckAsync(Solution original, DocumentId documentId, TypeDeclarationSyntax container, MemberDeclarationSyntax newMember, int insertIndex, string updatedText, CancellationToken cancellationToken = default)
    {
        try
        {
            var name = CheckedNameOf(newMember);
            if (name is null)
            {
                return CallBindingCheckResult.Ok(0);
            }

            var scope = new CallBindingScope(new[] { name });
            var before = await CallBindingCheck.CaptureAsync(original, scope, cancellationToken);
            if (before.Problems.Count == 0 && before.Observations.Count == 0)
            {
                // The name is never bound anywhere today, and the new member's text adds sites only inside the new member.
                return CallBindingCheckResult.Ok(0);
            }

            var trial = original.WithDocumentText(documentId, SourceText.From(updatedText));
            var (newId, ownErrors, failure) = await LocateNewMemberAsync(trial, documentId, container, newMember, name, insertIndex, cancellationToken);
            if (failure is not null)
            {
                return CallBindingCheckResult.Failed(failure);
            }

            if (ownErrors)
            {
                // The write's compile gate reports the real cause; a comparison against an erroneous member would only add noise.
                return CallBindingCheckResult.Ok(0);
            }

            var after = WithoutAddedCandidate(await CallBindingCheck.CaptureAsync(trial, scope, cancellationToken), newId!);
            return CallBindingCheck.Compare(before, after, new CallBindingExpectations { AddedMemberIds = new[] { newId! } });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type is echoed: its text can carry file paths.
            return CallBindingCheckResult.Failed($"the add check raised {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// A bind that sees every overload (nameof(M), a method group) lists the new member once it exists. That growth is the add itself,
    /// not a re-pointed call, so the new id is removed from such candidate sets before the comparison; a set left with one id is that id.
    /// </summary>
    private static CallBindingSnapshot WithoutAddedCandidate(CallBindingSnapshot snapshot, string newId)
    {
        var observations = snapshot.Observations.Select(o =>
        {
            if (!o.BoundId.StartsWith(CandidatesPrefix, StringComparison.Ordinal))
            {
                return o;
            }

            var ids = o.BoundId[CandidatesPrefix.Length..].Split('|');
            var rest = ids.Where(id => id != newId).ToList();
            if (rest.Count == ids.Length || rest.Count == 0)
            {
                return o;
            }

            return o with { BoundId = rest.Count == 1 ? rest[0] : CandidatesPrefix + string.Join("|", rest) };
        }).ToList();
        return snapshot with { Observations = observations };
    }

    /// <summary>
    /// Finds the added member in the trial (the container is the type with one more member than before, the member is at the insert
    /// index) and returns its documentation id, whether it has compile errors of its own, or the reason it could not be located.
    /// </summary>
    private static async Task<(string? Id, bool OwnErrors, string? Failure)> LocateNewMemberAsync(Solution trial, DocumentId documentId, TypeDeclarationSyntax container, MemberDeclarationSyntax newMember, string name, int insertIndex, CancellationToken cancellationToken)
    {
        var document = trial.GetDocument(documentId);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
        var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return (null, false, "the trial document has no syntax tree or semantic model");
        }

        var arity = container.TypeParameterList?.Parameters.Count ?? 0;
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Kind() == container.Kind() && t.Identifier.ValueText == container.Identifier.ValueText && (t.TypeParameterList?.Parameters.Count ?? 0) == arity && t.Members.Count == container.Members.Count + 1)
            .ToList();
        if (matches.Count > 1)
        {
            matches = matches.Where(t => t.SpanStart == container.SpanStart).ToList();
        }

        if (matches.Count != 1 || insertIndex < 0 || insertIndex >= matches[0].Members.Count)
        {
            return (null, false, "the added member could not be located in the trial");
        }

        var added = matches[0].Members[insertIndex];
        if (added.Kind() != newMember.Kind() || CheckedNameOf(added) != name)
        {
            return (null, false, "the added member could not be matched in the trial");
        }

        if (model.GetDiagnostics(added.Span, cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return (null, true, null);
        }

        var id = model.GetDeclaredSymbol(added, cancellationToken)?.OriginalDefinition.GetDocumentationCommentId();
        return id is null ? (null, false, $"the added member '{name}' has no documentation id") : (id, false, null);
    }
}

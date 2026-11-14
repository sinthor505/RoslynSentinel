using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Member addMember's (and insertAfter/insertBefore's) use of <see cref="CallBindingCheck"/>: a new method, property or instance
/// constructor can capture a call that bound elsewhere before (an overload that fits an argument better, an instance member that
/// beats an extension method or an outer member of the same name, a constructor that fits a `new T("x")`, a target-typed `new("x")`,
/// a `: this("x")` or a derived class's `: base("x")` better than the one it bound). The add introduces no moved id, so the
/// expectation is plain: every existing site keeps its binding; the new member and the occurrences in its own body are the only additions.
/// A constructor is checked under the type name (the scope CallBindingCheck captures as "new TypeName").
/// <para>
/// Cost control, in order: a member that is not a method, property or instance constructor is not checked; a name that appears in no document of the
/// solution cannot be called anywhere (the new member is not in the original), so the check is skipped; a name that is only
/// declared or mentioned, never bound, is cleared after one capture of the original; only then is the trial built and captured.
/// A trial whose new member has compile errors of its own is not compared (the write's compile gate reports those). Limits: those
/// of <see cref="CallBindingCheck"/>; static constructors, operators, indexers, fields and nested types are not checked, and a
/// derived class's IMPLICIT base() call (no written initializer) is not a syntax node, so it is not captured.
/// </para>
/// </summary>
public static class AddMemberCallBindingCheck
{
    private const string CandidatesPrefix = "candidates:";

    /// <summary>The name whose bindings an add of <paramref name="member"/> can change (the type name for an instance constructor); null for a kind that is not checked.</summary>
    public static string? CheckedNameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        // A static constructor cannot be called, so it cannot capture a call.
        // TODO a derived class's implicit base() call (no written initializer) can rebind to a new all-optional base constructor; it is not a syntax node, so CallBindingCheck does not capture it
        ConstructorDeclarationSyntax constructor when !constructor.Modifiers.Any(SyntaxKind.StaticKeyword) => constructor.Identifier.ValueText,
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
    /// Multi-member form of the single-member overload: the whole batch is checked in one trial, so a call that binds to one new
    /// member and the members themselves are judged together. The refusal names every member of the batch.
    /// </summary>
    public static async Task RequireKeepsBindingsAsync(Solution solution, Document document, TypeDeclarationSyntax container, IReadOnlyList<MemberDeclarationSyntax> newMembers, int insertIndex, string updatedText, CancellationToken cancellationToken = default)
    {
        var appears = false;
        foreach (var name in newMembers.Select(CheckedNameOf).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (await NameAppearsAsync(solution, name, cancellationToken))
            {
                appears = true;
                break;
            }
        }

        if (!appears)
        {
            return;
        }

        var check = await CheckAsync(solution, document.Id, container, newMembers, insertIndex, updatedText, cancellationToken);
        if (!check.IsRefusal)
        {
            return;
        }

        var target = container.Identifier.ValueText;
        if (newMembers.Count > 1)
        {
            var listed = string.Join(", ", newMembers.Select(DescribeMember));
            throw new ToolInvalidArgumentException(CallBindingCheck.FormatRefusal(check, $"add members {listed} to '{target}'", bindsElsewhereFix: "rename the new member or members, or cast the captured call's argument to the original parameter type", unmatchedFix: "review that call by hand before retrying, or rename the new member or members"));
        }

        if (newMembers[0] is ConstructorDeclarationSyntax)
        {
            // Rename does not apply to a constructor (it must carry the type's name): the fix is the argument or the parameters.
            throw new ToolInvalidArgumentException(CallBindingCheck.FormatRefusal(check, $"add a constructor to '{target}'", bindsElsewhereFix: "cast the captured call's argument to the original parameter type, or change the new constructor's parameters", unmatchedFix: "review that call by hand before retrying, or change the new constructor's parameters"));
        }

        throw new ToolInvalidArgumentException(CallBindingCheck.FormatRefusal(check, $"add {DescribeMember(newMembers[0])} to '{target}'", bindsElsewhereFix: "rename the new member, or cast the captured call's argument to the original parameter type", unmatchedFix: "review that call by hand before retrying, or rename the new member"));
    }

    /// <summary>
    /// Captures the bindings of the new member's name in <paramref name="original"/> and in a trial with <paramref name="updatedText"/>
    /// applied, and compares them. Fails closed: anything that cannot be located or captured returns a CheckFailed result.
    /// </summary>
    public static Task<CallBindingCheckResult> CheckAsync(Solution original, DocumentId documentId, TypeDeclarationSyntax container, MemberDeclarationSyntax newMember, int insertIndex, string updatedText, CancellationToken cancellationToken = default)
        => CheckAsync(original, documentId, container, new[] { newMember }, insertIndex, updatedText, cancellationToken);

    /// <summary>
    /// Captures the bindings of the names of every new member in <paramref name="original"/> and in ONE trial with
    /// <paramref name="updatedText"/> applied, and compares them: a call that re-points to one new member and a call that re-points to
    /// another are judged in the same trial. Fails closed: anything that cannot be located or captured returns a CheckFailed result.
    /// </summary>
    public static async Task<CallBindingCheckResult> CheckAsync(Solution original, DocumentId documentId, TypeDeclarationSyntax container, IReadOnlyList<MemberDeclarationSyntax> newMembers, int insertIndex, string updatedText, CancellationToken cancellationToken = default)
    {
        try
        {
            var names = newMembers.Select(CheckedNameOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 0)
            {
                return CallBindingCheckResult.Ok(0);
            }

            var scope = new CallBindingScope(names);
            var before = await CallBindingCheck.CaptureAsync(original, scope, cancellationToken);
            if (before.Problems.Count == 0 && before.Observations.Count == 0)
            {
                // The names are never bound anywhere today, and the new members' text adds sites only inside the new members.
                return CallBindingCheckResult.Ok(0);
            }

            var trial = original.WithDocumentText(documentId, SourceText.From(updatedText));
            var (newIds, ownErrors, failure) = await LocateNewMembersAsync(trial, documentId, container, newMembers, insertIndex, cancellationToken);
            if (failure is not null)
            {
                return CallBindingCheckResult.Failed(failure);
            }

            if (ownErrors)
            {
                // The write's compile gate reports the real cause; a comparison against an erroneous member would only add noise.
                return CallBindingCheckResult.Ok(0);
            }

            var after = await CallBindingCheck.CaptureAsync(trial, scope, cancellationToken);
            foreach (var newId in newIds)
            {
                after = WithoutAddedCandidate(after, newId);
            }

            return CallBindingCheck.Compare(before, after, new CallBindingExpectations { AddedMemberIds = newIds });
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

    /// <summary>Kind and name of a member for refusal text, for example "method 'Save'" or "property 'P'".</summary>
    private static string DescribeMember(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => $"method '{method.Identifier.ValueText}'",
        PropertyDeclarationSyntax property => $"property '{property.Identifier.ValueText}'",
        ConstructorDeclarationSyntax constructor => $"constructor '{constructor.Identifier.ValueText}'",
        FieldDeclarationSyntax field => $"field '{field.Declaration.Variables.First().Identifier.ValueText}'",
        EventFieldDeclarationSyntax eventField => $"event '{eventField.Declaration.Variables.First().Identifier.ValueText}'",
        _ => "member",
    };

    /// <summary>The nodes whose symbols a member declares: each declarator of a field or event field, otherwise the member itself.</summary>
    private static IEnumerable<SyntaxNode> DeclaredNodesOf(MemberDeclarationSyntax member) => member switch
    {
        FieldDeclarationSyntax field => field.Declaration.Variables.Cast<SyntaxNode>(),
        EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Cast<SyntaxNode>(),
        _ => new SyntaxNode[] { member },
    };

    /// <summary>
    /// Finds the added members in the trial (the container is the type with one more member per new member than before, and they sit
    /// at the insert index in order) and returns their documentation ids, whether any has compile errors of its own, or the reason they
    /// could not be located.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Ids, bool OwnErrors, string? Failure)> LocateNewMembersAsync(Solution trial, DocumentId documentId, TypeDeclarationSyntax container, IReadOnlyList<MemberDeclarationSyntax> newMembers, int insertIndex, CancellationToken cancellationToken)
    {
        var document = trial.GetDocument(documentId);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
        var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null)
        {
            return (Array.Empty<string>(), false, "the trial document has no syntax tree or semantic model");
        }

        var arity = container.TypeParameterList?.Parameters.Count ?? 0;
        var added = newMembers.Count;
        var matches = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Kind() == container.Kind() && t.Identifier.ValueText == container.Identifier.ValueText && (t.TypeParameterList?.Parameters.Count ?? 0) == arity && t.Members.Count == container.Members.Count + added)
            .ToList();
        if (matches.Count > 1)
        {
            matches = matches.Where(t => t.SpanStart == container.SpanStart).ToList();
        }

        if (matches.Count != 1 || insertIndex < 0 || insertIndex + added > matches[0].Members.Count)
        {
            return (Array.Empty<string>(), false, "the added member could not be located in the trial");
        }

        var addedMembers = new List<MemberDeclarationSyntax>(added);
        for (var i = 0; i < added; i++)
        {
            var addedMember = matches[0].Members[insertIndex + i];
            if (addedMember.Kind() != newMembers[i].Kind() || CheckedNameOf(addedMember) != CheckedNameOf(newMembers[i]))
            {
                return (Array.Empty<string>(), false, "the added member could not be matched in the trial");
            }

            addedMembers.Add(addedMember);
        }

        if (addedMembers.Any(m => model.GetDiagnostics(m.Span, cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error)))
        {
            return (Array.Empty<string>(), true, null);
        }

        var ids = new List<string>();
        foreach (var addedMember in addedMembers)
        {
            foreach (var declared in DeclaredNodesOf(addedMember))
            {
                var id = model.GetDeclaredSymbol(declared, cancellationToken)?.OriginalDefinition.GetDocumentationCommentId();
                if (id is null)
                {
                    return (Array.Empty<string>(), false, $"the added member '{CheckedNameOf(addedMember) ?? DescribeMember(addedMember)}' has no documentation id");
                }

                ids.Add(id);
            }
        }

        return (ids, false, null);
    }
}

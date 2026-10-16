using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>What binding a reference in the trial text found.</summary>
public enum MoveBindOutcome
{
    /// <summary>The reference binds to the moved member as it now sits on the destination type.</summary>
    BindsToMovedMember = 0,

    /// <summary>The reference binds, but to a different member (another overload, another type, or an unrelated symbol).</summary>
    BindsToOtherSymbol,

    /// <summary>Binding produced no symbol and no candidates (for example CS1061: the receiver type has no such member any more).</summary>
    BindFailed,

    /// <summary>Binding produced no single symbol but candidates exist (overload-resolution failure, true ambiguity, inaccessible).</summary>
    BindAmbiguous,

    /// <summary>The offset is outside the document or is not on an identifier that names a member reference.</summary>
    NoReferenceAtOffset,

    /// <summary>The trial solution has no document at the given path.</summary>
    TrialDocumentNotFound,

    /// <summary>The trial document has no syntax root or semantic model.</summary>
    SemanticModelUnavailable,
}

/// <summary>
/// The moved member's identity on the destination type, as plain strings so it can be compared against symbols from a
/// different compilation (the trial compilation) than the one the member was originally declared in.
/// </summary>
/// <param name="DestinationTypeName">Fully qualified display name of the destination type's original definition.</param>
/// <param name="MemberName">The member's name.</param>
/// <param name="Kind">The member's symbol kind (Method, Property, Field).</param>
/// <param name="TypeParameterCount">Method arity; 0 for non-methods.</param>
/// <param name="ParameterTypes">Fully qualified parameter type names in order (methods and indexers); empty otherwise.</param>
public sealed record MovedMemberIdentity(string DestinationTypeName, string MemberName, SymbolKind Kind, int TypeParameterCount, IReadOnlyList<string> ParameterTypes)
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>Builds the identity from the member's declared symbol in the original compilation and the destination type symbol.</summary>
    public static MovedMemberIdentity From(ISymbol sourceMember, INamedTypeSymbol destinationType)
    {
        var definition = sourceMember.OriginalDefinition;
        return new MovedMemberIdentity(
            destinationType.OriginalDefinition.ToDisplayString(TypeFormat),
            definition.Name,
            definition.Kind,
            (definition as IMethodSymbol)?.TypeParameters.Length ?? 0,
            ParametersOf(definition).Select(p => p.Type.ToDisplayString(TypeFormat)).ToList());
    }

    /// <summary>True when <paramref name="symbol"/> (from any compilation) is this member declared on the destination type.</summary>
    public bool Matches(ISymbol? symbol)
    {
        if (symbol == null)
        {
            return false;
        }

        // A reduced extension-method call binds to the reduced form; the static definition is what carries the declared parameters.
        var definition = ((symbol as IMethodSymbol)?.ReducedFrom ?? symbol).OriginalDefinition;
        if (definition.Kind != Kind
            || !string.Equals(definition.Name, MemberName, StringComparison.Ordinal)
            || ((definition as IMethodSymbol)?.TypeParameters.Length ?? 0) != TypeParameterCount
            || definition.ContainingType == null
            || !string.Equals(definition.ContainingType.OriginalDefinition.ToDisplayString(TypeFormat), DestinationTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = ParametersOf(definition).Select(p => p.Type.ToDisplayString(TypeFormat)).ToList();
        return parameters.SequenceEqual(ParameterTypes, StringComparer.Ordinal);
    }

    private static IEnumerable<IParameterSymbol> ParametersOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.Parameters,
        IPropertySymbol p => p.Parameters,
        _ => Enumerable.Empty<IParameterSymbol>(),
    };
}

/// <summary>Result of <see cref="MoveBindCheck"/>.</summary>
/// <param name="Outcome">What binding found.</param>
/// <param name="BoundSymbol">The symbol the reference bound to (set for BindsToMovedMember and BindsToOtherSymbol).</param>
/// <param name="CandidateCount">Number of candidate symbols (BindAmbiguous), else 0.</param>
/// <param name="AnyCandidateIsMovedMember">For BindAmbiguous: true when one of the candidates is the moved member.</param>
/// <param name="BoundNodeSpan">Span, in the trial document, of the node that was bound (the whole invocation for a call); empty when none.</param>
/// <param name="Note">Human-facing detail for a non-matching outcome; null when the reference binds to the moved member.</param>
public sealed record MoveBindCheckResult(MoveBindOutcome Outcome, ISymbol? BoundSymbol, int CandidateCount, bool AnyCandidateIsMovedMember, TextSpan BoundNodeSpan, string? Note);

/// <summary>
/// Slice D2.2 of docs/current/proposals/proposal_instance_move_preview_position_map_classification.md: given the trial
/// solution (ValidationEngine.ValidateChangesWithCandidateAsync) and a trial-text offset (from MovePositionMap), binds the
/// reference at that offset and reports whether it resolves to the moved member on the destination type. The reference's
/// enclosing invocation is what is bound for a call, so a call wrapped over several lines needs no special handling.
/// </summary>
public static class MoveBindCheck
{
    /// <summary>Looks up the trial document by path and checks the reference at <paramref name="trialOffset"/>.</summary>
    public static async Task<MoveBindCheckResult> CheckAsync(Solution trialSolution, FilePathWrapper trialFilePath, int trialOffset, MovedMemberIdentity moved, CancellationToken cancellationToken = default)
    {
        var documentId = trialSolution.GetDocumentIdsWithFilePath(trialFilePath).FirstOrDefault();
        var document = documentId == null ? null : trialSolution.GetDocument(documentId);
        if (document == null)
        {
            return Failure(MoveBindOutcome.TrialDocumentNotFound, $"The trial solution has no document at '{trialFilePath}'.");
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
        {
            return Failure(MoveBindOutcome.SemanticModelUnavailable, $"No syntax root or semantic model for '{trialFilePath}' in the trial solution.");
        }

        return Check(root, model, trialOffset, moved, cancellationToken);
    }

    /// <summary>
    /// Checks the reference whose identifier token contains <paramref name="trialOffset"/>. For a call the enclosing invocation is
    /// bound; for a property or field reference the member access (or bare name) is bound.
    /// </summary>
    public static MoveBindCheckResult Check(SyntaxNode root, SemanticModel model, int trialOffset, MovedMemberIdentity moved, CancellationToken cancellationToken = default)
    {
        if (trialOffset < 0 || trialOffset >= root.FullSpan.End)
        {
            return Failure(MoveBindOutcome.NoReferenceAtOffset, $"Offset {trialOffset} is outside the trial document.");
        }

        var token = root.FindToken(trialOffset);
        if (!token.IsKind(SyntaxKind.IdentifierToken) || !token.Span.Contains(trialOffset) || token.Parent is not SimpleNameSyntax name)
        {
            return Failure(MoveBindOutcome.NoReferenceAtOffset, $"Offset {trialOffset} is not on an identifier naming a member reference.");
        }

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
            bound = invocation;
        }

        var info = model.GetSymbolInfo(bound, cancellationToken);
        if (info.Symbol != null)
        {
            return moved.Matches(info.Symbol)
                ? new MoveBindCheckResult(MoveBindOutcome.BindsToMovedMember, info.Symbol, 0, false, bound.Span, null)
                : new MoveBindCheckResult(MoveBindOutcome.BindsToOtherSymbol, info.Symbol, 0, false, bound.Span, $"Binds to '{info.Symbol.ToDisplayString()}', not the moved member '{moved.DestinationTypeName}.{moved.MemberName}'.");
        }

        if (info.CandidateSymbols.Length > 0)
        {
            var anyMoved = info.CandidateSymbols.Any(moved.Matches);
            return new MoveBindCheckResult(MoveBindOutcome.BindAmbiguous, null, info.CandidateSymbols.Length, anyMoved, bound.Span, $"No single symbol ({info.CandidateReason}); {info.CandidateSymbols.Length} candidate(s).");
        }

        return new MoveBindCheckResult(MoveBindOutcome.BindFailed, null, 0, false, bound.Span, $"'{name.Identifier.Text}' does not bind to any symbol at this position.");
    }

    private static MoveBindCheckResult Failure(MoveBindOutcome outcome, string note) => new(outcome, null, 0, false, default, note);
}

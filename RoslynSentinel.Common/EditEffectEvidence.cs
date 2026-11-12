using System.Diagnostics;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Common;

/// <summary>
/// The not-found note built from recorded <see cref="EditEffect"/>s: "call X renamed, removed or moved the symbol you asked for".
/// Unlike <see cref="NotFoundEvidence"/> it needs no agent id and no file path, includes the caller's own writes, and states what a
/// tool call recorded rather than what a text search suggests. The note puts facts side by side and never claims a cause.
/// Never throws.
/// </summary>
public static class EditEffectEvidence
{
    /// <summary>The <see cref="Finding.Source"/> of the note.</summary>
    public const string FindingSource = "EditEffect";

    private const int MaxChainSteps = 8;

    // A plain or dotted identifier path; anything else (a snippet, a regex, a parameter-bearing signature after cleaning) is not a symbol name.
    private static readonly Regex QualifiedName = new(@"^@?[A-Za-z_]\w*(\.@?[A-Za-z_]\w*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Arity = new(@"`+\d+", RegexOptions.CultureInvariant);
    private static readonly Regex InnerGenerics = new(@"<[^<>]*>", RegexOptions.CultureInvariant);

    /// <summary>
    /// The note for the current tool call, read from <see cref="ToolCallContext.Current"/>. Empty unless the error code is a not-found code
    /// and the call carries a mapped term and the effect index. Identity is the context's session id, falling back to the server instance.
    /// </summary>
    public static IReadOnlyList<Finding> ForCurrentCall(string? errorCode)
    {
        var context = ToolCallContext.Current;
        if (context is not { MissingTerm: { } term, EditEffects: { } index }
            || errorCode is not (ToolErrorCode.NotFound or "MethodNotFound" or "FileNotFound"))
        {
            return [];
        }

        Solution? solution = null;
        try
        {
            solution = context.CurrentSolution?.Invoke();
        }
        catch (Exception ex)
        {
            // A failed solution read costs only the "declared at" part of the note.
            Debug.WriteLine($"Edit effect note: solution read failed: {ex}");
        }

        var note = Find(index, term, context.SessionId, ServerInstanceIdentity.Id, context.AgentId, solution, context.SolutionRoot, DateTime.UtcNow);
        return note is null ? [] : [new Finding(FindingSource, note, FindingSeverity.Info)];
    }

    /// <summary>
    /// The note text for <paramref name="term"/>, or null when no recorded effect explains it. Only effects of the same session count
    /// (both ids present and equal), or of the same server instance when either side has no session id. <paramref name="solution"/>
    /// null means the declaration of a renamed symbol is not looked up.
    /// </summary>
    public static string? Find(
        EditEffectIndex index,
        string term,
        string? sessionId,
        string serverInstanceId,
        string? callerAgentId,
        Solution? solution,
        string? solutionRoot,
        DateTime utcNow)
    {
        try
        {
            if (!TryParseTerm(term, out var qualifier, out var simple))
            {
                return null;
            }

            bool SameCaller(EditEffect e) => IsSameCaller(e, sessionId, serverInstanceId);

            var head = index.Find(simple).FirstOrDefault(e => SameCaller(e) && QualifierMatches(e.SymbolDisplay, qualifier, simple));
            if (head is null || head.Kind == EditEffectKind.Added)
            {
                // The newest fact about the name is that a symbol with it was declared: nothing to explain.
                return null;
            }

            var headContainer = ContainerLast(head.SymbolDisplay);
            if (index.FindByNewName(simple).Any(e => SameCaller(e) && e.UtcTime > head.UtcTime && ContainerLast(e.SymbolDisplay) == headContainer))
            {
                // Something was renamed to the missing name after this effect, so the name is declared again.
                return null;
            }

            var steps = FollowChain(index, head, SameCaller);
            var last = steps[^1];
            var phrases = steps.Select((e, i) => Phrase(e, i == 0, callerAgentId, utcNow));
            var suffix = last.Kind is EditEffectKind.Renamed or EditEffectKind.SignatureChanged
                ? DeclaredNow(solution, solutionRoot, last)
                : string.Empty;
            var shown = qualifier.Length == 0 ? simple : $"{string.Join('.', qualifier)}.{simple}";
            return $"`{shown}` was not found. {string.Join("; ", phrases)}{suffix}.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Edit effect evidence failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// True when the newest recorded effect of the same caller (same identity rule as <see cref="Find"/>) for <paramref name="term"/> is a
    /// fact other than <see cref="EditEffectKind.Added"/>. Lets a tool with several candidate names (MoveMember's memberNames) choose the one
    /// that has something to report. Never throws; a term that is not a name has no effect.
    /// </summary>
    public static bool HasEffectFor(EditEffectIndex index, string term, string? sessionId, string serverInstanceId)
    {
        try
        {
            if (!TryParseTerm(term, out var qualifier, out var simple))
            {
                return false;
            }

            var head = index.Find(simple).FirstOrDefault(e => IsSameCaller(e, sessionId, serverInstanceId) && QualifierMatches(e.SymbolDisplay, qualifier, simple));
            return head is not null && head.Kind != EditEffectKind.Added;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Edit effect lookup failed: {ex}");
            return false;
        }
    }

    // Both session ids present: they must be equal. Either missing: fall back to the same server instance.
    private static bool IsSameCaller(EditEffect effect, string? sessionId, string serverInstanceId)
    {
        return sessionId is not null && effect.SessionId is not null
            ? string.Equals(sessionId, effect.SessionId, StringComparison.Ordinal)
            : string.Equals(effect.ServerInstanceId, serverInstanceId, StringComparison.Ordinal);
    }

    // Reduces a term to its qualifier and simple name. Accepts a documentation comment id ("M:Foo.Bar(System.Int32)"), generics, arity and a
    // parameter list; rejects anything that is not an identifier path afterwards (a snippet, a regex, an empty string).
    private static bool TryParseTerm(string? term, out string[] qualifier, out string simple)
    {
        qualifier = [];
        simple = string.Empty;
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var text = term.Trim();
        if (text.Length > 2 && text[1] == ':' && char.IsLetter(text[0]))
        {
            text = text[2..];
        }

        var paren = text.IndexOf('(');
        if (paren > 0)
        {
            text = text[..paren];
        }

        text = Arity.Replace(text, string.Empty);
        string previous;
        do
        {
            previous = text;
            text = InnerGenerics.Replace(text, string.Empty);
        }
        while (text != previous);

        text = text.Trim();
        if (!QualifiedName.IsMatch(text))
        {
            return false;
        }

        simple = EditEffectNames.SimpleName(text);
        var segments = text.Split('.');
        qualifier = segments[..^1];
        return simple.Length > 0;
    }

    // The term "Foo.Bar" must match an effect displayed as "N.Foo.Bar" (and the reverse): one segment list is a suffix of the other.
    private static bool QualifierMatches(string display, string[] qualifier, string simple)
    {
        if (qualifier.Length == 0)
        {
            return true;
        }

        var wanted = qualifier.Append(simple).ToArray();
        var actual = display.Split('.');
        var shorter = Math.Min(wanted.Length, actual.Length);
        for (var i = 1; i <= shorter; i++)
        {
            if (!string.Equals(wanted[^i], actual[^i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ContainerLast(string display)
    {
        var segments = display.Split('.');
        return segments.Length >= 2 ? segments[^2] : string.Empty;
    }

    // Bar -> Baz -> Qux: each later effect on the new name (and, for a move, the new container) is the next step. Stops at a cycle, a
    // step with no successor, or MaxChainSteps.
    private static List<EditEffect> FollowChain(EditEffectIndex index, EditEffect head, Func<EditEffect, bool> sameCaller)
    {
        var steps = new List<EditEffect> { head };
        var current = head;
        for (var i = 0; i < MaxChainSteps; i++)
        {
            string nextName;
            string nextContainer;
            if (current.Kind == EditEffectKind.Renamed && !string.IsNullOrEmpty(current.NewName))
            {
                nextName = current.NewName;
                nextContainer = ContainerLast(current.SymbolDisplay);
            }
            else if (current.Kind == EditEffectKind.Moved && !string.IsNullOrEmpty(current.NewContainer))
            {
                nextName = current.SimpleName;
                nextContainer = current.NewContainer.Split('.')[^1];
            }
            else
            {
                break;
            }

            var from = current;
            // Find is newest first, so the last match is the first step after the current one.
            var next = index.Find(nextName)
                .Where(e => sameCaller(e)
                    && e.UtcTime >= from.UtcTime
                    && e.Kind is EditEffectKind.Renamed or EditEffectKind.Removed or EditEffectKind.Moved or EditEffectKind.SignatureChanged
                    && ContainerLast(e.SymbolDisplay) == nextContainer
                    && !steps.Contains(e))
                .LastOrDefault();
            if (next is null)
            {
                break;
            }

            steps.Add(next);
            current = next;
        }

        return steps;
    }

    private static string Phrase(EditEffect effect, bool first, string? callerAgentId, DateTime utcNow)
    {
        var who = string.Equals(effect.AgentId, callerAgentId, StringComparison.Ordinal)
            ? "this agent"
            : effect.AgentId is null ? "another agent" : $"agent {effect.AgentId}";
        var age = EditAttributionFindings.FormatAge(utcNow - effect.UtcTime);
        var actor = effect.ToolCallId is null
            ? $"{(first ? "A" : "a")} `{effect.ToolName}` call ({who}, {age} ago)"
            : $"{(first ? "Call" : "call")} `{effect.ToolCallId}` (`{effect.ToolName}`, {who}, {age} ago)";
        return effect.Kind switch
        {
            EditEffectKind.Renamed => $"{actor} renamed `{effect.SymbolDisplay}` to `{Qualify(effect.SymbolDisplay, effect.NewName)}`",
            EditEffectKind.Removed => $"{actor} removed `{effect.SymbolDisplay}`" + (string.IsNullOrEmpty(effect.NewFilePath) ? string.Empty : $" from `{effect.NewFilePath}`"),
            EditEffectKind.Moved => $"{actor} moved `{effect.SymbolDisplay}`"
                + (string.IsNullOrEmpty(effect.NewContainer) ? string.Empty : $" to `{effect.NewContainer}`")
                + (string.IsNullOrEmpty(effect.NewFilePath) ? string.Empty : $" in `{effect.NewFilePath}`"),
            EditEffectKind.SignatureChanged => $"{actor} changed the signature of `{effect.SymbolDisplay}`",
            EditEffectKind.FileDeleted => $"{actor} deleted `{effect.SymbolDisplay}`",
            _ => $"{actor} recorded {effect.Kind} for `{effect.SymbolDisplay}`",
        };
    }

    private static string Qualify(string display, string? newName)
    {
        if (string.IsNullOrEmpty(newName))
        {
            return display;
        }

        var dot = display.LastIndexOf('.');
        return dot >= 0 ? display[..(dot + 1)] + newName : newName;
    }

    // ", declared at `file:line`" for a renamed (or re-signed) symbol, "; no declaration ... was found" when the solution has none, and
    // nothing when no solution is available (no fact to state).
    private static string DeclaredNow(Solution? solution, string? solutionRoot, EditEffect last)
    {
        if (solution is null)
        {
            return string.Empty;
        }

        var finalDisplay = last.Kind == EditEffectKind.Renamed ? Qualify(last.SymbolDisplay, last.NewName) : last.SymbolDisplay;
        var location = LocateDeclaration(solution, solutionRoot, finalDisplay, last.NewFilePath);
        return location is null
            ? $"; no declaration of `{finalDisplay}` was found in the loaded solution"
            : $", declared at `{location}`";
    }

    // First declaration (type, delegate, method, property, event, field, enum member, namespace) whose name and enclosing type or namespace
    // match the display name. Walks declarations only, not method bodies. The effect's own file is tried first.
    private static string? LocateDeclaration(Solution solution, string? solutionRoot, string display, string? hintFile)
    {
        var segments = display.Split('.');
        var name = segments[^1];
        var container = segments.Length >= 2 ? segments[^2] : null;
        var hint = string.IsNullOrEmpty(hintFile) ? null : hintFile.Replace('\\', '/');
        // The hint file only orders the scan (a case-insensitive suffix test); no document is picked by path here, so a document without a
        // path is skipped in the loop rather than filtered with a path predicate over .Documents.
        var documents = solution.Projects
            .SelectMany(p => p.Documents)
            .OrderByDescending(d => hint is not null && d.FilePath is not null && d.FilePath.Replace('\\', '/').EndsWith(hint, StringComparison.OrdinalIgnoreCase));
        foreach (var document in documents)
        {
            if (document.FilePath is null)
            {
                continue;
            }

            SyntaxNode? root = null;
            try
            {
                // Source text documents parse on the calling thread, so this does not wait on anything.
                root = document.TryGetSyntaxRoot(out var cached) ? cached : document.GetSyntaxRootAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Edit effect note: parse of {document.FilePath} failed: {ex}");
            }

            if (root is null)
            {
                continue;
            }

            var descend = (SyntaxNode n) => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax
                or EnumDeclarationSyntax or BaseFieldDeclarationSyntax or VariableDeclarationSyntax;
            foreach (var node in root.DescendantNodes(descend))
            {
                var identifier = DeclaredIdentifier(node);
                if (identifier is not { } token || token.ValueText != name || !EnclosedBy(node, container))
                {
                    continue;
                }

                var line = root.SyntaxTree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
                return $"{EditEffectNames.DisplayPath(document.FilePath!, solutionRoot)}:{line}";
            }
        }

        return null;
    }

    private static SyntaxToken? DeclaredIdentifier(SyntaxNode node)
    {
        return node switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier,
            MethodDeclarationSyntax method => method.Identifier,
            PropertyDeclarationSyntax property => property.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            EnumMemberDeclarationSyntax member => member.Identifier,
            BaseNamespaceDeclarationSyntax @namespace => @namespace.Name.GetLastToken(),
            VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } field => field.Identifier,
            _ => null,
        };
    }

    // True when no container is wanted, or the nearest enclosing type or namespace has the wanted (last-segment) name.
    private static bool EnclosedBy(SyntaxNode node, string? container)
    {
        if (container is null)
        {
            return true;
        }

        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case BaseTypeDeclarationSyntax type:
                    return type.Identifier.ValueText == container;
                case BaseNamespaceDeclarationSyntax @namespace:
                    return @namespace.Name.GetLastToken().ValueText == container;
            }
        }

        return false;
    }
}

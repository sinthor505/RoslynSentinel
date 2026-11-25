using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// The best-effort caller migration of MoveFile's copyAndObsolete handling, in memory: it builds the copy and the marked original
/// (<see cref="CopyAndObsoleteEdits"/>), checks that pair compiles on its own, rewrites the callers (<see cref="CopyAndObsoleteCallerRewrite"/>),
/// compiles the result and takes every caller file whose rewrite gained a compile error back out, repeating up to <see cref="MaxCompileRounds"/> rounds.
/// The change set it returns has been compiled error-free, so applying it cannot introduce a compile error; callers that could not move stay unchanged
/// (compiler warnings about the [Obsolete] types only) and are listed with the error that blocked them. Nothing is written.
/// </summary>
public static class CopyAndObsoleteMigration
{
    /// <summary>Compile rounds of the caller set: the initial pass plus two repair rounds. After the last one, files that still error are reverted without a further repair.</summary>
    public const int MaxCompileRounds = 3;

    // Errors listed in the refusal of the base set, and the length cap of a message kept for a remaining caller.
    private const int MaxListedErrors = 3;

    private const int MaxMessageLength = 200;

    /// <summary>
    /// Runs the migration of <paramref name="source"/> to <paramref name="destination"/> against <paramref name="baseline"/> (the solution as it is today, never
    /// a candidate). <paramref name="sourceText"/> is the source file text exactly as it will be written; <paramref name="check"/> an accepted check of the move.
    /// <list type="number">
    /// <item>The copy and the marked original are built; a refusal there is returned as is. That pair alone is compiled; any newly introduced error refuses
    /// the migration (ValidationFailed, with the first errors as facts and the namespaceHandling keep option).</item>
    /// <item>Round 1 rewrites all callers in their default modes and compiles base plus rewrites; no new error ends the run.</item>
    /// <item>For each new error: in a rewritten caller in Using mode, that file is retried in Qualify mode; in a rewritten caller in Qualify mode, that file is
    /// reverted. In an unrewritten file, the rewritten files that declare the symbols the error's member binds to (else the whole file; else every rewritten file of
    /// the same project; else all) are reverted. In the copy, the original or a position-less error, every caller is reverted. A reverted caller keeps its
    /// original text and is recorded with the error that blocked it.</item>
    /// <item>After round <see cref="MaxCompileRounds"/> the files that still error are reverted without a repair, and the surviving set is compiled once more
    /// (not a round); if that still errors, every caller is reverted (<see cref="CopyAndObsoleteMigrationResult.FellBackToBaseSet"/>).</item>
    /// </list>
    /// A cancelled token throws <see cref="OperationCanceledException"/>; any other unexpected failure is returned as a refusal without internals.
    /// </summary>
    public static async Task<CopyAndObsoleteMigrationResult> RunAsync(Solution baseline, FilePathWrapper source, FilePathWrapper destination, string sourceText, CopyAndObsoleteCheckResult check, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunCoreAsync(baseline, source, destination, sourceText, check, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CopyAndObsoleteMigrationResult.Refused(ToolErrorCode.Exception,
                $"copyAndObsolete: the migration of '{source.Absolute}' stopped on an unexpected {ex.GetType().Name}, and nothing was written. Options: retry the call, or use namespaceHandling keep to move the file as it is.");
        }
    }

    private static async Task<CopyAndObsoleteMigrationResult> RunCoreAsync(Solution baseline, FilePathWrapper source, FilePathWrapper destination, string sourceText, CopyAndObsoleteCheckResult check, CancellationToken cancellationToken)
    {
        var edits = await CopyAndObsoleteEdits.BuildAsync(baseline, source, sourceText, check, cancellationToken);
        if (edits.Refusal is not null)
        {
            return CopyAndObsoleteMigrationResult.Refused(edits.Refusal);
        }

        var baseSet = new DocumentChangeSet { [destination] = edits.NewText!, [source] = edits.OldText! };
        var (baseErrors, _) = await ValidateSetAsync(baseline, baseSet, new Dictionary<FilePathWrapper, CallerFileRewrite>(), cancellationToken, validateBaseSet: true);
        if (baseErrors.Count > 0)
        {
            return CopyAndObsoleteMigrationResult.Refused(ToolErrorCode.ValidationFailed, BaseSetRefusalMessage(source, destination, baseErrors));
        }

        var forced = new Dictionary<FilePathWrapper, CallerRewriteMode>();
        var excluded = new List<FilePathWrapper>();
        var blocked = new Dictionary<FilePathWrapper, DiagnosticInfo>();
        var facts = new List<string>();

        var pass = await CopyAndObsoleteCallerRewrite.RewriteAsync(baseline, source, check, forced, excluded, cancellationToken);
        if (pass.Refusal is not null)
        {
            return CopyAndObsoleteMigrationResult.Refused(pass.Refusal);
        }

        var current = ToMap(pass.Files);
        facts.AddRange(pass.Facts);
        var rounds = 0;
        var fellBack = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rounds++;
            var (errors, candidate) = await ValidateSetAsync(baseline, baseSet, current, cancellationToken);
            if (errors.Count == 0)
            {
                break;
            }

            var lastRound = rounds >= MaxCompileRounds;
            var decision = await AttributeAsync(baseline, candidate, errors, current, source, destination, allowRetry: !lastRound, cancellationToken);
            foreach (var (path, error) in decision.Reverts)
            {
                current.Remove(path);
                forced.Remove(path);
                excluded.Add(path);
                blocked[path] = error;
                facts.Add($"Round {rounds}: '{path.Absolute}' stays unchanged because its rewrite gained {error.Id}.");
            }

            if (lastRound)
            {
                // The files that still errored are reverted; one more compile of the survivors (not a round) proves the final set.
                var (finalErrors, _) = await ValidateSetAsync(baseline, baseSet, current, cancellationToken);
                if (finalErrors.Count > 0)
                {
                    // The lookup key is a FilePathWrapper, so the match ignores path case like every other path comparison here.
                    var finalByPath = finalErrors.ToLookup(error => error.FilePath);
                    foreach (var path in current.Keys.ToList())
                    {
                        blocked[path] = finalByPath[path].FirstOrDefault() ?? finalErrors[0];
                    }

                    current.Clear();
                    fellBack = true;
                    facts.Add($"After round {rounds} the remaining caller rewrites still did not compile together ({finalErrors[0].Id}), so every caller stays unchanged.");
                }

                break;
            }

            if (decision.Retries.Count > 0)
            {
                foreach (var path in decision.Retries)
                {
                    forced[path] = CallerRewriteMode.Qualify;
                    facts.Add($"Round {rounds}: '{path.Absolute}' is retried with its references qualified, because the using-directive rewrite gained an error.");
                }

                pass = await CopyAndObsoleteCallerRewrite.RewriteAsync(baseline, source, check, forced, excluded, cancellationToken);
                if (pass.Refusal is not null)
                {
                    return CopyAndObsoleteMigrationResult.Refused(pass.Refusal);
                }

                current = ToMap(pass.Files);
            }
        }

        var changes = new DocumentChangeSet();
        changes.Merge(baseSet);
        foreach (var file in current.Values)
        {
            changes[file.FilePath] = file.NewText;
        }

        var oldNamespace = check.OldNamespace ?? string.Empty;
        var typeNames = check.Types.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var remaining = new List<RemainingObsoleteCaller>();
        foreach (var (path, error) in blocked.OrderBy(entry => entry.Key))
        {
            var line = await FirstReferenceLineAsync(baseline, path, oldNamespace, typeNames, cancellationToken);
            remaining.Add(new RemainingObsoleteCaller(path, line, error.Id, ShortMessage(error.Message), error.FilePath, error.StartLine));
        }

        return new CopyAndObsoleteMigrationResult(
            changes,
            current.Values.OrderBy(file => file.FilePath).ToList(),
            remaining,
            edits.AddedUsings,
            edits.StaticFieldTypeNames,
            edits.AlreadyObsoleteTypeNames,
            facts,
            rounds,
            fellBack,
            null);
    }

    private static Dictionary<FilePathWrapper, CallerFileRewrite> ToMap(IReadOnlyList<CallerFileRewrite> files) => files.ToDictionary(file => file.FilePath);

    // Compiles the base set plus the caller rewrites and returns the newly introduced errors with the candidate solution; a set without rewrites was
    // already compiled as the base set, so it is not compiled again.
    private static async Task<(IReadOnlyList<DiagnosticInfo> Errors, Solution Candidate)> ValidateSetAsync(Solution baseline, DocumentChangeSet baseSet, Dictionary<FilePathWrapper, CallerFileRewrite> rewrites, CancellationToken cancellationToken, bool validateBaseSet = false)
    {
        if (rewrites.Count == 0 && !validateBaseSet)
        {
            return ([], baseline);
        }

        var set = new DocumentChangeSet();
        set.Merge(baseSet);
        foreach (var file in rewrites.Values)
        {
            set[file.FilePath] = file.NewText;
        }

        var result = await ValidationEngine.ValidateChangesWithCandidateAsync(baseline, set, null, cancellationToken);
        return (result.Report.Diagnostics, result.Candidate);
    }

    internal sealed record Attribution(Dictionary<FilePathWrapper, DiagnosticInfo> Reverts, HashSet<FilePathWrapper> Retries);

    // Decides per error which rewritten file is retried in Qualify mode and which is reverted (see RunAsync for the rules). Every call with errors and a
    // non-empty rewrite set changes at least one file, so the loop always makes progress.
    internal static async Task<Attribution> AttributeAsync(Solution baseline, Solution candidate, IReadOnlyList<DiagnosticInfo> errors, Dictionary<FilePathWrapper, CallerFileRewrite> current, FilePathWrapper source, FilePathWrapper destination, bool allowRetry, CancellationToken cancellationToken)
    {
        var reverts = new Dictionary<FilePathWrapper, DiagnosticInfo>();
        var retries = new HashSet<FilePathWrapper>();

        foreach (var error in errors)
        {
            var path = error.FilePath;
            if (current.TryGetValue(path, out var rewrite))
            {
                if (allowRetry && rewrite.Mode == CallerRewriteMode.Using)
                {
                    retries.Add(path);
                }
                else
                {
                    reverts.TryAdd(path, error);
                }

                continue;
            }

            IReadOnlyCollection<FilePathWrapper> blamed;
            if (path.Absolute.Length == 0 || path == source || path == destination)
            {
                blamed = current.Keys;
            }
            else
            {
                blamed = await ReferencedRewrittenAsync(candidate, error, current.Keys, cancellationToken);
                if (blamed.Count == 0)
                {
                    var project = ProjectOf(baseline, path);
                    blamed = project is null ? current.Keys : current.Keys.Where(key => ProjectOf(baseline, key) == project).ToList();
                    if (blamed.Count == 0)
                    {
                        blamed = current.Keys;
                    }
                }
            }

            foreach (var blamedPath in blamed)
            {
                reverts.TryAdd(blamedPath, error);
            }
        }

        retries.ExceptWith(reverts.Keys);
        return new Attribution(reverts, retries);
    }

    private static ProjectId? ProjectOf(Solution solution, FilePathWrapper path) => solution.GetDocumentIdsWithFilePath(path.Absolute).FirstOrDefault()?.ProjectId;

    // The rewritten files that declare the symbols an unrewritten erroring file binds to: first in the member the error sits in, then in the whole file.
    private static async Task<IReadOnlyCollection<FilePathWrapper>> ReferencedRewrittenAsync(Solution candidate, DiagnosticInfo error, IReadOnlyCollection<FilePathWrapper> rewritten, CancellationToken cancellationToken)
    {
        var document = candidate.GetDocumentIdsWithFilePath(error.FilePath.Absolute).Select(candidate.GetDocument).FirstOrDefault(found => found is not null);
        if (document is null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return [];
        }

        var scopes = new List<SyntaxNode>();
        var text = await document.GetTextAsync(cancellationToken);
        if (error.StartLine >= 1 && error.StartLine <= text.Lines.Count)
        {
            var line = text.Lines[error.StartLine - 1];
            var position = Math.Min(line.Start + Math.Max(error.StartColumn - 1, 0), line.End);
            var member = root.FindToken(position).Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(declaration => declaration is not BaseNamespaceDeclarationSyntax);
            if (member is not null)
            {
                scopes.Add(member);
            }
        }

        scopes.Add(root);
        var rewrittenSet = rewritten.ToHashSet();
        foreach (var scope in scopes)
        {
            var found = new HashSet<FilePathWrapper>();
            foreach (var name in scope.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(name, cancellationToken);
                foreach (var declaringPath in DeclaringPaths(info.Symbol ?? info.CandidateSymbols.FirstOrDefault()))
                {
                    var wrapped = new FilePathWrapper(declaringPath);
                    if (!PathComparison.Comparer.Equals(declaringPath, error.FilePath.Absolute) && rewrittenSet.Contains(wrapped))
                    {
                        found.Add(wrapped);
                    }
                }
            }

            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    // Paths of the files that declare a symbol; a local or parameter stands for its type, and a namespace (declared in many files) for nothing.
    private static IEnumerable<string> DeclaringPaths(ISymbol? symbol)
    {
        switch (symbol)
        {
            case null or INamespaceSymbol or ITypeParameterSymbol or IAliasSymbol:
                return [];
            case ILocalSymbol local:
                return DeclaringPaths(local.Type);
            case IParameterSymbol parameter:
                return DeclaringPaths(parameter.Type);
            case IMethodSymbol { ReducedFrom: { } extension }:
                return DeclaringPaths(extension);
            default:
                return symbol.OriginalDefinition.DeclaringSyntaxReferences.Select(reference => reference.SyntaxTree.FilePath).Where(path => !string.IsNullOrEmpty(path)).ToList();
        }
    }

    // 1-based line of the first name in the unchanged file that binds to one of the moved types (a constructor stands for its type); 1 when none is found.
    private static async Task<int> FirstReferenceLineAsync(Solution baseline, FilePathWrapper path, string oldNamespace, HashSet<string> typeNames, CancellationToken cancellationToken)
    {
        var document = baseline.GetDocumentIdsWithFilePath(path.Absolute).Select(baseline.GetDocument).FirstOrDefault(found => found is not null);
        if (document is null || await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
        {
            return 1;
        }

        var text = await document.GetTextAsync(cancellationToken);
        foreach (var name in root.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
        {
            var written = name.Identifier.ValueText;
            if (!typeNames.Contains(written) && !typeNames.Contains(written + "Attribute"))
            {
                continue;
            }

            var info = model.GetSymbolInfo(name, cancellationToken);
            var type = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) switch
            {
                INamedTypeSymbol named => named,
                IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType,
                _ => null,
            };
            if (type is { ContainingType: null } && typeNames.Contains(type.Name) && string.Equals(type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString(), oldNamespace, StringComparison.Ordinal))
            {
                return text.Lines.GetLinePosition(name.SpanStart).Line + 1;
            }
        }

        return 1;
    }

    private static string ShortMessage(string message)
    {
        var firstLine = message.Split('\n')[0].Trim();
        return firstLine.Length > MaxMessageLength ? firstLine[..MaxMessageLength] + "..." : firstLine;
    }

    private static string BaseSetRefusalMessage(FilePathWrapper source, FilePathWrapper destination, IReadOnlyList<DiagnosticInfo> errors)
    {
        var listed = errors.Take(MaxListedErrors).Select(error => $"{error.Id} at '{error.FilePath.Absolute}' line {error.StartLine}: {ShortMessage(error.Message)}");
        var more = errors.Count > MaxListedErrors ? $" (and {errors.Count - MaxListedErrors} more)" : string.Empty;
        return $"copyAndObsolete: the copy at '{destination.Absolute}' and the marked original '{source.Absolute}' introduce {errors.Count} compile error(s) before any caller is rewritten: {string.Join("; ", listed)}{more}. "
            + $"Options: pass a {MoveFileParameterNames.DestinationPath} whose folder gives a namespace without these conflicts, or use namespaceHandling keep to move the file as it is.";
    }
}

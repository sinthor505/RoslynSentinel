using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Plans a namespace change across a committed solution: composes the set overload of <see cref="CopyAndObsoleteCallerRewrite"/> (moved, not copied) into one
/// <see cref="CallerRewriteResult"/> after the checks that belong to the change itself (one target namespace per call, one namespace per file, every part of a
/// partial type in the set, valid namespace names) and adds the name-shadowing facts. Nothing is written and nothing is compiled here; the caller compiles the
/// candidate and applies it. Refusals carry facts and options in <see cref="CallerRewriteResult.Refusal"/>.
/// </summary>
public static class ChangeNamespaceEngine
{
    /// <summary>
    /// Plans the namespace change of the files named by <paramref name="request"/>, one <see cref="NamespaceMove"/> per file. Checks, in order (each a refusal with
    /// facts and options): every new namespace is a valid namespace name; the moves share one new namespace (<c>InvalidArgument</c>, the distinct targets listed);
    /// each file is a document of <paramref name="committed"/> and declares its top-level types in one namespace (<c>TargetIneligible</c>); every part of a partial
    /// type that moves is in the set (<c>TargetIneligible</c>, the missing parts listed). Then the shared core rewrites the set and its callers; its refusals
    /// (overlapping edits, a file shape the namespace swap cannot edit) are returned as they are. On success the result also carries the name-shadowing facts:
    /// a type that has the name of a segment of the new namespace and either sits in the namespace the segment is declared in, or is used by its simple name in a
    /// moved file (where the namespace segment would now be found first). The shadowing facts never refuse; the compile check of the caller is the backstop.
    /// </summary>
    // TODO compile rounds (CopyAndObsoleteMigration.MaxCompileRounds = 3): RunCoreAsync is tied to the copyAndObsolete source/destination pair and cannot be reused, so this plans a single pass; the tool's compile gate refuses a candidate that does not compile instead of retrying callers with forced Qualify mode.
    public static Task<CallerRewriteResult> PlanAsync(ChangeNamespaceRequest request, Solution committed, CancellationToken cancellationToken)
        => PlanMovesAsync(request.Moves, committed, [], cancellationToken);

    // How many names a refusal or fact lists before it says "and N more".
    private const int MaxNamed = 10;

    /// <summary>
    /// Plans a namespace change for a file or a folder. <paramref name="path"/> is one document of <paramref name="committed"/>, or a folder: every document under it
    /// (subfolders included, no globs, <c>bin</c> and <c>obj</c> folders left out; a file that declares no top-level type is left out with a fact). Exactly one of
    /// <paramref name="newNamespace"/> (one target for every file) and <paramref name="matchFolder"/> (each file gets <see cref="FolderNamespace.Expected"/>) must be given
    /// (<c>InvalidArgument</c> otherwise). Because one call has one target, a matchFolder set whose files expect different namespaces is refused with the targets listed.
    /// The moves are then planned as in <see cref="PlanAsync(ChangeNamespaceRequest, Solution, CancellationToken)"/>; a path that is neither a document nor a folder holding
    /// documents is refused with the document lookup's not-found error.
    /// </summary>
    public static async Task<CallerRewriteResult> PlanAsync(FilePathWrapper path, string? newNamespace, bool matchFolder, Solution committed, CancellationToken cancellationToken)
    {
        var hasNamespace = !string.IsNullOrWhiteSpace(newNamespace);
        if (hasNamespace == matchFolder)
        {
            return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument, hasNamespace
                ? "Both newNamespace and matchFolder were given, and they name two different targets. Options: pass newNamespace for one fixed namespace, or matchFolder for the namespace each file's folder gives."
                : "Neither newNamespace nor matchFolder was given, so the target namespace is unknown. Options: pass newNamespace for one fixed namespace, or matchFolder for the namespace each file's folder gives.");
        }

        var documents = ExpandDocuments(committed, path, out var isFolder, out var expandRefusal);
        if (documents is null)
        {
            return CallerRewriteResult.Refused(expandRefusal!);
        }

        var facts = new List<string>();
        var moves = new List<NamespaceMove>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = new FilePathWrapper(document.FilePath!);
            var found = await DeclaredNamespacesAsync(document, cancellationToken);
            if (found.Count == 0)
            {
                if (isFolder)
                {
                    facts.Add($"'{filePath.Absolute}' declares no top-level type, so it was left out of the namespace change.");
                    continue;
                }

                return CallerRewriteResult.Refused(ToolErrorCode.TargetIneligible,
                    $"'{filePath.Absolute}' declares no top-level type (class, struct, interface, enum, record or delegate), so there is no namespace to change. Options: name a file that declares one, or a folder.");
            }

            if (found.Count > 1)
            {
                return CallerRewriteResult.Refused(MultiNamespaceRefusal(filePath, found));
            }

            var target = matchFolder ? FolderNamespace.Expected(document.Project, filePath) : newNamespace!.Trim();
            if (target is null)
            {
                return CallerRewriteResult.Refused(ToolErrorCode.PreconditionNotMet,
                    $"Project '{document.Project.Name}' has no project file path, so the namespace its folders give '{filePath.Absolute}' cannot be derived. Options: pass newNamespace instead of matchFolder.");
            }

            moves.Add(new NamespaceMove(filePath, found[0], target));
        }

        if (moves.Count == 0)
        {
            return CallerRewriteResult.Refused(ToolErrorCode.PreconditionNotMet,
                $"None of the {documents.Count} file(s) under '{path.Absolute}' declares a top-level type, so there is no namespace to change. Options: name a folder that holds types.");
        }

        return await PlanMovesAsync(moves, committed, facts, cancellationToken);
    }

    // The checks of the change itself, then the shared core, then the shadowing facts; leadFacts (from the expansion) come first in the result.
    private static async Task<CallerRewriteResult> PlanMovesAsync(IReadOnlyList<NamespaceMove> moves, Solution committed, List<string> leadFacts, CancellationToken cancellationToken)
    {
        var invalid = moves.Select(move => move.NewNamespace).Where(ns => ns.Length > 0 && !IsValidNamespaceName(ns)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (invalid.Count > 0)
        {
            return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument,
                $"The new namespace {Listed(invalid)} is not a valid namespace name: every dot-separated segment must be a C# identifier (a folder name with a space, a dash or a leading digit does not give one). Options: pass newNamespace with a valid name, or rename the folder.");
        }

        var targets = moves.GroupBy(move => move.NewNamespace, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        if (targets.Count > 1)
        {
            var described = targets.Select(group => $"'{group.Key}' ({group.Count()} file(s), for example '{group.OrderBy(move => move.FilePath).First().FilePath.Absolute}')");
            return CallerRewriteResult.Refused(ToolErrorCode.InvalidArgument,
                $"The files expect {targets.Count} different namespaces: {string.Join(", ", described.Take(MaxNamed))}{(targets.Count > MaxNamed ? $" and {targets.Count - MaxNamed} more" : string.Empty)}. One call changes the files to one namespace. Options: call once per target namespace (one folder at a time), or pass newNamespace to give every file the same one.");
        }

        var active = new List<(NamespaceMove Move, Document Document)>();
        foreach (var move in moves.Where(move => !string.Equals(move.OldNamespace, move.NewNamespace, StringComparison.Ordinal)).OrderBy(move => move.FilePath))
        {
            var lookup = DocumentLookup.TryGetDocument(committed, move.FilePath);
            if (!lookup.TryGetDocument(out var document))
            {
                return CallerRewriteResult.Refused(lookup.ToResultError());
            }

            var found = await DeclaredNamespacesAsync(document, cancellationToken);
            if (found.Count > 1)
            {
                return CallerRewriteResult.Refused(MultiNamespaceRefusal(move.FilePath, found));
            }

            active.Add((move, document));
        }

        var partialRefusal = await CheckPartialTypesAsync(active, cancellationToken);
        if (partialRefusal is not null)
        {
            return CallerRewriteResult.Refused(partialRefusal);
        }

        var result = await CopyAndObsoleteCallerRewrite.RewriteAsync(committed, moves, cancellationToken: cancellationToken);
        if (result.IsRefusal)
        {
            return result;
        }

        var shadowing = await ShadowingFactsAsync(active, cancellationToken);
        return result with { Facts = [.. leadFacts, .. result.Facts, .. shadowing] };
    }

    // The documents the path names: one document, or every document under the folder (null with the refusal when it names neither).
    private static List<Document>? ExpandDocuments(Solution committed, FilePathWrapper path, out bool isFolder, out ResultError? refusal)
    {
        isFolder = false;
        refusal = null;
        var lookup = DocumentLookup.TryGetDocument(committed, path);
        if (lookup.TryGetDocument(out var single))
        {
            return [single];
        }

        if (lookup.Status == DocumentLookupStatus.Ambiguous || string.IsNullOrWhiteSpace(path.Absolute))
        {
            refusal = lookup.ToResultError();
            return null;
        }

        var separator = Path.DirectorySeparatorChar;
        var folder = path.Absolute.TrimEnd(separator) + separator;
        var candidates = new List<Document>();
        foreach (var project in committed.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { Length: > 0 } filePath
                    && filePath.StartsWith(folder, PathComparison.Comparison)
                    && !filePath[folder.Length..].Split(separator).SkipLast(1).Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(document);
                }
            }
        }

        var documents = candidates
            .GroupBy(document => document.FilePath!, PathComparison.Comparer)
            .Select(group => group.First())
            .OrderBy(document => document.FilePath, PathComparison.Comparer)
            .ToList();
        if (documents.Count == 0)
        {
            refusal = lookup.ToResultError();
            return null;
        }

        isFolder = true;
        return documents;
    }

    // The distinct namespaces the document's top-level types (and delegates) are declared in, ordinal-sorted; empty when it declares none.
    private static async Task<List<string>> DeclaredNamespacesAsync(Document document, CancellationToken cancellationToken)
    {
        if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
        {
            return [];
        }

        return root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .Select(CopyAndObsoleteEdits.DeclaredNamespace)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static ResultError MultiNamespaceRefusal(FilePathWrapper file, IReadOnlyList<string> found)
        => new(ToolErrorCode.TargetIneligible,
            $"'{file.Absolute}' declares its top-level types in {found.Count} namespaces ({string.Join(", ", found.Select(ns => ns.Length == 0 ? "the global namespace" : $"'{ns}'"))}), and a namespace change gives all of a file's types one namespace. Options: split the file so each part sits in one namespace, or leave the file out of the namespace change.");

    // A partial type that moves needs every one of its parts in the set: the parts must share one namespace.
    private static async Task<ResultError?> CheckPartialTypesAsync(IReadOnlyList<(NamespaceMove Move, Document Document)> active, CancellationToken cancellationToken)
    {
        var inSet = new HashSet<string>(active.Select(entry => entry.Move.FilePath.Absolute), PathComparison.Comparer);
        var missing = new List<(string Type, string Part)>();
        foreach (var (_, document) in active)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<BaseTypeDeclarationSyntax>())
            {
                if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword) || model.GetDeclaredSymbol(declaration, cancellationToken) is not { } symbol)
                {
                    continue;
                }

                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    if (reference.SyntaxTree.FilePath is { Length: > 0 } part && !inSet.Contains(part))
                    {
                        missing.Add((symbol.ToDisplayString(), part));
                    }
                }
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        var types = missing.Select(entry => entry.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var parts = missing.Select(entry => entry.Part).Distinct(PathComparison.Comparer).Order(PathComparison.Comparer).ToList();
        return new ResultError(ToolErrorCode.TargetIneligible,
            $"The partial type(s) {Listed(types)} have {parts.Count} part(s) outside the namespace change: {Listed(parts)}. A partial type's parts must all sit in one namespace, so moving some of them cannot compile. Options: include the missing parts (name their common folder), or leave the files that hold the other parts out as well.");
    }

    // Name shadowing, as facts only. For each segment of the new namespace: (a) a type of that name in the namespace the segment is declared in, and (b) a type outside
    // the new namespace's chain that a moved file uses by that simple name (the namespace segment is found before a using directive, so it would bind there).
    private static async Task<List<string>> ShadowingFactsAsync(IReadOnlyList<(NamespaceMove Move, Document Document)> active, CancellationToken cancellationToken)
    {
        var shadows = new List<string>();
        if (active.Count == 0)
        {
            return shadows;
        }

        // One target per call (checked by the caller), so the first move names it.
        var target = active[0].Move.NewNamespace;
        var segments = target.Split('.');
        var segmentNames = new HashSet<string>(segments, StringComparer.Ordinal);
        var chain = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        for (var i = 1; i <= segments.Length; i++)
        {
            chain.Add(string.Join('.', segments.Take(i)));
        }

        foreach (var project in active.Select(entry => entry.Document.Project).DistinctBy(project => project.Id))
        {
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation)
            {
                continue;
            }

            var parent = compilation.GlobalNamespace;
            for (var i = 0; i < segments.Length; i++)
            {
                if (parent.GetTypeMembers(segments[i]).FirstOrDefault() is { } clash)
                {
                    var where = i == 0 ? "the global namespace" : $"'{string.Join('.', segments.Take(i))}'";
                    shadows.Add($"Name shadowing: '{clash.ToDisplayString()}' is a type in {where}, and the new namespace '{target}' also gives '{segments[i]}' there as a namespace; code in {where} that writes '{segments[i]}.' would bind to the namespace.");
                }

                INamespaceSymbol? next = null;
                foreach (var candidate in parent.GetNamespaceMembers())
                {
                    if (string.Equals(candidate.Name, segments[i], StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }
                if (next is null)
                {
                    break;
                }

                parent = next;
            }
        }

        var memberPaths = new HashSet<string>(active.Select(entry => entry.Move.FilePath.Absolute), PathComparison.Comparer);
        foreach (var (move, document) in active)
        {
            if (await document.GetSyntaxRootAsync(cancellationToken) is not { } root || await document.GetSemanticModelAsync(cancellationToken) is not { } model)
            {
                continue;
            }

            var uses = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!segmentNames.Contains(name.Identifier.ValueText) || IsQualifiedRight(name) || name.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                {
                    continue;
                }

                if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamedTypeSymbol { ContainingType: null } type
                    || !string.Equals(type.Name, name.Identifier.ValueText, StringComparison.Ordinal)
                    || chain.Contains(type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString())
                    || type.DeclaringSyntaxReferences.Any(reference => memberPaths.Contains(reference.SyntaxTree.FilePath)))
                {
                    continue;
                }

                var key = type.ToDisplayString();
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }

            foreach (var (typeName, count) in uses)
            {
                shadows.Add($"Name shadowing: '{move.FilePath.Absolute}' uses the type '{typeName}' by its simple name {count} time(s); in the new namespace '{target}' that name is found as a namespace segment first, so the reference would bind to the namespace.");
            }
        }

        var distinct = shadows.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > MaxNamed)
        {
            var omitted = distinct.Count - MaxNamed;
            distinct = distinct.Take(MaxNamed).ToList();
            distinct.Add($"{omitted} more name-shadowing observation(s) were left out of this list.");
        }

        return distinct;
    }

    private static bool IsQualifiedRight(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Right == name,
        MemberAccessExpressionSyntax access => access.Name == name,
        AliasQualifiedNameSyntax alias => alias.Name == name,
        MemberBindingExpressionSyntax => true,
        _ => false,
    };

    private static bool IsValidNamespaceName(string ns)
        => ns.Split('.').All(segment => SyntaxFacts.IsValidIdentifier(segment) && SyntaxFacts.GetKeywordKind(segment) == SyntaxKind.None);

    // The items quoted and comma-joined, capped at MaxNamed with a count of the rest.
    private static string Listed(IReadOnlyList<string> items)
        => string.Join(", ", items.Take(MaxNamed).Select(item => $"'{item}'")) + (items.Count > MaxNamed ? $" and {items.Count - MaxNamed} more" : string.Empty);
}

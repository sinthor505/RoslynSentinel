using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>Works out which project references and InternalsVisibleTo edges a multi-file move set needs.</summary>
public static class MoveSetEdgeAnalyzer
{
    /// <summary>
    /// Analyzes the move set plan against the committed solution and returns the project edges it needs. A ProjectReference edge runs
    /// FromProject (referencing) to ToProject; an InternalsVisibleTo edge runs FromProject (granting) to ToProject (receiving).
    /// An edge that would close a project cycle is not returned (see <see cref="FindCycleEdgesAsync"/>); a package reference is never an edge
    /// (see <see cref="FindMissingPackageFactsAsync"/>).
    /// </summary>
    public static async Task<IReadOnlyList<NeededProjectEdge>> AnalyzeAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        var result = await AnalyzeCoreAsync(plan, committed, cancellationToken);
        return result.Accepted;
    }

    /// <summary>
    /// The ProjectReference edges the move set needs but that AnalyzeAsync withheld because adding them would close a cycle in the project
    /// dependency graph. The tool lifts each to an EdgeWouldCloseCycle fact. Repeats the analysis; call it only when AnalyzeAsync found work.
    /// </summary>
    public static async Task<IReadOnlyList<NeededProjectEdge>> FindCycleEdgesAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        var result = await AnalyzeCoreAsync(plan, committed, cancellationToken);
        return result.Cyclic;
    }

    /// <summary>
    /// MissingPackageReference facts for assemblies a moved file uses that are not projects and that the destination project does not
    /// reference. Packages are facts only, never edges. Repeats the analysis; call it only when the move set crosses projects.
    /// </summary>
    public static async Task<IReadOnlyList<MoveFact>> FindMissingPackageFactsAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        var result = await AnalyzeCoreAsync(plan, committed, cancellationToken);
        return result.PackageFacts;
    }

    /// <summary>
    /// Returns a copy of the committed solution with the needed edges added as references or InternalsVisibleTo.
    /// A ProjectReference edge is FromProject (the referencing project) to ToProject; an InternalsVisibleTo edge is FromProject
    /// (the GRANTING project, whose project file would carry the item) to ToProject (the RECEIVING project). Idempotent; an
    /// unknown project name, an existing reference or a reference that would close a cycle is skipped, never thrown.
    /// </summary>
    public static Solution AugmentBaseline(Solution committed, IReadOnlyList<NeededProjectEdge> edges, bool addReferences, bool addInternalsVisibleTo)
    {
        var solution = committed;
        foreach (var edge in edges)
        {
            var from = FindProject(solution, edge.FromProject);
            var to = FindProject(solution, edge.ToProject);
            if (from is null || to is null || from.Id == to.Id)
            {
                continue;
            }

            if (edge.Kind == ProjectEdgeKind.ProjectReference)
            {
                if (!addReferences
                    || from.ProjectReferences.Any(r => r.ProjectId == to.Id)
                    || solution.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(to.Id).Contains(from.Id))
                {
                    continue;
                }

                solution = solution.AddProjectReference(from.Id, new ProjectReference(to.Id));
            }
            else if (edge.Kind == ProjectEdgeKind.InternalsVisibleTo && addInternalsVisibleTo)
            {
                // The attribute names the receiving assembly, which can differ from the project name.
                var receiver = string.IsNullOrEmpty(to.AssemblyName) ? to.Name : to.AssemblyName;
                var directory = from.FilePath is { } projectPath ? Path.GetDirectoryName(projectPath) : null;
                var fileName = $"MoveSetInternalsVisibleTo.{receiver}.g.cs";
                var path = directory is null ? fileName : Path.Combine(directory, "obj", fileName);
                if (!solution.GetDocumentIdsWithFilePath(path).IsEmpty)
                {
                    continue;
                }

                // In memory only: the path is a unique key and is never written to disk.
                var text = SourceText.From($"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{receiver}\")]\r\n", Encoding.UTF8);
                solution = from.AddDocument("MoveSetInternalsVisibleTo.g.cs", text, folders: null, filePath: path).Project.Solution;
            }
        }

        return solution;
    }

    // Project names come from the plan (PlannedFileMove), which took them from Project.Name; paths are never compared here.
    private static Project? FindProject(Solution solution, string projectName)
        => ProjectLookup.FindProject(solution, projectName);

    // The shared analysis behind AnalyzeAsync, FindCycleEdgesAsync and FindMissingPackageFactsAsync.
    // Accepted: edges to add (ProjectReference edges first, then InternalsVisibleTo edges). Cyclic: ProjectReference candidates
    // dropped because adding them would close a cycle. PackageFacts: referenced assemblies that are not projects and are
    // absent from the destination (never an edge: a restore cannot be modelled in memory).
    private static async Task<(List<NeededProjectEdge> Accepted, List<NeededProjectEdge> Cyclic, List<MoveFact> PackageFacts)> AnalyzeCoreAsync(
        MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)
    {
        var accepted = new List<NeededProjectEdge>();
        var cyclic = new List<NeededProjectEdge>();
        var packageFacts = new List<MoveFact>();

        // (a) P12: when no C# move crosses a project boundary there is nothing to analyze; return before any semantic work.
        if (!plan.Moves.Any(IsCrossProjectCSharpMove))
        {
            return (accepted, cyclic, packageFacts);
        }

        // Every moved C# document maps to its destination project: a symbol declared in one is "in the set" and lives in that project after the move.
        var movedDestination = new Dictionary<DocumentId, string?>();
        var crossMoves = new List<(PlannedFileMove Move, Document Document)>();
        foreach (var move in plan.Moves)
        {
            if (!IsCSharpFile(move.Source.Absolute) || FindSourceDocument(committed, move) is not { } document)
            {
                continue;
            }

            movedDestination[document.Id] = move.DestinationProjectName;
            if (IsCrossProjectCSharpMove(move))
            {
                crossMoves.Add((move, document));
            }
        }

        var compilations = new Dictionary<ProjectId, Compilation?>();
        async Task<Compilation?> CompilationOfAsync(Project project)
        {
            if (!compilations.TryGetValue(project.Id, out var compilation))
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
                compilations[project.Id] = compilation;
            }

            return compilation;
        }

        var referencedNames = new Dictionary<ProjectId, HashSet<string>>();
        async Task<HashSet<string>> ReferencedNamesAsync(Project project)
        {
            if (!referencedNames.TryGetValue(project.Id, out var names))
            {
                var compilation = await CompilationOfAsync(project);
                names = new HashSet<string>(compilation?.ReferencedAssemblyNames.Select(a => a.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                referencedNames[project.Id] = names;
            }

            return names;
        }

        // True when granting already gives receiving access to its internals; true as well when either compilation is unavailable (no judgement possible, no edge).
        async Task<bool> GrantsAccessAsync(Project granting, Project receiving)
        {
            var grantingCompilation = await CompilationOfAsync(granting);
            var receivingCompilation = await CompilationOfAsync(receiving);
            return grantingCompilation is null || receivingCompilation is null || grantingCompilation.Assembly.GivesAccessTo(receivingCompilation.Assembly);
        }

        var candidates = new List<NeededProjectEdge>();
        var seenEdges = new HashSet<(ProjectEdgeKind Kind, string From, string To)>();
        void AddCandidate(ProjectEdgeKind kind, string from, string to, string reason)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal) && seenEdges.Add((kind, from, to)))
            {
                candidates.Add(new NeededProjectEdge(kind, from, to, reason));
            }
        }

        var packageSeen = new HashSet<(string Project, string Assembly)>();

        // (b) + (d): every symbol a moved file references whose home project differs from the destination needs a reference from the destination,
        // and an InternalsVisibleTo grant when it is internal and the grant is not already in place.
        foreach (var (move, document) in crossMoves)
        {
            var destinationName = move.DestinationProjectName!;
            if (FindProject(committed, destinationName) is not { } destination
                || await document.GetSemanticModelAsync(cancellationToken) is not { } model
                || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            var destinationNames = await ReferencedNamesAsync(destination);
            var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = model.GetSymbolInfo(name, cancellationToken);
                var symbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault())?.OriginalDefinition;
                if (symbol is null
                    || symbol.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)
                    || symbol is INamedTypeSymbol { TypeKind: TypeKind.Error }
                    || !seenSymbols.Add(symbol)
                    || !TryGetHome(committed, symbol, movedDestination, cancellationToken, out var home, out var homeName))
                {
                    continue;
                }

                if (homeName is null)
                {
                    // (f) A metadata reference (package or framework) the destination does not see: a fact only.
                    var assemblyName = symbol.ContainingAssembly?.Name;
                    if (assemblyName is not null
                        && !destinationNames.Contains(assemblyName)
                        && packageSeen.Add((destinationName, assemblyName)))
                    {
                        packageFacts.Add(new MoveFact(
                            MoveFactKind.MissingPackageReference,
                            assemblyName,
                            $"the moved file '{move.Source.Absolute}' uses '{symbol.ToDisplayString()}' from '{assemblyName}', which project '{destinationName}' does not reference; package references are not modelled."));
                    }

                    continue;
                }

                if (string.Equals(homeName, destinationName, StringComparison.Ordinal))
                {
                    continue;
                }

                var reason = $"the moved file '{move.Source.Absolute}' uses '{symbol.ToDisplayString()}' declared in project '{homeName}'.";
                AddCandidate(ProjectEdgeKind.ProjectReference, destinationName, homeName, reason);
                if (home is not null && IsInternalOnly(symbol) && !await GrantsAccessAsync(home, destination))
                {
                    AddCandidate(ProjectEdgeKind.InternalsVisibleTo, homeName, destinationName, reason + " It is internal.");
                }
            }
        }

        // (c) + (e): every project outside the set that references a moved type needs a reference to the destination (this covers the
        // source project's remaining code), and an InternalsVisibleTo grant from the destination when the type is internal.
        foreach (var (move, document) in crossMoves)
        {
            var destinationName = move.DestinationProjectName!;
            if (FindProject(committed, destinationName) is not { } destination
                || await document.GetSemanticModelAsync(cancellationToken) is not { } model
                || await document.GetSyntaxRootAsync(cancellationToken) is not { } root)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
                {
                    continue;
                }

                var internalOnly = IsInternalOnly(type);
                var siteProjects = new Dictionary<ProjectId, Project>();
                foreach (var referenced in await SymbolFinder.FindReferencesAsync(type, committed, cancellationToken))
                {
                    foreach (var location in referenced.Locations)
                    {
                        if (!location.IsCandidateLocation && !movedDestination.ContainsKey(location.Document.Id))
                        {
                            siteProjects.TryAdd(location.Document.Project.Id, location.Document.Project);
                        }
                    }
                }

                foreach (var site in siteProjects.Values)
                {
                    if (site.Id == destination.Id)
                    {
                        continue;
                    }

                    var reason = $"project '{site.Name}' references '{type.ToDisplayString()}', moved by '{move.Source.Absolute}' into project '{destinationName}'.";
                    AddCandidate(ProjectEdgeKind.ProjectReference, site.Name, destinationName, reason);
                    if (internalOnly && !await GrantsAccessAsync(destination, site))
                    {
                        AddCandidate(ProjectEdgeKind.InternalsVisibleTo, destinationName, site.Name, reason + " It is internal.");
                    }
                }
            }
        }

        // (g) Decide on the dependency graph plus the edges accepted so far, never by letting Solution.AddProjectReference throw. A candidate is dropped when
        // the destination already sees the project: it is in the ProjectReference closure OR its assembly is in the compilation's references (R8: it is
        // unverified whether SDK-style transitive ProjectReference shows up in MSBuildWorkspace compilations, so both are honoured).
        var graph = committed.GetProjectDependencyGraph();
        var dependencies = committed.Projects.ToDictionary(p => p.Id, p => new HashSet<ProjectId>(graph.GetProjectsThatThisProjectDirectlyDependsOn(p.Id)));
        var cyclicPairs = new HashSet<(string Referencing, string Referenced)>();
        foreach (var edge in candidates.Where(e => e.Kind == ProjectEdgeKind.ProjectReference))
        {
            if (FindProject(committed, edge.FromProject) is not { } from || FindProject(committed, edge.ToProject) is not { } to)
            {
                continue;
            }

            if (Reaches(dependencies, from.Id, to.Id)
                || (await ReferencedNamesAsync(from)).Contains(to.AssemblyName))
            {
                continue;
            }

            if (Reaches(dependencies, to.Id, from.Id))
            {
                cyclic.Add(edge);
                cyclicPairs.Add((edge.FromProject, edge.ToProject));
                continue;
            }

            dependencies[from.Id].Add(to.Id);
            accepted.Add(edge);
        }

        // A grant the receiving project could never use (its reference would close a cycle) is not an edge either.
        accepted.AddRange(candidates.Where(e => e.Kind == ProjectEdgeKind.InternalsVisibleTo && !cyclicPairs.Contains((e.ToProject, e.FromProject))));
        return (accepted, cyclic, packageFacts);
    }

    private static bool IsCSharpFile(string path) => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsCrossProjectCSharpMove(PlannedFileMove move)
        => IsCSharpFile(move.Source.Absolute)
            && move.SourceProjectName is not null
            && move.DestinationProjectName is not null
            && !string.Equals(move.SourceProjectName, move.DestinationProjectName, StringComparison.Ordinal);

    // Roslyn's own path comparer (GetDocumentIdsWithFilePath) finds the document; a file linked into several projects prefers the planned source project.
    private static Document? FindSourceDocument(Solution committed, PlannedFileMove move)
    {
        Document? first = null;
        foreach (var id in committed.GetDocumentIdsWithFilePath(move.Source.Absolute))
        {
            if (committed.GetDocument(id) is not { } document)
            {
                continue;
            }

            if (string.Equals(document.Project.Name, move.SourceProjectName, StringComparison.Ordinal))
            {
                return document;
            }

            first ??= document;
        }

        return first;
    }

    // Resolves where a symbol lives once the move set has been applied. Returns false when it cannot be placed (ignore it). A metadata symbol
    // returns true with homeName null. A source symbol declared in a moved document lives in that document's destination project.
    private static bool TryGetHome(Solution committed, ISymbol symbol, IReadOnlyDictionary<DocumentId, string?> movedDestination, CancellationToken cancellationToken, out Project? home, out string? homeName)
    {
        home = null;
        homeName = null;
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource)
            ?? symbol.ContainingType?.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is { } tree)
        {
            if (committed.GetDocument(tree) is not { } document)
            {
                return false;
            }

            if (movedDestination.TryGetValue(document.Id, out var destination) && destination is not null)
            {
                home = FindProject(committed, destination);
                homeName = destination;
                return home is not null;
            }

            home = document.Project;
            homeName = home.Name;
            return true;
        }

        // No source location (an implicit member, say): a source assembly still maps back to its project.
        if (symbol.ContainingAssembly is { } assembly && committed.GetProject(assembly, cancellationToken) is { } project)
        {
            home = project;
            homeName = project.Name;
        }

        return true;
    }

    // Internal (or private protected) anywhere up the containing-type chain: a project other than its own needs an InternalsVisibleTo grant to use it.
    private static bool IsInternalOnly(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal)
            {
                return true;
            }
        }

        return false;
    }

    // True when target is reachable from start over the "depends on" edges.
    private static bool Reaches(Dictionary<ProjectId, HashSet<ProjectId>> dependencies, ProjectId start, ProjectId target)
    {
        var visited = new HashSet<ProjectId>();
        var stack = new Stack<ProjectId>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            if (!dependencies.TryGetValue(stack.Pop(), out var next))
            {
                continue;
            }

            foreach (var id in next)
            {
                if (id == target)
                {
                    return true;
                }

                if (visited.Add(id))
                {
                    stack.Push(id);
                }
            }
        }

        return false;
    }
}

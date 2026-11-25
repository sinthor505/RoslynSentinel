using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Step 24 edge analysis (MoveSetEdgeAnalyzer) over in-memory multi-project solutions.
/// Fixture justification: the rows need three or more projects with explicit ProjectReferences, per-project assembly
/// names and an InternalsVisibleTo grant. TestSolutionBuilder builds at most two projects with relative document paths
/// and keeps its framework references private, so this fixture builds N projects whose documents use the absolute paths
/// the planned moves carry. No disk writes. Asserts are structural only: edge kinds, From/To project names and counts.
/// </summary>
[TestFixture]
public sealed class MoveSetEdgeAnalyzerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "MoveSetEdgeAnalyzerTests");

    private static readonly MetadataReference[] SharedReferences = BuildFrameworkReferences();

    private sealed record SourceFile(string Name, string Content);

    private sealed record SolutionProject(string Name, string[] References, SourceFile[] Files);

    [Test]
    public async Task Analyze_ClusterFromTestsToCore_TestsOnlyDependency_MissingReferenceEdge()
    {
        // Pins: a file moved out of Tests that uses Core gives the destination a ProjectReference to Core, and no other edge.
        var solution = BuildSolution(
            new SolutionProject("Core", [], [new SourceFile("CoreType.cs", "namespace Core { public class CoreType { } }")]),
            new SolutionProject("Tests", ["Core"], [new SourceFile("Cluster.cs", "namespace Tests { public class ClusterWorker { public Core.CoreType Make() => new Core.CoreType(); } }")]),
            new SolutionProject("Dest", [], [new SourceFile("Placeholder.cs", "namespace Dest { public class Placeholder { } }")]));

        var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(PlanOf(Move("Tests", "Cluster.cs", "Dest")), solution, CancellationToken.None);

        Assert.That(Shape(edges), Is.EquivalentTo(new[] { (ProjectEdgeKind.ProjectReference, "Dest", "Core") }));
    }

    [Test]
    public async Task Analyze_AddedEdgeWouldCloseCycle_NoEdgeAndFact()
    {
        // Pins: the Dest -> Lib edge is withheld by AnalyzeAsync and returned by FindCycleEdgesAsync, which the tool lifts to EdgeWouldCloseCycle.
        var solution = BuildSolution(
            new SolutionProject("Dest", [], [new SourceFile("Placeholder.cs", "namespace Dest { public class Placeholder { } }")]),
            new SolutionProject("Lib", ["Dest"], [new SourceFile("LibType.cs", "namespace Lib { public class LibType { } }")]),
            new SolutionProject("Src", ["Lib"], [new SourceFile("Mover.cs", "namespace Src { public class Mover { public Lib.LibType Make() => new Lib.LibType(); } }")]));
        var plan = PlanOf(Move("Src", "Mover.cs", "Dest"));

        var accepted = await MoveSetEdgeAnalyzer.AnalyzeAsync(plan, solution, CancellationToken.None);
        var cyclic = await MoveSetEdgeAnalyzer.FindCycleEdgesAsync(plan, solution, CancellationToken.None);

        Assert.That(accepted, Is.Empty);
        Assert.That(Shape(cyclic), Is.EquivalentTo(new[] { (ProjectEdgeKind.ProjectReference, "Dest", "Lib") }));
    }

    [Test]
    public async Task Analyze_InternalSymbolAcrossBoundary_InternalsVisibleToEdge()
    {
        // Pins: an internal helper that the moved file uses from a project the destination cannot see yields an InternalsVisibleTo grant from the helper's project plus a ProjectReference.
        var solution = BuildSolution(
            new SolutionProject("Home", [], [new SourceFile("Helpers.cs", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Src\")]\nnamespace Home { internal static class Helper { public static int Value() => 1; } }")]),
            new SolutionProject("Src", ["Home"], [new SourceFile("Mover.cs", "namespace Src { public class Mover { public int Run() => Home.Helper.Value(); } }")]),
            new SolutionProject("Dest", [], [new SourceFile("Placeholder.cs", "namespace Dest { public class Placeholder { } }")]));

        var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(PlanOf(Move("Src", "Mover.cs", "Dest")), solution, CancellationToken.None);

        Assert.That(Shape(edges), Is.EquivalentTo(new[]
        {
            (ProjectEdgeKind.ProjectReference, "Dest", "Home"),
            (ProjectEdgeKind.InternalsVisibleTo, "Home", "Dest"),
        }));
    }

    [Test]
    public async Task Analyze_RemainingCodeUsesTypeNowInternalInDestination_DestinationGrantsSource()
    {
        // Pins: a type moved out of A that A's remaining code uses, and that becomes internal, gives B an InternalsVisibleTo grant to A plus a ProjectReference A to B (reverse direction, P12).
        var solution = BuildSolution(
            new SolutionProject("A", [], [
                new SourceFile("Shared.cs", "namespace A { internal class Shared { public static int Value() => 1; } }"),
                new SourceFile("Remaining.cs", "namespace A { public class Remaining { public int Run() => Shared.Value(); } }"),
            ]),
            new SolutionProject("B", [], [new SourceFile("Placeholder.cs", "namespace B { public class Placeholder { } }")]));

        var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(PlanOf(Move("A", "Shared.cs", "B")), solution, CancellationToken.None);

        Assert.That(Shape(edges), Is.EquivalentTo(new[]
        {
            (ProjectEdgeKind.InternalsVisibleTo, "B", "A"),
            (ProjectEdgeKind.ProjectReference, "A", "B"),
        }));
    }

    [Test]
    public async Task Analyze_AllPairsInOneProject_ReturnsNoEdgesEvenWhenAnotherProjectReferencesTheMovedType()
    {
        // Pins: a move that stays inside one project short-circuits to no edges even though Q consumes the moved type (the analysis cost is not asserted).
        var solution = BuildSolution(
            new SolutionProject("P", [], [new SourceFile("Moved.cs", "namespace P { public class Moved { } }")]),
            new SolutionProject("Q", ["P"], [new SourceFile("Consumer.cs", "namespace Q { public class Consumer { public P.Moved Make() => new P.Moved(); } }")]));
        var move = new PlannedFileMove(
            Path.Combine(Root, "P", "Moved.cs"),
            Path.Combine(Root, "P", "New", "Moved.cs"),
            "P",
            "P",
            false);

        var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(PlanOf(move), solution, CancellationToken.None);

        Assert.That(edges, Is.Empty);
    }

    [Test]
    public async Task Analyze_DestinationAlreadySeesProjectViaTransitiveReference_NoRedundantEdge()
    {
        // Pins (R8): A references B, B references C, and the destination A uses C through that chain, so no A to C edge is added.
        var solution = BuildSolution(
            new SolutionProject("C", [], [new SourceFile("CType.cs", "namespace C { public class CType { } }")]),
            new SolutionProject("B", ["C"], [new SourceFile("BType.cs", "namespace B { public class BType { } }")]),
            new SolutionProject("A", ["B"], [new SourceFile("Placeholder.cs", "namespace A { public class Placeholder { } }")]),
            new SolutionProject("S", ["C"], [new SourceFile("Mover.cs", "namespace S { public class Mover { public C.CType Make() => new C.CType(); } }")]));

        var edges = await MoveSetEdgeAnalyzer.AnalyzeAsync(PlanOf(Move("S", "Mover.cs", "A")), solution, CancellationToken.None);

        Assert.That(edges, Is.Empty);
    }

    private static MoveSetPlanResult PlanOf(params PlannedFileMove[] moves)
        => new(moves, new DocumentChangeSet(), [], [], []);

    private static PlannedFileMove Move(string fromProject, string fileName, string toProject)
        => new(Path.Combine(Root, fromProject, fileName), Path.Combine(Root, toProject, fileName), fromProject, toProject, false);

    private static (ProjectEdgeKind Kind, string From, string To)[] Shape(IEnumerable<NeededProjectEdge> edges)
        => edges.Select(e => (e.Kind, e.FromProject, e.ToProject)).ToArray();

    private static Solution BuildSolution(params SolutionProject[] projects)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        var ids = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var id = ProjectId.CreateNewId();
            ids[project.Name] = id;
            var info = ProjectInfo.Create(id, VersionStamp.Default, project.Name, project.Name, LanguageNames.CSharp)
                .WithMetadataReferences(SharedReferences)
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithFilePath(Path.Combine(Root, project.Name, project.Name + ".csproj"));
            solution = solution.AddProject(info);
        }

        foreach (var project in projects)
        {
            var id = ids[project.Name];
            solution = solution.AddProjectReferences(id, project.References.Select(r => new ProjectReference(ids[r])));
            foreach (var file in project.Files)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(id, file.Name),
                    file.Name,
                    SourceText.From(file.Content),
                    filePath: Path.Combine(Root, project.Name, file.Name));
            }
        }

        return solution;
    }

    private static MetadataReference[] BuildFrameworkReferences()
    {
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var names = new[] { "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" };
        return names
            .Select(name => Path.Combine(coreDir, name))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .ToArray();
    }
}

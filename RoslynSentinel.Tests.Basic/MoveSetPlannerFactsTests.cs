using Microsoft.CodeAnalysis;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Stage 2 of the multi-file move planner (MoveSetPlanner.CheckProjectsAndCollectFactsAsync) on real files: the
/// outside-project refusal, kept-namespace facts, the source csproj item fact, the empty-source-folder fact and the
/// absence of a partial-type rule. Stage 1 (ExpandAndCheckAsync) runs first on disk and feeds stage 2.
/// </summary>
[TestFixture]
public sealed class MoveSetPlannerFactsTests
{
    private const string PlainCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\" />\n";

    private string _root = "";

    private string DemoRoot => Path.Combine(_root, "Demo");

    private string DemoProjectPath => Path.Combine(DemoRoot, "Demo.csproj");

    [SetUp]
    public void SetUp() => _root = MoveSetFixture.CreateRoot(nameof(MoveSetPlannerFactsTests));

    [TearDown]
    public void TearDown() => MoveSetFixture.DeleteRoot(_root);

    [Test]
    public async Task Check_ThreeFilesSameNamespaceMismatch_OneKeptNamespaceFactPerDistinctMismatch()
    {
        WriteDemoProject(PlainCsproj);
        var a = MoveSetFixture.WriteText(Path.Combine(_root, "src", "A.cs"), "namespace Wrong.Place;\nclass Item {}\n");
        var b = MoveSetFixture.WriteText(Path.Combine(_root, "src", "B.cs"), "namespace Wrong.Place;\nclass Item {}\n");
        var c = MoveSetFixture.WriteText(Path.Combine(_root, "src", "C.cs"), "namespace Wrong.Place;\nclass Item {}\n");
        var d = MoveSetFixture.WriteText(Path.Combine(_root, "src", "D.cs"), "namespace Other.Place;\nclass Item {}\n");

        var plan = await PlanAsync(Request(
            (a, Path.Combine(DemoRoot, "Sub", "A.cs")),
            (b, Path.Combine(DemoRoot, "Sub", "B.cs")),
            (c, Path.Combine(DemoRoot, "Sub", "C.cs")),
            (d, Path.Combine(DemoRoot, "Sub", "D.cs"))));

        Assert.That(plan.Facts.Count(fact => fact.Kind == MoveFactKind.KeptNamespace), Is.EqualTo(2));
    }

    [Test]
    public async Task Check_SourceCsprojCompileRemoveNamesMovedFolder_FactNotEdit()
    {
        var csproj = WriteDemoProject("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Remove=\"Git\\**\" />\n  </ItemGroup>\n</Project>\n");
        var source = Path.Combine(DemoRoot, "Git");
        MoveSetFixture.WriteText(Path.Combine(source, "Repo.cs"), "namespace Demo.Git;\nclass Repo {}\n");

        var plan = await PlanAsync(Request((source, Path.Combine(DemoRoot, "Moved", "Git"))));

        var csprojFacts = plan.Facts.Where(fact => fact.Kind == MoveFactKind.SourceCsprojItemNamesFolder).ToList();
        Assert.That(csprojFacts.Select(fact => fact.Subject), Is.EqualTo(new[] { csproj }));
        Assert.That(plan.Changes.Keys.Select(key => key.Absolute), Does.Not.Contain(csproj));
    }

    [Test]
    public async Task Check_DestinationInNoLoadedProject_CsFileRefuses()
    {
        WriteDemoProject(PlainCsproj);
        var source = MoveSetFixture.WriteText(Path.Combine(_root, "src", "A.cs"), "namespace Demo;\nclass A {}\n");

        var ex = await Assert.ThrowsAsync<MoveSetRefusedException>(async () =>
            await PlanAsync(Request((source, Path.Combine(_root, "Outside", "A.cs")))));

        Assert.That(ex.Offenders.Select(offender => offender.Reason), Is.EqualTo(new[] { MoveOffenderReason.OutsideLoadedProject }));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task Check_DestinationInNoLoadedProject_TxtPlans()
    {
        WriteDemoProject(PlainCsproj);
        var source = MoveSetFixture.WriteText(Path.Combine(_root, "src", "notes.txt"), "notes\n");

        var plan = await PlanAsync(Request((source, Path.Combine(_root, "Outside", "notes.txt"))));

        Assert.That(plan.Moves.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Check_PartialTypePartMovedWithinOneProject_Plans()
    {
        WriteDemoProject(PlainCsproj);
        var models = Path.Combine(DemoRoot, "Models");
        MoveSetFixture.WriteText(Path.Combine(models, "Customer.Part1.cs"), "namespace Demo.Models;\npartial class Customer { }\n");
        var part2 = MoveSetFixture.WriteText(Path.Combine(models, "Customer.Part2.cs"), "namespace Demo.Models;\npartial class Customer { }\n");
        MoveSetFixture.WriteText(Path.Combine(models, "Customer.Part3.cs"), "namespace Demo.Models;\npartial class Customer { }\n");

        var plan = await PlanAsync(Request((part2, Path.Combine(DemoRoot, "Other", "Customer.Part2.cs"))));

        Assert.That(plan.Moves.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Check_FolderPair_EmptySourceFolderLeftFact()
    {
        WriteDemoProject(PlainCsproj);
        var source = Path.Combine(_root, "src", "Old");
        MoveSetFixture.WriteText(Path.Combine(source, "Thing.cs"), "namespace Old;\nclass Thing {}\n");

        var plan = await PlanAsync(Request((source, Path.Combine(DemoRoot, "New"))));

        Assert.That(plan.Facts.Where(fact => fact.Kind == MoveFactKind.EmptySourceFolderLeft).Select(fact => fact.Subject), Does.Contain(source));
    }

    [Test]
    public async Task Check_NonCsFile_NoKeptNamespaceFact()
    {
        WriteDemoProject(PlainCsproj);
        var source = MoveSetFixture.WriteText(Path.Combine(_root, "src", "Readme.txt"), "namespace Wrong.Place;\n");

        var plan = await PlanAsync(Request((source, Path.Combine(DemoRoot, "Docs", "Readme.txt"))));

        Assert.That(plan.Facts.Any(fact => fact.Kind == MoveFactKind.KeptNamespace), Is.False);
    }

    /// <summary>Writes the Demo project file under the test root and returns its path.</summary>
    private string WriteDemoProject(string body) => MoveSetFixture.WriteText(DemoProjectPath, body);

    /// <summary>The committed solution: one project named Demo whose project file is the one on disk, with no documents.</summary>
    private Solution DemoSolution() =>
        TestSolutionBuilder.CreateSolutionWithProject("Demo", DemoProjectPath, Array.Empty<(string name, string content, string filePath)>());

    /// <summary>Runs stage 1 on the real files, then stage 2 against the committed Demo solution.</summary>
    private async Task<MoveSetPlanResult> PlanAsync(MoveSetRequest request)
    {
        var expanded = await MoveSetPlanner.ExpandAndCheckAsync(request, CancellationToken.None);
        return await MoveSetPlanner.CheckProjectsAndCollectFactsAsync(expanded, DemoSolution(), CancellationToken.None);
    }

    private static MoveSetRequest Request(params (string Source, string Destination)[] pairs)
    {
        return new MoveSetRequest(
            pairs.Select(pair => new FileMovePair { SourcePath = pair.Source, DestinationPath = pair.Destination }).ToList());
    }
}

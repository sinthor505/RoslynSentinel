using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The file-candidate lookup (<see cref="DocumentLookup.FindFileCandidates"/>) that feeds the "did you mean" text, and the
/// candidate overload of <see cref="FileNotFoundMessage.Build"/> (builder path). The missing paths used here sit under a
/// rooted directory that does not exist, so no disk branch fires; the disk branches are covered by the last tests.
/// </summary>
[TestFixture]
// Not parallel: the per-test temp root lives in an instance field, which parallel tests would share and delete under each other.
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileNotFoundCandidatesTests
{
    private const string OrderLineSource = "public class OrderLine { }";
    private const string CustomerRepositorySource = "public class CustomerRepository { }";

    private static readonly IReadOnlyList<FileCandidate> NoCandidates = [];

    private static readonly string MissingPath = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz", "OrderPricingCalculater.cs");

    private string _root = "";

    [SetUp]
    public void CreateTempRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), "fnfc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteTempRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Solution BuildSolution(params (string Name, string Source)[] extra)
    {
        var files = new List<(string name, string content)>
        {
            (OrderPricingFixture.FileName, OrderPricingFixture.Source),
            ("OrderLine.cs", OrderLineSource),
            ("CustomerRepository.cs", CustomerRepositorySource),
        };
        files.AddRange(extra.Select(e => (e.Name, e.Source)));
        return TestSolutionBuilder.CreateSolutionWithProject("TestProj", files.ToArray());
    }

    // ── FindFileCandidates (lookup path) ──────────────────────────────────────

    [Test]
    public void FindFileCandidates_MisspeltNameUnderMissingDirectory_OffersTheFixtureFileFirst()
    {
        var solution = BuildSolution();

        var candidates = DocumentLookup.FindFileCandidates(solution, MissingPath);

        Assert.That(candidates, Is.Not.Empty);
        Assert.That(candidates[0].Path, Is.EqualTo(OrderPricingFixture.FileName));
        Assert.That(candidates[0].SameFileName, Is.False);
        Assert.That(candidates[0].Distance, Is.EqualTo(1));
    }

    [Test]
    public void FindFileCandidates_UnrelatedName_ReturnsNoCandidates()
    {
        // NEGATIVE threshold gate: if this ever returns a candidate, report THRESHOLD: with the candidates and distances.
        var solution = BuildSolution();
        var requested = Path.Combine(Path.GetTempPath(), "no-such-dir-xyz", "ShippingLabelPrinter.cs");

        var candidates = DocumentLookup.FindFileCandidates(solution, requested);

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void FindFileCandidates_SameFileNameInOtherDirectory_IsListedFirstAtDistanceZero()
    {
        var solution = BuildSolution(("OrderLines.cs", "public class OrderLines { }"));
        var requested = Path.Combine("Other", "OrderLine.cs");

        var candidates = DocumentLookup.FindFileCandidates(solution, requested);

        Assert.That(candidates, Is.Not.Empty);
        Assert.That(candidates[0].Path, Is.EqualTo("OrderLine.cs"));
        Assert.That(candidates[0].SameFileName, Is.True);
        Assert.That(candidates[0].Distance, Is.EqualTo(0));
    }

    [Test]
    public void FindFileCandidates_MaxOne_ReturnsOnlyTheNearest()
    {
        var solution = BuildSolution();

        var candidates = DocumentLookup.FindFileCandidates(solution, MissingPath, 1);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].Path, Is.EqualTo(OrderPricingFixture.FileName));
    }

    [Test]
    public void FindFileCandidates_DefaultCap_IsThree()
    {
        var solution = BuildSolution(
            ("OrderPricingCalculators.cs", "public class OrderPricingCalculators { }"),
            ("OrderPricingCalculatorX.cs", "public class OrderPricingCalculatorX { }"),
            ("OrderPricingCalculatorY.cs", "public class OrderPricingCalculatorY { }"),
            ("OrderPricingCalculatorZ.cs", "public class OrderPricingCalculatorZ { }"));

        var candidates = DocumentLookup.FindFileCandidates(solution, MissingPath);

        Assert.That(candidates, Has.Count.EqualTo(3));
    }

    // ── FileNotFoundMessage.Build candidate overload (builder path) ───────────

    [Test]
    public void Build_BuilderPath_OneCandidate_NamesPathPrefilledReadFileAndDidYouMean()
    {
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(OrderPricingFixture.FileName, 1, false)];

        var text = FileNotFoundMessage.Build(MissingPath, candidates, 2);

        Assert.That(text, Does.Contain(OrderPricingFixture.FileName));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ReadFile", ("filePath", OrderPricingFixture.FileName))));
        Assert.That(text, Does.Contain("Did you mean"));
        Assert.That(text, Does.Contain("does not exist in this solution"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public void Build_BuilderPath_NoCandidatesListingCallable_SaysNoSimilarNameAndListsSolutionItems()
    {
        var text = FileNotFoundMessage.Build(MissingPath, NoCandidates, 3);

        Assert.That(text, Does.Contain("No file in the solution has a similar name"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ListSolutionItems", ("kind", "all"))));
        Assert.That(text, Does.Contain("does not exist in this solution"));
        Assert.That(text, Does.Contain("tell the user"));
        Assert.That(text, Does.Contain("If your task is to create this file"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public void Build_BuilderPath_NoCandidatesListingNotCallable_NamesNoListingTool()
    {
        var text = FileNotFoundMessage.Build(MissingPath, NoCandidates, 3, name => name != "ListSolutionItems");

        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
        Assert.That(text, Does.Contain("does not exist in this solution"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public void Build_BuilderPath_CandidatePresentReadFileNotCallable_ListsPathWithoutReadFileCall()
    {
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(OrderPricingFixture.FileName, 1, false)];

        var text = FileNotFoundMessage.Build(MissingPath, candidates, 2, name => name != "ReadFile");

        Assert.That(text, Does.Contain(OrderPricingFixture.FileName));
        Assert.That(text, Does.Not.Contain("ReadFile"));
    }

    [Test]
    public void Build_BuilderPath_DeclaringTypeSentence_IsPresentWithCandidates()
    {
        IReadOnlyList<(string TypeName, string FilePath)> declaring = [("StandardOrderPricingCalculator", OrderPricingFixture.FileName)];
        IReadOnlyList<FileCandidate> candidates = [new FileCandidate(OrderPricingFixture.FileName, 1, false)];

        var text = FileNotFoundMessage.Build(MissingPath, candidates, 2, declaringTypeFiles: declaring);

        Assert.That(text, Does.Contain($"Type 'StandardOrderPricingCalculator' is declared in {OrderPricingFixture.FileName}; read that file. "));
    }

    [Test]
    public void Build_BuilderPath_DeclaringTypeSentence_IsPresentWithoutCandidates()
    {
        IReadOnlyList<(string TypeName, string FilePath)> declaring = [("StandardOrderPricingCalculator", OrderPricingFixture.FileName)];

        var text = FileNotFoundMessage.Build(MissingPath, NoCandidates, 3, declaringTypeFiles: declaring);

        Assert.That(text, Does.Contain($"Type 'StandardOrderPricingCalculator' is declared in {OrderPricingFixture.FileName}; read that file. "));
        Assert.That(text, Does.Contain("No file in the solution has a similar name"));
    }

    [Test]
    public void Build_BuilderPath_ExistingDirectory_StillSaysDirectory()
    {
        var directory = Path.Combine(_root, "src");
        Directory.CreateDirectory(directory);

        var text = FileNotFoundMessage.Build(directory, NoCandidates, 2);

        Assert.That(text, Does.Contain("is a directory"));
    }

    [Test]
    public void Build_BuilderPath_ExistingProjectFile_StillSaysProjectFile()
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project />");

        var text = FileNotFoundMessage.Build(project, NoCandidates, 2);

        Assert.That(text, Does.Contain("is a project/solution file"));
    }
}

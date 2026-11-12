using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The shared symbol-candidate builder (<see cref="SymbolNotFoundDetail"/>): candidate lookup, container resolution, the
/// two renderers, and the one entry point <see cref="SymbolNotFoundDetail.BuildAsync"/> (builder path). The OrderPricing
/// fixture declares CalcDisc, OrderCheckout and GetTotal on the static class OrderPricingCalculator and no ApplyLoyaltyBonus.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // Not parallel: shares ToolCallHint's process-wide catalog with the other hint tests.
public class SymbolNotFoundDetailTests
{
    private const string ScopeDescription = "1 project(s), test projects excluded";
    private const string ProjectName = "TestProj";

    private static Solution BuildSolution(params (string Name, string Source)[] extra)
    {
        var files = new List<(string name, string content)> { (OrderPricingFixture.FileName, OrderPricingFixture.Source) };
        files.AddRange(extra.Select(e => (e.Name, e.Source)));
        return TestSolutionBuilder.CreateSolutionWithProject(ProjectName, files.ToArray());
    }

    private static IReadOnlyCollection<Project> Projects(Solution solution) => solution.Projects.ToList();

    // ── FindCandidatesAsync (lookup path) ─────────────────────────────────────

    [Test]
    public async Task FindCandidates_MisspeltMethod_ReturnsCalcDiscWithLocationAndDocCommentId()
    {
        var projects = Projects(BuildSolution());

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDsc", "method");

        Assert.That(candidates, Is.Not.Empty);
        var first = candidates[0];
        Assert.That(first.Name, Is.EqualTo("CalcDisc"));
        Assert.That(first.Kind, Is.EqualTo("method"));
        Assert.That(first.Container, Is.EqualTo("OrderPricingCalculator"));
        Assert.That(first.FilePath, Does.EndWith(OrderPricingFixture.FileName));
        Assert.That(first.Line, Is.EqualTo(5));
        Assert.That(first.DocCommentId, Does.Contain("CalcDisc"));
        Assert.That(first.Distance, Is.EqualTo(1));
    }

    [Test]
    public async Task FindCandidates_MisspeltTypeName_ReportsNamespaceAsContainer()
    {
        var projects = Projects(BuildSolution());

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "OrderPricingCalculater", "type");

        Assert.That(candidates, Is.Not.Empty);
        Assert.That(candidates[0].Name, Is.EqualTo("OrderPricingCalculator"));
        Assert.That(candidates[0].Kind, Is.EqualTo("type"));
        Assert.That(candidates[0].Container, Is.EqualTo("Fixture"));
    }

    [Test]
    public async Task FindCandidates_PropertyFieldAndEvent_AreOfferedWithTheirKinds()
    {
        const string members = "namespace Fixture; public class Gadget { public int Widgets { get; set; } public int widgetCount; public event System.Action? WidgetChanged; }";
        var projects = Projects(BuildSolution(("Gadget.cs", members)));

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "Widgets", "any", cap: 10);

        Assert.That(candidates.Select(c => (c.Name, c.Kind)), Does.Contain(("Widgets", "property")));
        Assert.That(candidates.Select(c => (c.Name, c.Kind)), Does.Contain(("widgetCount", "field")));
        Assert.That(candidates.Select(c => (c.Name, c.Kind)), Does.Contain(("WidgetChanged", "event")));
    }

    [Test]
    public async Task FindCandidates_CompilerGeneratedMembers_AreSkipped()
    {
        // Auto-property backing fields, accessors and a field-like event's backing field are not user-declared symbols.
        const string members = "namespace Fixture; public class Gadget { public int Widgets { get; set; } public event System.Action? WidgetChanged; }";
        var projects = Projects(BuildSolution(("Gadget.cs", members)));

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "Widgets", "any", cap: 20);

        Assert.That(candidates.Select(c => c.Name), Has.None.Contains("k__BackingField"));
        Assert.That(candidates.Select(c => c.Name), Has.None.StartWith("get_"));
        Assert.That(candidates.Select(c => c.Name), Has.None.StartWith("set_"));
        Assert.That(candidates.Select(c => c.Name), Has.None.StartWith("add_"));
    }

    [Test]
    public async Task FindCandidates_SameKindFirst_MethodOutranksNearerTypeForMethodKindAndNotForTypeOrAny()
    {
        // CalcDsk is 1 edit from the type CalcDisk and 2 from the method CalcDisc: the kind preference must beat the distance.
        var projects = Projects(BuildSolution(("CalcDisk.cs", "namespace Fixture; public class CalcDisk { }")));

        var forMethod = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDsk", "method");
        var forType = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDsk", "type");
        var forAny = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDsk", "any");

        Assert.That(forMethod[0].Name, Is.EqualTo("CalcDisc"));
        Assert.That(forMethod[0].Kind, Is.EqualTo("method"));
        Assert.That(forMethod[1].Name, Is.EqualTo("CalcDisk"));
        Assert.That(forType[0].Name, Is.EqualTo("CalcDisk"));
        Assert.That(forType[0].Kind, Is.EqualTo("type"));
        Assert.That(forAny[0].Name, Is.EqualTo("CalcDisk"));
    }

    [Test]
    public async Task FindCandidates_FourSimilarNames_DefaultCapIsThreeAndExplicitCapIsHonoured()
    {
        const string many = "namespace Fixture; public class Many { public void CalcDiscA() { } public void CalcDiscB() { } public void CalcDiscC() { } public void CalcDiscD() { } }";
        var projects = Projects(BuildSolution(("Many.cs", many)));

        var byDefault = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDisc", "method");
        var two = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDisc", "method", cap: 2);

        Assert.That(byDefault, Has.Count.EqualTo(3));
        Assert.That(two, Has.Count.EqualTo(2));
        Assert.That(byDefault[0].Name, Is.EqualTo("CalcDisc"));
        Assert.That(byDefault[0].Distance, Is.EqualTo(0));
    }

    [Test]
    public async Task FindCandidates_Overloads_AreListedOnce()
    {
        const string overloads = "namespace Fixture; public class Over { public void CalcDiscX(int a) { } public void CalcDiscX(string a) { } }";
        var projects = Projects(BuildSolution(("Over.cs", overloads)));

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, "CalcDiscX", "method", cap: 10);

        Assert.That(candidates.Count(c => c.Name == "CalcDiscX"), Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase("Ca")]
    public async Task FindCandidates_NameShorterThanThreeCharacters_ReturnsNothing(string wanted)
    {
        var projects = Projects(BuildSolution());

        var candidates = await SymbolNotFoundDetail.FindCandidatesAsync(projects, wanted, "any");

        Assert.That(candidates, Is.Empty);
    }

    // ── FindContainerFileAsync (container path) ───────────────────────────────

    [Test]
    public async Task FindContainerFile_OneTypeOfThatName_ReturnsItsFile()
    {
        var projects = Projects(BuildSolution());

        var file = await SymbolNotFoundDetail.FindContainerFileAsync(projects, "OrderPricingCalculator");
        var qualified = await SymbolNotFoundDetail.FindContainerFileAsync(projects, "Fixture.OrderPricingCalculator");

        Assert.That(file, Is.EqualTo(OrderPricingFixture.FileName));
        Assert.That(qualified, Is.EqualTo(OrderPricingFixture.FileName));
    }

    [Test]
    public async Task FindContainerFile_GenericArityIgnored()
    {
        var projects = Projects(BuildSolution(("Box.cs", "namespace Fixture; public class Box<T> { }")));

        var file = await SymbolNotFoundDetail.FindContainerFileAsync(projects, "Box<int>");

        Assert.That(file, Is.EqualTo("Box.cs"));
    }

    [Test]
    public async Task FindContainerFile_NoSuchType_OrWrongCase_ReturnsNull()
    {
        var projects = Projects(BuildSolution());

        Assert.That(await SymbolNotFoundDetail.FindContainerFileAsync(projects, "ShippingLabelPrinter"), Is.Null);
        Assert.That(await SymbolNotFoundDetail.FindContainerFileAsync(projects, "orderpricingcalculator"), Is.Null);
    }

    [Test]
    public async Task FindContainerFile_TwoSameNamedTypes_ReturnsNull()
    {
        var projects = Projects(BuildSolution(
            ("DupA.cs", "namespace A; public class Dup { }"),
            ("DupB.cs", "namespace B; public class Dup { }")));

        var file = await SymbolNotFoundDetail.FindContainerFileAsync(projects, "Dup");

        Assert.That(file, Is.Null);
    }

    // ── BuildAsync (builder path) ─────────────────────────────────────────────

    [Test]
    public async Task BuildAsync_MisspeltMethod_NamesCandidateFileAndPrefilledSearchCall()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(projects, "CalcDsc", "method", ScopeDescription, ProjectName);

        Assert.That(text, Does.Contain("Symbol 'CalcDsc' (method) was not found in " + ScopeDescription));
        Assert.That(text, Does.Contain("Did you mean one of these?"));
        Assert.That(text, Does.Contain("CalcDisc (method) in OrderPricingCalculator, " + OrderPricingFixture.FileName));
        Assert.That(text, Does.Contain(ToolCallHint.Call("Search", ("mode", "symbol"), ("query", "CalcDisc"), ("filePath", OrderPricingFixture.FileName))));
        Assert.That(text, Does.Contain("does not exist in this solution"));
        Assert.That(text, Does.Not.Contain("You MUST"));
    }

    [Test]
    public async Task BuildAsync_NoSimilarNameNoContainer_PointsAtListAllAndSaysItDoesNotExist()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(projects, "ApplyLoyaltyBonus", "method", ScopeDescription, ProjectName);

        Assert.That(text, Does.Contain(ScopeDescription));
        Assert.That(text, Does.Contain("No symbol"));
        Assert.That(text, Does.Contain("similar name"));
        var listAll = ToolCallHint.Call("ListAll", ("kind", "method"), ("projectName", ProjectName));
        Assert.That(text, Does.Contain(listAll));
        Assert.That(listAll, Does.Contain("kind: method"));
        Assert.That(listAll, Does.Contain("projectName: \"" + ProjectName + "\""));
        Assert.That(text, Does.Contain("does not exist"));
        Assert.That(text, Does.Contain("tell the user so"));
        Assert.That(text, Does.Not.Contain("exactMatch"));
        Assert.That(text, Does.Not.Contain("MUST"));
        Assert.That(text, Does.Not.Contain("GetFileOutline"));
    }

    [Test]
    public async Task BuildAsync_NoSimilarNameWithResolvingContainer_PointsAtGetFileOutlineOfTheContainerFile()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(projects, "OrderPricingCalculator.ApplyLoyaltyBonus", "method", ScopeDescription, ProjectName);

        Assert.That(text, Does.Contain("Symbol 'ApplyLoyaltyBonus' (method)"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("GetFileOutline", ("filePath", OrderPricingFixture.FileName))));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.Contain("does not exist"));
    }

    [Test]
    public async Task BuildAsync_NoSimilarNameWithUnknownContainer_FallsBackToListAll()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(projects, "ShippingLabelPrinter.ApplyLoyaltyBonus(decimal)", "method", ScopeDescription, null);

        Assert.That(text, Does.Contain("Symbol 'ApplyLoyaltyBonus' (method)"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ListAll", ("kind", "method"))));
        Assert.That(text, Does.Not.Contain("projectName"));
        Assert.That(text, Does.Not.Contain("GetFileOutline"));
    }

    [Test]
    public async Task BuildAsync_NoCandidatesNeitherOrientationToolCallable_NamesNoCallButStillSaysItDoesNotExist()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(
            projects, "OrderPricingCalculator.ApplyLoyaltyBonus", "method", ScopeDescription, ProjectName,
            isCallable: name => name != "GetFileOutline" && name != "ListAll");

        Assert.That(text, Does.Not.Contain("GetFileOutline"));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.Contain("does not exist in this solution"));
        Assert.That(text, Does.Contain(ScopeDescription));
    }

    [Test]
    public async Task BuildAsync_NoCandidatesGetFileOutlineNotCallable_FallsBackToListAll()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(
            projects, "OrderPricingCalculator.ApplyLoyaltyBonus", "method", ScopeDescription, null,
            isCallable: name => name != "GetFileOutline");

        Assert.That(text, Does.Not.Contain("GetFileOutline"));
        Assert.That(text, Does.Contain(ToolCallHint.Call("ListAll", ("kind", "method"))));
    }

    [Test]
    public async Task BuildAsync_CandidatePresentSearchNotCallable_ListsCandidateWithoutCall()
    {
        var projects = Projects(BuildSolution());

        var text = await SymbolNotFoundDetail.BuildAsync(
            projects, "CalcDsc", "method", ScopeDescription, ProjectName, isCallable: name => name != "Search");

        Assert.That(text, Does.Contain("CalcDisc (method) in OrderPricingCalculator, " + OrderPricingFixture.FileName));
        Assert.That(text, Does.Not.Contain("->"));
        Assert.That(text, Does.Not.Contain("Search("));
    }

    [Test]
    public async Task BuildAsync_CallerSuppliedNextCall_ReplacesTheSearchCallAndRunsOnlyWhenCandidatesAreRendered()
    {
        var projects = Projects(BuildSolution());
        var invocations = 0;
        string NextCall(SymbolCandidate c)
        {
            invocations++;
            return $"Rename(symbol: \"{c.DocCommentId}\")";
        }

        var withCandidate = await SymbolNotFoundDetail.BuildAsync(projects, "CalcDsc", "method", ScopeDescription, ProjectName, nextCall: NextCall);
        var invokedForCandidates = invocations;
        var withoutCandidate = await SymbolNotFoundDetail.BuildAsync(projects, "ApplyLoyaltyBonus", "method", ScopeDescription, ProjectName, nextCall: NextCall);

        Assert.That(withCandidate, Does.Contain("-> Rename(symbol: \"M:Fixture.OrderPricingCalculator.CalcDisc"));
        Assert.That(withCandidate, Does.Not.Contain("Search("));
        Assert.That(invokedForCandidates, Is.EqualTo(1));
        Assert.That(invocations, Is.EqualTo(1), "a no-candidate message must not build a candidate next call");
        Assert.That(withoutCandidate, Does.Contain("No symbol"));
    }

    // ── Renderers (builder path, hand-made candidates) ────────────────────────

    [Test]
    public void RenderCandidates_CandidateWithoutContainerOrFile_ListsNameAndKindOnly()
    {
        IReadOnlyList<SymbolCandidate> candidates = [new SymbolCandidate("CalcDisc", "method", null, null, null, null, 1)];

        var text = SymbolNotFoundDetail.RenderCandidates("CalcDsc", "method", ScopeDescription, candidates, name => name != "Search");

        Assert.That(text, Does.Contain("\n  - CalcDisc (method)\n"));
        Assert.That(text, Does.Not.Contain("(method) in"));
    }

    [TestCase("method", "method")]
    [TestCase("property", "property")]
    [TestCase("field", "field")]
    [TestCase("type", "all")]
    [TestCase("event", "all")]
    [TestCase("any", "all")]
    public void RenderNoCandidates_SymbolKind_MapsOntoAListAllKind(string symbolKind, string expectedListAllKind)
    {
        var text = SymbolNotFoundDetail.RenderNoCandidates("Missing", symbolKind, ScopeDescription, null, null);

        Assert.That(text, Does.Contain(ToolCallHint.Call("ListAll", ("kind", expectedListAllKind))));
        Assert.That(text, Does.Contain("kind: " + expectedListAllKind));
    }

    [Test]
    public void RenderNoCandidates_OrientationFileNamed_PrefersGetFileOutlineOverListAll()
    {
        var text = SymbolNotFoundDetail.RenderNoCandidates("Missing", "method", ScopeDescription, "Some/File.cs", ProjectName);

        Assert.That(text, Does.Contain("To see what exists in the file you named, call " + ToolCallHint.Call("GetFileOutline", ("filePath", "Some/File.cs"))));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.EndWith("the symbol does not exist in this solution: tell the user so instead of renaming or editing another symbol."));
    }
}

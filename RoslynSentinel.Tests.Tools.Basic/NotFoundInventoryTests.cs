using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.Basic;

// TODO plan_error_policy_pointer arm I: temporary A/B switch; resolve at plan close
/// <summary>
/// Arm I of plan_error_policy_pointer (Step 11g): the scoped inventory appended to the arm H no-candidates not-found text, behind
/// <see cref="NotFoundInventoryOptions"/>. With the switch off every text equals the arm H text. The switch is forced per test through
/// <see cref="NotFoundInventoryOptions.OverrideForCurrentContext"/> (an AsyncLocal scope, so parallel fixtures never see it) and the ambient
/// <see cref="ToolCallContext"/> is cleared in TearDown.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // Not parallel: shares ToolCallHint's process-wide catalog with the other hint tests.
public class NotFoundInventoryTests
{
    private const string ScopeDescription = "1 project(s), test projects excluded";
    private const string ProjectName = "TestProj";
    private static readonly IReadOnlyList<FileCandidate> NoCandidates = [];

    private string _root = "";

    [SetUp]
    public void CreateRoot() => _root = Path.Combine(Path.GetTempPath(), "nfinv_" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void ClearToolCallContext() => ToolCallContext.Set(null);

    private static void SetContext(bool? workspaceCurrent, string? solutionRoot = null) =>
        ToolCallContext.Set(workspaceCurrent is null
            ? null
            : new ToolCallContext(null, null, "ReadFile", null, 0) { WorkspaceCurrent = workspaceCurrent, SolutionRoot = solutionRoot });

    // ── File inventory ────────────────────────────────────────────────────────

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private Solution FolderSolution()
    {
        (string, string, string) Doc(params string[] parts) => (parts[^1], "class C { }", P(parts));
        return TestSolutionBuilder.CreateSolutionWithProject("ContosoOrders.Core", P("ContosoOrders.Core", "ContosoOrders.Core.csproj"),
        [
            Doc("ContosoOrders.Core", "FixtureHelpers", "B.cs"),
            Doc("ContosoOrders.Core", "FixtureHelpers", "a.cs"),
            Doc("ContosoOrders.Core", "FixtureHelpers", "C.cs"),
            Doc("ContosoOrders.Core", "FixtureHelpers", "Sub", "Deep.cs"),
            Doc("ContosoOrders.Core", "Other", "X.cs"),
            Doc("Program.cs"),
        ]);
    }

    private string BuildFileText(string missing, IReadOnlyList<string>? folderFileNames, bool? workspaceCurrent = true)
    {
        SetContext(workspaceCurrent, _root);
        return FileNotFoundMessage.Build(missing, NoCandidates, 3, folderFileNames: folderFileNames);
    }

    private static string InventoryFolder(params string[] parts) => $"`{Path.Combine(parts)}{Path.DirectorySeparatorChar}`";

    [Test]
    public void FindFolderFileNames_SwitchOff_ReturnsNullAndTextEqualsArmH()
    {
        var solution = FolderSolution();
        var missing = P("ContosoOrders.Core", "FixtureHelpers", "Missing.cs");

        using var off = NotFoundInventoryOptions.OverrideForCurrentContext(false);
        var names = DocumentLookup.FindFolderFileNames(solution, missing);

        Assert.That(names, Is.Null);
        Assert.That(BuildFileText(missing, names), Is.EqualTo(BuildFileText(missing, null)));
        Assert.That(BuildFileText(missing, names), Does.Not.Contain("contains"));
    }

    [Test]
    public void FindFolderFileNames_SwitchOn_ListsOnlyTheFilesDirectlyInTheFolderSortedIgnoringCase()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var names = DocumentLookup.FindFolderFileNames(FolderSolution(), P("ContosoOrders.Core", "FixtureHelpers", "Missing.cs"));

        Assert.That(names, Is.EqualTo(new[] { "a.cs", "B.cs", "C.cs" }));
    }

    [Test]
    public void Build_SwitchOnFolderWithFiles_AppendsTheCountedSortedListAfterTheArmHText()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var missing = P("ContosoOrders.Core", "FixtureHelpers", "Missing.cs");
        var names = DocumentLookup.FindFolderFileNames(FolderSolution(), missing);

        var text = BuildFileText(missing, names);
        var armH = BuildFileText(missing, null);

        var folder = InventoryFolder("ContosoOrders.Core", "FixtureHelpers");
        Assert.That(text, Is.EqualTo(armH + $" {folder} contains 3 files: a.cs, B.cs, C.cs"));
        Assert.That(text, Does.StartWith("Your tool call and file path were well-formed, but file `"));
    }

    [Test]
    public void Build_SwitchOnFolderWithOneFile_UsesTheSingular()
    {
        var text = BuildFileText(P("Lone", "Missing.cs"), ["Only.cs"]);

        Assert.That(text, Does.EndWith($" {InventoryFolder("Lone")} contains 1 file: Only.cs"));
    }

    [Test]
    public void Build_FolderOverTheCap_ListsTheFirst30AndSaysFirst30OfN()
    {
        var names = Enumerable.Range(1, 45).Select(i => $"f{i:00}.cs").Reverse().ToList();

        var text = BuildFileText(P("Big", "Missing.cs"), names);

        var expectedFirst30 = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"f{i:00}.cs"));
        Assert.That(text, Does.EndWith($" {InventoryFolder("Big")} contains 45 files (first 30 of 45): {expectedFirst30}"));
        Assert.That(text, Does.Not.Contain("f31.cs"));
    }

    [Test]
    public void Build_FolderWithExactlyTheCap_IsNotMarkedAsCapped()
    {
        var names = Enumerable.Range(1, 30).Select(i => $"f{i:00}.cs").ToList();

        var text = BuildFileText(P("Big", "Missing.cs"), names);

        Assert.That(text, Does.Contain(" contains 30 files: f01.cs"));
        Assert.That(text, Does.Not.Contain("first 30 of"));
    }

    [Test]
    public void Build_DuplicateNames_AreListedOnce()
    {
        var text = BuildFileText(P("Dup", "Missing.cs"), ["B.cs", "b.cs", "A.cs"]);

        Assert.That(text, Does.EndWith(" contains 2 files: A.cs, B.cs"));
    }

    [Test]
    public void FindFolderFileNames_FolderWithNoSolutionFile_ReturnsEmptyAndTextStatesIt()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var missing = P("ContosoOrders.Core", "NoSuchFolder", "Missing.cs");

        var names = DocumentLookup.FindFolderFileNames(FolderSolution(), missing);
        var text = BuildFileText(missing, names);

        Assert.That(names, Is.Empty);
        Assert.That(text, Does.EndWith($" No file in the solution is in {InventoryFolder("ContosoOrders.Core", "NoSuchFolder")}."));
    }

    [Test]
    public void Build_PathAtTheSolutionRoot_NamesTheSolutionRootFolder()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var missing = P("Missing.cs");
        SetContext(true, _root);

        var names = DocumentLookup.FindFolderFileNames(FolderSolution(), missing);
        var text = BuildFileText(missing, names);

        Assert.That(names, Is.EqualTo(new[] { "Program.cs" }));
        Assert.That(text, Does.EndWith(" The solution root folder contains 1 file: Program.cs"));
    }

    [Test]
    public void FindFolderFileNames_BareNameWithoutKnownSolutionRoot_ReturnsNull()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        ToolCallContext.Set(null);

        Assert.That(DocumentLookup.FindFolderFileNames(FolderSolution(), "Missing.cs"), Is.Null);
    }

    [Test]
    public void Build_WorkspaceStaleOrUnknown_IgnoresTheInventory()
    {
        var missing = P("Big", "Missing.cs");
        var withoutNames = BuildFileText(missing, null, workspaceCurrent: false);

        Assert.That(BuildFileText(missing, ["A.cs"], workspaceCurrent: false), Is.EqualTo(withoutNames));
        Assert.That(BuildFileText(missing, ["A.cs"], workspaceCurrent: null), Is.EqualTo(BuildFileText(missing, null, workspaceCurrent: null)));
    }

    [Test]
    public void DescribeOfALookupMiss_SwitchOn_CarriesTheFolderInventory()
    {
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var missing = P("ContosoOrders.Core", "FixtureHelpers", "Missing.cs");
        SetContext(true, _root);

        var result = DocumentLookup.TryGetDocument(FolderSolution(), new FilePathWrapper(missing));

        Assert.That(result.IsFound, Is.False);
        Assert.That(result.Describe(), Does.EndWith($" {InventoryFolder("ContosoOrders.Core", "FixtureHelpers")} contains 3 files: a.cs, B.cs, C.cs"));
    }

    // ── Symbol inventory ──────────────────────────────────────────────────────

    private static Solution BuildSymbolSolution(params (string Name, string Source)[] extra)
    {
        var files = new List<(string name, string content)> { (OrderPricingFixture.FileName, OrderPricingFixture.Source) };
        files.AddRange(extra.Select(e => (e.Name, e.Source)));
        return TestSolutionBuilder.CreateSolutionWithProject(ProjectName, files.ToArray());
    }

    private static Task<string> BuildSymbolTextAsync(
        Solution solution, string query, bool wholeSolution = false, string? containingType = null, string? containerFilePath = null) =>
        SymbolNotFoundDetail.BuildAsync(
            solution.Projects.ToList(), query, "method", ScopeDescription, ProjectName, wholeSolution: wholeSolution,
            containingType: containingType, containerFilePath: containerFilePath);

    private const string OrderPricingMembers = "CalcDisc, GetTotal, OrderCheckout";

    [Test]
    public async Task BuildAsync_SwitchOnContainingType_AppendsTheTypesMembersToTodaysText()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        string today;
        using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
        {
            today = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator");
        }

        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator");

        Assert.That(text, Is.EqualTo(today + $" Type `Fixture.OrderPricingCalculator` declares 3 members: {OrderPricingMembers}"));
        Assert.That(today, Does.Contain("If it is not in that list, the symbol does not exist in this solution."), "a narrowed search keeps today's text");
    }

    [Test]
    public async Task BuildAsync_SwitchOnContainingTypeAndWholeSolution_AppendsToTheArmHText()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", wholeSolution: true, containingType: "OrderPricingCalculator");

        Assert.That(text, Does.StartWith("Your tool call was well-formed, but symbol `ApplyLoyaltyBonus` does not exist in the solution. "));
        Assert.That(text, Does.EndWith($" Type `Fixture.OrderPricingCalculator` declares 3 members: {OrderPricingMembers}"));
    }

    [Test]
    public async Task BuildAsync_SwitchOnQualifiedQueryResolvingToOneType_AppendsTheTypesMembers()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var text = await BuildSymbolTextAsync(solution, "OrderPricingCalculator.ApplyLoyaltyBonus", wholeSolution: true);

        Assert.That(text, Does.StartWith("Your tool call was well-formed, but symbol `ApplyLoyaltyBonus` does not exist in the solution. "));
        Assert.That(text, Does.EndWith($" Type `Fixture.OrderPricingCalculator` declares 3 members: {OrderPricingMembers}"));
    }

    [Test]
    public async Task BuildAsync_SwitchOnPinnedFile_AppendsTheFilesMembersAndTypeCount()
    {
        var solution = BuildSymbolSolution(("Gadget.cs", "namespace Fixture; public class Gadget { public int Widgets { get; set; } public event System.Action? Changed; public class Inner { public int Hidden; } }"));
        SetContext(true);
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containerFilePath: "Gadget.cs");
        var orderText = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containerFilePath: OrderPricingFixture.FileName);

        Assert.That(text, Does.EndWith(" File `Gadget.cs` declares 3 members across 1 type(s): Changed, Inner, Widgets"));
        Assert.That(orderText, Does.EndWith($" File `{OrderPricingFixture.FileName}` declares 3 members across 1 type(s): {OrderPricingMembers}"));
    }

    [Test]
    public async Task BuildAsync_SwitchOnNoContainer_AppendsNothingAndEqualsArmH()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        string armH;
        using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
        {
            armH = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", wholeSolution: true);
        }

        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", wholeSolution: true);

        Assert.That(text, Is.EqualTo(armH));
        Assert.That(text, Does.StartWith("Your tool call was well-formed, but symbol `ApplyLoyaltyBonus`"));
        Assert.That(text, Does.Not.Contain("declares"));
    }

    [Test]
    public async Task BuildAsync_SwitchOffWithContainers_EqualsTheTextWithoutThem()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        using var off = NotFoundInventoryOptions.OverrideForCurrentContext(false);

        var plain = await BuildSymbolTextAsync(solution, "OrderPricingCalculator.ApplyLoyaltyBonus", wholeSolution: true);
        var withContainers = await BuildSymbolTextAsync(solution, "OrderPricingCalculator.ApplyLoyaltyBonus", wholeSolution: true, containingType: "OrderPricingCalculator", containerFilePath: OrderPricingFixture.FileName);

        Assert.That(withContainers, Is.EqualTo(plain));
    }

    [Test]
    public async Task BuildAsync_SwitchOnUnknownContainingType_StatesItAsAFact()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "NoSuchType");

        Assert.That(text, Does.EndWith(" No type named `NoSuchType` is declared in the searched projects."));
    }

    [Test]
    public async Task BuildAsync_SwitchOnQualifiedQueryWithNonTypeContainer_AppendsNothing()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        string today;
        using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
        {
            today = await BuildSymbolTextAsync(solution, "Fixture.ApplyLoyaltyBonus", wholeSolution: true);
        }

        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var text = await BuildSymbolTextAsync(solution, "Fixture.ApplyLoyaltyBonus", wholeSolution: true);

        Assert.That(text, Is.EqualTo(today), "Fixture is a namespace, not a type");
    }

    [Test]
    public async Task BuildAsync_SwitchOnContainerOverTheCap_ListsTheFirst30AndSaysFirst30OfN()
    {
        var fields = string.Join(" ", Enumerable.Range(1, 35).Select(i => $"public int F{i:00};"));
        var solution = BuildSymbolSolution(("Gadget.cs", $"namespace Fixture; public class Gadget {{ {fields} }}"));
        SetContext(true);
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        var text = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "Gadget");

        var expectedFirst30 = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"F{i:00}"));
        Assert.That(text, Does.EndWith($" Type `Fixture.Gadget` declares 35 members (first 30 of 35): {expectedFirst30}"));
        Assert.That(text, Does.Not.Contain("F31"));
    }

    [Test]
    public async Task BuildAsync_SwitchOnWorkspaceStaleOrUnknown_AppendsNothing()
    {
        var solution = BuildSymbolSolution();
        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);

        SetContext(false);
        var stale = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator");
        SetContext(null);
        var unknown = await BuildSymbolTextAsync(solution, "ApplyLoyaltyBonus", containingType: "OrderPricingCalculator");

        Assert.That(stale, Does.Not.Contain("declares"));
        Assert.That(unknown, Does.Not.Contain("declares"));
    }

    [Test]
    public async Task BuildAsync_SwitchOnWithCandidates_KeepsTheCandidateText()
    {
        var solution = BuildSymbolSolution();
        SetContext(true);
        string today;
        using (NotFoundInventoryOptions.OverrideForCurrentContext(false))
        {
            today = await BuildSymbolTextAsync(solution, "CalcDsc", containingType: "OrderPricingCalculator");
        }

        using var on = NotFoundInventoryOptions.OverrideForCurrentContext(true);
        var text = await BuildSymbolTextAsync(solution, "CalcDsc", containingType: "OrderPricingCalculator");

        Assert.That(text, Is.EqualTo(today));
    }
}

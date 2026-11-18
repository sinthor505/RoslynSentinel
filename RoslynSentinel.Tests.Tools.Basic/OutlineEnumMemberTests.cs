// Coverage for enum member names in GetFileOutline and ListAll. Members are listed as Type.Member
// so a model reading the outline never retypes the member alone; ListAll's nameFilter still finds
// the row by the bare member name, and by the qualified name.

using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("WorkspaceTools")]
public class OutlineEnumMemberTests
{
    private const string ProjectCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
    private const string ShippingSource = "public enum ShippingKinds { Standard, Overnight, TwoDay }";

    private string _dir = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceReadNavigationImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OutlineEnumMemberTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "ShipProj"));
        File.WriteAllText(Path.Combine(_dir, "ShipProj", "ShipProj.csproj"), ProjectCsproj);

        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _impl = TestTools.WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "ShipProj",
            Path.Combine(_dir, "ShipProj", "ShipProj.csproj"),
            [("ShippingKinds.cs", ShippingSource, Path.Combine(_dir, "ShipProj", "ShippingKinds.cs"))]);

        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static List<SolutionSymbolEntry> EntriesOf(SentinelCallToolResult<object> result) =>
        (List<SolutionSymbolEntry>)result.SuccessData!;

    [Test]
    public async Task GetFileOutline_EnumMembers_AreListedQualifiedAsync()
    {
        var filePath = Path.Combine(_dir, "ShipProj", "ShippingKinds.cs");

        var result = await _impl.GetFileOutline(reason: "test message", filePath: filePath);

        Assert.That(result.IsError, Is.False);
        var members = result.SuccessData!.Symbols.Where(s => s.Kind == "enum member").ToList();
        Assert.That(members.Select(m => m.Name), Is.EquivalentTo(new[] { "ShippingKinds.Standard", "ShippingKinds.Overnight", "ShippingKinds.TwoDay" }));
        Assert.That(members.Select(m => m.Container), Is.All.EqualTo("ShippingKinds"));
    }

    [Test]
    public async Task ListAll_EnumMemberBareNameFilter_StillFindsQualifiedRowAsync()
    {
        var bare = await _impl.ListAll(reason: "test message", kind: ListAllKind.enumMember, nameFilter: "Overnight", exactMatch: true);

        Assert.That(bare.IsError, Is.False);
        var bareEntries = EntriesOf(bare);
        Assert.That(bareEntries, Has.Count.EqualTo(1));
        Assert.That(bareEntries[0].Name, Is.EqualTo("ShippingKinds.Overnight"));

        var qualified = await _impl.ListAll(reason: "test message", kind: ListAllKind.enumMember, nameFilter: "ShippingKinds.Overnight", exactMatch: true);

        Assert.That(qualified.IsError, Is.False);
        Assert.That(EntriesOf(qualified).Select(e => e.Name).ToList(), Is.EqualTo(new[] { "ShippingKinds.Overnight" }));
    }
}

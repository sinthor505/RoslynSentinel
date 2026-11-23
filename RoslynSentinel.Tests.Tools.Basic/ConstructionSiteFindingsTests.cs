using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Search(references/callers) and InspectSymbol(blastRadius) on a constructor or a type list its reflective construction sites
/// (ActivatorUtilities.CreateInstance) and DI registrations (IServiceCollection.AddSingleton and friends) as findings, one label per kind, and never as
/// hit rows. Tool-layer tests go through SymbolRelationshipImpl.FindReferences and SymbolNavigationImpl.InspectSymbol; the matcher tests call the helper directly.
/// </summary>
[TestFixture]
public class ConstructionSiteFindingsTests
{
    private const string TestSdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\" />\n  </ItemGroup>\n</Project>\n";

    private const string DiStubSource = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
            public static class ActivatorUtilities
            {
                public static T CreateInstance<T>(System.IServiceProvider p, params object[] a) => default!;
            }
            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s) where TService : class => s;
                public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) where TService : class where TImpl : class, TService => s;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s, System.Func<System.IServiceProvider, TService> factory) where TService : class => s;
                public static IServiceCollection AddSingleton(this IServiceCollection s, System.Type t) => s;
            }
        }
        """;

    private const string WidgetSource = """
        public interface IWidget { }
        public class Widget : IWidget { public Widget(int a) { } }
        public class Quiet { public Quiet() { } }
        """;

    // Use.cs lines: 7 ActivatorUtilities.CreateInstance<Widget>, 8 AddSingleton<Widget>, 9 AddSingleton<IWidget, Widget>, 10 AddSingleton(typeof(Widget)),
    // 11 AddSingleton<IWidget>(factory lambda that does `new Widget(1)`; Widget is not a type argument of the call), 12 new Quiet().
    private const string UseSource = """
        using Microsoft.Extensions.DependencyInjection;

        public class Use
        {
            public void Build(System.IServiceProvider sp, IServiceCollection services)
            {
                var w = ActivatorUtilities.CreateInstance<Widget>(sp, 1);
                services.AddSingleton<Widget>();
                services.AddSingleton<IWidget, Widget>();
                services.AddSingleton(typeof(Widget));
                services.AddSingleton<IWidget>(sp2 => new Widget(1));
                var q = new Quiet();
            }
        }
        """;

    private const string WidgetCtorSnippet = "public Widget(int a)";
    private const string QuietCtorSnippet = "public Quiet()";

    private string _dir = string.Empty;
    private string _widgetPath = string.Empty;
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolRelationshipImpl _relationships = null!;
    private SymbolNavigationImpl _navigation = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ConstructionSiteFindingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "SiteProj"));
        var csprojPath = Path.Combine(_dir, "SiteProj", "SiteProj.csproj");
        File.WriteAllText(csprojPath, TestSdkCsproj);
        _widgetPath = Path.Combine(_dir, "SiteProj", "Widget.cs");

        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var navigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _relationships = Battery.Basic.TestTools.SymbolRelationshipImpl(
            Battery.Basic.TestEngines.DiscoveryEngine(_workspaceManager), navigationEngine, _workspaceManager, NullLogger<SymbolRelationshipImpl>.Instance);
        _navigation = new SymbolNavigationImpl(
            navigationEngine, Battery.Basic.TestEngines.ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance), _workspaceManager, NullLogger<SymbolNavigationImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "SiteProj",
            csprojPath,
            [
                ("Di.cs", DiStubSource, Path.Combine(_dir, "SiteProj", "Di.cs")),
                ("Widget.cs", WidgetSource, _widgetPath),
                ("Use.cs", UseSource, Path.Combine(_dir, "SiteProj", "Use.cs"))
            ]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static List<int> LinesOf(IEnumerable<Finding>? findings, string label) =>
        (findings ?? [])
            .Where(f => f.Message.StartsWith(label + ": Use.cs:", StringComparison.Ordinal))
            .Select(f => int.Parse(f.Message[(label.Length + ": Use.cs:".Length)..].Split(' ')[0]))
            .OrderBy(l => l)
            .ToList();

    private async Task<INamedTypeSymbol> TypeAsync(string name)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var compilation = await solution.Projects.Single().GetCompilationAsync();
        return compilation!.GetTypeByMetadataName(name)!;
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task SearchCallers_OnConstructor_ListsBothKindsAsFindingsAndNotAsHits()
    {
        var result = await _relationships.FindReferences("construction sites on the Widget ctor", "Widget", FindReferencesKind.callers, _widgetPath, WidgetCtorSnippet);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(LinesOf(result.Findings, "Reflective construction"), Is.EqualTo(new[] { 7 }), "ActivatorUtilities.CreateInstance<Widget> is the one reflective site");
        Assert.That(LinesOf(result.Findings, "DI registration"), Is.EqualTo(new[] { 8, 9, 10 }), "AddSingleton<Widget>, AddSingleton<IWidget, Widget> and AddSingleton(typeof(Widget)); the factory lambda on line 11 does not name Widget as a type argument");
        var hits = ((CallersSearchResult)result.SuccessData!).Callers;
        Assert.That(hits.Select(c => c.Line).ToList(), Is.EqualTo(new[] { 11 }), "The only call that binds to the constructor is the new Widget(1) in the lambda; no site is a hit row");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task SearchReferences_OnConstructor_ListsBothKindsAsFindingsAndNotAsHits()
    {
        var result = await _relationships.FindReferences("construction sites on the Widget ctor, all kind", "Widget", FindReferencesKind.all, _widgetPath, WidgetCtorSnippet);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(LinesOf(result.Findings, "Reflective construction"), Is.EqualTo(new[] { 7 }));
        Assert.That(LinesOf(result.Findings, "DI registration"), Is.EqualTo(new[] { 8, 9, 10 }));
        Assert.That(((ReferencesSearchResult)result.SuccessData!).Callers.Select(c => c.Line).ToList(), Is.EqualTo(new[] { 11 }));
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task SearchReferences_OnType_ListsBothKindsAsFindings()
    {
        var result = await _relationships.FindReferences("construction sites on the Widget type", "Widget", FindReferencesKind.all);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(LinesOf(result.Findings, "Reflective construction"), Is.EqualTo(new[] { 7 }));
        Assert.That(LinesOf(result.Findings, "DI registration"), Is.EqualTo(new[] { 8, 9, 10 }));
        Assert.That(result.Findings!.All(f => f.Severity == FindingSeverity.Info), Is.True, "Construction sites are leads, not cautions");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task InspectSymbolBlastRadius_OnConstructor_ListsBothKindsAsFindings()
    {
        var result = await _navigation.InspectSymbol("blast radius of the Widget ctor", _widgetPath, WidgetCtorSnippet, InspectSymbolAspect.blastRadius);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(LinesOf(result.Findings, "Reflective construction"), Is.EqualTo(new[] { 7 }));
        Assert.That(LinesOf(result.Findings, "DI registration"), Is.EqualTo(new[] { 8, 9, 10 }));
        var report = (ImpactReport)result.SuccessData!;
        Assert.That(report.References.Where(r => r.filePath.ToString().EndsWith("Use.cs", StringComparison.Ordinal)).Select(r => r.Line).ToList(), Is.EqualTo(new[] { 11 }), "No site is a reference row");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task InspectSymbolBlastRadius_OnConstructorWithoutSites_HasNoFindings()
    {
        var result = await _navigation.InspectSymbol("blast radius of the Quiet ctor", _widgetPath, QuietCtorSnippet, InspectSymbolAspect.blastRadius);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.Findings ?? [], Is.Empty, "Quiet is constructed only by a plain new expression");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task SearchCallers_OnConstructorWithoutSites_HasNoFindings()
    {
        var result = await _relationships.FindReferences("construction sites on the Quiet ctor", "Quiet", FindReferencesKind.callers, _widgetPath, QuietCtorSnippet);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.Findings ?? [], Is.Empty);
        Assert.That(((CallersSearchResult)result.SuccessData!).Callers.Select(c => c.Line).ToList(), Is.EqualTo(new[] { 12 }), "The new Quiet() stays an ordinary hit");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task FindDiRegistrationSites_ListsEveryPositionOfTheTypeArgument_AndTheInterfaceToo()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);

        var widgetSites = await ConstructionSiteFinder.FindDiRegistrationSitesAsync(solution, await TypeAsync("Widget"), CancellationToken.None);
        var widgetInterfaceSites = await ConstructionSiteFinder.FindDiRegistrationSitesAsync(solution, await TypeAsync("IWidget"), CancellationToken.None);

        Assert.That(widgetSites.Select(s => s.LineNumber).ToList(), Is.EqualTo(new[] { 8, 9, 10 }));
        Assert.That(widgetSites.All(s => s.Reason.StartsWith("DI registration: AddSingleton names 'Widget'", StringComparison.Ordinal)), Is.True, widgetSites.FirstOrDefault()?.Reason);
        Assert.That(widgetInterfaceSites.Select(s => s.LineNumber).ToList(), Is.EqualTo(new[] { 9, 11 }), "AddSingleton<IWidget, Widget> and AddSingleton<IWidget>(factory) name the interface as a type argument");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task ForSymbol_OnMethodOrInterface_IsEmpty_AndOnConstructorResolvesToItsType()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var widget = await TypeAsync("Widget");
        var objectToString = widget.BaseType!.GetMembers("ToString").First();

        Assert.That(await ConstructionSiteFindings.ForSymbolAsync(solution, await TypeAsync("IWidget"), "Test", CancellationToken.None), Is.Empty, "An interface is not a constructed type");
        Assert.That(await ConstructionSiteFindings.ForSymbolAsync(solution, objectToString, "Test", CancellationToken.None), Is.Empty, "A non-constructor method yields nothing");
        Assert.That((await ConstructionSiteFindings.ForSymbolAsync(solution, widget.Constructors.Single(), "Test", CancellationToken.None)).Count, Is.EqualTo(4), "The constructor resolves to its type: one reflective and three DI findings");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task ForSymbol_WithExcludedFile_DropsThoseSites()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var widget = await TypeAsync("Widget");

        var unfiltered = await ConstructionSiteFindings.ForSymbolAsync(solution, widget, "Search", CancellationToken.None);
        var filtered = await ConstructionSiteFindings.ForSymbolAsync(solution, widget, "Search", CancellationToken.None, path => path.EndsWith("Use.cs", StringComparison.Ordinal));

        Assert.That(unfiltered, Is.Not.Empty, "Use.cs holds the reflective and DI sites for Widget");
        Assert.That(filtered, Is.Empty, "Every Widget site is in Use.cs, so the predicate drops all of them before the per-kind cap");
    }

    [Test]
    [Category("ConstructionSiteFindings")]
    public async Task SearchCallers_IncludeTestsFalse_DropsFindingsInTestProjects()
    {
        var hidden = await _relationships.FindReferences("construction sites hidden by includeTests false", "Widget", FindReferencesKind.callers, filePath: null, includeTests: false);
        var shown = await _relationships.FindReferences("construction sites shown by includeTests true", "Widget", FindReferencesKind.callers, filePath: null, includeTests: true);

        Assert.That(hidden.IsError, Is.False, hidden.ErrorData?.Message);
        Assert.That(shown.IsError, Is.False, shown.ErrorData?.Message);
        Assert.That(LinesOf(hidden.Findings, "Reflective construction"), Is.Empty, "SiteProj is a test project, so its reflective site is dropped when includeTests is false");
        Assert.That(LinesOf(hidden.Findings, "DI registration"), Is.Empty, "SiteProj is a test project, so its DI sites are dropped when includeTests is false");
        Assert.That(LinesOf(shown.Findings, "Reflective construction"), Is.EqualTo(new[] { 7 }), "Same expectation as SearchReferences_OnType_ListsBothKindsAsFindings, so the test is not vacuous");
        Assert.That(LinesOf(shown.Findings, "DI registration"), Is.EqualTo(new[] { 8, 9, 10 }), "Same expectation as SearchReferences_OnType_ListsBothKindsAsFindings, so the test is not vacuous");
    }
}

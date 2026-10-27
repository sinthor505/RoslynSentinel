using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for SemanticFindReplace operation replaceConstruction: rewrite object creations of a type
/// into calls to a static factory method, skipping sites the factory cannot take.
/// </summary>
[TestFixture]
[Category("SemanticFindReplaceTools")] // sentinel:auto-category
public class SemanticFindReplaceConstructionTests
{
    private const string WidgetsPath = "ContosoOrders.Core/Cf/Widgets.cs";
    private const string ConsumersPath = "ContosoOrders.Core/Cf/Consumers.cs";
    private const string TestConsumersPath = "ContosoOrders.Tests/CfTests/TestConsumers.cs";

    private const string WidgetType = "T:Cf.Widget";
    private const string CreateFactory = "M:Cf.WidgetFactory.Create(System.Int32,System.String)";
    private const string CreateOneFactory = "M:Cf.WidgetFactory.CreateOne(System.Int32)";
    private const string CreateObjectFactory = "M:Cf.WidgetFactory.CreateObject(System.Int32,System.String)";

    private const string WidgetsSource = """
        namespace Cf;

        public class Widget
        {
            public Widget(int a, string b) { A = a; B = b; }
            public Widget(int a) : this(a, "default") { }
            public int A { get; }
            public string B { get; }
            public int Extra { get; set; }
        }

        public class Gadget
        {
            public int N { get; set; }
        }

        public static class WidgetFactory
        {
            public static Widget Create(int a, string b) => new Widget(a, b);
            public static Widget CreateOne(int a) => new Widget(a);
            public static object CreateObject(int a, string b) => new Widget(a, b);
        }

        public static class GadgetFactory
        {
            public static Gadget Create() => new Gadget();
        }
        """;

    private const string ConsumersSource = """
        namespace Cf;

        public class Consumers
        {
            public Widget Plain() => new Widget(1, "a");
            public Widget Named() => new Widget(b: "n", a: 2);
            public Widget Target()
            {
                Widget w = new(3, "t");
                return w;
            }
            public Widget Shape() => new Widget(4);
            public Widget Init() => new Widget(5, "i") { Extra = 9 };
            public Gadget MakeGadget() => new Gadget() { N = 1 };
        }
        """;

    private const string TestConsumersSource = """
        namespace CfTests;

        public class TestConsumers
        {
            public Cf.Widget Make() => new Cf.Widget(6, "tp");
        }
        """;

    private IWorkspaceManager _workspaceManager;
    private ValidationEngine _validationEngine;
    private SemanticFindReplaceTools _tool;
    private TestSolutionFixture? _fixture;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _validationEngine = new ValidationEngine(_workspaceManager);
        _tool = new SemanticFindReplaceTools(
            new SemanticReplaceEngine(_workspaceManager),
            _validationEngine,
            _workspaceManager,
            NullLogger<SemanticFindReplaceTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
        _fixture = null;
    }

    private async Task SeedAsync()
    {
        _fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
        await _fixture.AddFileToSolution(_workspaceManager, WidgetsPath, WidgetsSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, ConsumersPath, ConsumersSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, TestConsumersPath, TestConsumersSource, reloadSolution: true);
    }

    private string ReadSeeded(string relativePath) =>
        File.ReadAllText(Path.Combine(_fixture!.SolutionDirectory, relativePath));

    private Task<SentinelCallToolResult<object>> RunAsync(
        SemanticReplaceMode mode, string symbol, string factory, string? projectFilter = null) =>
        _tool.SemanticFindReplaceTool(
            new ToolCallReason("testing replaceConstruction"),
            SemanticReplaceOperation.replaceConstruction,
            symbol,
            mode,
            factory: factory,
            projectFilter: projectFilter);

    private static JsonElement Data(SentinelCallToolResult<object> result)
    {
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return JsonSerializer.SerializeToElement(result.SuccessData);
    }

    private static JsonElement FindSite(JsonElement data, string fileName, string before)
    {
        foreach (var site in data.GetProperty("sites").EnumerateArray())
        {
            if (Path.GetFileName(site.GetProperty("FilePath").GetString()) == fileName
                && site.GetProperty("Before").GetString() == before)
            {
                return site;
            }
        }

        Assert.Fail($"No site in {fileName} with before text '{before}'. Sites: {data.GetProperty("sites")}");
        return default;
    }

    private void AssertSeedUnchanged()
    {
        Assert.That(ReadSeeded(WidgetsPath), Is.EqualTo(WidgetsSource));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource));
        Assert.That(ReadSeeded(TestConsumersPath), Is.EqualTo(TestConsumersSource));
    }

    [Test]
    [Description("Preview lists rewritten and skipped sites with reasons and writes nothing")]
    public async Task ReplaceConstruction_Preview_ListsRewrittenAndSkippedSitesWithoutWriting()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.preview, WidgetType, CreateFactory));

        Assert.That(data.GetProperty("mode").GetString(), Is.EqualTo("preview"));
        Assert.That(data.GetProperty("rewritten").GetInt32(), Is.EqualTo(4));
        Assert.That(data.GetProperty("skipped").GetInt32(), Is.EqualTo(5));

        // Plain, named-argument, target-typed and cross-namespace sites are rewritten.
        var plain = FindSite(data, "Consumers.cs", "new Widget(1, \"a\")");
        Assert.That(plain.GetProperty("After").GetString(), Is.EqualTo("WidgetFactory.Create(1, \"a\")"));
        Assert.That(plain.GetProperty("UnsupportedReason").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(plain.GetProperty("Line").GetInt32(), Is.GreaterThan(0));

        var named = FindSite(data, "Consumers.cs", "new Widget(b: \"n\", a: 2)");
        Assert.That(named.GetProperty("After").GetString(), Is.EqualTo("WidgetFactory.Create(b: \"n\", a: 2)"));

        var target = FindSite(data, "Consumers.cs", "new(3, \"t\")");
        Assert.That(target.GetProperty("After").GetString(), Is.EqualTo("WidgetFactory.Create(3, \"t\")"));

        var otherNamespace = FindSite(data, "TestConsumers.cs", "new Cf.Widget(6, \"tp\")");
        Assert.That(otherNamespace.GetProperty("After").GetString(), Is.EqualTo("Cf.WidgetFactory.Create(6, \"tp\")"));

        // Shape mismatch, initializer and the factory's own type are skipped with a reason.
        var shape = FindSite(data, "Consumers.cs", "new Widget(4)");
        Assert.That(shape.GetProperty("UnsupportedReason").GetString(), Does.Contain("argument shape does not match"));
        Assert.That(shape.GetProperty("After").GetString(), Is.EqualTo("new Widget(4)"));

        var init = FindSite(data, "Consumers.cs", "new Widget(5, \"i\") { Extra = 9 }");
        Assert.That(init.GetProperty("UnsupportedReason").GetString(), Does.Contain("initializer"));

        var inFactory = FindSite(data, "Widgets.cs", "new Widget(a, b)");
        Assert.That(inFactory.GetProperty("UnsupportedReason").GetString(), Does.Contain("factory's containing type"));

        AssertSeedUnchanged();
    }

    [Test]
    [Description("Apply rewrites matching sites, leaves skipped sites and the factory body untouched")]
    public async Task ReplaceConstruction_Apply_RewritesMatchingSitesAndLeavesSkippedOnes()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory);
        var data = Data(result);

        Assert.That(data.GetProperty("mode").GetString(), Is.EqualTo("apply"));
        Assert.That(data.GetProperty("changeId").GetString(), Is.Not.Empty);
        Assert.That(data.GetProperty("rewritten").GetInt32(), Is.EqualTo(4));
        Assert.That(data.GetProperty("skipped").GetInt32(), Is.EqualTo(5));
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(2));

        var expectedConsumers = ConsumersSource
            .Replace("new Widget(1, \"a\")", "WidgetFactory.Create(1, \"a\")")
            .Replace("new Widget(b: \"n\", a: 2)", "WidgetFactory.Create(b: \"n\", a: 2)")
            .Replace("new(3, \"t\")", "WidgetFactory.Create(3, \"t\")");
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(expectedConsumers));
        Assert.That(ReadSeeded(TestConsumersPath), Is.EqualTo(
            TestConsumersSource.Replace("new Cf.Widget(6, \"tp\")", "Cf.WidgetFactory.Create(6, \"tp\")")));
        Assert.That(ReadSeeded(WidgetsPath), Is.EqualTo(WidgetsSource), "The factory's own type is never rewritten");
    }

    [Test]
    [Description("Apply returns a changeId that UndoLastApply reverts")]
    public async Task ReplaceConstruction_ApplyThenUndoLastApply_RestoresOriginalFiles()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory));
        var changeId = data.GetProperty("changeId").GetString()!;
        Assert.That(ReadSeeded(ConsumersPath), Is.Not.EqualTo(ConsumersSource));

        var editImpl = new WorkspaceFileEditImpl(
            _workspaceManager,
            new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            NullLogger.Instance,
            _validationEngine,
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
        var undo = await editImpl.UndoLastApply(new ToolCallReason("testing undo of replaceConstruction"), changeId);

        Assert.That(undo.IsError, Is.False, undo.ErrorData?.Message);
        AssertSeedUnchanged();
    }

    [Test]
    [Description("A constructor docCommentId restricts matching to that overload")]
    public async Task ReplaceConstruction_ConstructorSymbol_OnlyRewritesThatOverload()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, "M:Cf.Widget.#ctor(System.Int32)", CreateOneFactory));

        Assert.That(data.GetProperty("rewritten").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("skipped").GetInt32(), Is.EqualTo(1), "The creation inside CreateOne itself is skipped");
        var expectedConsumers = ConsumersSource.Replace("new Widget(4)", "WidgetFactory.CreateOne(4)");
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(expectedConsumers));
        Assert.That(ReadSeeded(ConsumersPath), Does.Contain("new Widget(1, \"a\")"), "Other overloads are untouched");
        Assert.That(ReadSeeded(TestConsumersPath), Is.EqualTo(TestConsumersSource));
    }

    [Test]
    [Description("projectFilter limits rewriting to the named project")]
    public async Task ReplaceConstruction_ProjectFilter_OnlyRewritesNamedProject()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory, "ContosoOrders.Tests"));

        Assert.That(data.GetProperty("rewritten").GetInt32(), Is.EqualTo(1));
        Assert.That(data.GetProperty("skipped").GetInt32(), Is.EqualTo(0));
        Assert.That(ReadSeeded(TestConsumersPath), Is.EqualTo(
            TestConsumersSource.Replace("new Cf.Widget(6, \"tp\")", "Cf.WidgetFactory.Create(6, \"tp\")")));
        Assert.That(ReadSeeded(ConsumersPath), Is.EqualTo(ConsumersSource), "Sites in other projects are untouched");
        Assert.That(ReadSeeded(WidgetsPath), Is.EqualTo(WidgetsSource));
    }

    [Test]
    [Description("An unknown projectFilter is refused with an InvalidArgument naming the parameter")]
    public async Task ReplaceConstruction_UnknownProjectFilter_ReturnsInvalidArgument()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateFactory, "NoSuchProject");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("projectFilter"));
        Assert.That(result.ErrorData.Message, Does.Contain("ContosoOrders.Core"));
        AssertSeedUnchanged();
    }

    [Test]
    [Description("A factory whose return type is not assignable to the constructed type refuses the whole operation")]
    public async Task ReplaceConstruction_FactoryReturnTypeNotAssignable_RefusesAndWritesNothing()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.apply, WidgetType, CreateObjectFactory);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData.Message, Does.Contain("factory:"));
        Assert.That(result.ErrorData.Message, Does.Contain("not implicitly assignable"));
        Assert.That(result.ErrorData.Message, Does.Contain(CreateFactory), "The message offers a correct factory id");
        AssertSeedUnchanged();
    }

    [Test]
    [Description("Apply with nothing rewritable writes nothing and issues no changeId")]
    public async Task ReplaceConstruction_ApplyWithNothingToRewrite_ReportsSkippedSitesWithoutChangeId()
    {
        await SeedAsync();

        var data = Data(await RunAsync(SemanticReplaceMode.apply, "T:Cf.Gadget", "M:Cf.GadgetFactory.Create"));

        Assert.That(data.GetProperty("rewritten").GetInt32(), Is.EqualTo(0));
        Assert.That(data.GetProperty("skipped").GetInt32(), Is.EqualTo(2));
        Assert.That(data.GetProperty("filesChanged").GetInt32(), Is.EqualTo(0));
        Assert.That(data.TryGetProperty("changeId", out _), Is.False);
        AssertSeedUnchanged();
    }

    [Test]
    [Description("An unknown symbol returns NotFound naming the symbol parameter")]
    public async Task ReplaceConstruction_UnknownSymbol_ReturnsNotFound()
    {
        await SeedAsync();

        var result = await RunAsync(SemanticReplaceMode.preview, "T:Cf.Nope", CreateFactory);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("symbol:"));
    }

    [Test]
    [Description("Missing per-operation parameters return InvalidArgument naming the parameter")]
    public async Task SemanticFindReplace_MissingOperationParameter_ReturnsInvalidArgument()
    {
        var noFactory = await _tool.SemanticFindReplaceTool(
            new ToolCallReason("testing missing factory"),
            SemanticReplaceOperation.replaceConstruction,
            WidgetType,
            SemanticReplaceMode.preview);
        Assert.That(noFactory.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(noFactory.ErrorData.Message, Does.Contain("factory"));

        var noNewName = await _tool.SemanticFindReplaceTool(
            new ToolCallReason("testing missing newName"),
            SemanticReplaceOperation.invertBoolean,
            "P:C.IsSuccess",
            SemanticReplaceMode.preview);
        Assert.That(noNewName.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(noNewName.ErrorData.Message, Does.Contain("newName"));
    }
}

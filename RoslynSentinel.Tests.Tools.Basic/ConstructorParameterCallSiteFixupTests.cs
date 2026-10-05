using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// ConstructorParameter(add, callSiteFixups): the new argument is added at every call site of the
/// constructor in one atomic change set. Engine-level tests exercise
/// MemberRefactoringEngine.AddConstructorParameterWithCallSitesAsync; the ConstructorParameterTool_*
/// tests go through the tool surface (error codes, dryRun, compile gate).
/// </summary>
[TestFixture]
public class ConstructorParameterCallSiteFixupTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private BasicRefactoringEngine _basicRefactoringEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;

    private const string SvcSource = """
namespace TestProj;
public class Config { }
public class Svc
{
    public Svc(int a) { }
}
""";

    private const string SimpleCaller = """
namespace TestProj;
public class Caller
{
    public Svc Make()
    {
        return new Svc(1);
    }
}
""";

    private const string OtherCaller = "namespace TestProj;\npublic class Other { public Svc Make() => new Svc(3); }";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _basicRefactoringEngine = new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, new SentinelConfiguration());
        _memberRefactoringEngine = new MemberRefactoringEngine(_workspaceManager, _symbolNavigationEngine, new ValidationEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetFiles(params (string name, string content)[] files) =>
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", files));

    private static int LineOf(string source, string needle) =>
        source.Split('\n').Select((text, index) => (text, index)).First(t => t.text.Contains(needle)).index + 1;

    private Task<AddConstructorParameterCascadeResult> CascadeAsync(string className, Dictionary<string, string> fixups, string file = "Svc.cs", string? defaultValue = null) =>
        _memberRefactoringEngine.AddConstructorParameterWithCallSitesAsync(file, className, "config", "Config", fixups, defaultValue: defaultValue);

    private static string ChangeFor(AddConstructorParameterCascadeResult result, string fileName) =>
        result.Changes.Single(kv => Path.GetFileName(kv.Key.ToString()) == fileName).Value;

    private RefactoringSignatureTools CreateSignatureTools() =>
        new(new RefactoringSignatureImpl(
            _basicRefactoringEngine,
            _memberRefactoringEngine,
            _workspaceManager,
            new ValidationEngine(_workspaceManager),
            _symbolNavigationEngine,
            NullLogger<RefactoringSignatureImpl>.Instance));

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_ExactKey_AddsArgumentAtThatSite()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller));

        var result = await CascadeAsync("Svc", new() { [$"Caller.cs:{LineOf(SimpleCaller, "new Svc(1)")}"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.UnresolvedSites, Is.Empty);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        Assert.That(ChangeFor(result, "Caller.cs"), Does.Contain("new Svc(1, cfg)"));
        Assert.That(ChangeFor(result, "Svc.cs"), Does.Contain("Config config").And.Contain("_config = config"));
        Assert.That(result.Changes, Has.Count.EqualTo(2), "Class file plus the one caller file.");
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_FileWildcard_AddsArgumentAtEverySiteInFile()
    {
        var caller = """
namespace TestProj;
public class Caller
{
    public void Make()
    {
        var a = new Svc(1);
        var b = new Svc(2);
    }
}
""";
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", caller));

        var result = await CascadeAsync("Svc", new() { ["Caller.cs:*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(2));
        Assert.That(ChangeFor(result, "Caller.cs"), Does.Contain("new Svc(1, cfg)").And.Contain("new Svc(2, cfg)"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_GlobalWildcard_AddsArgumentAcrossFiles()
    {
        SetFiles(("Svc.cs", SvcSource),
            ("A.cs", "namespace TestProj;\npublic class A { public Svc Make() => new Svc(1); }"),
            ("B.cs", "namespace TestProj;\npublic class B { public Svc Make() => new Svc(2); }"));

        var result = await CascadeAsync("Svc", new() { ["*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(2));
        Assert.That(ChangeFor(result, "A.cs"), Does.Contain("new Svc(1, cfg)"));
        Assert.That(ChangeFor(result, "B.cs"), Does.Contain("new Svc(2, cfg)"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_Precedence_ExactBeatsFileBeatsGlobal()
    {
        var caller = """
namespace TestProj;
public class Caller
{
    public void Make()
    {
        var a = new Svc(1);
        var b = new Svc(2);
    }
}
""";
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", caller), ("Other.cs", OtherCaller));

        var result = await CascadeAsync("Svc", new()
        {
            [$"Caller.cs:{LineOf(caller, "new Svc(1)")}"] = "exactCfg",
            ["Caller.cs:*"] = "fileCfg",
            ["*"] = "globalCfg",
        });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(ChangeFor(result, "Caller.cs"), Does.Contain("new Svc(1, exactCfg)").And.Contain("new Svc(2, fileCfg)"));
        Assert.That(ChangeFor(result, "Other.cs"), Does.Contain("new Svc(3, globalCfg)"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_TrailingOptionalsOmitted_UsesNamedArgument()
    {
        var svc = """
namespace TestProj;
public class Config { }
public class Svc
{
    public Svc(int a, int b = 5) { }
}
""";
        var caller = """
namespace TestProj;
public class Caller
{
    public void Make()
    {
        var omitted = new Svc(1);
        var full = new Svc(1, 2);
        var named = new Svc(a: 1, b: 2);
    }
}
""";
        SetFiles(("Svc.cs", svc), ("Caller.cs", caller));

        // defaultValue keeps the new parameter legal after the existing optional one.
        var result = await CascadeAsync("Svc", new() { ["*"] = "cfg" }, defaultValue: "null");

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        var text = ChangeFor(result, "Caller.cs");
        Assert.That(text, Does.Contain("new Svc(1, config: cfg)"), "Omitted trailing optional: a positional argument would bind to 'b'.");
        Assert.That(text, Does.Contain("new Svc(1, 2, cfg)"), "All parameters supplied: positional is correct.");
        Assert.That(text, Does.Contain("new Svc(a: 1, b: 2, config: cfg)"), "Site already uses named arguments.");
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_TargetTypedNew_AddsArgument()
    {
        var caller = """
namespace TestProj;
public class Caller
{
    public Svc Make()
    {
        Svc svc = new(1);
        return svc;
    }
}
""";
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", caller));

        var result = await CascadeAsync("Svc", new() { ["*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        Assert.That(ChangeFor(result, "Caller.cs"), Does.Contain("new(1, cfg)"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_DerivedBaseInitializer_AddsArgument()
    {
        var derived = """
namespace TestProj;
public class Derived : Svc
{
    public Derived() : base(1) { }
}
""";
        SetFiles(("Svc.cs", SvcSource), ("Derived.cs", derived));

        var result = await CascadeAsync("Svc", new() { ["*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        Assert.That(ChangeFor(result, "Derived.cs"), Does.Contain(": base(1, cfg)"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_ThisInitializerInSameFile_MergesIntoOneText()
    {
        var svc = """
namespace TestProj;
public class Config { }
public class Svc
{
    public Svc(int a) { }
    public Svc() : this(7) { }
}
""";
        SetFiles(("Svc.cs", svc));

        var result = await CascadeAsync("Svc", new() { ["*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.Changes, Has.Count.EqualTo(1), "The this(...) site and the class edit share one file, so one text.");
        var text = ChangeFor(result, "Svc.cs");
        Assert.That(text, Does.Contain(": this(7, cfg)"));
        Assert.That(text, Does.Contain("Config config").And.Contain("_config = config"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_NestedCreation_EditsTheInnerConstructorOnly()
    {
        var types = """
namespace TestProj;
public class Config { }
public class Inner
{
    public Inner(int x) { }
}
public class Outer
{
    public Outer(Inner inner) { }
}
""";
        var caller = """
namespace TestProj;
public class Caller
{
    public Outer Make()
    {
        return new Outer(new Inner(1));
    }
}
""";
        SetFiles(("Types.cs", types), ("Caller.cs", caller));

        var result = await CascadeAsync("Inner", new() { ["*"] = "cfg" }, file: "Types.cs");

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        Assert.That(ChangeFor(result, "Caller.cs"), Does.Contain("new Outer(new Inner(1, cfg))"),
            "The argument belongs to Inner's argument list, not the enclosing Outer creation.");
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_UnresolvedWithoutDefault_ReportsSiteKeysAndChangesNothing()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller), ("Other.cs", OtherCaller));

        var result = await CascadeAsync("Svc", new() { ["Other.cs:*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Null);
        Assert.That(result.UnresolvedSites, Has.Count.EqualTo(1));
        Assert.That(result.UnresolvedSites[0].FilePath, Does.EndWith("Caller.cs"));
        Assert.That(result.UnresolvedSites[0].Line, Is.EqualTo(LineOf(SimpleCaller, "new Svc(1)")));
        Assert.That(result.Changes, Is.Empty, "Fail closed: no partial edit when any site is unresolved.");
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_UnresolvedWithDefaultValue_LeavesSiteOnDefaultAndSucceeds()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller), ("Other.cs", OtherCaller));

        var result = await CascadeAsync("Svc", new() { ["Other.cs:*"] = "cfg" }, defaultValue: "null");

        Assert.That(result.InvalidArgumentMessage, Is.Null, result.ClassEdit.Message);
        Assert.That(result.UnresolvedSites, Is.Empty);
        Assert.That(result.CallSitesUpdated, Is.EqualTo(1));
        Assert.That(result.CallSitesLeftToDefault, Is.EqualTo(1));
        Assert.That(ChangeFor(result, "Other.cs"), Does.Contain("new Svc(3, cfg)"));
        Assert.That(result.Changes.Keys.Select(k => Path.GetFileName(k.ToString())), Does.Not.Contain("Caller.cs"), "The defaulted site must not be rewritten.");
        Assert.That(ChangeFor(result, "Svc.cs"), Does.Contain("Config config = null"));
    }

    [Test]
    public async Task AddConstructorParameter_CallSiteFixups_ExactKeyMatchingNoSite_IsInvalidArgumentListingRealKeys()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller));
        var realLine = LineOf(SimpleCaller, "new Svc(1)");

        var result = await CascadeAsync("Svc", new() { [$"Caller.cs:{realLine + 40}"] = "cfg", ["*"] = "cfg" });

        Assert.That(result.InvalidArgumentMessage, Is.Not.Null);
        Assert.That(result.InvalidArgumentMessage, Does.Contain($"Caller.cs:{realLine + 40}"));
        Assert.That(result.InvalidArgumentMessage, Does.Contain($"Caller.cs:{realLine}"), "The error must name the real site keys so the caller can recover.");
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    public async Task ConstructorParameterTool_CallSiteFixups_WithRemove_IsInvalidArgument()
    {
        SetFiles(("Svc.cs", SvcSource));
        var tools = CreateSignatureTools();

        var result = await tools.ConstructorParameter(reason: "test", "Svc.cs", AddRemoveViewAction.remove, "Svc", "config", callSiteFixups: new() { ["*"] = "cfg" });

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ConstructorParameterTool_CallSiteFixups_UnresolvedSites_ReturnsUnresolvedCallSitesWithKeys()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller), ("Other.cs", OtherCaller));
        var tools = CreateSignatureTools();

        var result = await tools.ConstructorParameter(reason: "test", "Svc.cs", AddRemoveViewAction.add, "Svc", "config", "Config", dryRun: true, callSiteFixups: new() { ["Other.cs:*"] = "cfg" });

        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.UnresolvedCallSites));
        Assert.That(result.ErrorData?.Detail, Does.Contain($"Caller.cs:{LineOf(SimpleCaller, "new Svc(1)")}"));
    }

    [Test]
    public async Task ConstructorParameterTool_CallSiteFixups_DryRun_PassesCompileGateAndWritesNothing()
    {
        SetFiles(("Svc.cs", SvcSource), ("Caller.cs", SimpleCaller));
        var tools = CreateSignatureTools();

        var result = await tools.ConstructorParameter(reason: "test", "Svc.cs", AddRemoveViewAction.add, "Svc", "config", "Config", dryRun: true, callSiteFixups: new() { ["*"] = "new Config()" });

        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message + " " + result.ErrorData?.Detail);
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var documents = solution.Projects.SelectMany(p => p.Documents).ToList();
        var callerText = (await documents.First(d => d.Name == "Caller.cs").GetTextAsync()).ToString();
        var svcText = (await documents.First(d => d.Name == "Svc.cs").GetTextAsync()).ToString();
        Assert.That(callerText, Does.Not.Contain("new Config()"), "dryRun must not modify the workspace.");
        Assert.That(svcText, Does.Not.Contain("Config config"), "dryRun must not modify the workspace.");
    }
}

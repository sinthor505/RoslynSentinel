using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for the new code-editing engine methods in RefactoringEngine:
/// AddMemberAsync (record/struct support), AddUsingDirectiveAsync, AddEnumValueAsync,
/// InsertMemberAfterAsync, InsertMemberBeforeAsync, AddAttributeAsync, AddBaseTypeAsync.
/// </summary>
[TestFixture]
public class CodeEditingTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private RefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance,
            _workspaceManager,
            new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // AddMemberAsync — record and struct support
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddMember_ToRecord_InsertsMethod()
    {
        SetSource(@"
public record Person(string Name, int Age);
", "Person.cs");

        var result = await _engine.AddMemberAsync("Person.cs", "Person", "public string Greet() => $\"Hello {Name}\";");

        Assert.That(result, Does.Contain("Greet"), "Method should be added to record.");
        Assert.That(result, Does.Contain("Person"), "Record declaration should still be present.");
    }

    [Test]
    public async Task AddMember_ToStruct_InsertsMethod()
    {
        SetSource(@"
public struct Point
{
    public int X;
    public int Y;
}
", "Point.cs");

        var result = await _engine.AddMemberAsync("Point.cs", "Point", "public double Length() => Math.Sqrt(X * X + Y * Y);");

        Assert.That(result, Does.Contain("Length"), "Method should be added to struct.");
        Assert.That(result, Does.Contain("Point"), "Struct declaration should still be present.");
    }

    [Test]
    public async Task AddMember_ToClass_StillWorks()
    {
        SetSource(@"
public class Animal
{
    public string Name { get; set; }
}
", "Animal.cs");

        var result = await _engine.AddMemberAsync("Animal.cs", "Animal", "public string Speak() => \"...\";");

        Assert.That(result, Does.Contain("Speak"), "Method should be added to class.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddUsingDirectiveAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddUsingDirective_AddsWhenNotPresent()
    {
        SetSource(@"
public class Foo { }
", "Foo.cs");

        var result = await _engine.AddUsingDirectiveAsync("Foo.cs", "System.Linq");

        Assert.That(result, Does.Contain("using System.Linq"), "New using directive should be present.");
    }

    [Test]
    public async Task AddUsingDirective_NoOpWhenAlreadyPresent()
    {
        SetSource(@"using System.Linq;

public class Foo { }
", "Foo.cs");

        var result = await _engine.AddUsingDirectiveAsync("Foo.cs", "System.Linq");

        // Should not duplicate
        var count = System.Text.RegularExpressions.Regex.Matches(result, "using System\\.Linq").Count;
        Assert.That(count, Is.EqualTo(1), "Duplicate using directive should not be added.");
    }

    [Test]
    public async Task AddUsingDirective_HandlesStaticUsing()
    {
        SetSource(@"
public class Calc { }
", "Calc.cs");

        var result = await _engine.AddUsingDirectiveAsync("Calc.cs", "static System.Math");

        Assert.That(result, Does.Contain("System.Math"), "Static using directive should reference the namespace.");
        Assert.That(result, Does.Contain("static"), "Static keyword should be present.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddEnumValueAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddEnumValue_AppendsValueToEnum()
    {
        SetSource(@"
public enum Color
{
    Red,
    Green
}
", "Color.cs");

        var result = await _engine.AddEnumValueAsync("Color.cs", "Color", "Blue");

        Assert.That(result, Does.Contain("Blue"), "New enum value should be present.");
        Assert.That(result, Does.Contain("Red"), "Existing values should remain.");
    }

    [Test]
    public async Task AddEnumValue_WithExplicitValue()
    {
        SetSource(@"
public enum Status
{
    Active,
    Inactive
}
", "Status.cs");

        var result = await _engine.AddEnumValueAsync("Status.cs", "Status", "Archived", explicitValue: 99);

        Assert.That(result, Does.Contain("Archived"), "New value should be present.");
        Assert.That(result, Does.Contain("99"), "Explicit integer value should be present.");
    }

    [Test]
    public async Task AddEnumValue_GracefulFallback_WhenEnumNotFound()
    {
        SetSource(@"
public class Foo { }
", "Foo.cs");

        var result = await _engine.AddEnumValueAsync("Foo.cs", "NonExistentEnum", "SomeValue");

        Assert.That(result, Does.Contain("class Foo"), "Original source should be returned unchanged.");
        Assert.That(result, Does.Not.Contain("SomeValue"), "Value should not be injected into wrong place.");
    }

    // ══════════════════════════════════════════════════════════════
    // InsertMemberAfterAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task InsertMemberAfter_InsertsAfterNamedMember()
    {
        SetSource(@"
public class Service
{
    public void Start() { }
    public void Stop() { }
}
", "Service.cs");

        var result = await _engine.InsertMemberAfterAsync("Service.cs", "Service", "Start",
            "public void Pause() { }");

        // Pause should appear between Start and Stop
        var startIdx = result.IndexOf("Start");
        var pauseIdx = result.IndexOf("Pause");
        var stopIdx = result.IndexOf("Stop");
        Assert.That(pauseIdx, Is.GreaterThan(startIdx), "Pause should come after Start.");
        Assert.That(pauseIdx, Is.LessThan(stopIdx), "Pause should come before Stop.");
    }

    [Test]
    public async Task InsertMemberAfter_AppendsWhenAfterMemberNotFound()
    {
        SetSource(@"
public class Repo
{
    public void Save() { }
}
", "Repo.cs");

        var result = await _engine.InsertMemberAfterAsync("Repo.cs", "Repo", "NonExistent",
            "public void Delete() { }");

        Assert.That(result, Does.Contain("Delete"), "Member should be appended when anchor not found.");
        Assert.That(result, Does.Contain("Save"), "Existing member should remain.");
    }

    [Test]
    public async Task InsertMemberAfter_WorksOnLastMember()
    {
        SetSource(@"
public class Widget
{
    public void Draw() { }
}
", "Widget.cs");

        var result = await _engine.InsertMemberAfterAsync("Widget.cs", "Widget", "Draw",
            "public void Resize() { }");

        var drawIdx = result.IndexOf("Draw");
        var resizeIdx = result.IndexOf("Resize");
        Assert.That(resizeIdx, Is.GreaterThan(drawIdx), "Resize should be after Draw.");
    }

    // ══════════════════════════════════════════════════════════════
    // InsertMemberBeforeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task InsertMemberBefore_InsertsBeforeNamedMember()
    {
        SetSource(@"
public class Controller
{
    public void Get() { }
    public void Post() { }
}
", "Controller.cs");

        var result = await _engine.InsertMemberBeforeAsync("Controller.cs", "Controller", "Post",
            "public void Put() { }");

        var getIdx = result.IndexOf("Get()");
        var putIdx = result.IndexOf("Put()");
        var postIdx = result.IndexOf("Post()");
        Assert.That(putIdx, Is.GreaterThan(getIdx), "Put should come after Get.");
        Assert.That(putIdx, Is.LessThan(postIdx), "Put should come before Post.");
    }

    [Test]
    public async Task InsertMemberBefore_AppendsWhenBeforeMemberNotFound()
    {
        SetSource(@"
public class Cache
{
    public void Set() { }
}
", "Cache.cs");

        var result = await _engine.InsertMemberBeforeAsync("Cache.cs", "Cache", "NonExistent",
            "public void Evict() { }");

        Assert.That(result, Does.Contain("Evict"), "Member should be appended when anchor not found.");
    }

    [Test]
    public async Task InsertMemberBefore_WorksOnFirstMember()
    {
        SetSource(@"
public class Logger
{
    public void Log() { }
    public void Flush() { }
}
", "Logger.cs");

        var result = await _engine.InsertMemberBeforeAsync("Logger.cs", "Logger", "Log",
            "public void Init() { }");

        var initIdx = result.IndexOf("Init");
        var logIdx = result.IndexOf("Log()");
        Assert.That(initIdx, Is.LessThan(logIdx), "Init should appear before Log.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddAttributeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddAttribute_ToClass_WithBrackets()
    {
        SetSource(@"
public class MyController
{
    public void Index() { }
}
", "MyController.cs");

        var result = await _engine.AddAttributeAsync("MyController.cs", "MyController", "[Serializable]");

        Assert.That(result, Does.Contain("Serializable"), "Attribute should be added to class.");
        Assert.That(result, Does.Contain("MyController"), "Class should still be present.");
    }

    [Test]
    public async Task AddAttribute_ToMethod_WithoutBrackets()
    {
        SetSource(@"
public class Api
{
    public void GetItems() { }
}
", "Api.cs");

        var result = await _engine.AddAttributeAsync("Api.cs", "GetItems", "Obsolete");

        Assert.That(result, Does.Contain("Obsolete"), "Attribute should be added to method.");
    }

    [Test]
    public async Task AddAttribute_ToClass_WithBrackets_StringArg()
    {
        SetSource(@"
public class Handler
{
    public void Handle() { }
}
", "Handler.cs");

        var result = await _engine.AddAttributeAsync("Handler.cs", "Handler", "[Description(\"My handler\")]");

        Assert.That(result, Does.Contain("Description"), "Attribute with argument should be added.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddBaseTypeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddBaseType_AddsFirstInterface()
    {
        SetSource(@"
public class Repository
{
    public void Save() { }
}
", "Repository.cs");

        var result = await _engine.AddBaseTypeAsync("Repository.cs", "Repository", "IRepository");

        Assert.That(result, Does.Contain("IRepository"), "Interface should be added to base list.");
    }

    [Test]
    public async Task AddBaseType_AddsSecondInterface()
    {
        SetSource(@"
public class Service : IService
{
    public void Run() { }
}
", "Service.cs");

        var result = await _engine.AddBaseTypeAsync("Service.cs", "Service", "IDisposable");

        Assert.That(result, Does.Contain("IService"), "First interface should still be present.");
        Assert.That(result, Does.Contain("IDisposable"), "Second interface should be added.");
    }

    [Test]
    public async Task AddBaseType_NoDuplicate_WhenAlreadyPresent()
    {
        SetSource(@"
public class Worker : IWorker
{
    public void Work() { }
}
", "Worker.cs");

        var result = await _engine.AddBaseTypeAsync("Worker.cs", "Worker", "IWorker");

        // Only one occurrence in the base list
        var count = System.Text.RegularExpressions.Regex.Matches(result, "IWorker").Count;
        Assert.That(count, Is.EqualTo(1), "IWorker should not be duplicated.");
    }
}

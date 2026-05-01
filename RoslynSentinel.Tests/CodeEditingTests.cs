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

    // ══════════════════════════════════════════════════════════════
    // RemoveAttributeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveAttribute_RemovesExistingAttribute()
    {
        SetSource(@"
[Obsolete]
public class Foo
{
    [Obsolete(""use Bar"")]
    public void DoIt() { }
}
", "Foo.cs");

        var result = await _engine.RemoveAttributeAsync("Foo.cs", "DoIt", "Obsolete");

        Assert.That(result, Does.Not.Contain("[Obsolete("), "Attribute should be removed from method.");
        Assert.That(result, Does.Contain("[Obsolete]"), "Class attribute should remain.");
    }

    [Test]
    public async Task RemoveAttribute_NoOpWhenAbsent()
    {
        SetSource(@"
public class Bar
{
    public void Run() { }
}
", "Bar.cs");

        var result = await _engine.RemoveAttributeAsync("Bar.cs", "Run", "Obsolete");

        Assert.That(result, Does.Contain("public void Run()"));
        Assert.That(result, Does.Not.Contain("[Obsolete]"));
    }

    [Test]
    public async Task RemoveAttribute_MatchesSuffixVariant()
    {
        SetSource(@"
[ObsoleteAttribute]
public class Baz { }
", "Baz.cs");

        var result = await _engine.RemoveAttributeAsync("Baz.cs", "Baz", "Obsolete");

        Assert.That(result, Does.Not.Contain("[ObsoleteAttribute]"));
        Assert.That(result, Does.Contain("public class Baz"));
    }

    // ══════════════════════════════════════════════════════════════
    // RemoveBaseTypeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveBaseType_RemovesOneInterface()
    {
        SetSource(@"
public class Service : IService, IDisposable
{
    public void Run() { }
    public void Dispose() { }
}
", "Service.cs");

        var result = await _engine.RemoveBaseTypeAsync("Service.cs", "Service", "IDisposable");

        Assert.That(result, Does.Contain("IService"), "IService should remain.");
        Assert.That(result, Does.Not.Contain("IDisposable"), "IDisposable should be removed.");
    }

    [Test]
    public async Task RemoveBaseType_RemovesOnlyBaseType_LeavesNoBaseList()
    {
        SetSource(@"
public class Child : Parent
{
    public void Act() { }
}
", "Child.cs");

        var result = await _engine.RemoveBaseTypeAsync("Child.cs", "Child", "Parent");

        Assert.That(result, Does.Not.Contain(": Parent"), "Base list should be gone.");
        Assert.That(result, Does.Contain("public class Child"), "Class declaration should remain.");
    }

    [Test]
    public async Task RemoveBaseType_NoOpWhenNotPresent()
    {
        SetSource(@"
public class Worker : IWorker { }
", "Worker.cs");

        var result = await _engine.RemoveBaseTypeAsync("Worker.cs", "Worker", "IDisposable");

        Assert.That(result, Does.Contain(": IWorker"), "Base list should be unchanged.");
    }

    // ══════════════════════════════════════════════════════════════
    // ChangeAccessibilityAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ChangeAccessibility_PublicToPrivate()
    {
        SetSource(@"
public class Calc
{
    public int Add(int a, int b) => a + b;
}
", "Calc.cs");

        var result = await _engine.ChangeAccessibilityAsync("Calc.cs", "Add", "private");

        Assert.That(result, Does.Contain("private int Add"), "Method should now be private.");
        Assert.That(result, Does.Not.Contain("public int Add"));
    }

    [Test]
    public async Task ChangeAccessibility_PrivateToPublic()
    {
        SetSource(@"
public class Calc
{
    private int _value;
}
", "Calc.cs");

        var result = await _engine.ChangeAccessibilityAsync("Calc.cs", "_value", "public");

        Assert.That(result, Does.Contain("public int _value"));
    }

    [Test]
    public async Task ChangeAccessibility_ProtectedInternalToInternal()
    {
        SetSource(@"
public class Base
{
    protected internal void Hook() { }
}
", "Base.cs");

        var result = await _engine.ChangeAccessibilityAsync("Base.cs", "Hook", "internal");

        Assert.That(result, Does.Contain("internal void Hook"), "Should be internal.");
        Assert.That(result, Does.Not.Contain("protected internal void Hook"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddModifierAsync / RemoveModifierAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddModifier_AddsVirtualToMethod()
    {
        SetSource(@"
public class Base
{
    public void Execute() { }
}
", "Base.cs");

        var result = await _engine.AddModifierAsync("Base.cs", "Execute", "virtual");

        Assert.That(result, Does.Contain("virtual"), "Method should now be virtual.");
    }

    [Test]
    public async Task AddModifier_IsIdempotent()
    {
        SetSource(@"
public class Base
{
    public virtual void Execute() { }
}
", "Base.cs");

        var result = await _engine.AddModifierAsync("Base.cs", "Execute", "virtual");
        var count = System.Text.RegularExpressions.Regex.Matches(result, @"\bvirtual\b").Count;

        Assert.That(count, Is.EqualTo(1), "virtual should appear only once.");
    }

    [Test]
    public async Task RemoveModifier_RemovesStatic()
    {
        SetSource(@"
public class Helper
{
    public static void Go() { }
}
", "Helper.cs");

        var result = await _engine.RemoveModifierAsync("Helper.cs", "Go", "static");

        Assert.That(result, Does.Not.Contain("static void Go"));
        Assert.That(result, Does.Contain("public void Go"));
    }

    [Test]
    public async Task RemoveModifier_NoOpWhenAbsent()
    {
        SetSource(@"
public class Helper
{
    public void Go() { }
}
", "Helper.cs");

        var result = await _engine.RemoveModifierAsync("Helper.cs", "Go", "static");

        Assert.That(result, Does.Contain("public void Go"));
        Assert.That(result, Does.Not.Contain("static"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddSummaryCommentAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddSummaryComment_AddsToMethod()
    {
        SetSource(@"
public class Greeter
{
    public string Hello(string name) => $""Hello {name}"";
}
", "Greeter.cs");

        var result = await _engine.AddSummaryCommentAsync("Greeter.cs", "Hello", "Returns a greeting.");

        Assert.That(result, Does.Contain("/// <summary>"));
        Assert.That(result, Does.Contain("Returns a greeting."));
    }

    [Test]
    public async Task AddSummaryComment_AddsToClass()
    {
        SetSource(@"
public class Widget { }
", "Widget.cs");

        var result = await _engine.AddSummaryCommentAsync("Widget.cs", "Widget", "A reusable widget.");

        Assert.That(result, Does.Contain("/// <summary>"));
        Assert.That(result, Does.Contain("A reusable widget."));
    }

    [Test]
    public async Task AddSummaryComment_ReplacesExistingDocComment()
    {
        SetSource(@"
public class Service
{
    /// <summary>
    /// Old comment.
    /// </summary>
    public void Run() { }
}
", "Service.cs");

        var result = await _engine.AddSummaryCommentAsync("Service.cs", "Run", "New comment.");

        Assert.That(result, Does.Contain("New comment."));
        Assert.That(result, Does.Not.Contain("Old comment."));
    }

    // ══════════════════════════════════════════════════════════════
    // AddPropertyAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddProperty_ReadonlyProperty()
    {
        SetSource(@"
public class Person { }
", "Person.cs");

        var result = await _engine.AddPropertyAsync("Person.cs", "Person", "Name", "string", hasSetter: false);

        Assert.That(result, Does.Contain("string Name"));
        Assert.That(result, Does.Contain("get;"));
        Assert.That(result, Does.Not.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_ReadWriteProperty()
    {
        SetSource(@"
public class Person { }
", "Person.cs");

        var result = await _engine.AddPropertyAsync("Person.cs", "Person", "Age", "int");

        Assert.That(result, Does.Contain("int Age"));
        Assert.That(result, Does.Contain("get;"));
        Assert.That(result, Does.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_InitOnlyProperty()
    {
        SetSource(@"
public class Record { }
", "Record.cs");

        var result = await _engine.AddPropertyAsync("Record.cs", "Record", "Id", "Guid", hasSetter: true, isInit: true);

        Assert.That(result, Does.Contain("Guid Id"));
        Assert.That(result, Does.Contain("init;"));
        Assert.That(result, Does.Not.Contain("set;"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddFieldAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddField_PrivateReadonly()
    {
        SetSource(@"
public class Service { }
", "Service.cs");

        var result = await _engine.AddFieldAsync("Service.cs", "Service", "_logger", "ILogger", isReadonly: true);

        Assert.That(result, Does.Contain("private"));
        Assert.That(result, Does.Contain("readonly"));
        Assert.That(result, Does.Contain("ILogger _logger"));
    }

    [Test]
    public async Task AddField_PublicStaticWithInitializer()
    {
        SetSource(@"
public class Config { }
", "Config.cs");

        var result = await _engine.AddFieldAsync("Config.cs", "Config", "MaxRetries", "int",
            accessibility: "public", isStatic: true, initializer: "3");

        Assert.That(result, Does.Contain("public"));
        Assert.That(result, Does.Contain("static"));
        Assert.That(result, Does.Contain("int MaxRetries"));
        Assert.That(result, Does.Contain("= 3"));
    }

    // ══════════════════════════════════════════════════════════════
    // SortMembersAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task SortMembers_FieldsBeforeMethods()
    {
        SetSource(@"
public class Repo
{
    public void Save() { }
    private string _name;
}
", "Repo.cs");

        var result = await _engine.SortMembersAsync("Repo.cs", "Repo");

        var fieldIdx = result.IndexOf("_name", StringComparison.Ordinal);
        var methodIdx = result.IndexOf("Save()", StringComparison.Ordinal);
        Assert.That(fieldIdx, Is.LessThan(methodIdx), "Fields should appear before methods.");
    }

    [Test]
    public async Task SortMembers_ConstructorBeforeProperties()
    {
        SetSource(@"
public class Dto
{
    public string Name { get; set; }
    public int Age { get; set; }
    public Dto(string name) { Name = name; }
}
", "Dto.cs");

        var result = await _engine.SortMembersAsync("Dto.cs", "Dto");

        var ctorIdx = result.IndexOf("Dto(string name)", StringComparison.Ordinal);
        var propIdx = result.IndexOf("Name", StringComparison.Ordinal);
        Assert.That(ctorIdx, Is.LessThan(propIdx), "Constructor should appear before properties.");
    }

    [Test]
    public async Task SortMembers_StaticBeforeInstance()
    {
        SetSource(@"
public class Utils
{
    public void InstanceMethod() { }
    public static void StaticMethod() { }
}
", "Utils.cs");

        var result = await _engine.SortMembersAsync("Utils.cs", "Utils");

        var staticIdx = result.IndexOf("StaticMethod()", StringComparison.Ordinal);
        var instanceIdx = result.IndexOf("InstanceMethod()", StringComparison.Ordinal);
        Assert.That(staticIdx, Is.LessThan(instanceIdx), "Static methods should come before instance methods.");
    }

    // ══════════════════════════════════════════════════════════════
    // WrapInTryCatchAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task WrapInTryCatch_WrapsSingleStatement()
    {
        SetSource(@"
public class Processor
{
    public void Process()
    {
        DoWork();
    }
}
", "Processor.cs");

        var result = await _engine.WrapInTryCatchAsync("Processor.cs", 6, 6);

        Assert.That(result, Does.Contain("try"));
        Assert.That(result, Does.Contain("catch"));
        Assert.That(result, Does.Contain("DoWork()"));
    }

    [Test]
    public async Task WrapInTryCatch_WrapsMultipleStatements()
    {
        SetSource(@"
public class Processor
{
    public void Process()
    {
        var a = 1;
        var b = 2;
        var c = a + b;
    }
}
", "Processor.cs");

        var result = await _engine.WrapInTryCatchAsync("Processor.cs", 6, 8);

        Assert.That(result, Does.Contain("try"));
        Assert.That(result, Does.Contain("var a = 1"));
        Assert.That(result, Does.Contain("var b = 2"));
        Assert.That(result, Does.Contain("var c = a + b"));
    }

    [Test]
    public async Task WrapInTryCatch_WithCustomCatchBody()
    {
        SetSource(@"
public class Handler
{
    public void Handle()
    {
        Execute();
    }
}
", "Handler.cs");

        var result = await _engine.WrapInTryCatchAsync("Handler.cs", 6, 6,
            exceptionType: "InvalidOperationException",
            catchVariableName: "ioe",
            catchBody: "Console.WriteLine(ioe.Message);");

        Assert.That(result, Does.Contain("InvalidOperationException"));
        Assert.That(result, Does.Contain("ioe"));
        Assert.That(result, Does.Contain("Console.WriteLine"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddConstructorParameterAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddConstructorParameter_AddsToExistingCtor()
    {
        SetSource(@"
public class OrderService
{
    private readonly IProductRepo _productRepo;
    public OrderService(IProductRepo productRepo)
    {
        _productRepo = productRepo;
    }
    public void Place() { }
}
", "OrderService.cs");

        var result = await _engine.AddConstructorParameterAsync("OrderService.cs", "OrderService", "logger", "ILogger");

        Assert.That(result, Does.Contain("ILogger logger"), "New param should be in ctor signature.");
        Assert.That(result, Does.Contain("private readonly ILogger _logger"), "Field should be added.");
        Assert.That(result, Does.Contain("_logger = logger"), "Assignment should be in body.");
    }

    [Test]
    public async Task AddConstructorParameter_CreatesCtorWhenNoneExists()
    {
        SetSource(@"
public class UserService
{
    public void Create() { }
}
", "UserService.cs");

        var result = await _engine.AddConstructorParameterAsync("UserService.cs", "UserService", "repo", "IUserRepo");

        Assert.That(result, Does.Contain("IUserRepo repo"), "New param should be in ctor.");
        Assert.That(result, Does.Contain("private readonly IUserRepo _repo"), "Field should exist.");
        Assert.That(result, Does.Contain("_repo = repo"), "Assignment should be in body.");
    }

    [Test]
    public async Task AddConstructorParameter_UsesCustomFieldName()
    {
        SetSource(@"
public class NotifyService { }
", "NotifyService.cs");

        var result = await _engine.AddConstructorParameterAsync("NotifyService.cs", "NotifyService",
            "sender", "IEmailSender", fieldName: "_emailSender");

        Assert.That(result, Does.Contain("private readonly IEmailSender _emailSender"));
        Assert.That(result, Does.Contain("_emailSender = sender"));
    }

    // ══════════════════════════════════════════════════════════════
    // WrapInRegionAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task WrapInRegion_InsertsRegionDirectives()
    {
        SetSource(@"
public class MyClass
{
    public void MethodA() { }
    public void MethodB() { }
}
", "MyClass.cs");

        var result = await _engine.WrapInRegionAsync("MyClass.cs", 4, 5, "Public Methods");

        Assert.That(result, Does.Contain("#region Public Methods"));
        Assert.That(result, Does.Contain("#endregion"));
        Assert.That(result, Does.Contain("MethodA"));
        Assert.That(result, Does.Contain("MethodB"));
    }

    [Test]
    public async Task WrapInRegion_RegionAppearsInCorrectOrder()
    {
        SetSource(@"
public class MyClass
{
    private int _x;
    public void Run() { }
}
", "MyClass.cs");

        var result = await _engine.WrapInRegionAsync("MyClass.cs", 5, 5, "Methods");

        var regionIdx = result.IndexOf("#region Methods", StringComparison.Ordinal);
        var runIdx = result.IndexOf("public void Run()", StringComparison.Ordinal);
        var endRegionIdx = result.IndexOf("#endregion", StringComparison.Ordinal);

        Assert.That(regionIdx, Is.LessThan(runIdx), "#region should precede the method.");
        Assert.That(runIdx, Is.LessThan(endRegionIdx), "#endregion should follow the method.");
    }
}

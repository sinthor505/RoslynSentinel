using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for the new code-editing engine methods in RefactoringEngine:
/// AddMemberAsync (record/struct support), AddUsingDirectiveAsync, ModifyEnumAsync,
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

        Assert.That(result.UpdatedText, Does.Contain("Greet"), "Method should be added to record.");
        Assert.That(result.UpdatedText, Does.Contain("Person"), "Record declaration should still be present.");
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

        Assert.That(result.UpdatedText, Does.Contain("Length"), "Method should be added to struct.");
        Assert.That(result.UpdatedText, Does.Contain("Point"), "Struct declaration should still be present.");
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

        Assert.That(result.UpdatedText, Does.Contain("Speak"), "Method should be added to class.");
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

        Assert.That(result.UpdatedText, Does.Contain("using System.Linq"), "New using directive should be present.");
    }

    [Test]
    public async Task AddUsingDirective_NoOpWhenAlreadyPresent()
    {
        SetSource(@"using System.Linq;

public class Foo { }
", "Foo.cs");

        var result = await _engine.AddUsingDirectiveAsync("Foo.cs", "System.Linq");

        // Should not duplicate
        var count = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, "using System\\.Linq").Count;
        Assert.That(count, Is.EqualTo(1), "Duplicate using directive should not be added.");
    }

    [Test]
    public async Task AddUsingDirective_HandlesStaticUsing()
    {
        SetSource(@"
public class Calc { }
", "Calc.cs");

        var result = await _engine.AddUsingDirectiveAsync("Calc.cs", "static System.Math");

        Assert.That(result.UpdatedText, Does.Contain("System.Math"), "Static using directive should reference the namespace.");
        Assert.That(result.UpdatedText, Does.Contain("static"), "Static keyword should be present.");
    }

    // ══════════════════════════════════════════════════════════════
    // ModifyEnumAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ModifyEnum_AppendsValueToEnum()
    {
        SetSource(@"
public enum Color
{
    Red,
    Green
}
", "Color.cs");

        var result = await _engine.ModifyEnumAsync("Color.cs", "Color", "Red,Green,Blue");

        Assert.That(result.UpdatedText, Does.Contain("Blue"), "New enum value should be present.");
        Assert.That(result.UpdatedText, Does.Contain("Red"), "Existing values should remain.");
    }

    [Test]
    public async Task ModifyEnum_WithExplicitValue()
    {
        SetSource(@"
public enum Status
{
    Active,
    Inactive
}
", "Status.cs");

        var result = await _engine.ModifyEnumAsync("Status.cs", "Status", "Active,Inactive,Archived=99");

        Assert.That(result.UpdatedText, Does.Contain("Archived"), "New value should be present.");
        Assert.That(result.UpdatedText, Does.Contain("99"), "Explicit integer value should be present.");
    }

    [Test]
    public async Task ModifyEnum_RemovesAndReordersValues()
    {
        SetSource(@"
public enum Color
{
    Red,
    Green,
    Blue
}
", "Color.cs");

        var result = await _engine.ModifyEnumAsync("Color.cs", "Color", "Blue,Red");

        Assert.That(result.UpdatedText, Does.Contain("Blue"));
        Assert.That(result.UpdatedText, Does.Contain("Red"));
        Assert.That(result.UpdatedText, Does.Not.Contain("Green"), "Omitted value should be removed.");
        Assert.That(result.Message, Does.Contain("removed Green"));
        Assert.That(result.Message, Does.Contain("reordered"));
    }

    [Test]
    public async Task ModifyEnum_GracefulFallback_WhenEnumNotFound()
    {
        SetSource(@"
public class Foo { }
", "Foo.cs");

        var result = await _engine.ModifyEnumAsync("Foo.cs", "NonExistentEnum", "SomeValue");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(result.UpdatedText, Is.Null.Or.Empty, "No text should be produced when the target enum doesn't exist.");
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
        var startIdx = result.UpdatedText!.IndexOf("Start");
        var pauseIdx = result.UpdatedText!.IndexOf("Pause");
        var stopIdx = result.UpdatedText!.IndexOf("Stop");
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

        Assert.That(result.UpdatedText, Does.Contain("Delete"), "Member should be appended when anchor not found.");
        Assert.That(result.UpdatedText, Does.Contain("Save"), "Existing member should remain.");
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

        var drawIdx = result.UpdatedText!.IndexOf("Draw");
        var resizeIdx = result.UpdatedText!.IndexOf("Resize");
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

        var getIdx = result.UpdatedText!.IndexOf("Get()");
        var putIdx = result.UpdatedText!.IndexOf("Put()");
        var postIdx = result.UpdatedText!.IndexOf("Post()");
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

        Assert.That(result.UpdatedText, Does.Contain("Evict"), "Member should be appended when anchor not found.");
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

        var initIdx = result.UpdatedText!.IndexOf("Init");
        var logIdx = result.UpdatedText!.IndexOf("Log()");
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

        Assert.That(result.UpdatedText, Does.Contain("Serializable"), "Attribute should be added to class.");
        Assert.That(result.UpdatedText, Does.Contain("MyController"), "Class should still be present.");
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

        Assert.That(result.UpdatedText, Does.Contain("Obsolete"), "Attribute should be added to method.");
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

        Assert.That(result.UpdatedText, Does.Contain("Description"), "Attribute with argument should be added.");
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

        Assert.That(result.UpdatedText, Does.Contain("IRepository"), "Interface should be added to base list.");
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

        Assert.That(result.UpdatedText, Does.Contain("IService"), "First interface should still be present.");
        Assert.That(result.UpdatedText, Does.Contain("IDisposable"), "Second interface should be added.");
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
        var count = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, "IWorker").Count;
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

        Assert.That(result.UpdatedText, Does.Not.Contain("[Obsolete("), "Attribute should be removed from method.");
        Assert.That(result.UpdatedText, Does.Contain("[Obsolete]"), "Class attribute should remain.");
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

        Assert.That(result.UpdatedText, Does.Contain("public void Run()"));
        Assert.That(result.UpdatedText, Does.Not.Contain("[Obsolete]"));
    }

    [Test]
    public async Task RemoveAttribute_MatchesSuffixVariant()
    {
        SetSource(@"
[ObsoleteAttribute]
public class Baz { }
", "Baz.cs");

        var result = await _engine.RemoveAttributeAsync("Baz.cs", "Baz", "Obsolete");

        Assert.That(result.UpdatedText, Does.Not.Contain("[ObsoleteAttribute]"));
        Assert.That(result.UpdatedText, Does.Contain("public class Baz"));
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

        Assert.That(result.UpdatedText, Does.Contain("IService"), "IService should remain.");
        Assert.That(result.UpdatedText, Does.Not.Contain("IDisposable"), "IDisposable should be removed.");
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

        Assert.That(result.UpdatedText, Does.Not.Contain(": Parent"), "Base list should be gone.");
        Assert.That(result.UpdatedText, Does.Contain("public class Child"), "Class declaration should remain.");
    }

    [Test]
    public async Task RemoveBaseType_NoOpWhenNotPresent()
    {
        SetSource(@"
public class Worker : IWorker { }
", "Worker.cs");

        var result = await _engine.RemoveBaseTypeAsync("Worker.cs", "Worker", "IDisposable");

        Assert.That(result.UpdatedText, Does.Contain(": IWorker"), "Base list should be unchanged.");
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

        Assert.That(result.UpdatedText, Does.Contain("private int Add"), "Method should now be private.");
        Assert.That(result.UpdatedText, Does.Not.Contain("public int Add"));
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

        Assert.That(result.UpdatedText, Does.Contain("public int _value"));
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

        Assert.That(result.UpdatedText, Does.Contain("internal void Hook"), "Should be internal.");
        Assert.That(result.UpdatedText, Does.Not.Contain("protected internal void Hook"));
    }

    [Test]
    public async Task ChangeAccessibility_DoesNotReformatUnrelatedMembers()
    {
        const string source = """

        public class Calc
        {
            public int Add(int a, int b) => a + b;

            public int Subtract(int a, int b) => a - b;


            public int Multiply(int a, int b) => a * b;
        }

        """;
        SetSource(source, "Calc.cs");

        var result = await _engine.ChangeAccessibilityAsync("Calc.cs", "Add", "private");

        Assert.That(result.UpdatedText, Does.Contain("private int Add"), "Method should now be private.");
        Assert.That(result.UpdatedText, Does.Contain("public int Subtract(int a, int b) => a - b;\r\n\r\n\r\n    public int Multiply")
            .Or.Contain("public int Subtract(int a, int b) => a - b;\n\n\n    public int Multiply"),
            "Blank lines between untouched members below the edit must survive unchanged — a whole-file reformat would collapse them.");
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

        Assert.That(result.UpdatedText, Does.Contain("virtual"), "Method should now be virtual.");
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
        var count = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"\bvirtual\b").Count;

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

        Assert.That(result.UpdatedText, Does.Not.Contain("static void Go"));
        Assert.That(result.UpdatedText, Does.Contain("public void Go"));
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

        Assert.That(result.UpdatedText, Does.Contain("public void Go"));
        Assert.That(result.UpdatedText, Does.Not.Contain("static"));
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

        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        Assert.That(result.UpdatedText, Does.Contain("Returns a greeting."));
    }

    [Test]
    public async Task AddSummaryComment_AddsToClass()
    {
        SetSource(@"
public class Widget { }
", "Widget.cs");

        var result = await _engine.AddSummaryCommentAsync("Widget.cs", "Widget", "A reusable widget.");

        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        Assert.That(result.UpdatedText, Does.Contain("A reusable widget."));
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

        Assert.That(result.UpdatedText, Does.Contain("New comment."));
        Assert.That(result.UpdatedText, Does.Not.Contain("Old comment."));
    }

    [Test]
    public async Task AddSummaryComment_CallerSuppliesAlreadyWrappedSingleLineSummary_DoesNotDoubleWrap()
    {
        SetSource(@"
public class OrderService
{
    public Order CreateOrder(string customerId) => new Order(customerId);
}
", "OrderService.cs");

        var result = await _engine.AddSummaryCommentAsync(
            "OrderService.cs", "CreateOrder",
            "/// <summary>Creates a new order and returns it.</summary>");

        Assert.That(result.UpdatedText, Does.Contain("/// Creates a new order and returns it."));
        Assert.That(result.UpdatedText, Does.Not.Contain("<summary><summary>"));
        Assert.That(Regex.Matches(result.UpdatedText!, "<summary>").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(result.UpdatedText!, "</summary>").Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddSummaryComment_CallerSuppliesAlreadyWrappedMultiLineSummary_DoesNotDoubleWrap()
    {
        SetSource(@"
public class Widget { }
", "Widget2.cs");

        var result = await _engine.AddSummaryCommentAsync(
            "Widget2.cs", "Widget",
            "/// <summary>\n/// A reusable widget.\n/// </summary>");

        Assert.That(result.UpdatedText, Does.Contain("/// A reusable widget."));
        Assert.That(result.UpdatedText, Does.Not.Contain("<summary><summary>"));
        Assert.That(Regex.Matches(result.UpdatedText!, "<summary>").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(result.UpdatedText!, "</summary>").Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddSummaryComment_CallerSuppliesBareSummaryTagsWithoutSlashes_StripsThemBeforeWrapping()
    {
        SetSource(@"
public class Widget { }
", "Widget3.cs");

        var result = await _engine.AddSummaryCommentAsync(
            "Widget3.cs", "Widget",
            "<summary>A reusable widget.</summary>");

        Assert.That(result.UpdatedText, Does.Contain("/// A reusable widget."));
        Assert.That(result.UpdatedText, Does.Not.Contain("<summary><summary>"));
        Assert.That(Regex.Matches(result.UpdatedText!, "<summary>").Count, Is.EqualTo(1));
        Assert.That(Regex.Matches(result.UpdatedText!, "</summary>").Count, Is.EqualTo(1));
    }

    [Test]
    public async Task AddSummaryComment_TargetHasPreexistingTrailingLineComment_DoesNotMisindentIt()
    {
        SetSource(@"
public class Order
{
    // Intentional typo target for RenameSymbol scenario (""CalcuateTotal"" -> ""CalculateTotal"").
    public decimal CalcuateTotal()
    {
        return 0m;
    }
}
", "Order.cs");

        var result = await _engine.AddSummaryCommentAsync(
            "Order.cs", "CalcuateTotal",
            "Calculates the total value of the order.");

        Assert.That(result.UpdatedText, Does.Contain("/// Calculates the total value of the order."));
        Assert.That(result.UpdatedText, Does.Contain(
            "    // Intentional typo target for RenameSymbol scenario (\"CalcuateTotal\" -> \"CalculateTotal\")."),
            "the pre-existing trailing comment's original 4-space indentation must be preserved, not widened");
    }

    [Test]
    public async Task AddSummaryComment_TargetHasBlankLineThenTrailingLineComment_DoesNotMisindentIt()
    {
        SetSource(@"
public class Order
{
    public string CustomerId => ""x"";

    // Intentional typo target for RenameSymbol scenario (""CalcuateTotal"" -> ""CalculateTotal"").
    public decimal CalcuateTotal()
    {
        return 0m;
    }
}
", "Order2.cs");

        var result = await _engine.AddSummaryCommentAsync(
            "Order2.cs", "CalcuateTotal",
            "Calculates the total value of the order.");

        Assert.That(result.UpdatedText, Does.Contain("/// Calculates the total value of the order."));
        Assert.That(result.UpdatedText, Does.Not.Contain(
            "     // Intentional typo"),
            "the pre-existing trailing comment must not gain an extra leading space");
        Assert.That(result.UpdatedText, Does.Contain(
            "    // Intentional typo target for RenameSymbol scenario (\"CalcuateTotal\" -> \"CalculateTotal\")."),
            "the pre-existing trailing comment's original 4-space indentation must be preserved");
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

        Assert.That(result.UpdatedText, Does.Contain("string Name"));
        Assert.That(result.UpdatedText, Does.Contain("get;"));
        Assert.That(result.UpdatedText, Does.Not.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_ReadWriteProperty()
    {
        SetSource(@"
public class Person { }
", "Person.cs");

        var result = await _engine.AddPropertyAsync("Person.cs", "Person", "Age", "int");

        Assert.That(result.UpdatedText, Does.Contain("int Age"));
        Assert.That(result.UpdatedText, Does.Contain("get;"));
        Assert.That(result.UpdatedText, Does.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_InitOnlyProperty()
    {
        SetSource(@"
public class Record { }
", "Record.cs");

        var result = await _engine.AddPropertyAsync("Record.cs", "Record", "Id", "Guid", hasSetter: true, isInit: true);

        Assert.That(result.UpdatedText, Does.Contain("Guid Id"));
        Assert.That(result.UpdatedText, Does.Contain("init;"));
        Assert.That(result.UpdatedText, Does.Not.Contain("set;"));
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

        Assert.That(result.UpdatedText, Does.Contain("private"));
        Assert.That(result.UpdatedText, Does.Contain("readonly"));
        Assert.That(result.UpdatedText, Does.Contain("ILogger _logger"));
    }

    [Test]
    public async Task AddField_PublicStaticWithInitializer()
    {
        SetSource(@"
public class Config { }
", "Config.cs");

        var result = await _engine.AddFieldAsync("Config.cs", "Config", "MaxRetries", "int",
            accessibility: "public", isStatic: true, initializer: "3");

        Assert.That(result.UpdatedText, Does.Contain("public"));
        Assert.That(result.UpdatedText, Does.Contain("static"));
        Assert.That(result.UpdatedText, Does.Contain("int MaxRetries"));
        Assert.That(result.UpdatedText, Does.Contain("= 3"));
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

        var fieldIdx = result.UpdatedText!.IndexOf("_name", StringComparison.Ordinal);
        var methodIdx = result.UpdatedText!.IndexOf("Save()", StringComparison.Ordinal);
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

        var ctorIdx = result.UpdatedText!.IndexOf("Dto(string name)", StringComparison.Ordinal);
        var propIdx = result.UpdatedText!.IndexOf("Name", StringComparison.Ordinal);
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

        var staticIdx = result.UpdatedText!.IndexOf("StaticMethod()", StringComparison.Ordinal);
        var instanceIdx = result.UpdatedText!.IndexOf("InstanceMethod()", StringComparison.Ordinal);
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

        Assert.That(result.UpdatedText, Does.Contain("try"));
        Assert.That(result.UpdatedText, Does.Contain("catch"));
        Assert.That(result.UpdatedText, Does.Contain("DoWork()"));
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

        Assert.That(result.UpdatedText, Does.Contain("try"));
        Assert.That(result.UpdatedText, Does.Contain("var a = 1"));
        Assert.That(result.UpdatedText, Does.Contain("var b = 2"));
        Assert.That(result.UpdatedText, Does.Contain("var c = a + b"));
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

        Assert.That(result.UpdatedText, Does.Contain("InvalidOperationException"));
        Assert.That(result.UpdatedText, Does.Contain("ioe"));
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine"));
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

        Assert.That(result.UpdatedText, Does.Contain("ILogger logger"), "New param should be in ctor signature.");
        Assert.That(result.UpdatedText, Does.Contain("private readonly ILogger _logger"), "Field should be added.");
        Assert.That(result.UpdatedText, Does.Contain("_logger = logger"), "Assignment should be in body.");
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

        Assert.That(result.UpdatedText, Does.Contain("IUserRepo repo"), "New param should be in ctor.");
        Assert.That(result.UpdatedText, Does.Contain("private readonly IUserRepo _repo"), "Field should exist.");
        Assert.That(result.UpdatedText, Does.Contain("_repo = repo"), "Assignment should be in body.");
    }

    [Test]
    public async Task AddConstructorParameter_UsesCustomFieldName()
    {
        SetSource(@"
public class NotifyService { }
", "NotifyService.cs");

        var result = await _engine.AddConstructorParameterAsync("NotifyService.cs", "NotifyService",
            "sender", "IEmailSender", fieldName: "_emailSender");

        Assert.That(result.UpdatedText, Does.Contain("private readonly IEmailSender _emailSender"));
        Assert.That(result.UpdatedText, Does.Contain("_emailSender = sender"));
    }

    [Test]
    public async Task AddConstructorParameter_FieldNameEqualsParamName_DisambiguatesWithUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _engine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "stopwatch", "System.Diagnostics.Stopwatch", fieldName: "stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch _stopwatch"),
            "Colliding fieldName should be disambiguated to _stopwatch, not left as a bare collision.");
        Assert.That(result.UpdatedText, Does.Contain("_stopwatch = stopwatch"),
            "Assignment must target the disambiguated field, never a self-assignment like 'stopwatch = stopwatch;'.");
        var assignmentStatements = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"\bstopwatch\s*=\s*stopwatch;");
        Assert.That(assignmentStatements, Is.Empty,
            "Must not degenerate into a no-op self-assignment of the parameter to itself.");
        Assert.That(result.Message, Does.Contain("paramName='stopwatch'"));
        Assert.That(result.Message, Does.Contain("fieldName='_stopwatch'"));
    }

    [Test]
    public async Task AddConstructorParameter_FieldNameAlreadyUnderscorePrefixedAndEqualsParam_DisambiguatesWithUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _engine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "stopwatch", "System.Diagnostics.Stopwatch", fieldName: "_stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch _stopwatch"));
        Assert.That(result.UpdatedText, Does.Contain("_stopwatch = stopwatch"));
    }

    [Test]
    public async Task AddConstructorParameter_ParamNameAlreadyUnderscorePrefixed_StillDisambiguatesViaDoubleUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _engine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "_stopwatch", "System.Diagnostics.Stopwatch", fieldName: "_stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch __stopwatch"));
        Assert.That(result.UpdatedText, Does.Contain("__stopwatch = _stopwatch"));
        var assignmentStatements = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"(?<!_)_stopwatch\s*=\s*_stopwatch;");
        Assert.That(assignmentStatements, Is.Empty,
            "Must not degenerate into a no-op self-assignment of the parameter to itself.");
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

        Assert.That(result.UpdatedText, Does.Contain("#region Public Methods"));
        Assert.That(result.UpdatedText, Does.Contain("#endregion"));
        Assert.That(result.UpdatedText, Does.Contain("MethodA"));
        Assert.That(result.UpdatedText, Does.Contain("MethodB"));
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

        var regionIdx = result.UpdatedText!.IndexOf("#region Methods", StringComparison.Ordinal);
        var runIdx = result.UpdatedText!.IndexOf("public void Run()", StringComparison.Ordinal);
        var endRegionIdx = result.UpdatedText!.IndexOf("#endregion", StringComparison.Ordinal);

        Assert.That(regionIdx, Is.LessThan(runIdx), "#region should precede the method.");
        Assert.That(runIdx, Is.LessThan(endRegionIdx), "#endregion should follow the method.");
    }
}

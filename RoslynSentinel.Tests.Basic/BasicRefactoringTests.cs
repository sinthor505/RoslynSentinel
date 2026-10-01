using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for the new code-editing engine methods in BasicRefactoringEngine:
/// AddMemberAsync (record/struct support), AddUsingDirectiveAsync, ModifyEnumAsync,
/// InsertMemberAfterAsync, InsertMemberBeforeAsync, AddAttributeAsync, AddBaseTypeAsync.
/// </summary>
[TestFixture]
public class BasicRefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    //private MemberRefactoringEngine _basicRefactoringEngine;
    private BasicRefactoringEngine _basicRefactoringEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _basicRefactoringEngine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
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
    // Enum member support: AddEnumMemberAsync / RemoveEnumMemberAsync /
    // ReplaceEnumMemberAsync / IsEnumContainerAsync / TryGetEnumMemberContainerNameAsync /
    // GetContainerMembersAsync(enum) -> all delegate to the pre-existing ModifyEnumAsync.
    // ══════════════════════════════════════════════════════════════

    private const string ToolScopeEnumSource = @"
public enum ToolScope
{
    file, project, solution
}
";

    [Test]
    public async Task IsEnumContainer_OnEnum_ReturnsTrue()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("ToolScope.cs", "ToolScope"), Is.True);
    }

    [Test]
    public async Task IsEnumContainer_OnClass_ReturnsFalse()
    {
        SetSource("public class Widget { }", "Widget.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("Widget.cs", "Widget"), Is.False);
    }

    [Test]
    public async Task IsEnumContainer_NotFound_ReturnsFalse()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("ToolScope.cs", "NoSuchType"), Is.False);
    }

    [Test]
    public async Task GetContainerMembers_OnEnum_ReturnsEnumMembers()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var (outcome, message, members) = await _symbolNavigationEngine.GetContainerMembersAsync("ToolScope.cs", "ToolScope");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(members.Select(m => m.Name), Is.EquivalentTo(new[] { "file", "project", "solution" }));
        Assert.That(members, Has.All.Matches<SymbolNavigationEngine.ContainerMemberInfo>(m => m?.Kind == "enumMember"));
    }

    [Test]
    public async Task TryGetEnumMemberContainerName_OnEnumMember_ReturnsEnumName()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("ToolScope.cs", "project");

        Assert.That(containerName, Is.EqualTo("ToolScope"));
    }

    [Test]
    public async Task TryGetEnumMemberContainerName_OnRegularMember_ReturnsNull()
    {
        SetSource(@"
public class Animal
{
    public string Name { get; set; }
}
", "Animal.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("Animal.cs", "Name");

        Assert.That(containerName, Is.Null);
    }

    [Test]
    public async Task TryGetEnumMemberContainerName_NotFound_ReturnsNull()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("ToolScope.cs", "nonexistent");

        Assert.That(containerName, Is.Null);
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

        var result = await _basicRefactoringEngine.AddUsingDirectiveAsync("Foo.cs", "System.Linq");

        Assert.That(result.UpdatedText, Does.Contain("using System.Linq"), "New using directive should be present.");
    }

    [Test]
    public async Task AddUsingDirective_NoOpWhenAlreadyPresent()
    {
        SetSource(@"using System.Linq;

public class Foo { }
", "Foo.cs");

        var result = await _basicRefactoringEngine.AddUsingDirectiveAsync("Foo.cs", "System.Linq");

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

        var result = await _basicRefactoringEngine.AddUsingDirectiveAsync("Calc.cs", "static System.Math");

        Assert.That(result.UpdatedText, Does.Contain("System.Math"), "Static using directive should reference the namespace.");
        Assert.That(result.UpdatedText, Does.Contain("static"), "Static keyword should be present.");
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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Greeter.cs", "Hello", "Returns a greeting.");

        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        Assert.That(result.UpdatedText, Does.Contain("Returns a greeting."));
    }

    [Test]
    public async Task AddSummaryComment_AddsToClass()
    {
        SetSource(@"
public class Widget { }
", "Widget.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Widget.cs", "Widget", "A reusable widget.");

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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Service.cs", "Run", "New comment.");

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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
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

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
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

    [Test]
    public async Task AddSummaryComment_PreservesBlankLineBeforeTargetAndIndentsDocComment()
    {
        // Live-run regression (RoslynSentinel Advanced commenting pass, 2026-08-22): every single
        // AddSummaryCommentAsync insertion across 4 files collapsed the blank line separating the
        // target from the previous member, and landed "/// <summary>" at column 0 instead of the
        // member's own indentation. Root cause: the synthetic doc comment (parsed from a string built
        // at column 0) was inserted into rebuiltTrivia without ever prepending baseIndent to it, and
        // WithLeadingTrivia(newTrivia) replaced the target's entire original leading trivia -> including
        // the blank-line EndOfLine trivia -> with only docTrivia + kept comments + one trailing baseIndent.
        SetSource(@"
public class Widget
{
    private readonly int _value;

    public Widget(int value)
    {
        _value = value;
    }
}
", "Widget.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "Widget.cs", "Widget",
            "Initializes a new instance of the Widget class.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText?.Replace("\r\n", "\n"), Does.Contain(
            "    private readonly int _value;\n\n    /// <summary>"),
            "the blank line separating the constructor from the previous field must survive, and the doc comment must be indented to match the member, not land at column 0");
    }

    [Test]
    public async Task AddSummaryComment_TargetAlreadyHasSummary_ReplacesIt()
    {
        SetSource(@"
public class Widget
{
    /// <summary>
    /// The old summary.
    /// </summary>
    public void DoWork()
    {
    }
}
", "Widget2.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "Widget2.cs", "DoWork",
            "The new summary.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("The new summary."));
        Assert.That(result.UpdatedText, Does.Not.Contain("The old summary."));
    }

    [Test]
    public async Task AddSummaryComment_TargetIsEnum_Succeeds()
    {
        // GetMemberName had no switch case for EnumDeclarationSyntax, so ResolveMemberByNameOrSnippet
        // silently dropped every enum candidate regardless of name -> a live agent's
        // AddSummaryCommentAsync("OrderStatus", ...) against a real, unambiguous top-level enum
        // failed "target not found" purely because of this gap, not any real ambiguity or missing file.
        SetSource(@"
namespace N;
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled
}
", "OrderStatus.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "OrderStatus.cs", "OrderStatus",
            "Enumeration of possible order status values.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        Assert.That(result.UpdatedText, Does.Contain("Enumeration of possible order status values."));
    }

    [Test]
    public async Task AddSummaryComment_TargetIsEnumMember_Succeeds()
    {
        // EnumMemberDeclarationSyntax does not derive from MemberDeclarationSyntax, so it was
        // invisible to ResolveMemberByNameOrSnippet's DescendantNodes().OfType<MemberDeclarationSyntax>()
        // scan regardless of GetMemberName -> a live agent's AddSummaryCommentAsync("Pending", ...)
        // against an unambiguous enum member failed "target not found" even though the enclosing
        // enum type itself resolved fine.
        SetSource(@"
namespace N;
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled
}
", "OrderStatus.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "OrderStatus.cs", "Pending",
            "The order is pending.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        Assert.That(result.UpdatedText, Does.Contain("The order is pending."));
    }

    [Test]
    public async Task AddSummaryComment_MethodWithParamsAndReturn_EmitsParamAndReturnsScaffold()
    {
        // Mirrors VS/Roslyn's native "///" auto-generate: the tag *shape* (param names taken from
        // the real signature, <returns> only for a non-void/non-Task result) is mechanical and
        // should always be right even though BulkComment's LLM-authored text only ever fills
        // <summary> -> the empty <param>/<returns> tags alone still improve IDE tooltip/IntelliSense
        // quality over a bare <summary>.
        SetSource(@"
public class Calculator
{
    public int Add(int left, int right) => left + right;
}
", "Calculator.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Calculator.cs", "Add", "Adds two integers.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <param name=\"left\"></param>"));
        Assert.That(result.UpdatedText, Does.Contain("/// <param name=\"right\"></param>"));
        Assert.That(result.UpdatedText, Does.Contain("/// <returns></returns>"));
    }

    [Test]
    public async Task AddSummaryComment_VoidMethod_EmitsNoReturnsTag()
    {
        SetSource(@"
public class Logger
{
    public void Log(string message) { }
}
", "Logger.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Logger.cs", "Log", "Logs a message.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <param name=\"message\"></param>"));
        Assert.That(result.UpdatedText, Does.Not.Contain("<returns>"));
    }

    [Test]
    public async Task AddSummaryComment_GenericMethod_EmitsTypeParamTag()
    {
        SetSource(@"
public class Repository
{
    public T Get<T>(int id) => default!;
}
", "Repository.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync("Repository.cs", "Get", "Retrieves an entity by id.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <typeparam name=\"T\"></typeparam>"));
        Assert.That(result.UpdatedText, Does.Contain("/// <param name=\"id\"></param>"));
        Assert.That(result.UpdatedText, Does.Contain("/// <returns></returns>"));
    }

    [Test]
    public async Task SummaryComment_EnumMember_ViewAndRemoveRoundtrip()
    {
        SetSource(@"
namespace N;
public enum OrderStatus
{
    Pending,
    Shipped,
    Cancelled
}
", "OrderStatus.cs");

        var added = await _basicRefactoringEngine.AddSummaryCommentAsync("OrderStatus.cs", "Shipped", "The order has shipped.");
        Assert.That(added.Outcome, Is.EqualTo(EditOutcome.Modified));

        // BasicRefactoringEngine methods read from the workspace's current solution rather than each
        // other's return values -> in the real tool layer, RefactoringTools writes
        // UpdatedText back into the workspace between calls, so mirror that here.
        SetSource(added.UpdatedText!, "OrderStatus.cs");

        var viewed = await _basicRefactoringEngine.GetSummaryCommentAsync("OrderStatus.cs", "Shipped");
        Assert.That(viewed.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(viewed.SummaryText, Does.Contain("The order has shipped."));

        var removed = await _basicRefactoringEngine.RemoveSummaryCommentAsync("OrderStatus.cs", "Shipped");
        Assert.That(removed.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(removed.UpdatedText, Does.Not.Contain("The order has shipped."));
    }

    [Test]
    public async Task AddSummaryComment_TargetIsRecord_Succeeds()
    {
        SetSource(@"
public record OrderLine(string Sku, int Quantity);
", "OrderLine.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "OrderLine.cs", "OrderLine",
            "A single line item within an order.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("A single line item within an order."));
    }

    [Test]
    public async Task AddSummaryComment_TargetIsStruct_Succeeds()
    {
        SetSource(@"
public struct Point
{
    public int X;
    public int Y;
}
", "Point.cs");

        var result = await _basicRefactoringEngine.AddSummaryCommentAsync(
            "Point.cs", "Point",
            "A 2D point.");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("A 2D point."));
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

        var result = await _basicRefactoringEngine.WrapInTryCatchAsync("Processor.cs", 6, 6);

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

        var result = await _basicRefactoringEngine.WrapInTryCatchAsync("Processor.cs", 6, 8);

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

        var result = await _basicRefactoringEngine.WrapInTryCatchAsync("Handler.cs", 6, 6,
            exceptionType: "InvalidOperationException",
            catchVariableName: "ioe",
            catchBody: "Console.WriteLine(ioe.Message);");

        Assert.That(result.UpdatedText, Does.Contain("InvalidOperationException"));
        Assert.That(result.UpdatedText, Does.Contain("ioe"));
        Assert.That(result.UpdatedText, Does.Contain("Console.WriteLine"));
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

        var result = await _basicRefactoringEngine.WrapInRegionAsync("MyClass.cs", 4, 5, "Public Methods");

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

        var result = await _basicRefactoringEngine.WrapInRegionAsync("MyClass.cs", 5, 5, "Methods");

        var regionIdx = result.UpdatedText!.IndexOf("#region Methods", StringComparison.Ordinal);
        var runIdx = result.UpdatedText!.IndexOf("public void Run()", StringComparison.Ordinal);
        var endRegionIdx = result.UpdatedText!.IndexOf("#endregion", StringComparison.Ordinal);

        Assert.That(regionIdx, Is.LessThan(runIdx), "#region should precede the method.");
        Assert.That(runIdx, Is.LessThan(endRegionIdx), "#endregion should follow the method.");
    }
}

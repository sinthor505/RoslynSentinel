using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free checks of <see cref="ScenarioCompilation"/> (plan_modeleval_first_slice_scenario_rework Step 5) over a temp folder of
/// small .cs files, and of <see cref="DotnetTestResult.FailedTests"/>. No fixture, no restore, no dotnet process.
/// </summary>
[TestFixture]
public class ScenarioCompilationTests
{
    private const string WidgetPath = "Core/Widget.cs";
    private const string ConsumerPath = "Core/Consumer.cs";
    private const string LookupPath = "Core/Lookup.cs";

    private const string WidgetSource = """
namespace Tiny;

public class Widget
{
    public Widget(int size)
    {
        Size = size;
    }

    public int Size { get; }

    public int Grow(int by) => Size + by;
}
""";

    // The cref names a member Widget does not have, so the compiler reports CS1574 for this file only.
    private const string ConsumerWithUnresolvedCrefSource = """
namespace Tiny;

/// <summary>Builds a <see cref="Widget"/> and grows it; see <see cref="Widget.Shrink(int)"/>.</summary>
public class Consumer
{
    public Widget Make() => new Widget(3);

    public int Use(Widget widget) => widget.Grow(2);
}
""";

    private const string ConsumerWithResolvedCrefSource = """
namespace Tiny;

/// <summary>Builds a <see cref="Widget"/> and grows it; see <see cref="Widget.Grow(int)"/>.</summary>
public class Consumer
{
    public Widget Make() => new Widget(3);

    public int Use(Widget widget) => widget.Grow(2);
}
""";

    // No using directive anywhere: Dictionary (System.Collections.Generic), Sum (System.Linq) and Path (System.IO) only resolve
    // through the generated global-usings tree.
    private const string LookupSource = """
namespace Tiny;

public class Lookup
{
    public Dictionary<string, int> Counts { get; } = new();

    public int Total() => Counts.Values.Sum();

    public string NameOf(string path) => Path.GetFileName(path);
}
""";

    // Names are single tokens (a parameterised name ends at its closing parenthesis, with no space inside), as the console logger prints them.
    private const string CannedConsoleOutput = """
Starting test execution, please wait...
A total of 1 test files matched the specified pattern.
  Passed Tiny.Tests.CalculatorTests.Add_AddsTwoNumbers [< 1 ms]
  Failed Tiny.Tests.CalculatorTests.Double_DoublesTheValue [12 ms]
  Error Message:
   Expected: 8
  But was:  6
  Stack Trace:
     at Tiny.Tests.CalculatorTests.Double_DoublesTheValue() in C:\work\CalculatorTests.cs:line 14
  Failed Tiny.Tests.CalculatorTests.Add_Cases(2) [1 ms]
  Error Message:
   Expected: 5
  But was:  -1
Failed!  - Failed:     2, Passed:     1, Skipped:     0, Total:     3, Duration: 20 ms - Tiny.Tests.dll (net10.0)
""";

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ScenarioCompilationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp leftover is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: temp leftover only.
        }
    }

    [Test]
    public void DocumentationDiagnostics_UnresolvedCref_ReportsCs1574()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithUnresolvedCrefSource));

        var diagnostics = compilation.DocumentationDiagnostics(ConsumerPath);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Id), Is.EqualTo(new[] { "CS1574" }), AllDiagnostics(compilation));
        Assert.That(compilation.DocumentationDiagnostics(WidgetPath), Is.Empty, "the diagnostics are filtered by file; Widget.cs has no cref problem");
    }

    [Test]
    public void DocumentationDiagnostics_ResolvedCref_ReportsNothing()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithResolvedCrefSource));

        Assert.That(compilation.DocumentationDiagnostics(ConsumerPath), Is.Empty, AllDiagnostics(compilation));
        Assert.That(compilation.DocumentationDiagnostics(WidgetPath), Is.Empty);
    }

    [Test]
    public void DocumentationDiagnostics_FileOutsideTheCompilation_ThrowsAnArgumentExceptionNamingTheFiles()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithResolvedCrefSource));

        var exception = Assert.Throws<ArgumentException>(() => compilation.DocumentationDiagnostics("Core/Missing.cs"));

        Assert.That(exception!.Message, Does.Contain("Core/Widget.cs"));
    }

    [Test]
    public void TypeOf_ObjectCreation_IsTheCreatedType()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithResolvedCrefSource));
        var creation = compilation.ModelFor(ConsumerPath).SyntaxTree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();

        var type = compilation.TypeOf(creation);

        Assert.That(type, Is.Not.Null, AllDiagnostics(compilation));
        Assert.That(type!.ToDisplayString(), Is.EqualTo("Tiny.Widget"));
    }

    [Test]
    public void SymbolOf_Invocation_BindsToTheMember()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithResolvedCrefSource));
        var invocation = compilation.ModelFor(ConsumerPath).SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var symbol = compilation.SymbolOf(invocation) as IMethodSymbol;

        Assert.That(symbol, Is.Not.Null, AllDiagnostics(compilation));
        Assert.That(symbol!.Name, Is.EqualTo("Grow"));
        Assert.That(symbol.ContainingType.ToDisplayString(), Is.EqualTo("Tiny.Widget"));
    }

    [Test]
    public void SymbolOf_ObjectCreation_BindsToTheConstructor()
    {
        var compilation = Compile((WidgetPath, WidgetSource), (ConsumerPath, ConsumerWithResolvedCrefSource));
        var creation = compilation.ModelFor(ConsumerPath).SyntaxTree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single();

        var symbol = compilation.SymbolOf(creation) as IMethodSymbol;

        Assert.That(symbol, Is.Not.Null, AllDiagnostics(compilation));
        Assert.That(symbol!.MethodKind, Is.EqualTo(MethodKind.Constructor));
        Assert.That(symbol.ContainingType.Name, Is.EqualTo("Widget"));
    }

    [Test]
    public void ImplicitUsings_Resolve()
    {
        var compilation = Compile((LookupPath, LookupSource));

        var errors = compilation.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();

        Assert.That(errors, Is.Empty, AllDiagnostics(compilation));
    }

    [Test]
    public void Type_FindsADeclaredTypeByMetadataName_AndNullForAnUnknownOne()
    {
        var compilation = Compile((WidgetPath, WidgetSource));

        Assert.That(compilation.Type("Tiny.Widget"), Is.Not.Null);
        Assert.That(compilation.Type("Tiny.DoesNotExist"), Is.Null);
    }

    [Test]
    public void ParseFailedTests_ReadsNamesFromConsoleOutput()
    {
        var result = new DotnetTestResult(Passed: 1, Failed: 2, Skipped: 0, Total: 3, ExitCode: 1, RawOutput: CannedConsoleOutput);

        Assert.That(
            result.FailedTests,
            Is.EqualTo(new[] { "Tiny.Tests.CalculatorTests.Double_DoublesTheValue", "Tiny.Tests.CalculatorTests.Add_Cases(2)" }));
    }

    [Test]
    public void ParseFailedTests_PassingOutput_IsEmpty()
    {
        var output = "Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 5 ms - Tiny.Tests.dll (net10.0)";
        var result = new DotnetTestResult(Passed: 2, Failed: 0, Skipped: 0, Total: 2, ExitCode: 0, RawOutput: output);

        Assert.That(result.FailedTests, Is.Empty);
    }

    // Writes each file under the temp root and builds the compilation over the Core/ folder.
    private ScenarioCompilation Compile(params (string RelativePath, string Source)[] files)
    {
        foreach (var (relativePath, source) in files)
        {
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        return ScenarioCompilation.Create(_root, "Core/");
    }

    private static string AllDiagnostics(ScenarioCompilation compilation) =>
        "compiler diagnostics: " + string.Join(" | ", compilation.Compilation.GetDiagnostics().Select(diagnostic => diagnostic.ToString()));
}

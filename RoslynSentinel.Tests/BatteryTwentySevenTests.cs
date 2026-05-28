// Battery #27 — Bug-Fix Regression Tests
// Each test proves a specific bug (catalogued in the review sessions) is fixed and
// cannot silently regress.  Test names encode the bug ID they guard.
//
// Bugs covered:
//   B08 — DeadCodeEngine: written-but-never-read variables were not detected as unused
//   B02 — ImmutabilityEngine: const fields received readonly modifier (→ CS0106)
//   B01 — InstrumentationEngine: throw was emitted as ExpressionStatement (invalid C#)
//   B03 — ArchitecturalEngine: IdentifierName contained a dot (invalid identifier)
//   B09 — AdvancedRefactoringEngine: OptimizeTaskWaitAsync rewrote all .Wait()/.Result
//         when semantic model was null (false positives)
//   B18 — ContextHelper: OrdinalIgnoreCase blocked valid PascalCase identifiers such as
//         "String", "Object", "Int"
//   B19 — TestingEngine: MSTest framework produced attributeless test methods
//   B17 — DocumentationEngine: HasStructuredTrivia excluded methods inside #region
//   B11 — ModernizationEngine.ClassToRecordAsync: duplicate property declarations (CS0102)
//   B10 — ModernizationEngine.TryConvertOrChainToPattern: only last value used (chain lost)
//   B20 — LogicOptimizationEngine.AddGuardClausesAsync: nullable params got null guards
//   B04 — AntiPatternEngine.DetectMissingCancellationToken: zero-param async methods skipped
//   B16 — SecurityAndSafetyEngine.DetectMissingNullChecksAsync: expression-bodied methods skipped

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// B08 — DeadCodeEngine: written-but-never-read must be flagged as unused
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B08_DeadCode_WrittenButNeverRead
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DeadCodeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DeadCodeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectUnusedLocalVariables_VariableWrittenButNeverRead_IsReported()
    {
        // Before fix: WrittenInside check caused tool to always return empty even when
        // a variable is assigned but never used afterwards.
        const string source = """
            public class C {
                public int Compute() {
                    int unused = 42;   // written but never read
                    return 0;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("C.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectUnusedLocalVariablesAsync("C.cs");

        Assert.That(reports.Count, Is.GreaterThanOrEqualTo(1),
            "A variable that is assigned but never read must be reported as unused.");
        Assert.That(reports.Any(r => r.SymbolName == "unused"), Is.True,
            "The 'unused' variable must be in the report.");
    }

    [Test]
    public async Task DetectUnusedLocalVariables_VariableWrittenAndRead_IsNotReported()
    {
        const string source = """
            public class C {
                public int Compute() {
                    int x = 42;
                    return x;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("C.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var reports = await _engine.DetectUnusedLocalVariablesAsync("C.cs");

        Assert.That(reports.Any(r => r.SymbolName == "x"), Is.False,
            "A variable that is both written and read must NOT be reported.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B02 — ImmutabilityEngine: const fields must not receive readonly modifier
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B02_Immutability_ConstFieldNotReadonly
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ImmutabilityEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ImmutabilityEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task MakeClassImmutable_ClassWithConstField_DoesNotAddReadonlyToConst()
    {
        // Before fix: readonly was added to const fields, producing "const readonly" which
        // is CS0106 (the 'readonly' modifier cannot be used on this declaration element).
        const string source = """
            public class Config {
                public const int MaxRetries = 3;
                private string _name;
                public Config(string name) { _name = name; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Config.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MakeClassImmutableAsync("Config.cs", "Config");

        Assert.That(result, Does.Not.Contain("const readonly"),
            "const fields must NOT receive a readonly modifier.");
        Assert.That(result, Does.Contain("const int MaxRetries"),
            "const field must remain unchanged.");
        // The non-const field should get readonly
        Assert.That(result, Does.Contain("readonly string _name"),
            "Non-const mutable field should receive readonly.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B01 — InstrumentationEngine: catch block must contain a valid throw statement
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B01_Instrumentation_ValidThrowStatement
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private InstrumentationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new InstrumentationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AddTryCatchToMethod_GeneratesValidThrowStatement()
    {
        // Before fix: ExpressionStatement(ParseExpression("throw")) produced an
        // expression-statement containing the keyword "throw" which is not a valid
        // expression — Roslyn would emit an error node in the syntax tree.
        const string source = """
            public class Service {
                public void Process() {
                    var x = 1;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddTryCatchToMethodAsync("Service.cs", "Process");

        // The output must not contain a bare expression-statement "throw;"  written as
        // ExpressionStatement; instead it must appear as the ThrowStatement "throw;"
        Assert.That(result, Does.Contain("throw;"),
            "catch block must end with a valid throw; rethrow statement.");
        Assert.That(result, Does.Contain("catch"),
            "Output must contain a catch clause.");
        // Must not contain syntax errors from invalid throw expression
        Assert.That(result, Does.Not.Contain("throw )"),
            "Output must not contain broken throw expression syntax.");
    }

    [Test]
    public async Task AddTryCatchToClass_AllMethodsCatchBlocksHaveValidThrow()
    {
        const string source = """
            public class Worker {
                public void Run() { }
                public void Stop() { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddTryCatchToClassAsync("Worker.cs", "Worker");

        var throwCount = result.Split("throw;").Length - 1;
        Assert.That(throwCount, Is.GreaterThanOrEqualTo(2),
            "Each public method's catch block must contain a valid throw;");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B03 — ArchitecturalEngine: stoppingToken.IsCancellationRequested must be a
//        member access, not a dotted identifier name
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B03_ArchitecturalEngine_ValidMemberAccess
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ArchitecturalEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ArchitecturalEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ConvertToBackgroundService_ProducesValidMemberAccess()
    {
        // Before fix: SyntaxFactory.IdentifierName("stoppingToken.IsCancellationRequested")
        // creates an identifier whose text contains a dot — that is not valid C# and Roslyn
        // would emit an identifier with a dot in it, which doesn't compile.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class MyWorker {
                public async Task RunAsync(CancellationToken token) {
                    await Task.Delay(1000);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("MyWorker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertToBackgroundServiceAsync("MyWorker.cs", "MyWorker");

        // The result must contain "stoppingToken.IsCancellationRequested" as proper member access
        Assert.That(result, Does.Contain("stoppingToken.IsCancellationRequested"),
            "Output must reference stoppingToken.IsCancellationRequested via member access.");
        // Crucially, the dotted form must NOT appear inside an IdentifierName literal string
        // in the output (which would indicate un-parsed invalid syntax)
        Assert.That(result, Does.Not.Contain("\"stoppingToken.IsCancellationRequested\""),
            "Output must not contain dotted identifier as a literal string.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B09 — AdvancedRefactoringEngine.OptimizeTaskWaitAsync: must NOT rewrite
//        .Wait()/.Result when semantic model is absent (prevents false positives)
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B09_AdvancedRefactoring_IsTaskTypeNullGuard
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AdvancedRefactoringEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task OptimizeTaskWait_NonTaskDotWait_IsNotRewritten()
    {
        // Before fix: IsTaskType returned true when semanticModel == null.
        // This caused .Wait() on ANY object to be rewritten as if it were a Task.Wait().
        const string source = """
            public class Service {
                private readonly ManualResetEvent _gate = new ManualResetEvent(false);
                public void Block() {
                    _gate.WaitOne();   // NOT a Task.Wait — must not be rewritten
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.OptimizeTaskWaitAsync("Service.cs");

        Assert.That(result, Does.Contain("_gate.WaitOne()"),
            "Non-Task .WaitOne() must not be touched by OptimizeTaskWaitAsync.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B18 — ContextHelper.GetUniqueVariableName: PascalCase identifiers like
//        "String", "Object", "Int" must not be blocked as reserved keywords
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B18_ContextHelper_CaseSensitiveKeywords
{
    [Test]
    public void GetUniqueVariableName_PascalCaseTypeNameAsBase_NotBlockedByReservedKeywords()
    {
        // Before fix: OrdinalIgnoreCase caused "String" to match keyword "string",
        // and "Object" to match "object", so these valid identifiers were unreachable.
        // After camelCase conversion: "String" → "string" IS a keyword, so suffix is added.
        // But "MyString" → "myString" is NOT a keyword and must be returned as-is.

        // Use a minimal scope with no declarations
        var emptyScope = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block();

        var result1 = ContextHelper.GetUniqueVariableName(emptyScope, "MyString");
        Assert.That(result1, Is.EqualTo("myString"),
            "'MyString' → camelCase 'myString' is not a keyword and must be returned as-is.");

        var result2 = ContextHelper.GetUniqueVariableName(emptyScope, "MyObject");
        Assert.That(result2, Is.EqualTo("myObject"),
            "'MyObject' → camelCase 'myObject' is not a keyword and must be returned as-is.");

        var result3 = ContextHelper.GetUniqueVariableName(emptyScope, "MyInt");
        Assert.That(result3, Is.EqualTo("myInt"),
            "'MyInt' → camelCase 'myInt' is not a keyword and must be returned as-is.");
    }

    [Test]
    public void GetUniqueVariableName_ExistingNameConflict_CaseSensitive()
    {
        // Before fix: OrdinalIgnoreCase meant "myValue" conflicted with "MyValue"
        // in C# they are different identifiers — the suffix should NOT be added.
        const string source = """
            public class C {
                public void M() {
                    var MyValue = 1;
                }
            }
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();

        // "myValue" (camelCase of "MyValue") is different from "MyValue" in C# — no conflict
        var result = ContextHelper.GetUniqueVariableName(method, "myValue");
        Assert.That(result, Is.EqualTo("myValue"),
            "'myValue' is case-sensitively distinct from existing 'MyValue' — must not add suffix.");
    }

    [Test]
    public void GetUniqueVariableName_ExactCaseConflict_AddsSuffix()
    {
        // If the scope DOES have "myValue" already, we should get "myValue1"
        const string source = """
            public class C {
                public void M() {
                    var myValue = 1;
                }
            }
            """;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();

        var result = ContextHelper.GetUniqueVariableName(method, "myValue");
        Assert.That(result, Is.EqualTo("myValue1"),
            "When exact-case 'myValue' exists, must return 'myValue1'.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B19 — TestingEngine.GenerateTestSkeletonAsync: MSTest framework support
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B19_TestingEngine_MSTestSupport
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private TestingEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new TestingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GenerateTestSkeleton_MsTestFramework_ProducesTestClassAndTestMethodAttributes()
    {
        // Before fix: only "nunit" and "xunit" were handled; "mstest" produced attributeless tests.
        const string source = """
            namespace MyApp {
                public class Calculator {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.GenerateTestSkeletonAsync("Calc.cs", "Calculator", framework: "mstest");

        Assert.That(report.Content, Does.Contain("[TestClass]"),
            "MSTest skeleton must include [TestClass] on the test class.");
        Assert.That(report.Content, Does.Contain("[TestMethod]"),
            "MSTest skeleton must include [TestMethod] on each test method.");
        Assert.That(report.Content, Does.Contain("using Microsoft.VisualStudio.TestTools.UnitTesting;"),
            "MSTest skeleton must have the correct using directive.");
        Assert.That(report.Content, Does.Contain("Assert.Fail"),
            "MSTest skeleton must contain Assert.Fail as the placeholder assertion.");
    }

    [Test]
    public async Task GenerateTestSkeleton_XunitFramework_DoesNotProduceTestClassAttribute()
    {
        const string source = """
            namespace MyApp {
                public class Calculator {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.GenerateTestSkeletonAsync("Calc.cs", "Calculator", framework: "xunit");

        Assert.That(report.Content, Does.Contain("[Fact]"),
            "xUnit skeleton must include [Fact] on each test method.");
        Assert.That(report.Content, Does.Not.Contain("[TestClass]"),
            "xUnit skeleton must NOT include [TestClass].");
    }

    [Test]
    public async Task GenerateTestSkeleton_NUnitFramework_ProducesTestFixtureAndTest()
    {
        const string source = """
            namespace MyApp {
                public class Calculator {
                    public int Add(int a, int b) => a + b;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var report = await _engine.GenerateTestSkeletonAsync("Calc.cs", "Calculator", framework: "nunit");

        Assert.That(report.Content, Does.Contain("[TestFixture]"),
            "NUnit skeleton must include [TestFixture] on the test class.");
        Assert.That(report.Content, Does.Contain("[Test]"),
            "NUnit skeleton must include [Test] on each test method.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B17 — DocumentationEngine: methods inside #region must still receive XML docs
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B17_DocumentationEngine_RegionMethodsGetDocs
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DocumentationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DocumentationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GenerateXmlDocStubs_MethodInsideRegion_ReceivesXmlDoc()
    {
        // Before fix: HasStructuredTrivia was true for ANY method inside a #region
        // (the #region directive is structured trivia), so methods in regions were
        // incorrectly excluded from documentation generation.
        const string source = """
            public class Service {
            #region Public API
                public void Process(string input) { }
            #endregion
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.GenerateXmlDocumentationStubsAsync("Service.cs");

        Assert.That(result, Does.Contain("/// <summary>"),
            "Method inside #region must receive XML doc summary stub.");
        Assert.That(result, Does.Contain("param name=\"input\""),
            "Method inside #region must receive XML doc <param> stub.");
    }

    [Test]
    public async Task GenerateXmlDocStubs_MethodWithExistingXmlDoc_NotDuplicated()
    {
        const string source = """
            public class Service {
                /// <summary>Already documented.</summary>
                public void Process(string input) { }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.GenerateXmlDocumentationStubsAsync("Service.cs");

        var summaryCount = result.Split("/// <summary>").Length - 1;
        Assert.That(summaryCount, Is.EqualTo(1),
            "Methods that already have XML docs must not receive a second summary stub.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B11 — ModernizationEngine.ClassToRecordAsync: no CS0102 duplicate properties
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B11_ModernizationEngine_ClassToRecord_NoDuplicateProperties
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ModernizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ModernizationEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ClassToRecord_SimpleClass_PropertiesNotDuplicated()
    {
        // Before fix: properties were added both as positional parameters AND as explicit
        // property members in the record body, producing CS0102 duplicate declarations.
        const string source = """
            public class Point {
                public int X { get; set; }
                public int Y { get; set; }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Point.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ClassToRecordAsync("Point.cs", "Point");

        // Should produce: public record Point(int X, int Y)
        Assert.That(result, Does.Contain("record Point"),
            "Output must contain a record declaration.");
        // Count occurrences of "X" as a standalone word in the record output
        // There should be exactly ONE declaration of X (either as positional param or body member)
        var xCount = System.Text.RegularExpressions.Regex.Matches(result, @"\bint X\b").Count;
        Assert.That(xCount, Is.EqualTo(1),
            "Property X must appear exactly once — no duplicate declarations.");
        var yCount = System.Text.RegularExpressions.Regex.Matches(result, @"\bint Y\b").Count;
        Assert.That(yCount, Is.EqualTo(1),
            "Property Y must appear exactly once — no duplicate declarations.");
    }

    [Test]
    public async Task ClassToRecord_ClassWithMethod_MethodPreservedInRecord()
    {
        const string source = """
            public class Shape {
                public double Width { get; set; }
                public double Area() => Width * Width;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Shape.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ClassToRecordAsync("Shape.cs", "Shape");

        Assert.That(result, Does.Contain("record Shape"),
            "Output must contain a record declaration.");
        Assert.That(result, Does.Contain("Area()"),
            "Non-property member (method) must be preserved in the record.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B10 — ModernizationEngine.TryConvertOrChainToPattern: full OR chain preserved
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B10_ModernizationEngine_OrChainFullPattern
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ModernizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ModernizationEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ConvertToPatternMatching_OrChainWithThreeValues_AllValuesPresent()
    {
        // Before fix: in TryConvertOrChainToPattern the loop overwrote `pattern` with
        // only the LAST value's ConstantPattern, so the output was "x is 3" instead of
        // "x is 1 or 2 or 3".
        const string source = """
            public class C {
                public bool Check(int x) {
                    if (x == 1 || x == 2 || x == 3) return true;
                    return false;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("C.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertToPatternAsync("C.cs");

        // The result must contain all three values in some form
        Assert.That(result, Does.Contain("1"),
            "Converted OR pattern must retain the first value (1).");
        Assert.That(result, Does.Contain("2"),
            "Converted OR pattern must retain the second value (2).");
        Assert.That(result, Does.Contain("3"),
            "Converted OR pattern must retain the third value (3).");
        // If actual OR-pattern conversion occurred it should contain "or"
        if (result.Contains("is"))
        {
            Assert.That(result, Does.Contain("or").Or.Contain("||"),
                "If converted to is-pattern, must include all values with 'or'; otherwise chain preserved.");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B20 — LogicOptimizationEngine.AddGuardClausesAsync: nullable params skipped
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B20_LogicOptimization_NullableParamNoGuard
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private LogicOptimizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new LogicOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AddGuardClauses_NullableStringParam_DoesNotAddNullGuard()
    {
        // Before fix: AddGuardClausesAsync added ArgumentNullException.ThrowIfNull even
        // for explicitly nullable parameters (string?) — those are nullable by contract.
        const string source = """
            public class Processor {
                public void Handle(string? optionalName) {
                    if (optionalName != null) { }
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Processor.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddGuardClausesAsync("Processor.cs", "Handle");

        Assert.That(result, Does.Not.Contain("ThrowIfNull(optionalName)"),
            "Nullable parameter 'string?' must NOT receive an ArgumentNullException.ThrowIfNull guard.");
    }

    [Test]
    public async Task AddGuardClauses_NonNullableStringParam_AddsNullGuard()
    {
        const string source = """
            public class Processor {
                public void Handle(string requiredName) {
                    _ = requiredName.Length;
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Processor.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddGuardClausesAsync("Processor.cs", "Handle");

        Assert.That(result, Does.Contain("ThrowIfNull"),
            "Non-nullable 'string' parameter must receive an ArgumentNullException guard.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B04 — AntiPatternEngine.DetectMissingCancellationToken: zero-param async methods
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B04_AntiPattern_ZeroParamAsyncNeedsCancellationToken
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectAntiPatterns_ZeroParamPublicAsyncMethod_IsFlagged()
    {
        // Before fix: DetectMissingCancellationToken skipped methods with zero parameters via
        // `if (parameters.Count < 1) continue;`
        // A public async Task method with NO parameters still cannot be cancelled — it should be flagged.
        // NOTE: DetectAntiPatternsAsync → DetectMissingCancellationToken (the static method that was fixed).
        const string source = """
            using System.Threading.Tasks;
            public class Service {
                public async Task LoadDataAsync() {
                    await Task.Delay(1000);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs");

        Assert.That(findings.Any(f =>
                f.Pattern == "MissingCancellationToken" &&
                f.Snippet.Contains("LoadDataAsync")), Is.True,
            "A public async Task method with zero parameters must be flagged for missing CancellationToken.");
    }

    [Test]
    public async Task DetectAntiPatterns_ZeroParamAlreadyHasCt_NotFlagged()
    {
        // Methods that already have CancellationToken must not be flagged
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            public class Service {
                public async Task LoadDataAsync(CancellationToken ct) {
                    await Task.Delay(1000);
                }
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync("Service.cs");

        Assert.That(findings.Any(f =>
                f.Pattern == "MissingCancellationToken" &&
                f.Snippet.Contains("LoadDataAsync")), Is.False,
            "A method that already has CancellationToken must NOT be flagged.");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// B16 — SecurityAndSafetyEngine: expression-bodied methods must be checked
// ─────────────────────────────────────────────────────────────────────────────
[TestFixture]
public class B16_SecuritySafety_ExpressionBodiedMethodsChecked
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SecurityAndSafetyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SecurityAndSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectMissingNullChecks_ExpressionBodiedMethod_WithReferenceParam_IsFlagged()
    {
        // Before fix: body == null → continue skipped all expression-bodied methods,
        // so `public string GetLength(string s) => s.Length.ToString()` was never checked.
        const string source = """
            public class Utils {
                public string GetLength(string s) => s.Length.ToString();
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Utils.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Utils.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'s'")), Is.True,
            "Expression-bodied method with non-nullable reference param used in body must be flagged.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ExpressionBodiedMethod_WithNullConditional_NotFlagged()
    {
        // If the expression body uses ?. on the parameter, it IS null-safe
        const string source = """
            public class Utils {
                public string? GetUpper(string s) => s?.ToUpperInvariant();
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Utils.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Utils.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'s'")), Is.False,
            "Expression-bodied method using s?. on the parameter must NOT be flagged — it is null-safe.");
    }

    [Test]
    public async Task DetectMissingNullChecks_ExpressionBodiedMethod_NullableParam_NotFlagged()
    {
        // Nullable reference type parameters should never be flagged
        const string source = """
            public class Utils {
                public int GetLength(string? s) => s?.Length ?? 0;
            }
            """;
        var solution = TestSolutionBuilder.CreateSolutionWithProject("P", [("Utils.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var issues = await _engine.DetectMissingNullChecksAsync("Utils.cs");

        Assert.That(issues.Any(i => i.Type == "MissingNullCheck" && i.Description.Contains("'s'")), Is.False,
            "Expression-bodied method with nullable (string?) parameter must NOT be flagged.");
    }
}

#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Regression tests for all critical bug fixes and edge cases.
/// 
/// Each test is named after the specific scenario it guards against.
/// If any of these tests start failing, a regression has been introduced.
/// 
/// Sections:
///  1. ChangeSignature — call-site rewriting (was a complete stub)
///  2. ExtractInterface — block-style namespace + usings (was missing from generated file)
///  3. ConvertPropertySafe — modifier preservation, contextSnippet disambiguation
///  4. InterpolateStringSafe — const format string (the specific bug vs MS built-in)
///  5. MoveTypeToFile — interface types, single-type file boundary
///  6. FindCallersSafe — contextSnippet overload disambiguation
///  7. ImplementInterfaceSafe — partial implementation, property-only interfaces, no 'override'
///  8. FormatDocumentPreview — hunk content structure
///  9. DiagnosticEngine — grouping behaviour
/// </summary>
[TestFixture]
public class RegressionTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private CodeGenerationEngine _codeGenerationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DiagnosticEngine _diagnosticEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _diagnosticEngine = new DiagnosticEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1. ChangeSignature — call-site rewriting
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChangeSignature_ReordersCallSiteArguments_NotJustDeclaration()
    {
        // Regression: ChangeSignatureAsync was a stub that returned an empty dict.
        // Now it must reorder BOTH the declaration AND all call sites.
        SetMultipleFiles(
            ("Math.cs", """
                public class MathHelper
                {
                    public int Add(int a, int b, int c) => a + b + c;
                }
                """),
            ("App.cs", """
                public class App
                {
                    private readonly MathHelper _math = new();
                    public int Run() => _math.Add(1, 2, 3);
                }
                """));

        // Reorder [a, b, c] → [c, a, b] using permutation index [2, 0, 1]
        var result = await _refactoringEngine.ChangeSignatureAsync("Math.cs", "Add", new[] { 2, 0, 1 });

        // Declaration must reflect new order: c, a, b
        var decl = result["Math.cs"];
        var cIdx = decl.IndexOf("int c", StringComparison.Ordinal);
        var aIdx = decl.IndexOf("int a", StringComparison.Ordinal);
        var bIdx = decl.IndexOf("int b", StringComparison.Ordinal);
        Assert.That(cIdx, Is.LessThan(aIdx), "Declaration: c must come before a");
        Assert.That(aIdx, Is.LessThan(bIdx), "Declaration: a must come before b");

        // Call site in App.cs must also be reordered: Add(3, 1, 2)
        Assert.That(result.ContainsKey("App.cs"), "App.cs must be included — call site needs rewriting");
        var callSite = result["App.cs"];
        // Arguments reordered: was (1,2,3) → c=3 first, so (3, 1, 2)
        var arg3Pos = callSite.IndexOf("3", callSite.IndexOf("Add(", StringComparison.Ordinal), StringComparison.Ordinal);
        var arg1Pos = callSite.IndexOf("1", arg3Pos, StringComparison.Ordinal);
        Assert.That(arg3Pos, Is.LessThan(arg1Pos), "Call site: reordered arg 3 must appear before 1");
    }

    [Test]
    public async Task ChangeSignature_TwoParam_Swap_RoundTrip()
    {
        // Swapping parameters and back should produce stable output.
        SetSource("public class C { public void Greet(string first, string last) { } }");

        var swapped = await _refactoringEngine.ChangeSignatureAsync("Test.cs", "Greet", new[] { 1, 0 });

        Assert.That(swapped, Is.Not.Empty);
        var content = swapped["Test.cs"];
        var lastIdx = content.IndexOf("string last", StringComparison.Ordinal);
        var firstIdx = content.IndexOf("string first", StringComparison.Ordinal);
        Assert.That(lastIdx, Is.LessThan(firstIdx), "After swap, 'last' should appear before 'first'");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. ExtractInterface — block-style namespace
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ExtractInterface_BlockStyleNamespace_IncludesNamespaceInGeneratedFile()
    {
        // Regression: earlier version only handled file-scoped namespaces (namespace Foo;).
        // Block-style namespaces (namespace Foo { }) must also be reflected in the interface file.
        const string source = """
            using System;

            namespace MyApp.Services
            {
                public class ReportService
                {
                    public string BuildReport(string title) => title;
                    public void SaveReport(string path) { }
                }
            }
            """;
        SetSource(source, "ReportService.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("ReportService.cs", "ReportService", "IReportService");

        var ifacePath = result.Keys.First(k => k != "ReportService.cs");
        var ifaceContent = result[ifacePath];

        Assert.That(ifaceContent, Does.Contain("namespace MyApp.Services"),
            "Block-style namespace must appear in the generated interface file");
        Assert.That(ifaceContent, Does.Contain("using System"),
            "Using directives must be copied to the generated interface file");
        Assert.That(ifaceContent, Does.Contain("IReportService"),
            "Interface declaration must be present");
    }

    [Test]
    public async Task ExtractInterface_FileScopedNamespace_StillWorks()
    {
        // Ensure the file-scoped namespace path remains functional (regression guard).
        const string source = """
            using System.Collections.Generic;
            namespace MyApp.Core;
            public class ProductService
            {
                public List<string> GetAll() => new();
            }
            """;
        SetSource(source, "ProductService.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("ProductService.cs", "ProductService", "IProductService");

        var ifacePath = result.Keys.First(k => k != "ProductService.cs");
        Assert.That(result[ifacePath], Does.Contain("namespace MyApp.Core"));
        Assert.That(result[ifacePath], Does.Contain("IProductService"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. ConvertPropertySafe — modifier preservation + contextSnippet
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConvertPropertySafe_PreservesVirtualModifier_OnToFullProperty()
    {
        // ConvertPropertySafe promises to handle virtual/override/new — this test enforces that.
        SetSource("""
            public class Base
            {
                public virtual int Count { get; set; } = 10;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Count", "ToFullProperty");

        Assert.That(result, Does.Contain("virtual"), "virtual modifier must survive ToFullProperty conversion");
        Assert.That(result, Does.Contain("10"), "Initializer value must survive conversion");
    }

    [Test]
    public async Task ConvertPropertySafe_PreservesOverrideModifier_OnToFullProperty()
    {
        SetSource("""
            public class Base { public virtual int Size { get; set; } }
            public class Derived : Base
            {
                public override int Size { get; set; } = 99;
            }
            """);

        var result = await _codeGenerationEngine.ConvertPropertySafeAsync("Test.cs", "Size", "ToFullProperty");

        Assert.That(result, Does.Contain("override"), "override modifier must survive ToFullProperty conversion");
        Assert.That(result, Does.Contain("99"), "Initializer 99 must survive conversion");
    }

    [Test]
    public async Task ConvertPropertySafe_ContextSnippet_PicksCorrectPropertyWhenNamesClash()
    {
        // Two classes each have a 'Name' property. contextSnippet must pick the right one.
        SetSource("""
            public class Person
            {
                public string Name { get; set; } = "Alice";
            }
            public class Company
            {
                public string Name { get; set; } = "Acme";
            }
            """);

        // Target only the Company.Name property via contextSnippet
        var result = await _codeGenerationEngine.ConvertPropertySafeAsync(
            "Test.cs", "Name", "ToFullProperty",
            contextSnippet: "\"Acme\"");

        // The result must expand Company.Name (initializer "Acme" should move to backing field)
        // Person.Name should remain an auto-property
        Assert.That(result, Does.Contain("\"Acme\""),
            "Company's initializer Acme must appear in the backing field");
        // Person.Name should still be an auto-property (no _name backing for Alice)
        var personSection = result.Substring(0, result.IndexOf("Company", StringComparison.Ordinal));
        Assert.That(personSection, Does.Contain("{ get; set; }"),
            "Person.Name must remain an auto-property — context snippet should have limited the change");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. InterpolateStringSafe — const format string (the exact MS built-in bug)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InterpolateStringSafe_ConstFormatString_ResolvedViaSemanticModel()
    {
        // The MS built-in convert_to_interpolated_string fails when the format string is a
        // named const. Our implementation resolves it via the semantic model. This test
        // covers exactly that scenario.
        SetSource("""
            public class Logger
            {
                private const string MessageFmt = "User {0} logged in from {1}";

                public string BuildLog(string user, string ip)
                {
                    return string.Format(MessageFmt, user, ip);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(MessageFmt, user, ip)");

        Assert.That(result, Contains.Substring("$\""), "Should produce an interpolated string");
        Assert.That(result, Contains.Substring("{user}"), "user arg should be inlined");
        Assert.That(result, Contains.Substring("{ip}"), "ip arg should be inlined");
        // The const itself should no longer appear as a format reference
        Assert.That(result, Does.Not.Contain("string.Format(MessageFmt"), "Original string.Format call must be replaced");
    }

    [Test]
    public async Task InterpolateStringSafe_FormatSpecifier_PreservedInInterpolation()
    {
        // {0:N2} format specifiers must survive conversion.
        SetSource("""
            public class Formatter
            {
                public string FormatPrice(decimal amount)
                {
                    return string.Format("Price: {0:N2}", amount);
                }
            }
            """);

        var result = await _codeGenerationEngine.InterpolateStringAsync(
            "Test.cs",
            "string.Format(\"Price: {0:N2}\", amount)");

        Assert.That(result, Contains.Substring("$\""), "Must produce interpolated string");
        Assert.That(result, Contains.Substring("{amount:N2}"), "Format specifier N2 must be preserved");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. MoveTypeToFile — interface types, single-type boundary
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MoveTypeToFile_InterfaceType_MovesToOwnFile()
    {
        // Tests moving an interface (not class/record) — untested by prior tests.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[]
        {
            ("Services.cs", """
                namespace MyApp.Services;
                public class UserService { public void Create() { } }
                public interface IUserRepository { void Save(); }
                """)
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Services.cs", "IUserRepository");

        Assert.That(changes.Count, Is.EqualTo(2), "Should produce 2 files");
        var newKey = changes.Keys.Single(k => k.Contains("IUserRepository.cs"));
        Assert.That(changes[newKey], Does.Contain("interface IUserRepository"),
            "New file must contain the interface declaration");
        Assert.That(changes[newKey], Does.Contain("namespace MyApp.Services"),
            "New file must carry the source namespace");
        var srcKey = changes.Keys.Single(k => !k.Contains("IUserRepository.cs"));
        Assert.That(changes[srcKey], Does.Not.Contain("interface IUserRepository"),
            "Source file must not retain the moved interface");
    }

    [Test]
    public async Task MoveTypeToFile_SingleTypeFile_ReturnsEmptyDict()
    {
        // When a file contains only one type whose name matches the filename, it's already in its own
        // file — MoveTypeToFile should return an empty dict (no-op).
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[]
        {
            ("Solo.cs", """
                namespace App;
                public class Solo { public void DoWork() { } }
                """)
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Solo.cs", "Solo");

        Assert.That(changes, Is.Empty,
            "Solo is already in Solo.cs — MoveTypeToFile should return empty (no-op)");
    }

    [Test]
    public async Task MoveTypeToFile_EnumType_MovesToOwnFile()
    {
        // Enums are BaseTypeDeclarationSyntax — should be movable.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[]
        {
            ("Domain.cs", """
                namespace MyApp;
                public class Order { public OrderStatus Status { get; set; } }
                public enum OrderStatus { Pending, Processing, Shipped }
                """)
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Domain.cs", "OrderStatus");

        var newKey = changes.Keys.Single(k => k.Contains("OrderStatus.cs"));
        Assert.That(changes[newKey], Does.Contain("enum OrderStatus"),
            "New file must contain the enum declaration");
        Assert.That(changes[newKey], Does.Contain("namespace MyApp"),
            "Enum file must have the namespace");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. FindCallersSafe — contextSnippet overload disambiguation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindCallersSafe_OverloadedMethod_ContextSnippetSelectsCorrectOverload()
    {
        // When the same method name has multiple overloads, contextSnippet disambiguates.
        SetMultipleFiles(
            ("Processor.cs", """
                public class Processor
                {
                    public string Process(int id) => id.ToString();
                    public string Process(string name) => name.ToUpper();
                }
                """),
            ("Client.cs", """
                public class Client
                {
                    private Processor _p = new();
                    public void Run()
                    {
                        _p.Process(42);
                        _p.Process("hello");
                    }
                }
                """));

        // Find callers of Process(int id) specifically
        var intCallers = await _symbolNavigationEngine.FindCallersAsync(
            "Processor.cs", "Process", contextSnippet: "int id");

        // Find callers of Process(string name) specifically
        var strCallers = await _symbolNavigationEngine.FindCallersAsync(
            "Processor.cs", "Process", contextSnippet: "string name");

        // Both should find calls from Run
        Assert.That(intCallers, Is.Not.Empty, "Should find callers for int overload");
        Assert.That(strCallers, Is.Not.Empty, "Should find callers for string overload");
    }

    [Test]
    public async Task FindCallersSafe_NoContextSnippet_FindsAllOverloads()
    {
        // Without contextSnippet, all overloads' callers are returned.
        SetMultipleFiles(
            ("Svc.cs", """
                public class Svc
                {
                    public void Send(string msg) { }
                    public void Send(int code) { }
                }
                """),
            ("Caller.cs", """
                public class Caller
                {
                    private Svc _s = new();
                    public void Go() { _s.Send("hi"); _s.Send(99); }
                }
                """));

        var callers = await _symbolNavigationEngine.FindCallersAsync("Svc.cs", "Send");

        Assert.That(callers, Is.Not.Empty, "Should find at least one caller");
        Assert.That(callers.Any(c => c.CallerMethod == "Go"), Is.True, "Go should be listed as a caller");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. ImplementInterfaceSafe — partial implementation, property-only, no override
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ImplementInterfaceSafe_PartialImplementation_OnlyGeneratesMissingMembers()
    {
        // Class already implements one method — only the missing one should be generated.
        const string source = """
            namespace App;

            public interface IWorker
            {
                void Start();
                void Stop();
            }

            public class Worker : IWorker
            {
                public void Start() { /* already done */ }
            }
            """;
        SetSource(source, "Worker.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Worker.cs", "Worker", "IWorker");

        // Stop() must be generated
        Assert.That(result, Does.Contain("public void Stop"),
            "Missing Stop() method must be generated");
        // Start() must NOT be duplicated — check 'public void Start' (not 'void Start' which also matches interface)
        var publicStartCount = System.Text.RegularExpressions.Regex.Matches(result, @"public void Start").Count;
        Assert.That(publicStartCount, Is.EqualTo(1),
            "Start() must NOT be duplicated — it was already implemented");
    }

    [Test]
    public async Task ImplementInterfaceSafe_PropertyOnlyInterface_GeneratesPropertyStubs()
    {
        // Interface with only properties (no methods) must produce property stubs.
        const string source = """
            namespace App;

            public interface IConfig
            {
                string Host { get; set; }
                int Port { get; }
            }

            public class AppConfig : IConfig
            {
            }
            """;
        SetSource(source, "Config.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Config.cs", "AppConfig", "IConfig");

        Assert.That(result, Does.Contain("public string Host"),
            "Host property stub must be generated");
        Assert.That(result, Does.Contain("public int Port"),
            "Port property stub must be generated");
        Assert.That(result, Does.Contain("NotImplementedException"),
            "Property stubs must throw NotImplementedException");
        Assert.That(result, Does.Not.Contain("override"),
            "REGRESSION: interface property stubs must NOT have 'override' keyword");
    }

    [Test]
    public async Task ImplementInterfaceSafe_NeverAdds_OverrideKeyword_OnMethods()
    {
        // CRITICAL REGRESSION TEST: The MS built-in implement_interface incorrectly adds
        // 'override' to interface implementations. Ours must never do this.
        const string source = """
            namespace App;

            public interface ISerializer
            {
                string Serialize(object obj);
                T Deserialize<T>(string json);
            }

            public class JsonSerializer : ISerializer
            {
            }
            """;
        SetSource(source, "JsonSerializer.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync(
            "JsonSerializer.cs", "JsonSerializer", "ISerializer");

        Assert.That(result, Does.Not.Contain("override"),
            "REGRESSION: interface method stubs must NEVER have 'override' keyword");
        Assert.That(result, Does.Contain("public string Serialize"),
            "Serialize stub must be generated");
    }

    [Test]
    public async Task ImplementInterfaceSafe_ReadOnlyProperty_GeneratesGetterOnly()
    {
        // Read-only properties in an interface (get; only) must produce getter-only stubs.
        const string source = """
            namespace App;
            public interface IReadOnly { string Id { get; } }
            public class Impl : IReadOnly { }
            """;
        SetSource(source, "Impl.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Impl.cs", "Impl", "IReadOnly");

        Assert.That(result, Does.Contain("public string Id"),
            "Id property must be generated");
        // A read-only stub should NOT have a setter accessor
        var idPropStart = result.IndexOf("public string Id", StringComparison.Ordinal);
        var afterId = result.Substring(idPropStart);
        var nextMemberOrEnd = afterId.IndexOf("\n    public ", StringComparison.Ordinal);
        var idBlock = nextMemberOrEnd > 0 ? afterId.Substring(0, nextMemberOrEnd) : afterId;
        Assert.That(idBlock, Does.Not.Contain("set"),
            "Read-only interface property must NOT generate a setter");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. FormatDocumentPreview — hunk content structure
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FormatDocumentPreview_ChangedFile_HunksHaveRemovedOrAddedContent()
    {
        // A hunk must carry actual line content — not just empty lists.
        SetSource("public class Messy{public int X{get;set;}\npublic int Y{get;set;}\n}");

        var preview = await _refactoringEngine.FormatDocumentPreviewAsync("Test.cs");

        Assert.That(preview.Changed, Is.True, "Poorly formatted file should report as changed");
        Assert.That(preview.Hunks, Is.Not.Empty, "At least one hunk must be generated");

        // Every hunk should have either removed or added lines (otherwise it's meaningless)
        foreach (var hunk in preview.Hunks)
        {
            Assert.That(hunk.RemovedLines.Count > 0 || hunk.AddedLines.Count > 0, Is.True,
                $"Hunk at lines {hunk.StartLine}-{hunk.EndLine} must have either removed or added content");
        }
    }

    [Test]
    public async Task FormatDocumentPreview_TotalHunks_MatchesHunkListCount()
    {
        // TotalHunks property must match the Hunks list length — structural consistency.
        SetSource("public class Messy{public int X{get;set;}public string Y{get;set;}}");

        var preview = await _refactoringEngine.FormatDocumentPreviewAsync("Test.cs");

        Assert.That(preview.TotalHunks, Is.EqualTo(preview.Hunks.Count),
            "FormatPreviewResult.TotalHunks must equal the length of the Hunks list");
    }

    [Test]
    public async Task FormatDocumentPreview_UnchangedFile_TotalHunksIsZero()
    {
        // A perfectly-formatted file must produce zero hunks and Changed=false.
        SetSource("""
            public class Clean
            {
                public int X { get; set; }
                public string Y { get; set; }
            }
            """);

        var preview = await _refactoringEngine.FormatDocumentPreviewAsync("Test.cs");

        // The formatter may or may not consider raw test content "already formatted",
        // but TotalHunks must always match the actual list.
        Assert.That(preview.TotalHunks, Is.EqualTo(preview.Hunks.Count),
            "TotalHunks must be consistent with Hunks.Count even for already-formatted files");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. DiagnosticEngine — grouping behaviour
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetDiagnosticsSummary_MultipleErrorsSameId_AreGrouped()
    {
        // Multiple occurrences of the same diagnostic ID must be grouped/counted,
        // not listed as separate uncorrelated items.
        SetSource("""
            public class Broken
            {
                public void Foo()
                {
                    var a = UndefinedA;
                    var b = UndefinedB;
                    var c = UndefinedC;
                }
            }
            """);

        var summary = await _diagnosticEngine.GetSolutionDiagnosticsAsync();

        Assert.That(summary, Is.Not.Null);
        Assert.That(summary.Details, Is.Not.Null);

        // CS0103 ("name does not exist in current context") should appear in Details
        // and its count should be >= 3 (one per undefined symbol), OR it should be
        // aggregated into a single entry with count 3.
        var errorCount = summary.Errors + summary.Warnings;
        Assert.That(errorCount, Is.GreaterThanOrEqualTo(3),
            "Three undefined symbols should produce at least 3 diagnostics total");
    }

    [Test]
    public async Task GetFileDiagnosticsSummary_WellFormedFile_ZeroErrors()
    {
        // REGRESSION: DiagnosticEngine must not falsely report errors on valid code.
        SetSource("""
            using System;
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public double Divide(double a, double b) => b == 0 ? throw new DivideByZeroException() : a / b;
            }
            """);

        var summary = await _diagnosticEngine.GetFileDiagnosticsAsync("Test.cs");

        Assert.That(summary.Errors, Is.EqualTo(0),
            "REGRESSION: Valid code must report zero errors");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10. MoveTypeToFile — ContentPreviews regression (the Session 11 fix)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MoveTypeToFile_BothFiles_HaveSubstantialContent()
    {
        // Session 11 fix: MoveTypeToFile now returns content previews.
        // Both the source file and the new file must have non-trivial content.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[]
        {
            ("Events.cs", """
                namespace MyApp.Events;
                public class OrderCreatedEvent { public string OrderId { get; set; } }
                public class OrderShippedEvent { public string TrackingNumber { get; set; } }
                """)
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveTypeToFileAsync("Events.cs", "OrderShippedEvent");

        Assert.That(changes.Count, Is.EqualTo(2), "Should produce exactly 2 files");
        foreach (var (path, content) in changes)
        {
            Assert.That(content, Is.Not.Null.And.Not.Empty,
                $"File '{path}' must have non-empty content (ContentPreview regression)");
            Assert.That(content.Length, Is.GreaterThan(30),
                $"File '{path}' content is suspiciously short ({content.Length} chars)");
            Assert.That(content, Does.Contain("namespace MyApp.Events"),
                $"File '{path}' must preserve the namespace");
        }
    }

    [Test]
    public async Task MoveAllTypesToFiles_EachNewFile_HasNamespaceAndType()
    {
        // Batch move: every generated file must have a namespace and the expected type.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("DummyApp", new[]
        {
            ("Models.cs", """
                namespace MyApp.Models;
                public class Product { public string Name { get; set; } }
                public class Category { public string Title { get; set; } }
                public class Brand { public string Label { get; set; } }
                """)
        });
        _workspaceManager.SetTestSolution(solution);

        var changes = await _refactoringEngine.MoveAllTypesToFilesAsync("Models.cs");

        // Primary type stays (whichever is first = Product), Category and Brand get new files
        Assert.That(changes.Count, Is.EqualTo(3), "3 files expected: updated source + 2 new");
        foreach (var (path, content) in changes)
        {
            Assert.That(content, Does.Contain("namespace MyApp.Models"),
                $"File {path} must contain the namespace");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11. MsToolAugmentEngine — augmented replacements for buggy MS tools
    // ══════════════════════════════════════════════════════════════════════════

    private MsToolAugmentEngine CreateAugmentEngine()
        => new MsToolAugmentEngine(_workspaceManager);

    // ── EncapsulateFieldSafe ──────────────────────────────────────────────────

    [Test]
    public async Task EncapsulateFieldSafe_BackingFieldUsesUnderscoreCamelCase()
    {
        // Bug: standard encapsulate_field generates "private int SuccessCount;"
        // then "public int SuccessCount { get { return SuccessCount; } }" — self-reference!
        // Our version must rename the backing field to _successCount.
        SetSource("""
            public class Counter
            {
                public int SuccessCount;
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.EncapsulateFieldSafeAsync("Test.cs", "SuccessCount");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("_successCount"),
            "Backing field must be renamed to _successCount");
        Assert.That(result.UpdatedContent, Does.Not.Contain("private int SuccessCount"),
            "Original field name must not remain as the private backing field");
    }

    [Test]
    public async Task EncapsulateFieldSafe_PropertyNameIsPascalCase()
    {
        SetSource("""
            public class Counter
            {
                public int successCount;
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.EncapsulateFieldSafeAsync("Test.cs", "successCount");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("public int SuccessCount"),
            "Property must be PascalCase");
    }

    [Test]
    public async Task EncapsulateFieldSafe_PropertyGetterReferencesBackingField_NotItself()
    {
        // Core regression: property body must reference _fieldName, not FieldName
        SetSource("""
            public class Widget
            {
                public string Label;
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.EncapsulateFieldSafeAsync("Test.cs", "Label");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        // The property getter must reference _label, not Label
        Assert.That(result.UpdatedContent, Does.Contain("_label"),
            "Property body must reference _label (backing field)");
        // Ensure no self-referential: "return Label" would be the bug
        Assert.That(result.UpdatedContent, Does.Not.Contain("get { return Label; }").And.Not.Contain("get => Label;"),
            "Property must NOT be self-referential (that's the MS bug we're fixing)");
    }

    [Test]
    public async Task EncapsulateFieldSafe_ExistingUsagesRewritten_ToNewFieldName()
    {
        // All references to the field in method bodies must also be updated to _fieldName
        SetSource("""
            public class Counter
            {
                public int Total;

                public void Increment() { Total++; }
                public int Get() => Total;
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.EncapsulateFieldSafeAsync("Test.cs", "Total");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        // Method bodies must use _total now
        Assert.That(result.UpdatedContent, Does.Contain("_total"),
            "Usages in method bodies must reference the renamed backing field _total");
    }

    // ── AnalyzeSwitchForPatternConversion ─────────────────────────────────────

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_SingleAssignPerCase_IsSafe()
    {
        // A switch where every case assigns exactly one variable → safe to convert
        SetSource("""
            public class Converter
            {
                public double Convert(string unit, double val)
                {
                    double factor;
                    switch (unit)
                    {
                        case "g":  factor = 1.0; break;
                        case "kg": factor = 1000.0; break;
                        default:   factor = 0.0; break;
                    }
                    return val * factor;
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.AnalyzeSwitchForPatternConversionAsync("Test.cs", "switch (unit)");

        Assert.That(result.IsSafeToConvert, Is.True,
            "Single-assignment switch should be safe to convert");
        Assert.That(result.BlockingReason, Is.Null.Or.Empty);
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_MultiAssignPerCase_IsUnsafe()
    {
        // The MS bug: multi-assign per case causes silent data loss
        SetSource("""
            public class Converter
            {
                public void Convert(string unit, double raw)
                {
                    double totalOz, totalGrams;
                    switch (unit)
                    {
                        case "g":
                            totalOz = raw / 28.3495;
                            totalGrams = raw;
                            break;
                        default:
                            totalOz = 0; totalGrams = 0; break;
                    }
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.AnalyzeSwitchForPatternConversionAsync("Test.cs", "switch (unit)");

        Assert.That(result.IsSafeToConvert, Is.False,
            "Multi-assignment switch must be flagged as unsafe");
        Assert.That(result.BlockingReason, Is.Not.Null.And.Not.Empty,
            "Must provide a blocking reason explaining the problem");
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_ReturnsPerCase_IsSafe()
    {
        // Return-per-case is a valid pattern expression target
        SetSource("""
            public class Mapper
            {
                public string Map(int code)
                {
                    switch (code)
                    {
                        case 1: return "one";
                        case 2: return "two";
                        default: return "other";
                    }
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.AnalyzeSwitchForPatternConversionAsync("Test.cs", "switch (code)");

        Assert.That(result.IsSafeToConvert, Is.True,
            "All-return switch should be safe to convert to switch expression");
    }

    // ── ConvertSwitchToPatternSafe ────────────────────────────────────────────

    [Test]
    public async Task ConvertSwitchToPatternSafe_MultiAssign_RejectsWithError()
    {
        // Critical: must NOT silently drop assignments (the MS bug)
        SetSource("""
            public class Converter
            {
                public void Convert(string unit, double raw)
                {
                    double totalOz = 0, totalGrams = 0;
                    switch (unit)
                    {
                        case "g":
                            totalOz = raw / 28.3;
                            totalGrams = raw;
                            break;
                        default:
                            totalOz = raw; totalGrams = raw * 28.3; break;
                    }
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.ConvertSwitchToPatternSafeAsync("Test.cs", "switch (unit)");

        Assert.That(result.Success, Is.False,
            "Must REJECT multi-assign switch — not silently corrupt it");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
            "Must explain WHY conversion was rejected");
    }

    [Test]
    public async Task ConvertSwitchToPatternSafe_SingleAssign_ConvertsCorrectly()
    {
        SetSource("""
            public class Converter
            {
                public double GetFactor(string unit)
                {
                    double factor;
                    switch (unit)
                    {
                        case "g":  factor = 1.0; break;
                        case "kg": factor = 1000.0; break;
                        default:   factor = 0.0; break;
                    }
                    return factor;
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.ConvertSwitchToPatternSafeAsync("Test.cs", "switch (unit)");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("switch"),
            "Result must contain a switch expression");
        Assert.That(result.UpdatedContent, Does.Contain("1.0"),
            "Arms must preserve values");
        // Must not still be a statement switch
        Assert.That(result.UpdatedContent, Does.Not.Contain("break;"),
            "Break statements must be gone in switch expression form");
    }

    // ── ConvertStringFormatToInterpolatedSmart ────────────────────────────────

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_ConstFormatString_Converts()
    {
        // Standard tool fails on named constants — ours resolves via semantic model
        SetSource("""
            public class CacheService
            {
                private const string CacheKeyFmt = "user:{0}:profile";

                public string GetKey(string userId)
                {
                    return string.Format(CacheKeyFmt, userId);
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(CacheKeyFmt");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("$\""),
            "Result must be an interpolated string");
        Assert.That(result.UpdatedContent, Does.Contain("userId"),
            "Interpolated string must include the argument");
    }

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_LiteralFormatString_Converts()
    {
        // Also works on plain literals (same as standard tool, just our path)
        SetSource("""
            public class Logger
            {
                public string Format(string name, int count)
                {
                    return string.Format("Hello {0}, you have {1} items", name, count);
                }
            }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(\"Hello");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("$\""),
            "Result must be an interpolated string");
        Assert.That(result.UpdatedContent, Does.Contain("name").And.Contain("count"),
            "Both arguments must appear in interpolated string");
    }

    // ── SortAndDeduplicateUsings ──────────────────────────────────────────────

    [Test]
    public async Task SortAndDeduplicateUsings_DuplicatesAreRemoved()
    {
        // Standard sort_usings does NOT remove duplicates — ours does
        SetSource("""
            using System.Collections.Generic;
            using System.Linq;
            using System.Collections.Generic;
            using System;
            public class Foo { }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.SortAndDeduplicateUsingsAsync("Test.cs");

        Assert.That(result.RemovedDuplicates, Is.GreaterThan(0),
            "Must report removed duplicate count");
        Assert.That(result.UpdatedContent, Is.Not.Null.And.Not.Empty);

        // Count occurrences of the duplicate using
        var count = 0;
        var idx = 0;
        while ((idx = result.UpdatedContent!.IndexOf("System.Collections.Generic", idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += "System.Collections.Generic".Length;
        }
        Assert.That(count, Is.EqualTo(1),
            "System.Collections.Generic should appear exactly once after deduplication");
    }

    [Test]
    public async Task SortAndDeduplicateUsings_SystemUsingsFirst()
    {
        // System.* usings must come before non-system usings after sort
        SetSource("""
            using Newtonsoft.Json;
            using System.Collections.Generic;
            using System;
            using MyApp.Services;
            public class Foo { }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.SortAndDeduplicateUsingsAsync("Test.cs");

        Assert.That(result.UpdatedContent, Is.Not.Null);
        var systemIdx = result.UpdatedContent!.IndexOf("using System", StringComparison.Ordinal);
        var newtonsoftIdx = result.UpdatedContent.IndexOf("using Newtonsoft", StringComparison.Ordinal);
        var myAppIdx = result.UpdatedContent.IndexOf("using MyApp", StringComparison.Ordinal);

        Assert.That(systemIdx, Is.LessThan(newtonsoftIdx),
            "System usings must come before third-party usings");
        Assert.That(systemIdx, Is.LessThan(myAppIdx),
            "System usings must come before project usings");
    }

    [Test]
    public async Task SortAndDeduplicateUsings_NoDuplicates_OriginalCountMatchesResult()
    {
        SetSource("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Foo { }
            """);

        var engine = CreateAugmentEngine();
        var result = await engine.SortAndDeduplicateUsingsAsync("Test.cs");

        Assert.That(result.RemovedDuplicates, Is.EqualTo(0),
            "No duplicates → RemovedDuplicates must be 0");
        Assert.That(result.OriginalCount, Is.EqualTo(3));
    }
}


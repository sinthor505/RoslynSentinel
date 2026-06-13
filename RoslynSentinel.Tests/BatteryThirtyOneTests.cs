// Battery #31 — Regression tests for inline_class and convert_method_to_indexer
// Covers the two bugs fixed in this battery:
//   1. inline_class was an unimplemented stub (InvalidOperationException)
//   2. convert_method_to_indexer had silent no-op on method-not-found / wrong param count

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryThirtyOneTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private StandardRefactoringEngine _standardRefactoringEngine;
    private AdvancedStructuralEngine _advancedStructuralEngine;
    private MappingEngine _mappingEngine;
    private SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private GranularRefactoringEngine _granularRefactoringEngine;
    private AdvancedLogicEngine _advancedLogicEngine;
    private RefinementEngine _refinementEngine;
    private AdvancedTypeEngine _advancedTypeEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeFlowEngine _codeFlowEngine;
    private AdvancedRefactoringEngine _advancedRefactoringEngine;
    private LogicOptimizationEngine _logicOptimizationEngine;
    private ModernizationEngine _modernizationEngine;
    private SentinelRefactoringTools _tools;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _standardRefactoringEngine = new StandardRefactoringEngine(_workspaceManager);
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _mappingEngine = new MappingEngine(_workspaceManager);
        _semanticRefactoringLibrary = new SemanticRefactoringLibrary(_workspaceManager);
        _granularRefactoringEngine = new GranularRefactoringEngine(_workspaceManager);
        _advancedLogicEngine = new AdvancedLogicEngine(_workspaceManager);
        _refinementEngine = new RefinementEngine(_workspaceManager);
        _advancedTypeEngine = new AdvancedTypeEngine(_workspaceManager);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, _config);
        _codeFlowEngine = new CodeFlowEngine(_workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(_workspaceManager);
        _logicOptimizationEngine = new LogicOptimizationEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, _config);

        _tools = new SentinelRefactoringTools(
            _refactoringEngine, _standardRefactoringEngine, _advancedStructuralEngine,
            _mappingEngine, _semanticRefactoringLibrary, _granularRefactoringEngine,
            _advancedLogicEngine, _refinementEngine, _advancedTypeEngine,
            _structuralRefinementEngine, _codeStyleEngine, _codeFlowEngine,
            _advancedRefactoringEngine, _logicOptimizationEngine, _modernizationEngine,
            new OutParamRefactoringEngine(_workspaceManager),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            _workspaceManager, _config, NullLogger<SentinelRefactoringTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultiFile(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ======================================================================
    // inline_class — Bug Fix: Was throwing InvalidOperationException (stub)
    // ======================================================================

    [Test]
    public async Task InlineClass_SameFile_MovesPublicMembers()
    {
        const string src = @"
public class Helper { public int Value = 42; public void Go() {} }
public class Owner {}";
        SetSource(src, "SameFile.cs");
        var result = await _tools.InlineClass("SameFile.cs", "SameFile.cs", "Helper");

        Assert.That(result, Does.ContainKey("SameFile.cs"), "Should produce updated file");
        var content = ((Dictionary<string, string>)result.Data)["SameFile.cs"];
        Assert.That(content, Does.Contain("Value"), "Owner should contain the inlined field");
        Assert.That(content, Does.Contain("Go"), "Owner should contain the inlined method");
        Assert.That(content, Does.Not.Contain("class Helper"), "Helper class should be removed");
        Assert.That(content, Does.Contain("class Owner"), "Owner class should remain");
    }

    [Test]
    public async Task InlineClass_SameFile_ReturnsDictionaryNotThrow()
    {
        // Regression: was throwing InvalidOperationException as a stub
        const string src = @"
public class HelperClass { public string Tag = ""hello""; }
public class Consumer {}";
        SetSource(src, "File.cs");
        // Must not throw
        Dictionary<string, string>? result = null;
        Assert.DoesNotThrowAsync(async () =>
        {
            var rawResult = await _tools.InlineClass("File.cs", "File.cs", "HelperClass");
            result = (Dictionary<string, string>?)rawResult.Data;
        });
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task InlineClass_SameFile_MultipleMembers_AllMoved()
    {
        const string src = @"
public class DataClass {
    public int Alpha;
    public string Beta = """";
    public void Gamma() {}
    public int Delta() => 1;
}
public class Target {}";
        SetSource(src, "Multi.cs");
        var result = await _tools.InlineClass("Multi.cs", "Multi.cs", "DataClass");
        var content = ((Dictionary<string, string>)result.Data)["Multi.cs"];
        Assert.That(content, Does.Contain("Alpha"));
        Assert.That(content, Does.Contain("Beta"));
        Assert.That(content, Does.Contain("Gamma"));
        Assert.That(content, Does.Contain("Delta"));
        Assert.That(content, Does.Not.Contain("class DataClass"));
    }

    [Test]
    public async Task InlineClass_CrossFile_MovesMembers()
    {
        SetMultiFile(
            ("Source.cs", "namespace App; public class Helper { public int Value; public void Act() {} }"),
            ("Target.cs", "namespace App; public class Owner {}"));
        var result = await _tools.InlineClass("Source.cs", "Target.cs", "Helper");

        Assert.That(result, Does.ContainKey("Target.cs"), "Target file should be updated");
        var targetContent = ((Dictionary<string, string>)result.Data)["Target.cs"];
        Assert.That(targetContent, Does.Contain("Value"), "Value field should be in target");
        Assert.That(targetContent, Does.Contain("Act"), "Act method should be in target");
    }

    [Test]
    public async Task InlineClass_CrossFile_SourceClassRemoved()
    {
        SetMultiFile(
            ("Source.cs", "namespace App; public class Helper { public int X; }"),
            ("Target.cs", "namespace App; public class Owner {}"));
        var result = await _tools.InlineClass("Source.cs", "Target.cs", "Helper");

        Assert.That(result, Does.ContainKey("Source.cs"), "Source file should also be returned");
        var sourceContent = ((Dictionary<string, string>)result.Data)["Source.cs"];
        Assert.That(sourceContent, Does.Not.Contain("class Helper"), "Helper should be removed from source");
    }

    [Test]
    public async Task InlineClass_ClassNotFound_ReturnsErrorKey()
    {
        const string src = "public class Existing {}";
        SetSource(src, "File.cs");
        var result = await _tools.InlineClass("File.cs", "File.cs", "NonExistent");

        Assert.That(result, Does.ContainKey("__error__"), "Should return __error__ key");
        var errorContent = ((Dictionary<string, string>)result.Data)["__error__"];
        Assert.That(errorContent, Does.Contain("NonExistent"), "Error should mention the missing class");
    }

    [Test]
    public async Task InlineClass_SourceFileNotFound_ReturnsErrorKey()
    {
        SetSource("public class Existing {}", "File.cs");
        var result = await _tools.InlineClass("doesnotexist.cs", "File.cs", "Anything");

        Assert.That(result, Does.ContainKey("__error__"), "Should return __error__ key");
    }

    [Test]
    public async Task InlineClass_TargetFileNotFound_ReturnsErrorKey()
    {
        SetSource("public class Src {}", "Source.cs");
        var result = await _tools.InlineClass("Source.cs", "doesnotexist.cs", "Src");

        Assert.That(result, Does.ContainKey("__error__"), "Should return __error__ key");
    }

    [Test]
    public async Task InlineClass_SameFile_NoOtherClass_ReturnsErrorKey()
    {
        const string src = "public class LoneClass { public int X; }";
        SetSource(src, "Lone.cs");
        var result = await _tools.InlineClass("Lone.cs", "Lone.cs", "LoneClass");

        Assert.That(result, Does.ContainKey("__error__"), "Should return error — no target class to inline into");
    }

    [Test]
    public async Task InlineClass_EmptyClass_ProducesValidResult()
    {
        const string src = @"
public class Empty {}
public class Recipient { public int Existing; }";
        SetSource(src, "F.cs");
        var result = await _tools.InlineClass("F.cs", "F.cs", "Empty");

        // Empty class inlined — should succeed, Recipient should still exist, Empty removed
        Assert.That(result, Does.ContainKey("F.cs"));
        var updatedContent = ((Dictionary<string, string>)result.Data)["F.cs"];
        Assert.That(updatedContent, Does.Contain("class Recipient"));
        Assert.That(updatedContent, Does.Not.Contain("class Empty"));
    }

    // ======================================================================
    // inline_class — Cross-file type reference updates (⭐⭐⭐⭐⭐)
    // ======================================================================

    [Test]
    public async Task InlineClass_CrossFile_UpdatesTypeReferencesInThirdFile()
    {
        // A third file that holds a variable typed as 'Helper'. After inlining Helper into Owner,
        // that reference should be renamed to 'Owner' in the returned dictionary.
        SetMultiFile(
            ("Helper.cs", "namespace App; public class Helper { public int Value; }"),
            ("Owner.cs", "namespace App; public class Owner {}"),
            ("Consumer.cs", "namespace App; public class Consumer { public Helper? Instance; }"));

        var result = await _tools.InlineClass("Helper.cs", "Owner.cs", "Helper");

        // Primary files updated
        Assert.That(result, Does.ContainKey("Owner.cs"), "Target file should be in result");
        Assert.That(((Dictionary<string, string>)result.Data)["Owner.cs"], Does.Contain("Value"), "Owner should contain inlined member");

        // Third file should also be updated: 'Helper' → 'Owner'
        Assert.That(result, Does.ContainKey("Consumer.cs"), "Third file with type reference should also be updated");
        Assert.That(((Dictionary<string, string>)result.Data)["Consumer.cs"], Does.Not.Contain("Helper"), "Old class name should be gone");
        Assert.That(((Dictionary<string, string>)result.Data)["Consumer.cs"], Does.Contain("Owner"), "New class name should appear");
    }

    [Test]
    public async Task ExtractClass_SameFile_ExposesMembersViaPublicProperty()
    {
        // After extraction, the source class should expose the new class via a public property, not a private field
        const string src = @"
public class Service
{
    public void Process() {}
    public void Validate() {}
    public string Name { get; set; }
}";
        SetSource(src, "Service.cs");
        var rawResult = await _tools.ExtractMembers("Service.cs", "Service", "class", "Validator", new[] { "Process", "Validate" });
        var result = (Dictionary<string, string>?)rawResult.Data;

        Assert.That(result, Does.ContainKey("Service.cs"), "Updated source should be in result");
        var updatedSource = ((Dictionary<string, string>)result)["Service.cs"];
        // Should have public property, NOT private readonly field
        Assert.That(updatedSource, Does.Contain("public Validator"), "Should expose extracted class via public property");
        Assert.That(updatedSource, Does.Not.Contain("private readonly Validator"), "Should not use private field");
    }

    [Test]
    public async Task ExtractClass_CrossFile_UpdatesCallSitesInThirdFile()
    {
        // Third file calls 'Process' and 'Validate' on a Service instance.
        // After extraction into 'Validator', those calls should go through Validator.
        SetMultiFile(
            ("Service.cs", "namespace App; public class Service { public void Process() {} public void Validate() {} public string Name { get; set; } }"),
            ("Client.cs", "namespace App; public class Client { public void Run(Service s) { s.Process(); s.Validate(); } }"));

        var rawResult = await _tools.ExtractMembers("Service.cs", "Service", "class", "Validator", new[] { "Process", "Validate" });
        var result = (Dictionary<string, string>?)rawResult.Data;

        Assert.That(result, Does.ContainKey("Client.cs"), "Client file should be updated with new call sites");
        var clientContent = ((Dictionary<string, string>)result)["Client.cs"];
        // Call sites should now go through the Validator property
        Assert.That(clientContent, Does.Contain("Validator"), "Cross-file call sites should reference the extracted class");
    }

    // ======================================================================
    // convert_method_to_indexer — Bug Fix: Silent no-op on error conditions
    // ======================================================================

    [Test]
    public async Task ConvertMethodToIndexer_MethodNotFound_ReturnsErrorComment()
    {
        const string src = "public class MyClass { public int Compute(int x) => x * 2; }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "NoSuchMethod");

        // Regression: was silently returning original content with no indication of failure
        Assert.That(result.UpdatedText!, Does.StartWith("// ERROR:"), "Should return error comment when method not found");
        Assert.That(result.UpdatedText!, Does.Contain("NoSuchMethod"), "Error should mention the method name");
    }

    [Test]
    public async Task ConvertMethodToIndexer_ZeroParams_ReturnsErrorComment()
    {
        const string src = "public class MyClass { public int GetValue() => 42; }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "GetValue");

        Assert.That(result.UpdatedText!, Does.StartWith("// ERROR:"), "Zero-param method cannot become indexer");
        Assert.That(result.UpdatedText!, Does.Contain("GetValue"), "Error should name the method");
    }

    [Test]
    public async Task ConvertMethodToIndexer_TwoParams_ReturnsErrorComment()
    {
        const string src = "public class MyClass { public int Get(int row, int col) => row + col; }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "Get");

        Assert.That(result.UpdatedText!, Does.StartWith("// ERROR:"), "Two-param method cannot become indexer");
        Assert.That(result.UpdatedText!, Does.Contain("Get"), "Error should name the method");
    }

    [Test]
    public async Task ConvertMethodToIndexer_StaticMethod_ReturnsErrorComment()
    {
        const string src = "public class MyClass { public static int Get(int i) => i; }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "Get");

        Assert.That(result.UpdatedText!, Does.StartWith("// ERROR:"), "Static method cannot become indexer");
        Assert.That(result.UpdatedText!, Does.Contain("static"), "Error should mention static");
    }

    [Test]
    public async Task ConvertMethodToIndexer_AbstractNoBody_ReturnsErrorComment()
    {
        const string src = "public abstract class Base { public abstract int Get(int i); }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "Get");

        Assert.That(result.UpdatedText!, Does.StartWith("// ERROR:"), "Abstract method with no body cannot become indexer");
    }

    [Test]
    public async Task ConvertMethodToIndexer_BlockBody_CreatesIndexer()
    {
        const string src = @"
public class MyList {
    private int[] _data = new int[10];
    public int Get(int i) { return _data[i]; }
}";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "Get");

        Assert.That(result.UpdatedText!, Does.Not.StartWith("// ERROR:"), "Should succeed for valid block-body method");
        Assert.That(result.UpdatedText!, Does.Contain("this[int i]"), "Should produce indexer syntax");
        Assert.That(result.UpdatedText!, Does.Not.Contain("Get(int i)"), "Original method should be replaced");
    }

    [Test]
    public async Task ConvertMethodToIndexer_ExpressionBody_CreatesIndexer()
    {
        const string src = @"
public class MyList {
    private int[] _data = new int[10];
    public int Get(int index) => _data[index];
}";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "Get");

        Assert.That(result.UpdatedText!, Does.Not.StartWith("// ERROR:"), "Should succeed for expression-body method");
        Assert.That(result.UpdatedText!, Does.Contain("this[int index]"), "Should produce indexer");
    }

    [Test]
    public async Task ConvertMethodToIndexer_UnknownFile_ReturnsEmpty()
    {
        SetSource("public class X {}", "Known.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("unknown_file.cs", "Get");

        Assert.That(result, Is.Empty, "File not found should return empty string");
    }

    [Test]
    public async Task ConvertMethodToIndexer_SuccessPreservesReturnType()
    {
        const string src = @"
public class Lookup {
    private string[] _items = new[] { ""a"", ""b"" };
    public string Item(int n) => _items[n];
}";
        SetSource(src, "Lookup.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("Lookup.cs", "Item");

        Assert.That(result, Does.Contain("string"), "Indexer should preserve string return type");
        Assert.That(result, Does.Contain("this[int n]"), "Indexer should use original parameter name");
    }

    [Test]
    public async Task ConvertMethodToIndexer_ErrorPreservesOriginalContent()
    {
        // When an error is returned, the original content should still be present after the comment
        const string src = "public class C { public int Compute(int x) => x; }";
        SetSource(src, "C.cs");
        var result = await _granularRefactoringEngine.ConvertMethodToIndexerAsync("C.cs", "NotAMethod");

        Assert.That(result, Does.Contain("class C"), "Original source should be included after error comment");
        Assert.That(result, Does.Contain("Compute"), "Original method should still be present");
    }
}

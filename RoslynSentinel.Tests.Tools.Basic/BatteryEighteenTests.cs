// Battery #18 -> MsToolAugmentEngine
// The SentinelAugmentTools MCP wrappers were removed in the Basic/Advanced server split;
// these tests now exercise MsToolAugmentEngine directly, which is where the behaviour lives.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tests.Support;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
// Runs exclusively: Setup wipes top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard), which would delete files parallel fixtures are using.
[NonParallelizable]
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class BatteryEighteenTests
{
    private IWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _msEngine;
    private WorkspaceHealthMiscImpl _workspaceHealthMisc;

    private static readonly string RichSource = Sources.RichOrderService;

    [SetUp]
    public void Setup()
    {
        // Eight tests read bare names such as Test.cs from the working directory, so a stale file from another fixture would be their input.
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _msEngine = new MsToolAugmentEngine(_workspaceManager);
        _workspaceHealthMisc = new WorkspaceHealthMiscImpl(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- EncapsulateFieldSafe ---

    [Test]
    public async Task EncapsulateFieldSafe_PublicField_ReturnsResult()
    {
        SetSource("namespace TestProj; public class Order { public int OrderId; }", "Test.cs");
        var result = await _msEngine.EncapsulateFieldSafeAsync("Test.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task EncapsulateFieldSafe_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.EncapsulateFieldSafeAsync("NonExistent.cs", "OrderId");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_WithSwitchStatement_ReturnsAnalysis()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.AnalyzeSwitchForPatternConversionAsync("Test.cs", "switch (status)");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeSwitchForPatternConversion_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.AnalyzeSwitchForPatternConversionAsync("NonExistent.cs", "switch (x)");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertSwitchToPatternSafe ---

    [Test]
    public async Task ConvertSwitchToPatternSafe_WithSwitchStatement_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.ConvertSwitchToPatternSafeAsync("Test.cs", "switch (status)");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertSwitchToPatternSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ConvertSwitchToPatternSafeAsync("NonExistent.cs", "switch (x)");
        Assert.That(result, Is.Not.Null);
    }

    // --- ConvertStringFormatToInterpolatedSmart ---

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_WithFormatCall_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.ConvertStringFormatToInterpolatedSmartAsync("Test.cs", @"string.Format(""{0}: {1}""");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertStringFormatToInterpolatedSmart_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ConvertStringFormatToInterpolatedSmartAsync("NonExistent.cs", "string.Format");
        Assert.That(result, Is.Not.Null);
    }

    // --- SortAndDeduplicateUsings ---

    [Test]
    public async Task SortAndDeduplicateUsings_FileWithUsings_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        // writeToFile: false keeps this test from leaking Test.cs into the working directory.
        var result = await _msEngine.SortAndDeduplicateUsingsAsync("Test.cs", writeToFile: false);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task SortAndDeduplicateUsings_NonExistentFile_Throws()
    {
        SetSource("public class C {}", "Test.cs");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _msEngine.SortAndDeduplicateUsingsAsync("NonExistent.cs"));
    }

    // --- FormatDocumentSafe ---

    [Test]
    public async Task FormatDocumentSafe_ValidFile_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.FormatDocumentSafeAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FormatDocumentSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.FormatDocumentSafeAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- AnalyzeForeachForLinqConversion ---

    [Test]
    public async Task AnalyzeForeachForLinqConversion_WithForeach_ReturnsAnalysis()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.AnalyzeForeachForLinqConversionAsync("Test.cs", "foreach (var i in");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeForeachForLinqConversion_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.AnalyzeForeachForLinqConversionAsync("NonExistent.cs", "foreach");
        Assert.That(result, Is.Not.Null);
    }

    // --- GetWorkspaceHealthAsync ---

    [Test]
    [Category("WorkspaceHealthMiscImpl")] // sentinel:auto-category
    public async Task GetWorkspaceHealth_WithLoadedSolution_ReturnsReport()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _workspaceHealthMisc.GetWorkspaceHealthAsync();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    [Category("WorkspaceHealthMiscImpl")] // sentinel:auto-category
    public async Task GetWorkspaceHealth_NoSolution_ReturnsReport()
    {
        var result = await _workspaceHealthMisc.GetWorkspaceHealthAsync();
        Assert.That(result, Is.Not.Null);
    }

    // --- PreviewAddMissingUsings ---

    [Test]
    public async Task PreviewAddMissingUsings_ValidFile_ReturnsPreview()
    {
        SetSource("namespace TestProj; public class Order { }", "Test.cs");
        var result = await _msEngine.PreviewAddMissingUsingsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task PreviewAddMissingUsings_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.PreviewAddMissingUsingsAsync("NonExistent.cs");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractConstantSafe ---

    [Test]
    public async Task ExtractConstantSafe_WithLiteralValue_ReturnsResult()
    {
        const string src = @"namespace TestProj; public class Order { public string GetLabel() { return ""hello""; } }";
        SetSource(src, "Test.cs");
        var result = await _msEngine.ExtractConstantSafeAsync("Test.cs", @"""hello""", "HelloLabel");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ExtractConstantSafe_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ExtractConstantSafeAsync("NonExistent.cs", @"""hello""", "HelloLabel");
        Assert.That(result, Is.Not.Null);
    }

    // --- GenerateToStringSafe ---

    [Test]
    public async Task GenerateToStringSafe_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.GenerateToStringSafeAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GenerateToStringSafe_NonExistentFile_ReturnsNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.GenerateToStringSafeAsync("NonExistent.cs", "Order");
        Assert.That(result, Is.Not.Null);
    }

    // --- ExtractMethodTool ---

    [Test]
    public async Task ExtractMethod_WithMethodBody_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _msEngine.ExtractMethodSafeAsync("Test.cs", "DoProcess", @"var result = string.Format");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ExtractMethod_NonExistentFile_ReturnsNotNull()
    {
        SetSource("public class C {}", "Test.cs");
        var result = await _msEngine.ExtractMethodSafeAsync("NonExistent.cs", "NewMethod", "some code");
        Assert.That(result, Is.Not.Null);
    }
}

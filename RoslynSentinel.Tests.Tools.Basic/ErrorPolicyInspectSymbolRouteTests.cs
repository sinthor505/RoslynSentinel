using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// InspectSymbol's not-found returns record the missing-target kind on the error-policy side channel. A missing file records "file"
// through DocumentLookupResult.ToResultError. A snippet that does not resolve to a symbol records "symbol" on the info and blastRadius
// paths (blastRadius only when the analyzer reports ToolErrorCode.NotFound). The returned ResultError text is unchanged; the last test pins it.
[TestFixture]
public class ErrorPolicyInspectSymbolRouteTests
{
    // Line 2 is a comment: a snippet on it matches text but binds to no symbol.
    private const string Source = "namespace Sample;\n// marker comment\npublic class Widget\n{\n    public int Run() => 1;\n}\n";

    private FakeWorkspaceManager _fake;
    private SymbolNavigationImpl _symbols;
    private string _dir;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ErrorPolicyInspectSymbolRoute_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fake = FakeWorkspaceManager.ReadOnly();
        _symbols = new SymbolNavigationImpl(
            Battery.Basic.TestEngines.SymbolNavigationEngine(_fake, NullLogger<SymbolNavigationEngine>.Instance),
            Battery.Basic.TestEngines.ImpactAnalyzer(_fake, NullLogger<ImpactAnalyzer>.Instance),
            _fake,
            NullLogger<SymbolNavigationImpl>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _fake.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void UseSolutionWithDocuments(params (string Path, string Text)[] docs) =>
        _fake.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(_dir, "TestProj.csproj"),
            docs.Select(d => (Path.GetFileName(d.Path), d.Text, d.Path)).ToArray()));

    private static ToolCallContext StartCall()
    {
        var ctx = new ToolCallContext(null, null, "InspectSymbol", null, 0);
        ToolCallContext.Set(ctx);
        return ctx;
    }

    [Test]
    public async Task InspectSymbol_MissingFile_RecordsFileKind()
    {
        var present = Path.Combine(_dir, "Widget.cs");
        var missing = Path.Combine(_dir, "Missing.cs");
        UseSolutionWithDocuments((present, Source));
        var ctx = StartCall();

        var result = await _symbols.InspectSymbol(
            reason: "test error policy missing file kind", missing, "public class Widget", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.EqualTo("file"));
    }

    [Test]
    public async Task InspectSymbol_InfoSnippetNotInFile_RecordsSymbolKind()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));
        var ctx = StartCall();

        var result = await _symbols.InspectSymbol(
            reason: "test error policy info snippet not in file", path, "this snippet is not in the file", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.EqualTo("symbol"));
    }

    [Test]
    public async Task InspectSymbol_InfoSnippetOnNonSymbolNode_RecordsSymbolKind()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));
        var ctx = StartCall();

        var result = await _symbols.InspectSymbol(
            reason: "test error policy info snippet on comment", path, "// marker comment", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.EqualTo("symbol"));
    }

    [Test]
    public async Task InspectSymbol_BlastRadiusSymbolMiss_RecordsSymbolKind()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));
        var ctx = StartCall();

        var result = await _symbols.InspectSymbol(
            reason: "test error policy blast radius symbol miss", path, "// marker comment", InspectSymbolAspect.blastRadius);

        Assert.That(result.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.Not.Null);
    }

    [Test]
    public async Task InspectSymbol_MissingFile_ErrorTextIsUnchanged()
    {
        var present = Path.Combine(_dir, "Widget.cs");
        var missing = Path.Combine(_dir, "Missing.cs");
        UseSolutionWithDocuments((present, Source));
        StartCall();

        var result = await _symbols.InspectSymbol(
            reason: "test error policy missing file text", missing, "public class Widget", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)));
        Assert.That(result.ErrorData?.Message, Does.Contain("Missing.cs"));
    }
}

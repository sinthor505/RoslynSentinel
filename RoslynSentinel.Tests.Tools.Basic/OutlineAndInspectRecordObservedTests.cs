using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// GetFileOutline and InspectSymbol(aspect: info) record the WHOLE-file text hash of the file they read (the
// declaring file for InspectSymbol) in the observed-content ledger on success only. InspectSymbol(aspect:
// blastRadius) is a read-only analysis and records nothing. Read-back uses TryGetObservedHash on the fake, which
// keys entries per agent exactly like PersistentWorkspaceManager.
[TestFixture]
public class OutlineAndInspectRecordObservedTests
{
    private const string AgentId = "agent-outliner";
    private const string Source = "namespace Sample;\npublic class Widget\n{\n    public int Run() => 1;\n    public int Other() => 2;\n}\n";
    private const string UnrelatedSource = "namespace Sample;\npublic class Unrelated\n{\n}\n";
    private const string LibSource = "namespace Sample;\npublic static class Lib\n{\n    public static int Compute() => 1;\n}\n";
    private const string UserSource = "namespace Sample;\npublic class User\n{\n    public int Use() => Lib.Compute();\n}\n";

    private FakeWorkspaceManager _fake;
    private WorkspaceTools _tools;
    private SymbolNavigationImpl _symbols;
    private string _dir;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(new ToolCallContext(AgentId, null, "GetFileOutline", null, 0));
        _dir = Path.Combine(Path.GetTempPath(), "OutlineAndInspectRecordObserved_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fake = FakeWorkspaceManager.ReadOnly();
        _tools = Battery.Basic.TestTools.WorkspaceTools(_fake);
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

    private bool TryObserved(string path, out string hash) =>
        _fake.TryGetObservedHash(new FilePathWrapper(path), out hash);

    private string ObservedHashOf(string path)
    {
        Assert.That(TryObserved(path, out var hash), Is.True, $"No observed entry was recorded for '{path}'.");
        return hash;
    }

    // ---- GetFileOutline (row I) ----

    [Test]
    public async Task GetFileOutline_SolutionDocument_RecordsHashOfWholeFile()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));

        var result = await _tools.GetFileOutlineTool(reason: "test observed outline", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(path), Is.EqualTo(ContentFingerprint.Compute(Source)));
    }

    [Test]
    public async Task GetFileOutline_FileNotInSolution_RecordsNothing()
    {
        var present = Path.Combine(_dir, "Widget.cs");
        var missing = Path.Combine(_dir, "Missing.cs");
        UseSolutionWithDocuments((present, Source));

        var result = await _tools.GetFileOutlineTool(reason: "test observed outline not found", missing);

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(missing, out _), Is.False, "A not-found outline must not record an observed hash.");
        Assert.That(TryObserved(present, out _), Is.False, "A failed outline must not record any other file either.");
    }

    // ---- InspectSymbol (row J) ----

    [Test]
    public async Task InspectSymbolInfo_DeclaredInSolutionFile_RecordsHashOfWholeDeclaringFile()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));

        var result = await _symbols.InspectSymbol(
            reason: "test observed inspect info", path, "public class Widget", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var hash = ObservedHashOf(path);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(Source)), "The whole declaring-file hash must be recorded.");
        Assert.That(hash, Is.Not.EqualTo(ContentFingerprint.Compute("public class Widget")), "The snippet hash must never be recorded.");
    }

    [Test]
    public async Task InspectSymbolInfo_SymbolDeclaredInOtherFile_RecordsDeclaringFileNotReferencingFile()
    {
        var lib = Path.Combine(_dir, "Lib.cs");
        var user = Path.Combine(_dir, "User.cs");
        UseSolutionWithDocuments((lib, LibSource), (user, UserSource));

        var result = await _symbols.InspectSymbol(
            reason: "test observed inspect cross-file", user, "Lib.Compute()", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(lib), Is.EqualTo(ContentFingerprint.Compute(LibSource)), "The declaring file's whole-file hash must be recorded.");
        Assert.That(TryObserved(user, out _), Is.False, "The referencing file must not be recorded.");
    }

    [Test]
    public async Task InspectSymbolBlastRadius_ResolvableSymbol_RecordsNothing()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        var other = Path.Combine(_dir, "Unrelated.cs");
        UseSolutionWithDocuments((path, Source), (other, UnrelatedSource));

        var result = await _symbols.InspectSymbol(
            reason: "test observed inspect blast radius", path, "public class Widget", InspectSymbolAspect.blastRadius);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(TryObserved(path, out _), Is.False, "blastRadius must not record an observed hash for the declaring file.");
        Assert.That(TryObserved(other, out _), Is.False, "blastRadius must not record an observed hash for any other file.");
    }

    [Test]
    public async Task InspectSymbolInfo_SnippetDoesNotResolve_RecordsNothing()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocuments((path, Source));

        var result = await _symbols.InspectSymbol(
            reason: "test observed inspect unresolved", path, "this snippet is not in the file", InspectSymbolAspect.info);

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(path, out _), Is.False, "A failed inspect must not record an observed hash.");
    }
}

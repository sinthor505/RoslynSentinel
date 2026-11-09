using System.Reflection;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Read-navigation tools record the WHOLE-file text hash in the observed-content ledger on their content-returning
// success paths: GetMethodSource (the file containing the method) and SearchSolutionText (each returned document,
// once). Failure branches record nothing, ListAll never records, and a document whose hits were
// cut by maxResults is not recorded. GetMethodSource cases run on the fake; SearchSolutionText cases run on the real
// PersistentWorkspaceManager because FakeWorkspaceManager throws NotImplementedException on the text-search path.
// Read-back uses TryGetObservedHash, which keys entries per agent.
[TestFixture]
public class ReadNavigationRecordsObservedTests
{
    private const string AgentId = "agent-navigator";
    private const string WidgetSource = "namespace Sample;\npublic class Widget\n{\n    public int Run() => 1;\n    public int Other() => 2;\n}\n";

    private FakeWorkspaceManager _fake;
    private WorkspaceReadNavigationImpl _impl;
    private string _dir;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(new ToolCallContext(AgentId, null, "GetMethodSource", null, 0));
        _dir = Path.Combine(Path.GetTempPath(), "ReadNavigationRecordsObserved_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fake = FakeWorkspaceManager.ReadOnly();
        _impl = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(_fake, NullLogger<WorkspaceReadNavigationImpl>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _fake.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Solution BuildSolution(params (string Path, string Text)[] docs) =>
        TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(_dir, "TestProj.csproj"),
            docs.Select(d => (Path.GetFileName(d.Path), d.Text, d.Path)).ToArray());

    private void UseSolutionWith(params (string Path, string Text)[] docs) =>
        _fake.SetTestSolution(BuildSolution(docs));

    private bool TryObserved(string path, out string hash) =>
        _fake.TryGetObservedHash(new FilePathWrapper(path), out hash);

    private string ObservedHashOf(string path)
    {
        Assert.That(TryObserved(path, out var hash), Is.True, $"No observed entry was recorded for '{path}'.");
        return hash;
    }

    private static bool ObservedIn(PersistentWorkspaceManager pwm, string path, out string hash) =>
        pwm.TryGetObservedHash(new FilePathWrapper(path), out hash);

    private static string ObservedHashIn(PersistentWorkspaceManager pwm, string path)
    {
        Assert.That(ObservedIn(pwm, path, out var hash), Is.True, $"No observed entry was recorded for '{path}'.");
        return hash;
    }

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task GetMethodSource_Success_RecordsHashOfWholeFile_NotTheMethodText()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWith((path, WidgetSource));

        var result = await _impl.GetMethodSource(reason: "test observed method source", path, "Run");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var methodSource = (string)GetProp(result.SuccessData!, "Source")!;
        var hash = ObservedHashOf(path);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(WidgetSource)), "The whole-file hash must be recorded.");
        Assert.That(hash, Is.Not.EqualTo(ContentFingerprint.Compute(methodSource)), "The method-text hash must never be recorded.");
    }

    [Test]
    public async Task GetMethodSource_MethodNotFound_RecordsNothing()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWith((path, WidgetSource));

        var result = await _impl.GetMethodSource(reason: "test observed method missing", path, "NoSuchMethod");

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(path, out _), Is.False, "A method-not-found read must not record an observed hash.");
    }

    [Test]
    public async Task GetMethodSource_FileNotFound_RecordsNothing()
    {
        var present = Path.Combine(_dir, "Widget.cs");
        var missing = Path.Combine(_dir, "Missing.cs");
        UseSolutionWith((present, WidgetSource));

        var result = await _impl.GetMethodSource(reason: "test observed file missing", missing, "Run");

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(missing, out _), Is.False, "A file-not-found read must not record an observed hash.");
        Assert.That(TryObserved(present, out _), Is.False, "Nothing in the solution may be recorded by a failed read.");
    }

    [Test]
    public async Task ListAll_SolutionDocuments_RecordsNothing()
    {
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWith((path, WidgetSource));

        var result = await _impl.ListAll(reason: "test observed list all");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(TryObserved(path, out _), Is.False, "ListAll must not record an observed hash.");
    }

    [Test]
    public async Task Search_TwoDocumentsWithOneHitEach_RecordsBothWholeFileHashes()
    {
        var a = Path.Combine(_dir, "A.cs");
        var b = Path.Combine(_dir, "B.cs");
        const string aText = "class A\n{\n    // Needle here\n}\n";
        const string bText = "class B\n{\n    // Needle there\n}\n";
        using var pwm = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance, solutionPath: Path.Combine(_dir, "Test.slnx"));
        pwm.SetTestSolution(BuildSolution((a, aText), (b, bText)));
        var impl = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(pwm, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var result = await impl.SearchSolutionText(reason: "test observed search two documents", pattern: "Needle");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashIn(pwm, a), Is.EqualTo(ContentFingerprint.Compute(aText)));
        Assert.That(ObservedHashIn(pwm, b), Is.EqualTo(ContentFingerprint.Compute(bText)));
    }

    [Test]
    public async Task Search_MaxResultsOne_RecordsOnlyTheDocumentOfTheReturnedHit()
    {
        var a = Path.Combine(_dir, "A.cs");
        var b = Path.Combine(_dir, "B.cs");
        const string aText = "class A\n{\n    // Needle here\n}\n";
        const string bText = "class B\n{\n    // Needle there\n}\n";
        using var pwm = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance, solutionPath: Path.Combine(_dir, "Test.slnx"));
        pwm.SetTestSolution(BuildSolution((a, aText), (b, bText)));
        var impl = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(pwm, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var result = await impl.SearchSolutionText(reason: "test observed search max results", pattern: "Needle", maxResults: 1);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((TextSearchResult)result.SuccessData!).LiteralResults.Count, Is.EqualTo(1), "Exactly one hit is returned.");
        Assert.That(ObservedHashIn(pwm, a), Is.EqualTo(ContentFingerprint.Compute(aText)), "A.cs holds the returned hit and must be recorded.");
        Assert.That(ObservedIn(pwm, b, out _), Is.False, "B.cs was trimmed by maxResults and must not be recorded.");
    }

    [Test]
    public async Task Search_ScannedDocumentWithoutHit_IsNotRecorded()
    {
        var a = Path.Combine(_dir, "A.cs");
        var c = Path.Combine(_dir, "C.cs");
        const string aText = "class A\n{\n    // Needle here\n}\n";
        const string cText = "class C\n{\n    // nothing relevant\n}\n";
        using var pwm = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance, solutionPath: Path.Combine(_dir, "Test.slnx"));
        pwm.SetTestSolution(BuildSolution((a, aText), (c, cText)));
        var impl = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(pwm, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var result = await impl.SearchSolutionText(reason: "test observed search no hit document", pattern: "Needle");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashIn(pwm, a), Is.EqualTo(ContentFingerprint.Compute(aText)));
        Assert.That(ObservedIn(pwm, c, out _), Is.False, "A scanned document without a returned hit must not be recorded.");
    }

    [Test]
    public async Task Search_DocumentWithSeveralHits_RecordsItsWholeFileHashOnce()
    {
        var path = Path.Combine(_dir, "Many.cs");
        const string text = "class Many\n{\n    // Needle one\n    // Needle two\n    // Needle three\n}\n";
        using var pwm = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance, solutionPath: Path.Combine(_dir, "Test.slnx"));
        pwm.SetTestSolution(BuildSolution((path, text)));
        var impl = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(pwm, NullLogger<WorkspaceReadNavigationImpl>.Instance);

        var result = await impl.SearchSolutionText(reason: "test observed search several hits", pattern: "Needle");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((TextSearchResult)result.SuccessData!).LiteralResults.Count, Is.EqualTo(3));
        Assert.That(ObservedHashIn(pwm, path), Is.EqualTo(ContentFingerprint.Compute(text)), "The final entry must be the whole-file hash.");
    }
}

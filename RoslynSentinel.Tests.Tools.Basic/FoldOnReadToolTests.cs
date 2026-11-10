// DTO-EXEMPT: test fixture class, not a DTO.
using System.Reflection;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Fold-on-read through the tools (plan_stale_read_guard_followups 9e/9f-2). ReadFile, GetMethodSource and text-mode
// SearchSolutionText fold queued or differing disk text BEFORE they read, so the returned text and the hash recorded
// for the next write both match disk. Each test uses a real PersistentWorkspaceManager rooted in its own temp directory.
[TestFixture]
public class FoldOnReadToolTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string AgentId = "agent-fold-reader";

    private string _root;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(new ToolCallContext(AgentId, null, "ReadFile", null, 0));
        _root = Path.Combine(Path.GetTempPath(), "FoldOnReadTool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Variant(int n) => Original.Replace("=> 1", $"=> {n}");

    private string FileIn(string name) => Path.Combine(_root, name);

    private static Solution SolutionWith(string file, string text)
    {
        var project = new AdhocWorkspace().AddProject("FoldOnReadTool", LanguageNames.CSharp);
        return project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project.Solution;
    }

    // The document holds `documentText` in memory; the test writes whatever disk should hold before calling the tool.
    private PersistentWorkspaceManager NewManager(string file, string documentText)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(SolutionWith(file, documentText));
        manager.SetTestSolutionRoot(_root);
        return manager;
    }

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    [CancelAfter(30000)]
    public async Task ReadFile_WholeFile_ChangedOnDiskInsideWindow_ReturnsNewTextRecordsItsHashAndFollowingWriteIsNotRefused()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(file, Original);
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);

        var read = await tools.ReadFileTool(reason: "test fold whole file read", file);

        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        Assert.That((string?)GetProp(read.SuccessData!, "source"), Is.EqualTo(Variant(3)));
        Assert.That(manager.TryGetObservedHash(new FilePathWrapper(file), out var hash), Is.True);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(Variant(3))));

        var changes = new Dictionary<FilePathWrapper, string> { [new FilePathWrapper(file)] = Variant(9) };
        var write = await manager.ApplyProposedChangesAsync(changes, staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

        Assert.That(write.IsError, Is.False, write.Code);
        Assert.That(write.RefusalCode, Is.Not.EqualTo(ToolErrorCode.StaleRead));
        Assert.That(write.RefusalCode, Is.Not.EqualTo(ToolErrorCode.FoldPending));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ReadFile_LineRange_ChangedOnDisk_ReturnsNewTextFolded()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(file, Original);
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);

        var read = await tools.ReadFileTool(reason: "test fold line range read", file, startLine: 4, endLine: 4);

        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        Assert.That((string?)GetProp(read.SuccessData!, "source"), Does.Contain("=> 3"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ReadFile_MemberRead_ChangedOnDisk_ReturnsNewMemberSource()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(file, Original);
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);

        var read = await tools.ReadFileTool(reason: "test fold member read", file, memberName: "X");

        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        Assert.That(((MemberSourceViewResult)read.SuccessData!).Member.Source, Does.Contain("=> 3"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task GetMethodSource_ChangedOnDisk_ReturnsNewSource_AndRecordsMatchingHash()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(file, Original);
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);

        var result = await tools.GetMethodSourceTool(reason: "test fold method source", file, "X");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((MethodSourceResult)result.SuccessData!).Source, Does.Contain("=> 3"));
        Assert.That(manager.TryGetObservedHash(new FilePathWrapper(file), out var hash), Is.True);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(Variant(3))));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ReadFile_UnchangedOnDisk_DoesNotBumpWorkspaceVersion()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager(file, Original);
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var versionBefore = manager.WorkspaceVersion;

        var read = await tools.ReadFileTool(reason: "test fold unchanged read", file);

        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ReadFile_QueuedPendingChange_IsFlushedBeforeReading()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Variant(3));
        using var manager = NewManager(file, Original);
        manager.AddPendingChangeForTest(file);
        Assert.That(manager.IsPendingChangeForTest(file), Is.True, "Test setup: the change must be queued.");
        var tools = Battery.Basic.TestTools.WorkspaceTools(manager);

        var read = await tools.ReadFileTool(reason: "test fold queued pending read", file);

        Assert.That(read.IsError, Is.False, read.ErrorData?.Message);
        Assert.That((string?)GetProp(read.SuccessData!, "source"), Is.EqualTo(Variant(3)));
        Assert.That(manager.IsPendingChangeForTest(file), Is.False);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_QueuedPendingChange_FindsNewTextImmediately()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original + "// FoldedMarker\r\n");
        using var manager = NewManager(file, Original);
        manager.AddPendingChangeForTest(file);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        var result = await readNav.SearchSolutionText(reason: "test fold queued pending text search", pattern: "FoldedMarker");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(JsonSerializer.Serialize(result.SuccessData), Does.Contain("FoldedMarker"));
        Assert.That(manager.IsPendingChangeForTest(file), Is.False);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_NothingPending_RunsNoFold()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original + "// FoldedMarker\r\n");
        using var manager = NewManager(file, Original);
        var versionBefore = manager.WorkspaceVersion;
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        var result = await readNav.SearchSolutionText(reason: "test fold nothing pending text search", pattern: "FoldedMarker");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(manager.WorkspaceVersion, Is.EqualTo(versionBefore));
    }
}

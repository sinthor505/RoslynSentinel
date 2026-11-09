using System.Reflection;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// ReadFile records the WHOLE-file raw text hash in the observed-content ledger on every successful
// content-returning branch, never the slice. Read-back uses TryGetObservedHash on the fake, which keys
// entries per agent exactly like PersistentWorkspaceManager. Failure branches must record nothing.
[TestFixture]
public class ReadFileRecordsObservedTests
{
    private const string AgentId = "agent-reader";
    private const string Lines = "line1\nline2\nline3\nline4\nline5\n";

    private FakeWorkspaceManager _fake;
    private WorkspaceTools _tools;
    private string _dir;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(new ToolCallContext(AgentId, null, "ReadFile", null, 0));
        _dir = Path.Combine(Path.GetTempPath(), "ReadFileRecordsObserved_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fake = FakeWorkspaceManager.ReadOnly();
        _tools = Battery.Basic.TestTools.WorkspaceTools(_fake);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _fake.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void UseSolutionWithDocument(string docPath, string text) =>
        _fake.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(_dir, "TestProj.csproj"),
            new[] { (Path.GetFileName(docPath), text, docPath) }));

    private bool TryObserved(string path, out string hash) =>
        _fake.TryGetObservedHash(new FilePathWrapper(path), out hash);

    private string ObservedHashOf(string path)
    {
        Assert.That(TryObserved(path, out var hash), Is.True, $"No observed entry was recorded for '{path}'.");
        return hash;
    }

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task WholeFileRead_SolutionDocument_RecordsHashOfFullText()
    {
        var path = Path.Combine(_dir, "Foo.cs");
        UseSolutionWithDocument(path, Lines);

        var result = await _tools.ReadFileTool(reason: "test observed whole file", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(path), Is.EqualTo(ContentFingerprint.Compute(Lines)));
    }

    [Test]
    public async Task RangeRead_SolutionDocument_RecordsHashOfWholeFile_NotTheSlice()
    {
        var path = Path.Combine(_dir, "Foo.cs");
        UseSolutionWithDocument(path, Lines);
        const string slice = "line2\nline3\n";

        var result = await _tools.ReadFileTool(reason: "test observed range read", path, startLine: 2, endLine: 3);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var hash = ObservedHashOf(path);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(Lines)), "The whole-file hash must be recorded.");
        Assert.That(hash, Is.Not.EqualTo(ContentFingerprint.Compute(slice)), "The slice hash must never be recorded.");
    }

    [Test]
    public async Task MemberRead_SolutionDocument_RecordsHashOfWholeFile()
    {
        const string source = "namespace Sample;\npublic class Widget\n{\n    public int Run() => 1;\n    public int Other() => 2;\n}\n";
        var path = Path.Combine(_dir, "Widget.cs");
        UseSolutionWithDocument(path, source);

        var result = await _tools.ReadFileTool(reason: "test observed member read", path, memberName: "Run");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(path), Is.EqualTo(ContentFingerprint.Compute(source)));
    }

    [Test]
    public async Task DiskFallback_FileNotInSolution_RecordsHashOfWholeDiskText()
    {
        var tracked = Path.Combine(_dir, "Tracked.cs");
        var loose = Path.Combine(_dir, "Loose.cs");
        UseSolutionWithDocument(tracked, Lines);
        const string diskText = "disk-only line1\ndisk-only line2\n";
        File.WriteAllText(loose, diskText);

        var result = await _tools.ReadFileTool(reason: "test observed disk fallback", loose);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(loose), Is.EqualTo(ContentFingerprint.Compute(diskText)));
    }

    [Test]
    public async Task NoSolutionWholeRead_RecordsHashOfDiskText_KeyedByAbsolutePath()
    {
        var path = Path.Combine(_dir, "Standalone.cs");
        File.WriteAllText(path, Lines);

        // No SetTestSolution: ResolveFromWire reports NoSolutionLoaded and ReadFile takes the disk path.
        var result = await _tools.ReadFileTool(reason: "test observed no-solution whole read", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(ObservedHashOf(path), Is.EqualTo(ContentFingerprint.Compute(Lines)));
    }

    [Test]
    public async Task NoSolutionRangeRead_RecordsHashOfWholeDiskText_NotTheSlice()
    {
        var path = Path.Combine(_dir, "Standalone.cs");
        File.WriteAllText(path, Lines);
        const string slice = "line2\nline3\n";

        var result = await _tools.ReadFileTool(reason: "test observed no-solution range read", path, startLine: 2, endLine: 3);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var hash = ObservedHashOf(path);
        Assert.That(hash, Is.EqualTo(ContentFingerprint.Compute(Lines)), "The whole-file hash must be recorded.");
        Assert.That(hash, Is.Not.EqualTo(ContentFingerprint.Compute(slice)), "The slice hash must never be recorded.");
    }

    [Test]
    public async Task LargeFileRead_StillReturnsInlineFirstPart_AndRecordsHashOfWholeFile()
    {
        var text = string.Concat(Enumerable.Range(1, 2000).Select(i => $"// line {i:D5}\n"));
        Assert.That(text.Length, Is.GreaterThan(LargeResultHelper.InlineBudgetChars), "Fixture must exceed the inline budget.");
        var path = Path.Combine(_dir, "Big.cs");
        UseSolutionWithDocument(path, text);

        var result = await _tools.ReadFileTool(reason: "test observed large file read", path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.StatusMessage, Does.Contain("over the inline limit"));
        Assert.That((string)GetProp(result.SuccessData!, "source")!, Does.StartWith("// line 00001"));
        Assert.That(ObservedHashOf(path), Is.EqualTo(ContentFingerprint.Compute(text)));
    }

    [Test]
    public async Task OutOfBoundsRange_RecordsNothing()
    {
        var path = Path.Combine(_dir, "Foo.cs");
        UseSolutionWithDocument(path, Lines);

        var result = await _tools.ReadFileTool(reason: "test observed out of bounds", path, startLine: 50, endLine: 60);

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(path, out _), Is.False, "A refused range must not record an observed hash.");
    }

    [Test]
    public async Task FileNotFound_InSolution_RecordsNothing()
    {
        var present = Path.Combine(_dir, "Foo.cs");
        var missing = Path.Combine(_dir, "Missing.cs");
        UseSolutionWithDocument(present, Lines);

        var result = await _tools.ReadFileTool(reason: "test observed file not found", missing);

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(missing, out _), Is.False, "A not-found read must not record an observed hash.");
    }

    [Test]
    public async Task FileNotFound_NoSolution_RecordsNothing()
    {
        var missing = Path.Combine(_dir, "Missing.cs");

        var result = await _tools.ReadFileTool(reason: "test observed no-solution not found", missing);

        Assert.That(result.IsError, Is.True);
        Assert.That(TryObserved(missing, out _), Is.False, "A not-found read must not record an observed hash.");
    }
}

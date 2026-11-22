namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class UndoLastOnlyGuardTests
{
    private static readonly DateTime OlderStamp = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewerStamp = OlderStamp.AddMinutes(5);

    private string _root = "";
    private string _fooPath = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "RoslynSentinelUndoGuard_" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        _fooPath = Path.Combine(_root, "Foo.cs");
        ToolCallContext.Set(null);
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

    [Test]
    public async Task OlderChange_WithNewerBlobOnSameFile_ReturnsRefusalNamingNewerChangeAndTool()
    {
        var older = await WriteBlobAsync("RenameSymbol", "older01", OlderStamp, _fooPath);
        await WriteBlobAsync("WriteFile", "newer01", NewerStamp, _fooPath);

        var message = UndoLastOnlyGuard.FindNewerWriters(_root, older, "older01", new[] { _fooPath });

        Assert.That(message, Is.Not.Null);
        Assert.That(message, Does.Contain("ChangeId 'older01' is not the newest change for 1 of its file(s)"));
        Assert.That(message, Does.Contain("newer01 (WriteFile: Foo.cs)"));
    }

    [Test]
    public async Task NewestChange_ReturnsNull()
    {
        await WriteBlobAsync("RenameSymbol", "older01", OlderStamp, _fooPath);
        var newest = await WriteBlobAsync("WriteFile", "newer01", NewerStamp, _fooPath);

        Assert.That(UndoLastOnlyGuard.FindNewerWriters(_root, newest, "newer01", new[] { _fooPath }), Is.Null);
    }

    [Test]
    public async Task NewerBlobOnDifferentFile_ReturnsNull()
    {
        var older = await WriteBlobAsync("RenameSymbol", "older01", OlderStamp, _fooPath);
        await WriteBlobAsync("WriteFile", "newer01", NewerStamp, Path.Combine(_root, "Bar.cs"));

        Assert.That(UndoLastOnlyGuard.FindNewerWriters(_root, older, "older01", new[] { _fooPath }), Is.Null);
    }

    [Test]
    public async Task NewerBlobMarkedUndone_ReturnsNull()
    {
        var older = await WriteBlobAsync("RenameSymbol", "older01", OlderStamp, _fooPath);
        var newer = await WriteBlobAsync("WriteFile", "newer01", NewerStamp, _fooPath);
        OperationBlobWriter.MarkUndone(newer);

        Assert.That(UndoLastOnlyGuard.FindNewerWriters(_root, older, "older01", new[] { _fooPath }), Is.Null);
    }

    [Test]
    public async Task NewerBlobWithOnlyFailedItems_ReturnsNull()
    {
        var older = await WriteBlobAsync("RenameSymbol", "older01", OlderStamp, _fooPath);
        await WriteBlobAsync("WriteFile", "newer01", NewerStamp, _fooPath, ItemRecordOutcome.Failed);

        Assert.That(UndoLastOnlyGuard.FindNewerWriters(_root, older, "older01", new[] { _fooPath }), Is.Null);
    }

    [Test]
    public void MissingOperationsFolder_ReturnsNull()
    {
        var target = Path.Combine(_root, ".roslynsentinel", "operations", "RenameSymbol_20261120T100000Z_0000000_older01.json");

        Assert.That(UndoLastOnlyGuard.FindNewerWriters(_root, target, "older01", new[] { _fooPath }), Is.Null);
    }

    private async Task<string> WriteBlobAsync(string toolName, string changeId, DateTime stamp, string filePath, ItemRecordOutcome outcome = ItemRecordOutcome.Succeeded)
    {
        var context = new ToolCallContext("main", "implementer", toolName, "undo guard test", 0) { SessionId = "s1" };
        context.MarkWritten(stamp);
        ToolCallContext.Set(context);
        try
        {
            await OperationBlobWriter.WriteAsync(
                toolName,
                changeId,
                [new OperationItemRecord { FilePath = filePath, Outcome = outcome, BeforeSource = "before" }],
                _root);
        }
        finally
        {
            ToolCallContext.Set(null);
        }

        return OperationBlobWriter.FindBlobPath(changeId, _root)!;
    }
}

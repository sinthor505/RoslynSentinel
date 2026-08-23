// GetOperationDetail — SentinelWorkspaceTools. Zero coverage before this file (GetTestCoverageMap
// flagged branches: blobPath == null, filter present, file: filter true/false, unknown filter).
// Blob schema follows OperationBlobWriter.WriteAsync's on-disk layout, same as UndoLastApplyTests.cs.
// ItemRecordOutcome has no JsonStringEnumConverter (see project_operation_blob_json_gotchas memory),
// so blob fixtures below write enum values, never string literals.

using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class GetOperationDetailTests
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private FakeWorkspaceManager _workspaceManager;
    private SentinelWorkspaceTools _tools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _tempDir = Path.Combine(Path.GetTempPath(), "GetOperationDetailTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine(_workspaceManager);
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
        var dependencyEngine = new DependencyEngine(_workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new SentinelWorkspaceTools(
            _workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, diagnosticEngine));
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteBlob(string changeId, object items)
    {
        var dir = Path.Combine(_tempDir, ".roslynsentinel", "operations");
        Directory.CreateDirectory(dir);
        var fileName = $"apply_diff_20260101T000000Z_{changeId}.json";
        var payload = new
        {
            toolName = "apply_diff",
            changeId,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            itemCount = ((System.Collections.ICollection)items).Count,
            items,
        };
        File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(payload, PrettyJson));
        return fileName;
    }

    [Test]
    public async Task GetOperationDetail_NoBlobForChangeId_ReturnsInvalidArgumentAsync()
    {
        var result = await _tools.GetOperationDetail("nonexistent-change-id");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task GetOperationDetail_NoFilter_ReturnsAllItemsAsync()
    {
        var changeId = "change-all";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
            new { FilePath = Path.Combine(_tempDir, "Bar.cs"), Outcome = ItemRecordOutcome.Failed, BeforeSource = (string?)null },
        });

        var result = await _tools.GetOperationDetail(changeId);

        Assert.That(result.Success, Is.True);
        var data = (OperationDetailResult)result.Data!;
        Assert.That(data.TotalItems, Is.EqualTo(2));
        Assert.That(data.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetOperationDetail_OutcomeFilter_ReturnsOnlyMatchingItemsAsync()
    {
        var changeId = "change-filtered";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
            new { FilePath = Path.Combine(_tempDir, "Bar.cs"), Outcome = ItemRecordOutcome.Failed, BeforeSource = (string?)null },
        });

        var result = await _tools.GetOperationDetail(changeId, filter: "fail");

        Assert.That(result.Success, Is.True);
        var data = (OperationDetailResult)result.Data!;
        Assert.That(data.TotalItems, Is.EqualTo(1));
        Assert.That(data.Items[0].FilePath, Does.Contain("Bar.cs"));
    }

    [Test]
    public async Task GetOperationDetail_FilePathFilter_ReturnsOnlyMatchingPathAsync()
    {
        var changeId = "change-filepath";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
            new { FilePath = Path.Combine(_tempDir, "Bar.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
        });

        var result = await _tools.GetOperationDetail(changeId, filter: "file:Foo.cs");

        Assert.That(result.Success, Is.True);
        var data = (OperationDetailResult)result.Data!;
        Assert.That(data.TotalItems, Is.EqualTo(1));
        Assert.That(data.Items[0].FilePath, Does.Contain("Foo.cs"));
    }

    [Test]
    public async Task GetOperationDetail_UnknownFilter_ReturnsInvalidArgumentAsync()
    {
        var changeId = "change-unknown-filter";
        WriteBlob(changeId, new[]
        {
            new { FilePath = Path.Combine(_tempDir, "Foo.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = (string?)null },
        });

        var result = await _tools.GetOperationDetail(changeId, filter: "bogus");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }
}

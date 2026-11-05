using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Proof (not the final suite) that the detail envelope RenameSymbol stores for residual mentions can be paged per
/// bucket with GetLargeResult(resultId, path), and that the hints the summary carries name those same paths.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class ResidualMentionDetailPagingTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private WorkspaceTools _tools = null!;
    private string _tempDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "ResidualDetail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", []));
        _tools = Battery.Basic.TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private ResidualMention Mention(string relative, int line, string text, ResidualBucket bucket, int column = 0) =>
        new(new FilePathWrapper(Path.Combine(_tempDir, relative), _tempDir), line, text, bucket, column);

    [Test]
    public async Task StoredDetailEnvelope_PagesEachBucketByPath_AndHintsNameThosePaths()
    {
        var mentions = new List<ResidualMention>
        {
            Mention("a.cs", 3, "var x = OldName;", ResidualBucket.Code, 8),
            Mention("a.cs", 9, "// OldName is gone", ResidualBucket.Comment, 3),
            Mention("sub\\b.cs", 4, "// see OldName", ResidualBucket.Comment, 7),
            Mention("c.md", 1, "OldName docs", ResidualBucket.NonSource),
        };

        string? resultId = await ResidualMentionDetailStore.StoreAsync(mentions, _tempDir, CancellationToken.None);
        Assert.That(resultId, Is.Not.Null.And.Not.Empty);

        var view = ResidualMentionSummarizer.Summarize(mentions, "OldName", resultId);
        Assert.That(view.Details, Is.Not.Null);
        Assert.That(view.Details!.Keys, Is.EquivalentTo(new[] { "code", "comment", "nonSource" }));
        Assert.That(view.Details["comment"], Does.Contain(resultId).And.Contain("successData.comment"));

        var page = await _tools.GetLargeResultTool(reason: "test message", resultId: resultId, limit: 50, offset: 0, path: "successData.comment");
        Assert.That(page.IsError, Is.False, page.ErrorData?.Message);
        var result = (EnvelopePageResult)page.SuccessData!;
        Assert.That(result.Path, Is.EqualTo("successData.comment"));
        Assert.That(result.Total, Is.EqualTo(2));
        Assert.That(result.Items!.Count, Is.EqualTo(2));
        Assert.That((string)result.Items[0]!["text"]!, Is.EqualTo("// OldName is gone"));
        Assert.That((int)result.Items[1]!["line"]!, Is.EqualTo(4));
    }

    [Test]
    public void BuildDetailEnvelope_HasSuccessDataBucketArrays_OnlyForNonEmptyBuckets()
    {
        var mentions = new List<ResidualMention> { Mention("a.cs", 1, new string('x', 400), ResidualBucket.String) };

        var root = JsonNode.Parse(ResidualMentionSummarizer.BuildDetailEnvelope(mentions))!;

        var rows = root["successData"]!["string"]!.AsArray();
        Assert.That(rows.Count, Is.EqualTo(1));
        Assert.That(((string)rows[0]!["text"]!).Length, Is.EqualTo(ResidualMentionSummarizer.DetailTextChars));
        Assert.That(root["successData"]!.AsObject().ContainsKey("code"), Is.False);
    }
}

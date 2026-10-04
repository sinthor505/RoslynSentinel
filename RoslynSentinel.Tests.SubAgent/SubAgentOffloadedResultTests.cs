using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.SubAgent;

/// <summary>
/// <see cref="SubAgentEvalResult.ResolveOffloadedResult"/>: a child Build/RunTest result that was too
/// large to return inline is a pointer to a file in the child's worktree. Found by the live smoke
/// run, where a full-solution Build came back as a pointer and BuildSucceeded was reported false.
/// </summary>
[TestFixture]
public class SubAgentOffloadedResultTests
{
    private string _worktree = null!;
    private string _largeResults = null!;

    [SetUp]
    public void SetUp()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "subagent-offload-tests-" + Guid.NewGuid().ToString("N")[..8], "Worktree");
        _largeResults = Path.Combine(_worktree, ".roslynsentinel", "largeresults");
        Directory.CreateDirectory(_largeResults);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(_worktree)!, recursive: true);

    private static string LargeResultPointer(string filePath) =>
        """{"isError":false,"largeResult":{"resultType":"BuildResult","writtenToFile":true,"filePath":"FILE"}}"""
            .Replace("FILE", filePath.Replace("\\", "\\\\"));

    [Test]
    public void TypedLargeResult_IsResolvedToInlineSuccessData()
    {
        var file = Path.Combine(_largeResults, "largeresult_20261001T221837Z_9ec8361b2614411fa7c69d895dd5e5eb.json");
        File.WriteAllText(file, """{"Type":"Raw","Data":{"Outcome":"Succeeded","ErrorCount":0,"WarningCount":110}}""");

        var resolved = SubAgentEvalResult.ResolveOffloadedResult(LargeResultPointer(file), _worktree);
        var result = SubAgentEvalResult.Build(AgentRun(), resolved, null, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.BuildSucceeded, Is.True);
            Assert.That(result.BuildErrorCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void GenericBackstopOffload_IsResolvedByResultId()
    {
        File.WriteAllText(
            Path.Combine(_largeResults, "largeresult_20261001T220405Z_4c74d82908f845e4a294755f2c044019.json"),
            """{"Type":"Raw","Data":{"isError":false,"successData":{"runCompleted":true,"passedCount":9,"failedCount":1}}}""");
        var pointer = """{"offloaded":true,"resultId":"4c74d82908f845e4a294755f2c044019","sizeBytes":31231,"isError":false}""";

        var resolved = SubAgentEvalResult.ResolveOffloadedResult(pointer, _worktree);
        var result = SubAgentEvalResult.Build(AgentRun(), """{"successData":{"outcome":"Succeeded","errorCount":0}}""", resolved, []);

        Assert.Multiple(() =>
        {
            Assert.That(result.TestPassedCount, Is.EqualTo(9));
            Assert.That(result.TestFailedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void PointerToAFileOutsideTheWorktree_IsNotFollowed()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_worktree)!, "outside.json");
        File.WriteAllText(outside, """{"Type":"Raw","Data":{"Outcome":"Succeeded"}}""");
        var pointer = LargeResultPointer(outside);

        Assert.That(SubAgentEvalResult.ResolveOffloadedResult(pointer, _worktree), Is.EqualTo(pointer));
    }

    [Test]
    public void MissingFile_AndNonPointerText_AreReturnedUnchanged()
    {
        var missing = LargeResultPointer(Path.Combine(_largeResults, "nope.json"));
        const string inline = """{"successData":{"outcome":"Failed","errorCount":2}}""";

        Assert.Multiple(() =>
        {
            Assert.That(SubAgentEvalResult.ResolveOffloadedResult(missing, _worktree), Is.EqualTo(missing));
            Assert.That(SubAgentEvalResult.ResolveOffloadedResult(inline, _worktree), Is.EqualTo(inline));
            Assert.That(SubAgentEvalResult.ResolveOffloadedResult("not json", _worktree), Is.EqualTo("not json"));
            Assert.That(SubAgentEvalResult.ResolveOffloadedResult(null, _worktree), Is.Null);
        });
    }

    [Test]
    public void ResultIdWithPathCharacters_IsNotUsedToBuildAFilePattern()
    {
        const string pointer = """{"offloaded":true,"resultId":"..\\..\\x"}""";

        Assert.That(SubAgentEvalResult.ResolveOffloadedResult(pointer, _worktree), Is.EqualTo(pointer));
    }

    private static AgentRunResult AgentRun() => new()
    {
        StopReason = AgentStopReason.ModelFinished,
        Transcript = new AgentTranscript(),
        TranscriptPath = "t.json",
        TurnCount = 1,
    };
}

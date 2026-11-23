using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.SubAgent;

[TestFixture]
public class ResultStatusSubAgentEvalTests
{
    private static SubAgentEvalResult EvalResult(bool converged = true, bool buildSucceeded = true, int buildErrors = 0, int testsFailed = 0, string? excerpt = null) =>
        new()
        {
            Converged = converged,
            StopReason = "end_turn",
            TurnCount = 3,
            BuildSucceeded = buildSucceeded,
            BuildErrorCount = buildErrors,
            TestPassedCount = 5,
            TestFailedCount = testsFailed,
            TouchedPaths = Array.Empty<string>(),
            FailureExcerpt = excerpt,
            TranscriptPath = "t.json",
        };

    [Test]
    public void SubAgentEvalResult_CleanRun_IsSuccess()
    {
        var result = EvalResult();

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Success));
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.Message, Is.Null);
    }

    [Test]
    public void SubAgentEvalResult_NotConverged_IsExceptionWithStopReasonMessage()
    {
        var result = EvalResult(converged: false);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.Exception));
        Assert.That(result.Message, Is.EqualTo("Did not converge: end_turn"));
    }

    [Test]
    public void SubAgentEvalResult_BuildFailed_IsBuildFailedWithErrorCountMessage()
    {
        var result = EvalResult(buildSucceeded: false, buildErrors: 3);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.BuildFailed));
        Assert.That(result.Message, Is.EqualTo("Build failed with 3 error(s)"));
    }

    [Test]
    public void SubAgentEvalResult_TestsFailed_IsTestRunFailedWithCountMessage()
    {
        var result = EvalResult(testsFailed: 2);

        Assert.That(result.Status, Is.EqualTo(ResultStatus.Failed));
        Assert.That(result.IsError, Is.True);
        Assert.That(result.Code, Is.EqualTo(ToolErrorCode.TestRunFailed));
        Assert.That(result.Message, Is.EqualTo("2 test(s) failed"));
    }

    [Test]
    public void SubAgentEvalResult_FailureExcerpt_WinsOverDerivedMessage()
    {
        var result = EvalResult(buildSucceeded: false, buildErrors: 1, excerpt: "boom");

        Assert.That(result.Message, Is.EqualTo("boom"));
    }

    [Test]
    public void SubAgentTypes_ComputedStatusMembers_AreJsonIgnored()
    {
        foreach (Type type in new[] { typeof(SubAgentResult), typeof(SubAgentEvalResult) })
        {
            foreach (string name in new[] { "IsError", "Status", "Code", "Message" })
            {
                Assert.That(type.GetProperty(name)!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false), Is.Not.Empty, $"{type.Name}.{name}");
            }
        }
    }
}

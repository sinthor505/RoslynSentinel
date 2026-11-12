using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.SnippetMatching;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the missing-target side channel: <see cref="ToolErrorMapper.ToResultError"/> records the kind of a
/// <see cref="ToolNotFoundException"/> on the ambient <see cref="ToolCallContext"/>, so the error-policy filter can
/// read it after the call returns. The side channel must never change the returned error.
/// </summary>
[TestFixture]
public class ErrorPolicyMissingTargetTests
{
    [TearDown]
    public void ClearAmbientContext() => ToolCallContext.Set(null);

    private static ToolCallContext NewContext() => new(null, null, "Probe", null, 0);

    [Test]
    public void ToResultError_ToolNotFoundException_RecordsKind()
    {
        ToolCallContext.Set(null);
        var baselineMessage = ToolErrorMapper.ToResultError(new ToolNotFoundException("m", "method", "Foo"), null!, "Probe").Message;

        var ctx = NewContext();
        ToolCallContext.Set(ctx);
        var result = ToolErrorMapper.ToResultError(new ToolNotFoundException("m", "method", "Foo"), null!, "Probe");

        Assert.That(ctx.MissingTargetKind, Is.EqualTo("method"));
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.Message, Is.EqualTo(baselineMessage));
    }

    [Test]
    public void ToResultError_NotFoundWithNullKind_LeavesKindNull()
    {
        var ctx = NewContext();
        ToolCallContext.Set(ctx);

        ToolErrorMapper.ToResultError(new ToolNotFoundException("m"), null!, "Probe");

        Assert.That(ctx.MissingTargetKind, Is.Null);
    }

    [Test]
    public void ToResultError_OtherExceptions_DoNotRecord()
    {
        var ctx = NewContext();
        ToolCallContext.Set(ctx);

        ToolErrorMapper.ToResultError(new InvalidOperationException("x"), null!, "Probe");
        ToolErrorMapper.ToResultError(new ToolAmbiguousMatchException("two matches",
        [
            new AmbiguousCandidate("A.cs", 10, "method", "void Run()", "A", "void Run()", null, true),
            new AmbiguousCandidate("B.cs", 20, "method", "void Run()", "B", "void Run()", null, true),
        ]), null!, "Probe");

        Assert.That(ctx.MissingTargetKind, Is.Null);
    }

    [Test]
    public void ToResultError_NoAmbientContext_DoesNotThrow()
    {
        ToolCallContext.Set(null);

        Assert.DoesNotThrow(() => ToolErrorMapper.ToResultError(new ToolNotFoundException("m", "document", "a.cs"), null!, "Probe"));
    }

    [Test]
    public async Task FailureHandler_ThrownToolNotFound_RecordsKind()
    {
        var ctx = NewContext();
        ToolCallContext.Set(ctx);

        await CallToolFailureHandler.InvokeAsync(
            () => ValueTask.FromException<CallToolResult>(new ToolNotFoundException("m", "document", "a.cs")),
            "Probe",
            default);

        Assert.That(ctx.MissingTargetKind, Is.EqualTo("document"));
    }

    [Test]
    public async Task MutationInsideChildTask_IsVisibleThroughTheOuterReference()
    {
        var ctx = NewContext();
        ToolCallContext.Set(ctx);

        await Task.Run(() => ToolErrorMapper.ToResultError(new ToolNotFoundException("m", "type", "T"), null!, "Probe"));

        Assert.That(ctx.MissingTargetKind, Is.EqualTo("type"));
    }
}

using System;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class ErrorPolicyDocumentLookupRouteTests
{
    [TearDown]
    public void TearDown() => ToolCallContext.Set(null);

    [Test]
    public void ToResultError_NotFound_RecordsFileKind()
    {
        var ctx = new ToolCallContext(null, null, "ReadFile", null, 0);
        ToolCallContext.Set(ctx);
        var result = new DocumentLookupResult(DocumentLookupStatus.NotFound, "Missing.cs", null, Array.Empty<string>(), 0);

        var error = result.ToResultError();

        Assert.That(ctx.MissingTargetKind, Is.EqualTo("file"));
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public void ToResultError_Ambiguous_RecordsNothing()
    {
        var ctx = new ToolCallContext(null, null, "ReadFile", null, 0);
        ToolCallContext.Set(ctx);
        var result = new DocumentLookupResult(DocumentLookupStatus.Ambiguous, "Program.cs", null, new[] { "A/Program.cs", "B/Program.cs" }, 0);

        var error = result.ToResultError();

        Assert.That(ctx.MissingTargetKind, Is.Null);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
    }

    [Test]
    public void ToResultError_NoAmbientContext_DoesNotThrow()
    {
        ToolCallContext.Set(null);
        var result = new DocumentLookupResult(DocumentLookupStatus.NotFound, "Missing.cs", null, Array.Empty<string>(), 0);

        Assert.DoesNotThrow(() => result.ToResultError());
    }

    [Test]
    public void ToResultError_NotFound_TextIsUnchanged()
    {
        var ctx = new ToolCallContext(null, null, "ReadFile", null, 0);
        ToolCallContext.Set(ctx);
        var result = new DocumentLookupResult(DocumentLookupStatus.NotFound, "Missing.cs", null, Array.Empty<string>(), 0);

        var error = result.ToResultError();

        Assert.That(error.Message, Is.EqualTo(result.Describe()));
    }
}

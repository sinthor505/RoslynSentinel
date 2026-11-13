using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class ErrorPolicySymbolWireResolverRouteTests
{
    [TearDown]
    public void TearDown() => ToolCallContext.Set(null);

    [Test]
    public void ToResultError_SymbolNotResolved_RecordsSymbolKind()
    {
        var ctx = new ToolCallContext(null, null, "RenameSymbol", null, 0);
        ToolCallContext.Set(ctx);
        var error = new EngineError(EngineErrorCode.SymbolNotResolved, "No symbol named Foo.");

        var result = SymbolWireResolver.ToResultError(error);

        Assert.That(ctx.MissingTargetKind, Is.EqualTo("symbol"));
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.Message, Is.EqualTo(error.Message));
    }

    [Test]
    public void ToResultError_MissingParameterListOverloads_RecordsNothing()
    {
        var ctx = new ToolCallContext(null, null, "RenameSymbol", null, 0);
        ToolCallContext.Set(ctx);
        var error = new EngineError(EngineErrorCode.MissingParameterListOverloads, "Overloads: M(int), M(string).");

        var result = SymbolWireResolver.ToResultError(error);

        Assert.That(ctx.MissingTargetKind, Is.Null);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [TestCase(EngineErrorCode.General)]
    [TestCase(EngineErrorCode.StaleSession)]
    [TestCase(EngineErrorCode.BuildNotRun)]
    public void ToResultError_OtherCode_RecordsNothing(EngineErrorCode code)
    {
        var ctx = new ToolCallContext(null, null, "RenameSymbol", null, 0);
        ToolCallContext.Set(ctx);
        var error = new EngineError(code, "Engine failure.");

        var result = SymbolWireResolver.ToResultError(error);

        Assert.That(ctx.MissingTargetKind, Is.Null);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.Exception));
    }

    [Test]
    public void ToResultError_NoAmbientContext_DoesNotThrow()
    {
        ToolCallContext.Set(null);
        var error = new EngineError(EngineErrorCode.SymbolNotResolved, "No symbol named Foo.");

        Assert.DoesNotThrow(() => SymbolWireResolver.ToResultError(error));
    }
}

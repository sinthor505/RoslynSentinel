using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

public class ToolCallContextTests
{
    [SetUp]
    public void SetUp() => ToolCallContext.Set(null);

    [TearDown]
    public void TearDown() => ToolCallContext.Set(null);

    [Test]
    public void Current_NothingSet_IsNull()
    {
        Assert.That(ToolCallContext.Current, Is.Null);
    }

    [Test]
    public async Task Current_SetThenReadAfterYield_ReturnsSameInstance()
    {
        var context = new ToolCallContext("agent-1", "general", "Search", "reason text", 1);
        ToolCallContext.Set(context);

        await Task.Yield();

        Assert.That(ToolCallContext.Current, Is.SameAs(context));
    }

    [Test]
    public async Task Current_SetBeforeTaskRun_VisibleInsideTaskRun()
    {
        var context = new ToolCallContext("agent-1", "general", "Search", "reason text", 1);
        ToolCallContext.Set(context);

        var seen = await Task.Run(() => ToolCallContext.Current);

        Assert.That(seen, Is.SameAs(context));
    }

    [Test]
    public async Task MarkGatePassed_InsideAwaitedHelperAndTaskRunChild_VisibleToCaller()
    {
        var context = new ToolCallContext("agent-1", "general", "Search", "reason text", 1);
        ToolCallContext.Set(context);
        Assert.That(context.GatePassed, Is.False);

        await MarkInsideAwaitedHelperAsync();
        Assert.That(context.GatePassed, Is.True);

        var fresh = new ToolCallContext(null, null, "Search", null, 2);
        ToolCallContext.Set(fresh);
        await Task.Run(() => ToolCallContext.Current!.MarkGatePassed());

        Assert.That(ToolCallContext.Current, Is.SameAs(fresh));
        Assert.That(fresh.GatePassed, Is.True);
    }

    [Test]
    public async Task Set_InsideAwaitedHelper_DoesNotReplaceCallerValueAfterReturn()
    {
        var callerContext = new ToolCallContext("agent-1", "general", "Search", "reason text", 1);
        ToolCallContext.Set(callerContext);

        await SetInsideAwaitedHelperAsync(new ToolCallContext("agent-2", null, "Other", null, 9));

        Assert.That(ToolCallContext.Current, Is.SameAs(callerContext));
    }

    [Test]
    public void Constructor_Values_RoundTripThroughProperties()
    {
        var context = new ToolCallContext(null, "explore", "ReplaceSnippet", "fix the thing", 42L);

        Assert.Multiple(() =>
        {
            Assert.That(context.AgentId, Is.Null);
            Assert.That(context.AgentType, Is.EqualTo("explore"));
            Assert.That(context.ToolName, Is.EqualTo("ReplaceSnippet"));
            Assert.That(context.Reason, Is.EqualTo("fix the thing"));
            Assert.That(context.StartSeq, Is.EqualTo(42L));
        });
    }

    private static async Task MarkInsideAwaitedHelperAsync()
    {
        await Task.Yield();
        ToolCallContext.Current!.MarkGatePassed();
    }

    private static async Task SetInsideAwaitedHelperAsync(ToolCallContext replacement)
    {
        await Task.Yield();
        ToolCallContext.Set(replacement);
    }
}

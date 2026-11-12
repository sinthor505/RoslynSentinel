using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Tests for the pending and committed edit effects a ToolCallContext carries.</summary>
[TestFixture]
public class ToolCallContextEffectsTests
{
    [TearDown]
    public void ResetContext()
    {
        ToolCallContext.Set(null);
    }

    private static ToolCallContext NewContext(string tool = "RenameSymbol")
    {
        var context = new ToolCallContext("agent-1", "coder", tool, "rename it", 0) { SessionId = "session-1" };
        context.AssignToolCallId("a76e9f1c");
        return context;
    }

    [Test]
    public void RecordEffect_StampsTheCallFieldsItself_AndTakeClears()
    {
        var context = NewContext();

        context.RecordEffect(EditEffectKind.Renamed, "Foo.Bar", "M:Foo.Bar", "Bar", "Baz", null, "Foo.cs");
        var taken = context.TakePendingEffects();

        Assert.That(taken, Has.Count.EqualTo(1));
        var effect = taken[0];
        Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.Renamed));
        Assert.That(effect.SymbolDisplay, Is.EqualTo("Foo.Bar"));
        Assert.That(effect.SymbolDocId, Is.EqualTo("M:Foo.Bar"));
        Assert.That(effect.SimpleName, Is.EqualTo("Bar"));
        Assert.That(effect.NewName, Is.EqualTo("Baz"));
        Assert.That(effect.NewFilePath, Is.EqualTo("Foo.cs"));
        Assert.That(effect.ToolCallId, Is.EqualTo("a76e9f1c"));
        Assert.That(effect.ToolName, Is.EqualTo("RenameSymbol"));
        Assert.That(effect.SessionId, Is.EqualTo("session-1"));
        Assert.That(effect.AgentId, Is.EqualTo("agent-1"));
        Assert.That(effect.ServerInstanceId, Is.EqualTo(ServerInstanceIdentity.Id));
        Assert.That(effect.ChangeId, Is.Null);
        Assert.That(effect.Reverted, Is.False);
        Assert.That(effect.UtcTime, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));
        Assert.That(context.TakePendingEffects(), Is.Empty, "taking clears the pending list");
    }

    [Test]
    public void RecordEffect_BeforeTheCallIdIsAssigned_RecordsANullCallId()
    {
        var context = new ToolCallContext(null, null, "Member", null, 0);

        context.RecordEffect(EditEffectKind.Removed, "Foo.Bar", null, "Bar", null, null, null);

        var effect = context.TakePendingEffects().Single();
        Assert.That(effect.ToolCallId, Is.Null);
        Assert.That(effect.SessionId, Is.Null);
        Assert.That(effect.AgentId, Is.Null);
    }

    [Test]
    public void NoCurrentContext_RecordsNothing_AndContextsDoNotShareEffects()
    {
        ToolCallContext.Set(null);

        // The emitters' call shape: with no context the null-conditional call does nothing and does not throw.
        ToolCallContext.Current?.RecordEffect(EditEffectKind.Added, "Foo.Bar", null, "Bar", null, null, null);
        Assert.That(ToolCallContext.Current, Is.Null);

        var one = NewContext();
        var other = NewContext("Member");
        one.RecordEffect(EditEffectKind.Added, "Foo.Bar", null, "Bar", null, null, null);
        Assert.That(other.TakePendingEffects(), Is.Empty);
        Assert.That(one.TakePendingEffects(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RecordEffect_FromConcurrentContinuations_KeepsEveryEffect()
    {
        var context = NewContext();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                context.RecordEffect(EditEffectKind.Added, $"Foo.W{worker}_{i}", null, $"W{worker}_{i}", null, null, null);
            }
        })));

        Assert.That(context.TakePendingEffects(), Has.Count.EqualTo(16 * 25));
    }

    [Test]
    public void CommitPendingEffects_AppendsToTheIndex_AndTheBlobStampGivesThemTheChangeIdOnce()
    {
        var context = NewContext();
        var index = new EditEffectIndex();
        context.RecordEffect(EditEffectKind.Renamed, "Foo.Bar", null, "Bar", "Baz", null, null);

        var committed = context.CommitPendingEffects(index);

        Assert.That(committed, Is.EqualTo(1));
        Assert.That(context.TakePendingEffects(), Is.Empty);
        Assert.That(index.Find("Bar").Single().ChangeId, Is.Null);

        var stamped = context.StampCommittedEffects("abc12345");

        Assert.That(stamped.Single().ChangeId, Is.EqualTo("abc12345"));
        Assert.That(index.Find("Bar").Single().ChangeId, Is.EqualTo("abc12345"));
        Assert.That(context.StampCommittedEffects("def67890"), Is.Empty, "a second blob in the call has no effects to carry");
        Assert.That(context.CommitPendingEffects(index), Is.EqualTo(0));
    }
}

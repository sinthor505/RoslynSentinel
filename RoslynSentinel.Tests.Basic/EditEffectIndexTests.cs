using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Tests for the in-memory edit effect index (Common/EditEffectIndex.cs).</summary>
[TestFixture]
public class EditEffectIndexTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private static EditEffect Effect(string simpleName, string? changeId = null, int minute = 0, EditEffectKind kind = EditEffectKind.Renamed)
    {
        return new EditEffect(
            kind, "Foo." + simpleName, null, simpleName, "New" + simpleName, null, null,
            "call-" + minute, "RenameSymbol", changeId, "session-1", "server-1", "agent-1", T0.AddMinutes(minute));
    }

    [Test]
    public void Find_ReturnsMatchingEffectsNewestFirst()
    {
        var index = new EditEffectIndex();
        index.Append([Effect("Bar", minute: 1), Effect("Other", minute: 2), Effect("Bar", minute: 3)]);

        var found = index.Find("Bar");

        Assert.That(found.Select(e => e.UtcTime), Is.EqualTo(new[] { T0.AddMinutes(3), T0.AddMinutes(1) }));
    }

    [Test]
    public void Find_IsCaseSensitiveAndUnknownOrEmptyNamesFindNothing()
    {
        var index = new EditEffectIndex();
        index.Append([Effect("Bar")]);

        Assert.That(index.Find("bar"), Is.Empty);
        Assert.That(index.Find("Baz"), Is.Empty);
        Assert.That(index.Find(""), Is.Empty);
    }

    [Test]
    public void Append_PastTheBound_DropsTheOldestFirst()
    {
        var index = new EditEffectIndex();
        index.Append(Enumerable.Range(0, EditEffectIndex.MaxEffects + 1).Select(i => Effect("N" + i, minute: i)));

        Assert.That(index.Count, Is.EqualTo(EditEffectIndex.MaxEffects));
        Assert.That(index.Find("N0"), Is.Empty, "the oldest effect is dropped");
        Assert.That(index.Find("N1"), Has.Count.EqualTo(1));
        Assert.That(index.Find("N" + EditEffectIndex.MaxEffects), Has.Count.EqualTo(1));
    }

    [Test]
    public void MarkReverted_ExcludesOnlyThatChangeFromFind()
    {
        var index = new EditEffectIndex();
        index.Append([Effect("Bar", "aaa", 1), Effect("Bar", "bbb", 2), Effect("Bar", "aaa", 3)]);

        var marked = index.MarkReverted("aaa");

        Assert.That(marked, Is.EqualTo(2));
        var found = index.Find("Bar");
        Assert.That(found, Has.Count.EqualTo(1));
        Assert.That(found[0].ChangeId, Is.EqualTo("bbb"));
        Assert.That(index.MarkReverted("aaa"), Is.EqualTo(0), "already reverted effects are not counted again");
        Assert.That(index.MarkReverted("unknown"), Is.EqualTo(0));
        Assert.That(index.MarkReverted(""), Is.EqualTo(0));
    }

    [Test]
    public void AssignChangeId_StampsTheIndexedEffectsAndReturnsTheCopies()
    {
        var index = new EditEffectIndex();
        var first = Effect("Bar", minute: 1);
        var second = Effect("Baz", minute: 2);
        index.Append([first, second]);

        var stamped = index.AssignChangeId([first, second], "abc12345");

        Assert.That(stamped.Select(e => e.ChangeId), Is.All.EqualTo("abc12345"));
        Assert.That(index.Find("Bar")[0].ChangeId, Is.EqualTo("abc12345"));
        Assert.That(index.Find("Baz")[0].ChangeId, Is.EqualTo("abc12345"));
        Assert.That(index.Count, Is.EqualTo(2), "stamping replaces entries, it does not add");
        Assert.That(index.MarkReverted("abc12345"), Is.EqualTo(2));
    }

    [Test]
    public void AssignChangeId_ForAnEffectAlreadyDroppedByTheBound_StillReturnsTheStampedCopy()
    {
        var index = new EditEffectIndex();
        var dropped = Effect("Old", minute: 0);
        index.Append([dropped]);
        index.Append(Enumerable.Range(1, EditEffectIndex.MaxEffects).Select(i => Effect("N" + i, minute: i)));

        var stamped = index.AssignChangeId([dropped], "abc12345");

        Assert.That(stamped, Has.Count.EqualTo(1));
        Assert.That(stamped[0].ChangeId, Is.EqualTo("abc12345"));
        Assert.That(index.Find("Old"), Is.Empty);
    }

    [Test]
    public void Seed_AddsInTimeOrder_SkipsEffectsAlreadyHeld_AndKeepsTheBound()
    {
        var index = new EditEffectIndex();
        var newer = Effect("Bar", "ccc", 5);
        index.Append([newer]);

        // The same effect again (as read from a blob, here marked reverted in memory) must not duplicate; an older one lands before it.
        index.MarkReverted("ccc");
        index.Seed([Effect("Bar", "ccc", 5), Effect("Bar", "aaa", 1), Effect("Bar", "bbb", 3)]);

        Assert.That(index.Count, Is.EqualTo(3));
        Assert.That(index.Find("Bar").Select(e => e.ChangeId), Is.EqualTo(new[] { "bbb", "aaa" }), "the held effect stays reverted and the rest are newest first");

        var bounded = new EditEffectIndex();
        bounded.Seed(Enumerable.Range(0, EditEffectIndex.MaxEffects + 5).Select(i => Effect("N" + i, minute: i)));
        Assert.That(bounded.Count, Is.EqualTo(EditEffectIndex.MaxEffects));
        Assert.That(bounded.Find("N0"), Is.Empty);
    }

    [Test]
    public async Task Append_FromManyThreads_KeepsTheBoundAndLosesNothingBelowIt()
    {
        var index = new EditEffectIndex();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                index.Append([Effect($"W{worker}_{i}", minute: i)]);
            }
        })));

        Assert.That(index.Count, Is.EqualTo(8 * 40));
    }

    [Test]
    public void JsonOptions_RoundTripsAndReadsAnEffectWithMissingFields()
    {
        var effect = Effect("Bar", "abc12345", 2, EditEffectKind.SignatureChanged);

        var json = System.Text.Json.JsonSerializer.Serialize(effect, EditEffect.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<EditEffect>(json, EditEffect.JsonOptions);

        Assert.That(json, Does.Contain("\"simpleName\":\"Bar\"").And.Contain("\"kind\":\"signatureChanged\""));
        Assert.That(back, Is.EqualTo(effect));

        var sparse = System.Text.Json.JsonSerializer.Deserialize<EditEffect>("{\"kind\":\"removed\",\"simpleName\":\"Bar\"}", EditEffect.JsonOptions);
        Assert.That(sparse, Is.Not.Null);
        Assert.That(sparse!.Kind, Is.EqualTo(EditEffectKind.Removed));
        Assert.That(sparse.SimpleName, Is.EqualTo("Bar"));
        Assert.That(sparse.ChangeId, Is.Null);
        Assert.That(sparse.Reverted, Is.False);
    }
}

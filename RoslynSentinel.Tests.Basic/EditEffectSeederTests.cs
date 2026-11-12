using System.Globalization;
using System.Text.Json;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for seeding the edit effect index from operation blobs (Common/EditEffectSeeder.cs) and for the undo marker beside a blob
/// (OperationBlobWriter.MarkUndone). Blobs are written into a temp solution root the way OperationBlobWriter names them.
/// </summary>
[TestFixture]
public class EditEffectSeederTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private string _root = "";
    private string _operations = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "EditEffectSeeder", Guid.NewGuid().ToString("n"));
        _operations = Path.Combine(_root, ".roslynsentinel", "operations");
        Directory.CreateDirectory(_operations);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static EditEffect Effect(string name, DateTime time, string? changeId = null, string? callId = null, string? session = "session-1")
    {
        return new EditEffect(
            EditEffectKind.Renamed, "Foo." + name, null, name, "New" + name, null, "Foo.cs",
            callId, "RenameSymbol", changeId, session, "server-1", "agent-1", time);
    }

    private static string BlobName(string changeId, DateTime stamp)
    {
        var timestamp = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var fraction = (stamp.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture);
        return $"RenameSymbol_{timestamp}_{fraction}_{changeId}.json";
    }

    private string WriteBlob(string changeId, DateTime stamp, IReadOnlyList<EditEffect> effects, string? toolCallId = "a76e9f1c")
    {
        var path = Path.Combine(_operations, BlobName(changeId, stamp));
        var payload = new
        {
            toolName = "RenameSymbol",
            changeId,
            toolCallId,
            generatedUtc = stamp.ToString("O"),
            itemCount = 0,
            effects = JsonSerializer.SerializeToElement(effects, EditEffect.JsonOptions),
            items = Array.Empty<object>(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private string WriteRaw(string changeId, DateTime stamp, string text)
    {
        var path = Path.Combine(_operations, BlobName(changeId, stamp));
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public void Seed_ABlobInsideTheWindow_SeedsItsEffectsStampedWithTheBlobsChangeIdAndCallId()
    {
        WriteBlob("abc12345", Now.AddMinutes(-5), [Effect("Bar", Now.AddMinutes(-5))]);
        var index = new EditEffectIndex();

        var offered = EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(offered, Is.EqualTo(1));
        var effect = index.Find("Bar").Single();
        Assert.Multiple(() =>
        {
            Assert.That(effect.ChangeId, Is.EqualTo("abc12345"));
            Assert.That(effect.ToolCallId, Is.EqualTo("a76e9f1c"));
            Assert.That(effect.Kind, Is.EqualTo(EditEffectKind.Renamed));
            Assert.That(effect.NewName, Is.EqualTo("NewBar"));
            Assert.That(effect.Reverted, Is.False);
        });
    }

    [Test]
    public void Seed_AnEffectOfAnotherSessionOrServerInstance_IsSeededToo()
    {
        WriteBlob("abc12345", Now.AddMinutes(-5), [Effect("Bar", Now.AddMinutes(-5), session: "other-window"), Effect("Baz", Now.AddMinutes(-5), session: null)]);
        var index = new EditEffectIndex();

        EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(index.Find("Bar").Single().SessionId, Is.EqualTo("other-window"), "identity is filtered at lookup (EditEffectEvidence), not at seeding");
        Assert.That(index.Find("Baz").Single().SessionId, Is.Null);
    }

    [Test]
    public void Seed_IgnoresABlobOlderThanTheWindow_AndAnEffectOlderThanTheWindowInARecentBlob()
    {
        WriteBlob("old11111", Now.AddMinutes(-90), [Effect("Old", Now.AddMinutes(-90))]);
        WriteBlob("new22222", Now.AddMinutes(-5), [Effect("Fresh", Now.AddMinutes(-5)), Effect("Stale", Now.AddMinutes(-70))]);
        var index = new EditEffectIndex();

        EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(index.Find("Old"), Is.Empty);
        Assert.That(index.Find("Stale"), Is.Empty);
        Assert.That(index.Find("Fresh"), Has.Count.EqualTo(1));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Seed_ToleratesMalformedAndOldFormatBlobs_AndSeedsTheValidEffectsAroundThem()
    {
        var recent = Now.AddMinutes(-5);
        WriteRaw("bad00001", recent.AddSeconds(1), "{ this is not json");
        WriteRaw("bad00002", recent.AddSeconds(2), "[1, 2, 3]");
        WriteRaw("old00003", recent.AddSeconds(3), "{\"toolName\":\"RenameSymbol\",\"changeId\":\"old00003\",\"itemCount\":0,\"items\":[]}");
        WriteRaw("bad00004", recent.AddSeconds(4), "{\"changeId\":\"bad00004\",\"effects\":\"not an array\",\"items\":[]}");
        var valid = JsonSerializer.Serialize(Effect("Good", recent), EditEffect.JsonOptions);
        WriteRaw(
            "mix00005",
            recent.AddSeconds(5),
            "{\"changeId\":\"mix00005\",\"effects\":[42,{\"kind\":\"teleported\",\"symbolDisplay\":\"Foo.X\",\"simpleName\":\"X\",\"utcTime\":\"" + recent.ToString("O") + "\"},{\"kind\":\"renamed\"}," + valid + "],\"items\":[]}");
        File.WriteAllText(Path.Combine(_operations, "notes.json"), "{}");
        var index = new EditEffectIndex();

        var offered = EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(offered, Is.EqualTo(1), "a non-object, an unknown kind and an effect with no names are each skipped alone");
        Assert.That(index.Find("Good").Single().ChangeId, Is.EqualTo("mix00005"));
        Assert.That(index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Seed_SkipsABlobWhoseChangeWasUndone()
    {
        var undone = WriteBlob("und11111", Now.AddMinutes(-6), [Effect("Undone", Now.AddMinutes(-6))]);
        WriteBlob("act22222", Now.AddMinutes(-5), [Effect("Active", Now.AddMinutes(-5))]);
        OperationBlobWriter.MarkUndone(undone);
        var index = new EditEffectIndex();

        EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(index.Find("Undone"), Is.Empty);
        Assert.That(index.Find("Active"), Has.Count.EqualTo(1));
    }

    [Test]
    public void MarkUndone_WritesAnUndoneFileBesideTheBlob_ThatNoBlobReaderEnumerates()
    {
        var blob = WriteBlob("und11111", Now.AddMinutes(-6), [Effect("Undone", Now.AddMinutes(-6))]);

        Assert.That(OperationBlobWriter.IsUndone(blob), Is.False);
        OperationBlobWriter.MarkUndone(blob);

        Assert.That(OperationBlobWriter.IsUndone(blob), Is.True);
        Assert.That(File.Exists(blob), Is.True, "the blob is left as written");
        Assert.That(Directory.EnumerateFiles(_operations, "*.json").ToList(), Is.EqualTo(new[] { blob }), "readers that enumerate *.json see no new file");
        Assert.That(Directory.EnumerateFiles(_operations, "*" + OperationBlobWriter.UndoneMarkerExtension), Has.Exactly(1).Items);
        Assert.That(OperationBlobWriter.FindBlobPath("und11111", _root), Is.EqualTo(blob), "lookup by change id still finds the blob");
    }

    [Test]
    public void MarkUndone_ForAnUnwritablePath_DoesNotThrow()
    {
        var missingFolder = Path.Combine(_root, "no-such-folder", "RenameSymbol_20261112T115500Z_0000000_abc12345.json");

        Assert.DoesNotThrow(() => OperationBlobWriter.MarkUndone(missingFolder));
    }

    [Test]
    public void Seed_CalledTwice_DoesNotDuplicateEffects()
    {
        WriteBlob("abc12345", Now.AddMinutes(-5), [Effect("Bar", Now.AddMinutes(-5)), Effect("Baz", Now.AddMinutes(-4))]);
        var index = new EditEffectIndex();

        EditEffectSeeder.Seed(index, _root, Now);
        EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(index.Count, Is.EqualTo(2));
    }

    [Test]
    public void Seed_DoesNotDuplicateAnEffectTheChokepointAlreadyCommittedAndStamped()
    {
        var time = Now.AddMinutes(-5);
        var live = Effect("Bar", time, callId: "a76e9f1c") with { ChangeId = "abc12345" };
        var index = new EditEffectIndex();
        index.Append([live]);
        WriteBlob("abc12345", time, [Effect("Bar", time, callId: "a76e9f1c")]);

        EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(index.Count, Is.EqualTo(1), "same call, change, kind, name and time as the live entry");
    }

    [Test]
    public void Seed_IsBoundedAtTheIndexLimit_KeepingTheNewestEffects()
    {
        // Three blobs of 300 effects (newest A, then B, then C): the oldest blob is never opened once 500 are in hand, and the
        // index keeps the newest 500 (all of A and the newest 200 of B).
        WriteBlob("aaa00001", Now.AddMinutes(-10), [.. Enumerable.Range(0, 300).Select(i => Effect("A" + i, Now.AddMinutes(-10).AddMilliseconds(i)))]);
        WriteBlob("bbb00002", Now.AddMinutes(-20), [.. Enumerable.Range(0, 300).Select(i => Effect("B" + i, Now.AddMinutes(-20).AddMilliseconds(i)))]);
        WriteBlob("ccc00003", Now.AddMinutes(-30), [.. Enumerable.Range(0, 300).Select(i => Effect("C" + i, Now.AddMinutes(-30).AddMilliseconds(i)))]);
        var index = new EditEffectIndex();

        var offered = EditEffectSeeder.Seed(index, _root, Now);

        Assert.That(offered, Is.EqualTo(EditEffectIndex.MaxEffects));
        Assert.That(index.Count, Is.EqualTo(EditEffectIndex.MaxEffects));
        Assert.That(index.Find("A0"), Has.Count.EqualTo(1));
        Assert.That(index.Find("A299"), Has.Count.EqualTo(1));
        Assert.That(index.Find("B299"), Has.Count.EqualTo(1));
        Assert.That(index.Find("B99"), Is.Empty, "the older half of the second blob is dropped by the bound");
        Assert.That(index.Find("C0"), Is.Empty);
    }

    [Test]
    public void Seed_WithNoRootNoFolderOrAnEmptyFolder_SeedsNothingAndDoesNotThrow()
    {
        var index = new EditEffectIndex();

        Assert.That(EditEffectSeeder.Seed(index, null, Now), Is.EqualTo(0));
        Assert.That(EditEffectSeeder.Seed(index, "", Now), Is.EqualTo(0));
        Assert.That(EditEffectSeeder.Seed(index, Path.Combine(_root, "elsewhere"), Now), Is.EqualTo(0));
        Assert.That(EditEffectSeeder.Seed(index, _root, Now), Is.EqualTo(0), "an empty operations folder");
        Assert.That(index.Count, Is.EqualTo(0));
    }

    [Test]
    public void Seed_OnlyReadsTheBlobFolderOfTheGivenRoot()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), "EditEffectSeeder", Guid.NewGuid().ToString("n"));
        try
        {
            var otherOperations = Path.Combine(otherRoot, ".roslynsentinel", "operations");
            Directory.CreateDirectory(otherOperations);
            var path = Path.Combine(otherOperations, BlobName("oth00001", Now.AddMinutes(-5)));
            File.WriteAllText(path, JsonSerializer.Serialize(new { changeId = "oth00001", effects = JsonSerializer.SerializeToElement(new[] { Effect("Elsewhere", Now.AddMinutes(-5)) }, EditEffect.JsonOptions) }));
            WriteBlob("abc12345", Now.AddMinutes(-5), [Effect("Here", Now.AddMinutes(-5))]);
            var index = new EditEffectIndex();

            EditEffectSeeder.Seed(index, _root, Now);

            Assert.That(index.Find("Here"), Has.Count.EqualTo(1));
            Assert.That(index.Find("Elsewhere"), Is.Empty);
        }
        finally
        {
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Test]
    public void Seed_AfterTheBudgetIsSpent_StopsOpeningBlobs()
    {
        WriteBlob("abc12345", Now.AddMinutes(-5), [Effect("Bar", Now.AddMinutes(-5))]);
        var index = new EditEffectIndex();

        var offered = EditEffectSeeder.Seed(index, _root, Now, budget: TimeSpan.FromTicks(-1));

        Assert.That(offered, Is.EqualTo(0));
        Assert.That(index.Count, Is.EqualTo(0));
    }
}

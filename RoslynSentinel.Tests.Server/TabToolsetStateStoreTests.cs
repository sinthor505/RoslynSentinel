using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Server;

// Coverage for plan_tab_scoped_toolset_persistence.md Step 2: TabToolsetStateStore Load, Save and Clear
// against real temp directories. Each test gets its own root with sibling tab dirs a, b and c.
[TestFixture]
public class TabToolsetStateStoreTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs-tabstate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string TabA => Path.Combine(_root, "a");
    private string TabB => Path.Combine(_root, "b");
    private string TabC => Path.Combine(_root, "c");

    [Test]
    public void SaveThenLoad_RoundTrips()
    {
        new TabToolsetStateStore(TabA, "session-1").Save(new[] { ToolSetName.git, ToolSetName.declarations });

        var loaded = new TabToolsetStateStore(TabA, "session-1").Load();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.EnabledToolSets, Is.EqualTo(new[] { "git", "declarations" }));
        Assert.That(loaded.SessionId, Is.EqualTo("session-1"));
        Assert.That(Math.Abs((DateTime.UtcNow - loaded.SavedUtc).TotalSeconds), Is.LessThan(60));
    }

    [Test]
    public void Load_NoFile_SeedsFromNewestSiblingWithSameSession()
    {
        WriteState(TabB, new[] { "git" }, "session-1", new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc));
        WriteState(TabC, new[] { "declarations" }, "session-1", new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc));

        var loaded = new TabToolsetStateStore(TabA, "session-1").Load();

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.EnabledToolSets, Is.EqualTo(new[] { "declarations" }));
        Assert.That(File.Exists(Path.Combine(TabA, TabToolsetStateStore.FileName)), Is.True);
    }

    [Test]
    public void Load_NoFile_IgnoresSiblingWithOtherSession()
    {
        WriteState(TabB, new[] { "git" }, "session-other", new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc));

        var loaded = new TabToolsetStateStore(TabA, "session-1").Load();

        Assert.That(loaded, Is.Null);
        Assert.That(File.Exists(Path.Combine(TabA, TabToolsetStateStore.FileName)), Is.False);
    }

    [Test]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(TabA);
        File.WriteAllText(Path.Combine(TabA, TabToolsetStateStore.FileName), "{not json");

        var loaded = new TabToolsetStateStore(TabA, "session-1").Load();

        Assert.That(loaded, Is.Null);
    }

    [Test]
    public void Clear_MissingFile_DoesNotThrow()
    {
        var store = new TabToolsetStateStore(Path.Combine(_root, "missing"), "session-1");

        Assert.DoesNotThrow(() => store.Clear());
    }

    private static void WriteState(string dir, string[] names, string sessionId, DateTime savedUtc)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new TabToolsetState(names, sessionId, savedUtc),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(Path.Combine(dir, TabToolsetStateStore.FileName), json);
    }
}

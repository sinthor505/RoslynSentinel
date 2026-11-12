using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Follow-up slice D of proposal_not_found_cause_from_prior_tool_call: loading a DIFFERENT solution into the same PersistentWorkspaceManager
// clears the edit effect index (its effects name the previous solution's symbols and files) before the new solution's blobs are seeded;
// reloading the SAME solution keeps it. Real on-disk workspaces over two temp solution copies.
[TestFixture]
public class EditEffectSolutionSwitchTests
{
    private PersistentWorkspaceManager _manager;
    private TestSolutionFixture? _first;
    private TestSolutionFixture? _second;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(null);
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _first = new TestSolutionFixture();
        _second = new TestSolutionFixture();
    }

    [TearDown]
    public void TearDown()
    {
        _manager?.Dispose();
        _first?.Dispose();
        _second?.Dispose();
        _first = null;
        _second = null;
    }

    private static EditEffect Effect(string name, DateTime time)
    {
        return new EditEffect(
            EditEffectKind.Renamed, "Foo." + name, null, name, "New" + name, null, "Foo.cs",
            "a76e9f1c", "RenameSymbol", null, "session-1", "server-1", "agent-1", time);
    }

    private static string OperationsDirOf(TestSolutionFixture fixture) =>
        Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, ".roslynsentinel", "operations");

    private static void WriteBlob(TestSolutionFixture fixture, string changeId, EditEffect effect)
    {
        var dir = OperationsDirOf(fixture);
        Directory.CreateDirectory(dir);
        var stamp = effect.UtcTime;
        var timestamp = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var fraction = (stamp.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture);
        var payload = new
        {
            toolName = "RenameSymbol",
            changeId,
            toolCallId = "a76e9f1c",
            generatedUtc = stamp.ToString("O"),
            itemCount = 0,
            effects = JsonSerializer.SerializeToElement(new[] { effect }, EditEffect.JsonOptions),
            items = Array.Empty<object>(),
        };
        File.WriteAllText(Path.Combine(dir, $"RenameSymbol_{timestamp}_{fraction}_{changeId}.json"), JsonSerializer.Serialize(payload));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    [Test]
    public async Task ReloadingTheSameSolution_KeepsTheEffects_EvenWhenThePathIsSpelledDifferently()
    {
        await _manager.LoadSolutionAsync(_first!.SolutionPath);
        _manager.EditEffects.Append([Effect("Kept", DateTime.UtcNow)]);

        await _manager.LoadSolutionAsync(_first.SolutionPath);
        Assert.That(_manager.EditEffects.Find("Kept"), Has.Count.EqualTo(1), "a reload of the same solution keeps the index");

        var respelled = _first.SolutionPath.Replace('\\', '/');
        await _manager.LoadSolutionAsync(respelled);
        Assert.That(_manager.EditEffects.Find("Kept"), Has.Count.EqualTo(1), "the same file under another separator style is the same solution");

        await _manager.LoadSolutionAsync(_first.SolutionPath.ToUpperInvariant());
        Assert.That(_manager.EditEffects.Find("Kept"), Has.Count.EqualTo(1), "path identity ignores case, as everywhere else in the server");
    }

    [Test]
    public async Task LoadingADifferentSolution_ClearsTheEffects()
    {
        await _manager.LoadSolutionAsync(_first!.SolutionPath);
        _manager.EditEffects.Append([Effect("FromFirst", DateTime.UtcNow)]);
        Assert.That(_manager.EditEffects.Count, Is.EqualTo(1));

        await _manager.LoadSolutionAsync(_second!.SolutionPath);

        Assert.That(_manager.EditEffects.Count, Is.EqualTo(0), "the old solution's effects do not explain a miss in the new one");
    }

    [Test]
    public async Task LoadingTheFirstSolution_KeepsEffectsRecordedBeforeAnyLoad()
    {
        _manager.EditEffects.Append([Effect("Early", DateTime.UtcNow)]);

        await _manager.LoadSolutionAsync(_first!.SolutionPath);

        Assert.That(_manager.EditEffects.Find("Early"), Has.Count.EqualTo(1), "there was no previous solution to clear for");
    }

    [Test]
    public async Task AfterSwitching_SeedingReadsTheNewSolutionsBlobFolderOnly()
    {
        var now = DateTime.UtcNow;
        WriteBlob(_first!, "aaa11111", Effect("FromFirst", now.AddMinutes(-3)));
        WriteBlob(_second!, "bbb22222", Effect("FromSecond", now.AddMinutes(-2)));

        await _manager.LoadSolutionAsync(_first.SolutionPath);
        await WaitUntilAsync(() => _manager.EditEffects.Find("FromFirst").Count > 0);
        Assert.That(_manager.EditEffects.Find("FromFirst"), Has.Count.EqualTo(1), "the first solution's blob is seeded at its load");
        Assert.That(_manager.EditEffects.Find("FromSecond"), Is.Empty);

        await _manager.LoadSolutionAsync(_second.SolutionPath);
        await WaitUntilAsync(() => _manager.EditEffects.Find("FromSecond").Count > 0);

        Assert.That(_manager.EditEffects.Find("FromSecond"), Has.Count.EqualTo(1), "the second solution's blob is seeded after the clear");
        Assert.That(_manager.EditEffects.Find("FromFirst"), Is.Empty, "the first solution's effects were cleared by the switch and its folder is not read again");
        Assert.That(_manager.EditEffects.Count, Is.EqualTo(1));
    }

    [Test]
    public void Clear_DropsEveryEffect_AndReportsHowManyWereHeld()
    {
        var index = new EditEffectIndex();
        index.Append([Effect("A", DateTime.UtcNow), Effect("B", DateTime.UtcNow)]);

        Assert.That(index.Clear(), Is.EqualTo(2));
        Assert.That(index.Count, Is.EqualTo(0));
        Assert.That(index.Find("A"), Is.Empty);
        Assert.That(index.Clear(), Is.EqualTo(0), "clearing an empty index is a no-op");
    }
}

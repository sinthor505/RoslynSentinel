using System.Text.Json;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ErrorVariants;

[TestFixture]
public sealed class ToolEffectListTests
{
    private const string HeaderTranscript = "FileNotFound.armF.sample-01.transcript.json";

    [Test]
    public void EveryToolOfTheCheckedInHeader_IsClassifiedReadOrWrite()
    {
        var header = SavedTranscript.Load(Path.Combine(AppContext.BaseDirectory, "TestData", HeaderTranscript));

        var unknown = header.Tools.Where(tool => ToolEffectList.Classify(tool) == ToolEffect.Unknown).ToList();

        Assert.That(header.Tools, Has.Count.EqualTo(55), "the checked-in header is expected to list 55 tools");
        Assert.That(unknown, Is.Empty, "tools in the header that ToolEffectList does not classify: " + string.Join(", ", unknown));
    }

    [Test]
    public void ReadAndWriteTools_CoverExactlyTheHeaderTools_WithNoOverlapAndNoDuplicates()
    {
        var header = SavedTranscript.Load(Path.Combine(AppContext.BaseDirectory, "TestData", HeaderTranscript));
        var headerTools = header.Tools.ToHashSet(StringComparer.Ordinal);

        var read = ToolEffectList.ReadTools;
        var write = ToolEffectList.WriteTools;
        var listed = read.Concat(write).ToList();

        Assert.That(read.Intersect(write, StringComparer.Ordinal), Is.Empty, "a tool is in both ReadTools and WriteTools");
        Assert.That(listed.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(listed.Count), "a tool is listed twice");

        var stale = listed.Where(name => !headerTools.Contains(name)).ToList();
        var missing = headerTools.Where(name => !listed.Contains(name, StringComparer.Ordinal)).ToList();
        Assert.That(stale, Is.Empty, "listed but not in the header (stale): " + string.Join(", ", stale));
        Assert.That(missing, Is.Empty, "in the header but not listed: " + string.Join(", ", missing));
        Assert.That(listed, Has.Count.EqualTo(55));
    }

    [TestCase("Search", ToolEffect.Read)]
    [TestCase("ReadFile", ToolEffect.Read)]
    [TestCase("GetFileOutline", ToolEffect.Read)]
    [TestCase("ReplaceSnippet", ToolEffect.Write)]
    [TestCase("Build", ToolEffect.Write)]
    [TestCase("RunTest", ToolEffect.Write)]
    [TestCase("Member", ToolEffect.Write)]
    [TestCase("NoSuchTool", ToolEffect.Unknown)]
    [TestCase("search", ToolEffect.Unknown)]
    [TestCase("", ToolEffect.Unknown)]
    public void Classify_ReturnsTheListedEffect_AndUnknownForAnythingElse(string toolName, ToolEffect expected)
    {
        Assert.That(ToolEffectList.Classify(toolName), Is.EqualTo(expected));
    }

    [Test]
    public void IsRead_IsTrueOnlyForReadTools()
    {
        Assert.That(ToolEffectList.IsRead("Search"), Is.True);
        Assert.That(ToolEffectList.IsRead("Build"), Is.False);
        Assert.That(ToolEffectList.IsRead("NoSuchTool"), Is.False);
    }

    [TestCase("Member", "{\"operation\":\"view\"}")]
    [TestCase("MethodSignature", "{\"operation\":\"view\"}")]
    [TestCase("SyncInterface", "{\"action\":\"verify\"}")]
    [TestCase("ProjectDoc", "{\"action\":\"trailers\"}")]
    public void ClassifyCall_ViewOperation_IsRead(string toolName, string argumentsJson)
    {
        Assert.That(ToolEffectList.ClassifyCall(toolName, argumentsJson), Is.EqualTo(ToolEffect.Read));
    }

    [TestCase("Member", "{\"operation\":\"replace\"}")]
    [TestCase("Member", "{}")]
    [TestCase("ProjectDoc", "{\"action\":\"close\"}")]
    [TestCase("Member", "not json")]
    public void ClassifyCall_WriteOrMissingOperation_IsWrite(string toolName, string argumentsJson)
    {
        Assert.That(ToolEffectList.ClassifyCall(toolName, argumentsJson), Is.EqualTo(ToolEffect.Write));
    }

    [Test]
    public void ClassifyCall_DictionaryWithJsonElementValue_IsRead()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"operation\":\"view\"}");

        Assert.That(ToolEffectList.ClassifyCall("Member", arguments!), Is.EqualTo(ToolEffect.Read));
    }

    [TestCase("Search", "{}", ToolEffect.Read)]
    [TestCase("Build", "{}", ToolEffect.Write)]
    [TestCase("NoSuchTool", "{}", ToolEffect.Unknown)]
    public void ClassifyCall_NonTableTool_MatchesClassify(string toolName, string argumentsJson, ToolEffect expected)
    {
        Assert.That(ToolEffectList.ClassifyCall(toolName, argumentsJson), Is.EqualTo(expected));
        Assert.That(ToolEffectList.ClassifyCall(toolName, argumentsJson), Is.EqualTo(ToolEffectList.Classify(toolName)));
    }
}

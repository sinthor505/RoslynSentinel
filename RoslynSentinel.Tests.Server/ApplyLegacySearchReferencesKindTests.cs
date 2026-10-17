using System.Collections.Generic;
using System.Text.Json;

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for <see cref="ToolArgumentValidator.ApplyLegacySearchReferencesKind"/>: a call to
/// <c>Search</c> that still passes the retired <c>referencesKind</c> parameter with
/// <c>mode: references</c> is rewritten to the folded mode, and every other call is left untouched.
/// </summary>
[TestFixture]
[Category("ToolArgumentValidator")]
public class ApplyLegacySearchReferencesKindTests
{
    private static Dictionary<string, JsonElement> Args(string mode, string referencesKind)
    {
        return new Dictionary<string, JsonElement>
        {
            ["mode"] = JsonSerializer.SerializeToElement(mode),
            ["referencesKind"] = JsonSerializer.SerializeToElement(referencesKind),
            ["query"] = JsonSerializer.SerializeToElement("Target"),
        };
    }

    [TestCase("callers", "callers")]
    [TestCase("implementations", "implementations")]
    [TestCase("all", "references")]
    public void ReferencesWithLegacyKind_RewritesModeAndRemovesReferencesKind(string legacyKind, string expectedMode)
    {
        var args = Args("references", legacyKind);

        var note = ToolArgumentValidator.ApplyLegacySearchReferencesKind(null, "Search", args);

        Assert.That(args.ContainsKey("referencesKind"), Is.False, "The retired referencesKind key must be removed.");
        Assert.That(args["mode"].GetString(), Is.EqualTo(expectedMode));
        Assert.That(args["query"].GetString(), Is.EqualTo("Target"), "Other arguments must be preserved.");
        Assert.That(note, Is.Not.Null, "A rewrite must return a note for the model.");
        Assert.That(note![0], Does.StartWith("Note: 'referencesKind' is no longer a parameter of Search"));
        Assert.That(note[0], Does.Contain($"treated as mode '{expectedMode}'"));
    }

    [Test]
    public void OtherToolName_IsLeftUntouched()
    {
        var args = Args("references", "callers");

        var note = ToolArgumentValidator.ApplyLegacySearchReferencesKind(null, "FindReferences", args);

        Assert.That(note, Is.Null);
        Assert.That(args.ContainsKey("referencesKind"), Is.True);
        Assert.That(args["mode"].GetString(), Is.EqualTo("references"));
    }

    [Test]
    public void ModeOtherThanReferences_IsLeftUntouched()
    {
        var args = Args("symbol", "callers");

        var note = ToolArgumentValidator.ApplyLegacySearchReferencesKind(null, "Search", args);

        Assert.That(note, Is.Null);
        Assert.That(args.ContainsKey("referencesKind"), Is.True);
        Assert.That(args["mode"].GetString(), Is.EqualTo("symbol"));
    }

    [Test]
    public void UnknownReferencesKindValue_IsLeftUntouched_SoValidateRejectsIt()
    {
        var args = Args("references", "bogus");

        var note = ToolArgumentValidator.ApplyLegacySearchReferencesKind(null, "Search", args);

        Assert.That(note, Is.Null);
        Assert.That(args.ContainsKey("referencesKind"), Is.True);
        Assert.That(args["mode"].GetString(), Is.EqualTo("references"));
    }

    [Test]
    public void NullArguments_ReturnsNull()
    {
        var note = ToolArgumentValidator.ApplyLegacySearchReferencesKind(null, "Search", null);

        Assert.That(note, Is.Null);
    }
}

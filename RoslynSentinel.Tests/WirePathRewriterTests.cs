using System.Text.Json.Nodes;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
public class WirePathRewriterTests
{
    private const string Root = @"C:\repo\Sol";

    #region TryRelativize tests

    [Test]
    public void TryRelativize_UnderRoot_ReturnsForwardSlashRelative()
    {
        var value = @"C:\repo\Sol\a\B.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.True, "Path under root must be relativized.");
        Assert.That(relative, Is.EqualTo("a/B.cs"), "Result must use forward slashes.");
    }

    [Test]
    public void TryRelativize_LowercaseDriveLetter_RewritesToRelative()
    {
        var value = @"c:\repo\sol\a\B.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.True, "Path must be matched case-insensitively.");
        Assert.That(relative, Is.EqualTo("a/B.cs"), "Result must use forward slashes.");
    }

    [Test]
    public void TryRelativize_MixedSeparators_RewritesToRelative()
    {
        var value = @"C:\repo\Sol/a\B.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.True, "Mixed separators in input must be accepted.");
        Assert.That(relative, Is.EqualTo("a/B.cs"), "Output must normalize to forward slashes.");
    }

    [Test]
    public void TryRelativize_SiblingPrefix_NotRewritten()
    {
        var value = @"C:\repo\Sol2\a.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.False, "Sibling prefix must not match.");
        Assert.That(relative, Is.EqualTo(value), "Relative out-param must be unchanged on failure.");
    }

    [Test]
    public void TryRelativize_OutsideRoot_NotRewritten()
    {
        var value = @"C:\other\path\a.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.False, "Path outside root must not match.");
        Assert.That(relative, Is.EqualTo(value), "Relative out-param must be unchanged on failure.");
    }

    [Test]
    public void TryRelativize_ExactRoot_NotRewritten()
    {
        var value = @"C:\repo\Sol";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.False, "Exact root path must not be rewritten.");
        Assert.That(relative, Is.EqualTo(value), "Relative out-param must be unchanged on failure.");
    }

    [Test]
    public void TryRelativize_MultilineValue_NotRewritten()
    {
        var value = @"C:\repo\Sol\a.cs" + "\nline2";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.False, "Multiline values must not be rewritten.");
        Assert.That(relative, Is.EqualTo(value), "Relative out-param must be unchanged on failure.");
    }

    [Test]
    public void TryRelativize_RootWithTrailingBackslash_Matches()
    {
        var rootWithTrailing = Root + "\\";

        var result = WirePathRewriter.TryRelativize(@"C:\repo\Sol\a\B.cs", rootWithTrailing, out var relative);

        Assert.That(result, Is.True, "Root with trailing backslash must be trimmed before matching.");
        Assert.That(relative, Is.EqualTo("a/B.cs"));
    }

    [Test]
    public void TryRelativize_RootWithTrailingForwardSlash_Matches()
    {
        var rootWithTrailing = Root + "/";

        var result = WirePathRewriter.TryRelativize(@"C:\repo\Sol\a\B.cs", rootWithTrailing, out var relative);

        Assert.That(result, Is.True, "Root with trailing forward slash must be trimmed before matching.");
        Assert.That(relative, Is.EqualTo("a/B.cs"));
    }

    [Test]
    public void TryRelativize_EmptyValue_ReturnsFalse()
    {
        var result = WirePathRewriter.TryRelativize(string.Empty, Root, out var relative);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryRelativize_NullRoot_ReturnsFalse()
    {
        var result = WirePathRewriter.TryRelativize(@"C:\repo\Sol\a.cs", null!, out var relative);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryRelativize_EmptyRoot_ReturnsFalse()
    {
        var result = WirePathRewriter.TryRelativize(@"C:\repo\Sol\a.cs", string.Empty, out var relative);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryRelativize_RootOnlySlashes_ReturnsFalse()
    {
        var result = WirePathRewriter.TryRelativize(@"C:\repo\Sol\a.cs", "\\\\", out var relative);

        Assert.That(result, Is.False, "Root consisting only of slashes is invalid.");
    }

    [Test]
    public void TryRelativize_ValueShorterThanRoot_ReturnsFalse()
    {
        var result = WirePathRewriter.TryRelativize(@"C:\repo", Root, out var relative);

        Assert.That(result, Is.False);
    }

    [Test]
    public void TryRelativize_DotDotSegment_NotRewritten()
    {
        var value = @"C:\repo\Sol\..\other\a.cs";

        var result = WirePathRewriter.TryRelativize(value, Root, out var relative);

        Assert.That(result, Is.False, "Paths containing .. segments must not be rewritten.");
    }

    #endregion

    #region RelativizeInText tests

    [Test]
    public void RelativizeInText_SingleOccurrence_Removed()
    {
        var text = $"Failed in {Root}\\x\\A.cs";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(1));
        Assert.That(result, Is.EqualTo("Failed in x\\A.cs"));
    }

    [Test]
    public void RelativizeInText_MultipleOccurrences_AllRemoved()
    {
        var text = $"Failed in {Root}\\x\\A.cs and {Root}\\y\\B.cs";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(2));
        Assert.That(result, Is.EqualTo("Failed in x\\A.cs and y\\B.cs"));
    }

    [Test]
    public void RelativizeInText_SiblingPrefix_NotRemoved()
    {
        var text = $"Path {Root}2\\a.cs is outside";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(text), "Sibling prefixes must not be removed.");
    }

    [Test]
    public void RelativizeInText_CaseInsensitive_Matches()
    {
        var text = "Failed in C:\\REPO\\SOL\\x\\A.cs";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(1));
        Assert.That(result, Is.EqualTo("Failed in x\\A.cs"));
    }

    [Test]
    public void RelativizeInText_ForwardSlashSeparator_Matches()
    {
        var text = $"Failed in {Root}/x/A.cs";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(1));
        Assert.That(result, Is.EqualTo("Failed in x/A.cs"));
    }

    [Test]
    public void RelativizeInText_DotDotSegmentFollowing_NotRemoved()
    {
        var text = $"Path {Root}\\..\\other\\a.cs";

        var result = WirePathRewriter.RelativizeInText(text, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(0), "..-segments must not be exposed by removal.");
        Assert.That(result, Is.EqualTo(text));
    }

    [Test]
    public void RelativizeInText_EmptyText_NoReplacements()
    {
        var result = WirePathRewriter.RelativizeInText(string.Empty, Root, out var replacements);

        Assert.That(replacements, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void RelativizeInText_NullRoot_NoReplacements()
    {
        var text = $"Failed in {Root}\\a.cs";

        var result = WirePathRewriter.RelativizeInText(text, null!, out var replacements);

        Assert.That(replacements, Is.EqualTo(0));
        Assert.That(result, Is.EqualTo(text));
    }

    [Test]
    public void RelativizeInText_RootWithTrailingSlashes_Matches()
    {
        var rootWithTrailing = Root + "\\\\";
        var text = $"Failed in {Root}\\x\\A.cs";

        var result = WirePathRewriter.RelativizeInText(text, rootWithTrailing, out var replacements);

        Assert.That(replacements, Is.EqualTo(1));
        Assert.That(result, Is.EqualTo("Failed in x\\A.cs"));
    }

    #endregion

    #region RewriteJsonText tests

    [Test]
    public void RewriteJsonText_WholeValueAndKey_Rewritten()
    {
        var json = "{\"successData\":[{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}],\"listSummary\":{\"byFile\":{\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\":5}}}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null, "JSON should be rewritten.");
        Assert.That(rewrites, Is.GreaterThan(0), "Rewrites should occur.");

        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);
        
        var successArray = parsed?["successData"]?.AsArray();
        var firstItem = successArray?[0]?.AsObject();
        Assert.That(firstItem?["filePath"]?.GetValue<string>(), Is.EqualTo("x/A.cs"));
    }

    [Test]
    public void RewriteJsonText_SolutionRootStampedFirst()
    {
        var json = "{\"data\":\"something\",\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out _);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        var keys = parsed.Select(kvp => kvp.Key).ToList();
        Assert.That(keys[0], Is.EqualTo("solutionRoot"), "solutionRoot must be inserted as first property.");
        Assert.That(parsed["solutionRoot"]?.GetValue<string>(), Is.EqualTo(Root));
    }

    [Test]
    public void RewriteJsonText_VerbatimKeys_NotRewritten()
    {
        var json = "{\"source\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\",\"preview\":\"C:\\\\repo\\\\Sol\\\\y\\\\B.cs\",\"contextSnippet\":\"C:\\\\repo\\\\Sol\\\\z\\\\C.cs\",\"filePath\":\"C:\\\\repo\\\\Sol\\\\changed\\\\D.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        Assert.That(parsed["source"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\x\A.cs"),
            "source is a verbatim key and must not be rewritten.");
        Assert.That(parsed["preview"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\y\B.cs"),
            "preview is a verbatim key and must not be rewritten.");
        Assert.That(parsed["contextSnippet"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\z\C.cs"),
            "contextSnippet is a verbatim key and must not be rewritten.");
        Assert.That(parsed["filePath"]?.GetValue<string>(), Is.EqualTo("changed/D.cs"),
            "filePath is not verbatim and should be rewritten.");
        Assert.That(rewrites, Is.EqualTo(1), "Only the filePath value should be rewritten; verbatim values are untouched.");
    }

    [Test]
    public void RewriteJsonText_KeepAbsoluteKeys_NotRewritten()
    {
        var json = "{\"solutionPath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\",\"solutionRoot\":\"C:\\\\repo\\\\Sol\",\"binaryPath\":\"C:\\\\repo\\\\Sol\\\\bin\\\\app.exe\",\"filePath\":\"C:\\\\repo\\\\Sol\\\\changed\\\\D.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        Assert.That(parsed["solutionPath"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\x\A.cs"),
            "solutionPath stays absolute.");
        Assert.That(parsed["binaryPath"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\bin\app.exe"),
            "binaryPath stays absolute.");
        Assert.That(parsed["filePath"]?.GetValue<string>(), Is.EqualTo("changed/D.cs"),
            "filePath is rewritten.");
    }

    [Test]
    public void RewriteJsonText_LargeResultSubtree_Untouched()
    {
        var json = "{\"largeResult\":{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"},\"filePath\":\"C:\\\\repo\\\\Sol\\\\changed\\\\D.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out _);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        var largeResult = parsed["largeResult"]?.AsObject();
        Assert.That(largeResult?["filePath"]?.GetValue<string>(), Is.EqualTo(@"C:\repo\Sol\x\A.cs"),
            "largeResult subtree must stay absolute.");
    }

    [Test]
    public void RewriteJsonText_StatusMessage_SubstringRewritten()
    {
        var json = "{\"statusMessage\":\"Failed in C:\\\\repo\\\\Sol\\\\x\\\\A.cs and C:\\\\repo\\\\Sol\\\\y\\\\B.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        var message = parsed["statusMessage"]?.GetValue<string>();
        Assert.That(message, Is.EqualTo("Failed in x\\A.cs and y\\B.cs"),
            "Root prefixes in message values must be removed.");
        Assert.That(rewrites, Is.GreaterThan(0));
    }

    [Test]
    public void RewriteJsonText_StatusMessageWithSibling_SiblingUntouched()
    {
        var json = "{\"statusMessage\":\"Failed in C:\\\\repo\\\\Sol\\\\x\\\\A.cs and C:\\\\repo\\\\Sol2\\\\a.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out _);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed, Is.Not.Null);

        var message = parsed["statusMessage"]?.GetValue<string>();
        Assert.That(message, Is.EqualTo("Failed in x\\A.cs and C:\\repo\\Sol2\\a.cs"),
            "Only the matching root prefix should be removed; sibling should stay.");
    }

    [Test]
    public void RewriteJsonText_NothingToRewrite_ReturnsNull()
    {
        var json = "{\"data\":\"no paths here\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Null, "No rewrites should return null.");
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_NotJson_ReturnsNull()
    {
        var text = "This is plain text, not JSON.";

        var result = WirePathRewriter.RewriteJsonText(text, Root, out var rewrites);

        Assert.That(result, Is.Null);
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_JsonArray_ReturnsNull()
    {
        var text = "[{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}]";

        var result = WirePathRewriter.RewriteJsonText(text, Root, out var rewrites);

        Assert.That(result, Is.Null, "JSON arrays must return null (not a JSON object).");
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_InvalidJson_ReturnsNull()
    {
        var text = "{ invalid json }";

        var result = WirePathRewriter.RewriteJsonText(text, Root, out var rewrites);

        Assert.That(result, Is.Null);
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_OutputKeepsAngleBracketsUnescaped()
    {
        var json = "{\"type\":\"List<int>\",\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out _);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("List<int>"),
            "Angle brackets must not be escaped in the output JSON.");
    }

    [Test]
    public void RewriteJsonText_EmptyRoot_ReturnsNull()
    {
        var json = "{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, string.Empty, out var rewrites);

        Assert.That(result, Is.Null);
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_NullRoot_ReturnsNull()
    {
        var json = "{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}";

        var result = WirePathRewriter.RewriteJsonText(json, null!, out var rewrites);

        Assert.That(result, Is.Null);
        Assert.That(rewrites, Is.EqualTo(0));
    }

    [Test]
    public void RewriteJsonText_NestedObjects_PathsRewritten()
    {
        var json = "{\"nested\":{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"}}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        var nested = parsed?["nested"]?.AsObject();
        Assert.That(nested?["filePath"]?.GetValue<string>(), Is.EqualTo("x/A.cs"));
        Assert.That(rewrites, Is.GreaterThan(0));
    }

    [Test]
    public void RewriteJsonText_ArrayOfObjects_PathsRewritten()
    {
        var json = "{\"items\":[{\"filePath\":\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\"},{\"filePath\":\"C:\\\\repo\\\\Sol\\\\y\\\\B.cs\"}]}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        var items = parsed?["items"]?.AsArray();
        Assert.That(items?[0]?.AsObject()?["filePath"]?.GetValue<string>(), Is.EqualTo("x/A.cs"));
        Assert.That(items?[1]?.AsObject()?["filePath"]?.GetValue<string>(), Is.EqualTo("y/B.cs"));
        Assert.That(rewrites, Is.GreaterThan(1));
    }

    [Test]
    public void RewriteJsonText_ObjectKeyRewrite_NoCollision()
    {
        var json = "{\"C:\\\\repo\\\\Sol\\\\x\\\\A.cs\":42}";

        var result = WirePathRewriter.RewriteJsonText(json, Root, out var rewrites);

        Assert.That(result, Is.Not.Null);
        var parsed = JsonNode.Parse(result)?.AsObject();
        Assert.That(parsed?["x/A.cs"]?.GetValue<int>(), Is.EqualTo(42),
            "Object keys should be rewritten to relative form.");
    }

    #endregion
}

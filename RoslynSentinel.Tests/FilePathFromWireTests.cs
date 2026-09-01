// Regression coverage for FilePath.FromWire.
//
// PersistentWorkspaceManager.GetSolutionRoot() returns null whenever no solution is loaded, or
// the loaded solution is in-memory and has no file path. Tool methods call FromWire before their
// own try/catch, so a null root used to reach Path.Combine and throw a raw ArgumentNullException
// out of the MCP boundary instead of returning a structured error.

namespace RoslynSentinel.Tests;

[TestFixture]
public class FilePathFromWireTests
{
    [Test]
    public void FromWire_NullSolutionRoot_RelativePath_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => FilePath.FromWire("Test.cs", null));
    }

    [Test]
    public void FromWire_NullSolutionRoot_RelativePath_PreservesCallerPath()
    {
        var result = FilePath.FromWire("Test.cs", null);

        Assert.That(result.Absolute, Is.EqualTo("Test.cs"),
            "With no solution root the caller's path must be preserved verbatim — resolving it "
            + "against the process working directory would point outside the solution.");
    }

    [Test]
    public void FromWire_EmptySolutionRoot_RelativePath_PreservesCallerPath()
    {
        var result = FilePath.FromWire("Sub/Test.cs", "   ");

        Assert.That(result.Absolute, Is.EqualTo(Path.Combine("Sub", "Test.cs")),
            "Path content is preserved but separators are canonicalized to the platform separator.");
    }

    [Test]
    public void FromWire_NullSolutionRoot_RootedPath_StillNormalizesToFullPath()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "Test.cs");

        var result = FilePath.FromWire(rooted, null);

        Assert.That(result.Absolute, Is.EqualTo(Path.GetFullPath(rooted)));
    }

    [Test]
    public void FromWire_WithSolutionRoot_RelativePath_ResolvesAgainstRoot()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var result = FilePath.FromWire("Test.cs", root);

        Assert.That(result.Absolute, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "Test.cs"))));
    }

    [Test]
    public void FromWire_EmptyPath_NullSolutionRoot_ReturnsEmpty()
    {
        var result = FilePath.FromWire("", null);

        Assert.That(result.Absolute, Is.Empty);
    }

    // Regression coverage for the LoadSolution "stray quotes baked into the path" bug — the same
    // sanitization now lives in NormalizeWirePath so every FromWire caller gets it for free.
    [Test]
    public void FromWire_PathWrappedInSingleQuotes_QuotesAreStripped()
    {
        var result = FilePath.FromWire("'Test.cs'", null);

        Assert.That(result.Absolute, Is.EqualTo("Test.cs"),
            "Stray wrapping single quotes must be stripped before the path is used.");
    }

    [Test]
    public void FromWire_PathWrappedInDoubleQuotes_QuotesAreStripped()
    {
        var result = FilePath.FromWire("\"Test.cs\"", null);

        Assert.That(result.Absolute, Is.EqualTo("Test.cs"),
            "Stray wrapping double quotes must be stripped before the path is used.");
    }

    [Test]
    public void FromWire_PathWithSurroundingWhitespace_IsTrimmed()
    {
        var result = FilePath.FromWire("  Test.cs\n", null);

        Assert.That(result.Absolute, Is.EqualTo("Test.cs"),
            "Leading/trailing whitespace must be trimmed before the path is used.");
    }

    [Test]
    public void FromWire_PathWithSmartQuotes_QuotesAreStripped()
    {
        var result = FilePath.FromWire("\u2018Test.cs\u2019", null);

        Assert.That(result.Absolute, Is.EqualTo("Test.cs"),
            "Smart/curly quotes must be stripped just like straight quotes.");
    }

    [Test]
    public void NormalizeWirePath_UncPathWrappedInQuotes_PreservesLeadingSlashes()
    {
        var result = FilePath.NormalizeWirePath("\"\\\\server\\share\\Test.cs\"");

        Assert.That(result, Is.EqualTo(@"\\server\share\Test.cs"),
            "Quote-stripping must not consume the UNC path's leading double backslash.");
    }

    // Regression coverage for the PlanImplementVerify run-5 harness bug: a model submitting
    // forward-slash paths for every ApplyDiff call got a FilePath whose Absolute never matched
    // FileSystemWatcher's always-backslash e.FullPath, silently defeating the self-write
    // drift-suppression dictionary lookup and flagging the model's own write as "external drift"
    // that no amount of reloading could ever clear.
    [Test]
    public void Constructor_ForwardSlashPath_CanonicalizesToPlatformSeparator()
    {
        var forwardSlash = new FilePath("C:/Users/dev/Foo.cs");
        var backSlash = new FilePath(@"C:\Users\dev\Foo.cs");

        Assert.That(forwardSlash.Absolute, Is.EqualTo(backSlash.Absolute),
            "A path submitted with forward slashes must produce the same Absolute value as the "
            + "equivalent backslash path, so dictionary lookups keyed by FileSystemWatcher's "
            + "backslash-only e.FullPath still find it.");
    }

    [Test]
    public void FromWire_UncPath_PreservesLeadingDoubleSlash()
    {
        var result = FilePath.FromWire(@"\\server\share\Test.cs", null);

        Assert.That(result.Absolute, Does.StartWith(@"\\"),
            "Canonicalization must not collapse the UNC path's required leading double separator.");
    }
}

// Unit coverage for DocumentLookup.TryGetDocument, the shared path -> Document core behind
// IWorkspaceReader.GetDocumentAsync (blocker: path lookup case sensitive / drive letter).
//
// Ambiguity rule under test: an exact path (compared ignoring case) always wins; a bare file name
// ("Foo.cs", no directory part) resolves only when exactly one document in the solution has that
// name, otherwise the result is Ambiguous and carries every candidate path. This replaces the old
// first-wins `Name ==` / FirstOrDefault lookups, which silently picked an arbitrary duplicate.

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Tests;

[TestFixture]
public class DocumentLookupTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "DocumentLookupTests_root");

    private static string PathOf(params string[] segments) => Path.Combine([Root, .. segments]);

    private static Solution BuildSolution(params string[] documentPaths)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        foreach (var path in documentPaths)
        {
            project = project.AddDocument(Path.GetFileName(path), "class C {}", filePath: path).Project;
        }

        return project.Solution;
    }

    [Test]
    public void ExactPath_Found()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper(PathOf("A", "Foo.cs")));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Found));
        Assert.That(result.Document!.FilePath, Is.EqualTo(PathOf("A", "Foo.cs")));
    }

    [Test]
    public void ExactPath_DifferentCase_Found_AndReturnsCanonicalWorkspacePath()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"));
        var misCased = PathOf("A", "Foo.cs").ToUpperInvariant();

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper(misCased));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Found));
        Assert.That(result.Document!.FilePath, Is.EqualTo(PathOf("A", "Foo.cs")),
            "callers must use the document's own path downstream, not the spelling they passed in");
    }

    [Test]
    public void BareName_Unique_Found()
    {
        var solution = BuildSolution(PathOf("A", "Unique.cs"), PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("Unique.cs"));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Found));
        Assert.That(result.Document!.FilePath, Is.EqualTo(PathOf("A", "Unique.cs")));
    }

    [Test]
    public void BareName_Unique_DifferentCase_Found()
    {
        var solution = BuildSolution(PathOf("A", "Unique.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("unique.CS"));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Found));
    }

    [Test]
    public void BareName_Duplicate_IsAmbiguous_AndNamesEveryCandidate()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs"), PathOf("A", "Other.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("Foo.cs"));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Ambiguous));
        Assert.That(result.Document, Is.Null, "an ambiguous lookup must never hand back an arbitrary document");
        Assert.That(result.CandidatePaths, Is.EquivalentTo(new[] { PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs") }));

        var error = result.ToResultError();
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(error.Message, Does.Contain(PathOf("A", "Foo.cs")).And.Contain(PathOf("B", "Foo.cs")));
    }

    [Test]
    public void ExactPath_WinsOverDuplicateBareName()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper(PathOf("B", "Foo.cs")));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.Found));
        Assert.That(result.Document!.FilePath, Is.EqualTo(PathOf("B", "Foo.cs")),
            "a full path must pick its own document even though another document shares the file name");
    }

    [Test]
    public void RootedPathInUnknownDirectory_IsNotFound_NotBareNameMatched()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper(PathOf("C", "Foo.cs")));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.NotFound),
            "a path with a directory part must not degrade to a file-name guess");
        Assert.That(result.CandidatePaths, Is.EquivalentTo(new[] { PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs") }),
            "NotFound must still name the closest real paths so the caller can recover in one turn");
        Assert.That(result.ToResultError().ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public void BareName_Missing_IsNotFound()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"));

        var result = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("Missing.cs"));

        Assert.That(result.Status, Is.EqualTo(DocumentLookupStatus.NotFound));
        Assert.That(result.CandidatePaths, Is.Empty);
    }

    [Test]
    public void GetDocumentOrThrow_MapsStatusToTypedToolExceptions()
    {
        var solution = BuildSolution(PathOf("A", "Foo.cs"), PathOf("B", "Foo.cs"));

        var ambiguous = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("Foo.cs"));
        var missing = DocumentLookup.TryGetDocument(solution, new FilePathWrapper("Missing.cs"));

        Assert.Throws<ToolAmbiguousMatchException>(() => ambiguous.GetDocumentOrThrow());
        Assert.Throws<ToolNotFoundException>(() => missing.GetDocumentOrThrow());
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class DocumentLookupBareNameTests
{
    private static Solution Build(AdhocWorkspace workspace, params (string Project, string Path)[] documents)
    {
        var solution = workspace.CurrentSolution;
        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (projectIds.ContainsKey(document.Project))
            {
                continue;
            }

            var projectId = ProjectId.CreateNewId(document.Project);
            projectIds[document.Project] = projectId;
            var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), document.Project, document.Project, LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
            solution = solution.AddProject(projectInfo);
        }

        foreach (var document in documents)
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(projectIds[document.Project]), Path.GetFileName(document.Path), SourceText.From("class C { }"), filePath: document.Path);
        }

        return solution;
    }

    [Test]
    public void UniqueBareName_ResolvesToTheOneDocument()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, ("P", "C:\\x\\Alpha.cs"), ("P", "C:\\x\\Beta.cs"));

        var result = DocumentLookup.TryResolveUniqueBareName(solution, "Alpha.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Path, Is.EqualTo("C:\\x\\Alpha.cs"));
        Assert.That(result.Distance, Is.EqualTo(0));
        Assert.That(result.SameFileName, Is.True);
    }

    [Test]
    public void SameFileNameInTwoProjects_ReturnsNull()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, ("P1", "C:\\a\\Util.cs"), ("P2", "C:\\b\\Util.cs"));

        var result = DocumentLookup.TryResolveUniqueBareName(solution, "Util.cs");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RequestedPathWithDirectoryPart_ReturnsNull()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, ("P", "C:\\x\\Gamma.cs"));

        Assert.That(DocumentLookup.TryResolveUniqueBareName(solution, "x\\Gamma.cs"), Is.Null);
        Assert.That(DocumentLookup.TryResolveUniqueBareName(solution, "C:\\x\\Gamma.cs"), Is.Null);
    }

    [Test]
    public void NameInNoProject_ReturnsNull()
    {
        using var workspace = new AdhocWorkspace();
        var solution = Build(workspace, ("P", "C:\\x\\Delta.cs"));

        var result = DocumentLookup.TryResolveUniqueBareName(solution, "Missing.cs");

        Assert.That(result, Is.Null);
    }
}

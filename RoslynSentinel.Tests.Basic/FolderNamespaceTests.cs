using Microsoft.CodeAnalysis;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Pins the folder-derived namespace convention shared by the namespace/path check, MoveFile's kept-namespace fact and the copyAndObsolete checks.
/// In-memory only: the paths sit under the scratch root but nothing is written there.
/// </summary>
[TestFixture]
[Category("FolderNamespace")] // sentinel:auto-category
public class FolderNamespaceTests
{
    private static readonly string ProjectDirectory = Path.Combine(ScratchRoot.Resolve(), "FolderNamespaceTests", "Demo");

    private static Project CreateProject(string defaultNamespace = "Demo")
    {
        var csproj = Path.Combine(ProjectDirectory, "Demo.csproj");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Demo", csproj, []);
        var project = solution.Projects.Single();
        return project.Solution.WithProjectDefaultNamespace(project.Id, defaultNamespace).GetProject(project.Id)!;
    }

    [Test]
    public void Expected_FileInProjectRoot_IsTheDefaultNamespace()
    {
        var expected = FolderNamespace.Expected(CreateProject(), Path.Combine(ProjectDirectory, "Foo.cs"));

        Assert.That(expected, Is.EqualTo("Demo"));
    }

    [Test]
    public void Expected_FileInNestedFolder_AppendsEachFolderAsASegment()
    {
        var expected = FolderNamespace.Expected(CreateProject(), Path.Combine(ProjectDirectory, "Models", "Orders", "Foo.cs"));

        Assert.That(expected, Is.EqualTo("Demo.Models.Orders"));
    }

    [Test]
    public void Expected_UsesTheProjectDefaultNamespaceAsRoot()
    {
        var expected = FolderNamespace.Expected(CreateProject("Acme.Core"), Path.Combine(ProjectDirectory, "Models", "Foo.cs"));

        Assert.That(expected, Is.EqualTo("Acme.Core.Models"));
    }

    [Test]
    public void Expected_ProjectWithoutAFilePath_IsNull()
    {
        var project = new AdhocWorkspace().CurrentSolution.AddProject("NoPath", "NoPath", LanguageNames.CSharp);

        Assert.That(FolderNamespace.Expected(project, Path.Combine(ProjectDirectory, "Foo.cs")), Is.Null);
    }

    [Test]
    public void FromFolder_FileDirectlyInTheProjectDirectory_IsJustTheRoot()
    {
        Assert.That(FolderNamespace.FromFolder("Root.Ns", ProjectDirectory, Path.Combine(ProjectDirectory, "Foo.cs")), Is.EqualTo("Root.Ns"));
    }

    [Test]
    public void FromFolder_NestedFolders_JoinsWithDots()
    {
        Assert.That(FolderNamespace.FromFolder("Root.Ns", ProjectDirectory, Path.Combine(ProjectDirectory, "A", "B", "Foo.cs")), Is.EqualTo("Root.Ns.A.B"));
    }
}

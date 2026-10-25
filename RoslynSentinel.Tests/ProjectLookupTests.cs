using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

public class ProjectLookupTests
{
    [Test]
    public void FindProject_NoMatch_ReturnsNull()
    {
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Alpha", "Alpha", LanguageNames.CSharp, filePath: @"C:\repo\Alpha.csproj"));

        Assert.That(ProjectLookup.FindProject(solution, "Missing"), Is.Null);
        Assert.That(ProjectLookup.FindProject(solution, null), Is.Null);
    }

    [Test]
    public void FindProject_SingleMatch_ReturnsIt()
    {
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Alpha", "Alpha", LanguageNames.CSharp, filePath: @"C:\repo\Alpha.csproj"));

        var project = ProjectLookup.FindProject(solution, "Alpha");

        Assert.That(project, Is.Not.Null);
        Assert.That(project!.Name, Is.EqualTo("Alpha"));
    }

    [Test]
    public void FindProject_SameNameDifferentFilePaths_ThrowsAmbiguousNamingBothPaths()
    {
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Dup", "Dup", LanguageNames.CSharp, filePath: @"C:\repo\A\Dup.csproj"))
            .AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Dup", "Dup", LanguageNames.CSharp, filePath: @"C:\repo\B\Dup.csproj"));

        var ex = Assert.Throws<ToolAmbiguousMatchException>(() => ProjectLookup.FindProject(solution, "Dup"));

        Assert.That(ex!.Message, Does.Contain(@"C:\repo\A\Dup.csproj").And.Contain(@"C:\repo\B\Dup.csproj"));
    }

    [Test]
    public void FindProject_IgnoreCase_MatchesDifferentCasing()
    {
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Alpha", "Alpha", LanguageNames.CSharp, filePath: @"C:\repo\Alpha.csproj"));

        Assert.That(ProjectLookup.FindProject(solution, "alpha", ignoreCase: true), Is.Not.Null);
        Assert.That(ProjectLookup.FindProject(solution, "alpha"), Is.Null);
    }
}

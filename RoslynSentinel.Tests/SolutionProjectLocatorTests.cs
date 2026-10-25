using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Tests;

[TestFixture]
public class SolutionProjectLocatorTests
{
    private static Solution BuildSolution(params (string FilePath, string Name)[] projects)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        foreach (var (filePath, name) in projects)
        {
            var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, name, LanguageNames.CSharp, filePath: filePath);
            solution = solution.AddProject(info);
        }

        return solution;
    }

    [Test]
    public void FilePathUnderSiblingWithSharedPrefix_ResolvesToItsOwnProject_NotPrefixMatch()
    {
        var solution = BuildSolution((@"C:\repo\Foo\Foo.csproj", "Foo"), (@"C:\repo\FooBar\FooBar.csproj", "FooBar"));

        var project = SolutionProjectLocator.FindContainingProject(solution, @"C:\repo\FooBar\x.cs");

        Assert.That(project, Is.Not.Null);
        Assert.That(project!.Name, Is.EqualTo("FooBar"));

        var fooOnly = BuildSolution((@"C:\repo\Foo\Foo.csproj", "Foo"));
        Assert.That(SolutionProjectLocator.FindContainingProject(fooOnly, @"C:\repo\FooBar\x.cs"), Is.Null,
            "a directory matches only on a path-separator boundary, so C:\\repo\\Foo must not contain C:\\repo\\FooBar\\x.cs");
    }

    [Test]
    public void FilePathInSubdirectory_ResolvesToContainingProject()
    {
        var solution = BuildSolution((@"C:\repo\Foo\Foo.csproj", "Foo"), (@"C:\repo\FooBar\FooBar.csproj", "FooBar"));

        var project = SolutionProjectLocator.FindContainingProject(solution, @"C:\repo\Foo\sub\y.cs");

        Assert.That(project, Is.Not.Null);
        Assert.That(project!.Name, Is.EqualTo("Foo"));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// After a type is renamed or moved, an agent still asks for the file named after the type. The not-found text, shared
/// by ReadFile (WorkspaceFileEditImpl), GetMethodSource / GetFileOutline (WorkspaceReadNavigationImpl) and the document
/// lookup, then names the file that declares a top-level type of that name before anything else.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WorkspaceTools")] // sentinel:auto-category
public class FileNotFoundDeclaringTypeTests
{
    private const string Missing = @"C:\repo\Core\FixtureHelpers\StandardOrderPricingCalculator.cs";
    private const string Declaring = @"C:\repo\Core\FixtureHelpers\OrderPricingCalculator.cs";
    private const string DeclaringSource = "namespace N { public class StandardOrderPricingCalculator { } }";

    private static Solution SolutionWith(params (string Path, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        foreach (var (path, source) in files)
        {
            project = project.AddDocument(System.IO.Path.GetFileName(path), SourceText.From(source), filePath: path).Project;
        }

        return project.Solution;
    }

    [Test]
    public void ReadFileAndNavigationReads_NameTheDeclaringFileFirst()
    {
        var solution = SolutionWith((Declaring, DeclaringSource));
        var expected = $"Type 'StandardOrderPricingCalculator' is declared in {Declaring}; read that file. ";

        var read = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, Missing);
        var navigation = WorkspaceReadNavigationImpl.BuildFileNotFoundError(solution, Missing);

        Assert.That(read.ErrorCode, Is.EqualTo("FileNotFound"));
        Assert.That(read.Message, Does.StartWith(expected));
        Assert.That(read.Message, Does.Contain("was not found in the loaded solution"), "the usual explanation still follows");
        Assert.That(navigation.ErrorCode, Is.EqualTo("FileNotFound"));
        Assert.That(navigation.Message, Is.EqualTo(read.Message), "both read paths share one text");
    }

    [Test]
    public void DocumentLookupDescribe_NamesTheDeclaringFileFirst()
    {
        var solution = SolutionWith((Declaring, DeclaringSource));

        var lookup = DocumentLookup.TryGetDocument(solution, Missing);

        Assert.That(lookup.Status, Is.EqualTo(DocumentLookupStatus.NotFound));
        Assert.That(lookup.Describe(), Does.StartWith($"Type 'StandardOrderPricingCalculator' is declared in {Declaring}; read that file. "));
    }

    [Test]
    public void SeveralDeclaringFiles_AreListed_UpToThree()
    {
        var files = Enumerable.Range(1, 4)
            .Select(i => ($@"C:\repo\Core\Part{i}.cs", "namespace N { public partial class StandardOrderPricingCalculator { } }"))
            .ToArray();
        var solution = SolutionWith(files);

        var message = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, Missing).Message;

        Assert.That(message, Does.StartWith("Type 'StandardOrderPricingCalculator' is declared in 3 files: C:\\repo\\Core\\Part1.cs; C:\\repo\\Core\\Part2.cs; C:\\repo\\Core\\Part3.cs; read the one you meant. "));
        Assert.That(message, Does.Not.Contain("Part4.cs"));
    }

    [Test]
    public void NestedTypeOtherNameOrNonCsPath_GetsTheUnchangedText()
    {
        var solution = SolutionWith((Declaring, "namespace N { public class Holder { public class StandardOrderPricingCalculator { } } public class Other { } }"));

        var nested = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, Missing).Message;
        var otherName = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, @"C:\repo\Core\Unrelated.cs").Message;
        var notCs = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, @"C:\repo\Core\Other.txt").Message;

        Assert.That(nested, Does.StartWith("File '"), "a nested type does not name a file");
        Assert.That(otherName, Does.StartWith("File '"));
        Assert.That(notCs, Does.StartWith("File '"), "only a .cs request is matched to a type");
        Assert.That(DocumentLookup.FindDeclaringTypeFiles(solution, @"C:\repo\Core\Other.cs").Select(d => d.TypeName), Is.EqualTo(new[] { "Other" }));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for the AppliedChangeSummary factory: fails when a production .cs file other than RoslynSentinel.Common/AppliedChangeSummary.cs
// writes "new AppliedChangeSummary(" directly. Only the factory-free constructor is flagged; AppliedChangeSummary.From(...) fills LineChanges
// and Findings. Only object-creation nodes are counted, so comments and string literals never match. Baseline is zero allowed files.
[TestFixture]
[Category("AppliedChangeSummaryConstructionRatchet")]
public class AppliedChangeSummaryConstructionRatchetTests
{
    private const string FactoryFile = "RoslynSentinel.Common/AppliedChangeSummary.cs";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsProductionProject(string directoryName) =>
        directoryName.StartsWith("RoslynSentinel.", StringComparison.Ordinal)
        && !directoryName.Contains("Tests", StringComparison.Ordinal)
        && !directoryName.Contains("Samples", StringComparison.Ordinal)
        && !directoryName.Contains("DummyConsole", StringComparison.Ordinal)
        && !directoryName.Contains("TempCheck", StringComparison.Ordinal);

    private static bool IsDirectConstruction(ObjectCreationExpressionSyntax creation) => creation.Type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "AppliedChangeSummary",
        QualifiedNameSyntax { Right.Identifier.ValueText: "AppliedChangeSummary" } => true,
        _ => false,
    };

    [Test]
    public void AppliedChangeSummary_IsBuiltOnlyByTheFactory()
    {
        var repo = RepoRoot();
        var filesScanned = 0;
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            if (!IsProductionProject(Path.GetFileName(projectDir)))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "Worktree"))
                {
                    continue;
                }

                filesScanned++;
                if (relativePath == FactoryFile)
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
                if (root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(IsDirectConstruction))
                {
                    offenders.Add(relativePath);
                }
            }
        }

        Assert.That(filesScanned, Is.GreaterThan(0), "The scan found no production .cs files; the repo root lookup is broken.");
        Assert.That(
            offenders,
            Is.Empty,
            "Build AppliedChangeSummary with AppliedChangeSummary.From(...) so LineChanges and Findings are filled."
                + Environment.NewLine + string.Join(Environment.NewLine, offenders.Select(path => "  " + path)));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Scans the engine sources with Roslyn: every explicit DocumentEditResult creation must assign UpdatedText, so a
/// new result cannot silently leave the text at string.Empty after the nullable guards were removed.
/// </summary>
[TestFixture]
[Category("DocumentEditResultContract")]
public class DocumentEditResultInitializerScanTests
{
    [Test]
    public void DocumentEditResult_InitializersAlwaysAssignUpdatedText()
    {
        static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName;
        }

        static bool IsDocumentEditResult(TypeSyntax type) => type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "DocumentEditResult",
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == "DocumentEditResult",
            _ => false,
        };

        // A creation sets the text when its initializer assigns UpdatedText, or, with no initializer, when it passes
        // constructor arguments (those constructors set the text). Anything else leaves UpdatedText at string.Empty.
        static bool SetsUpdatedText(ObjectCreationExpressionSyntax creation)
        {
            if (creation.Initializer is not null)
            {
                return creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                    .Any(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "UpdatedText" });
            }

            return creation.ArgumentList is { Arguments.Count: > 0 };
        }

        var repo = FindRepoRoot() ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
        var engineDirs = Directory.GetDirectories(repo)
            .Where(dir => Path.GetFileName(dir).StartsWith("RoslynSentinel.Engines.", StringComparison.Ordinal))
            .ToList();
        Assert.That(engineDirs, Is.Not.Empty, "No RoslynSentinel.Engines.* directories found; the repo root lookup is broken.");

        var creationsSeen = 0;
        var violations = new List<string>();
        foreach (var dir in engineDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "Worktree"))
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
                foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    if (!IsDocumentEditResult(creation.Type))
                    {
                        continue;
                    }

                    creationsSeen++;
                    if (!SetsUpdatedText(creation))
                    {
                        var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        violations.Add($"  {relativePath}:{line}");
                    }
                }
            }
        }

        Assert.That(creationsSeen, Is.GreaterThan(0), "The scan found no explicit DocumentEditResult creations; the scan is broken.");
        Assert.That(violations, Is.Empty,
            "Every DocumentEditResult must set UpdatedText (the unchanged text for AlreadyInTargetState/NoCandidatesFound, "
            + "string.Empty only for a failure outcome):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}

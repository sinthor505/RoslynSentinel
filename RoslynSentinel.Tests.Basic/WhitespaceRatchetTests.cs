using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Architecture ratchets for the whitespace initiative. Production sources are parsed with Roslyn and
/// only invocation nodes are counted, so comments, doc crefs and string literals never match.
/// The allowlist may only shrink and the deleted helper names must never come back; new formatting goes through
/// RoslynFormattingHelper.FormatAnnotatedSpansAsync or FormatWholeDocumentAsync.
/// </summary>
[TestFixture]
[Category("WhitespaceRatchet")]
public class WhitespaceRatchetTests
{
    // Test 1 allowlist. Keys are "<relative file>::<enclosing member>". Only shrinks.
    private static readonly string[] RawFormatterAllowlist =
    {
        // The helper's own internals.
        "RoslynSentinel.Common/RoslynFormattingHelper.cs::FormatAnnotatedSpansAsync",
        "RoslynSentinel.Common/RoslynFormattingHelper.cs::FormatWholeDocumentAsync",
        // Semantic hash only; the normalized text is compared in memory and never written to disk.
        "RoslynSentinel.Common/ContentHasher.cs::ComputeHash",
        // Semantic comparison of proposed content only; nothing normalized is persisted.
        "RoslynSentinel.Common/PersistentWorkspaceManager.cs::ApplyProposedChangesAsync",
        // Parked private remove helpers (formatted removal path), kept until the C6 retirement.
        "RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::RemoveNodeFormattedAsync",
        "RoslynSentinel.Engines.Basic/SemanticRefactoringEngine.cs::RemoveNodeFormattedAsync",
        "RoslynSentinel.Engines.Basic/StructuralRefinementEngine.cs::RemoveNodeFormattedAsync",
        // Legitimate format-document tools (whole-document format is their job).
        "RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::FormatDocumentAsync",
        "RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::FormatDocumentPreviewAsync",
        // Slice C9 (owner-approved): renders caller-supplied attribute text into a TextEdit string, not a document node,
        // so there is no Document for the annotation formatter; every trivia is checked (comments rejected) and stripped
        // first, so NormalizeWhitespace discards no user formatting. Replaced the NormalizeNewChildWhitespace site there.
        "RoslynSentinel.Engines.Basic/AttributeTextEditBuilder.cs::NormalizeAttributeText",
    };

    // Test 2 names: the NormalizeWhitespace-family helpers deleted in slice D1 (plus a planned variant that was never
    // built). No production file, RoslynFormattingHelper.cs included, may declare or invoke one again. Never shrink it.
    private static readonly HashSet<string> DeletedWhitespaceHelpers = new(StringComparer.Ordinal)
    {
        "NormalizeWholeSubtreeWhitespace",
        "NormalizeNewChildWhitespace",
        "NormalizeNewRootWhitespace",
        "NormalizeNewRootWhitespaceKeepingComments",
        "NormalizeNewRootWhitespaceKeepingUsingHeaders",
        "StripFormattingTriviaForNormalize",
        "StripUsingKeepingLeadingTrivia",
    };

    [Test]
    public void RawFormatterCalls_AreOnlyInTheAllowlist()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, root) in sources)
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (IsRawFormatterCall(invocation))
                {
                    actual.Add(relativePath + "::" + EnclosingMemberName(invocation));
                }
            }
        }

        var allowed = new SortedSet<string>(RawFormatterAllowlist, StringComparer.Ordinal);
        var unexpected = actual.Except(allowed).ToList();
        var stale = allowed.Except(actual).ToList();

        var problems = new List<string>();
        if (unexpected.Count > 0)
        {
            problems.Add("Unexpected raw Formatter.FormatAsync / NormalizeWhitespace call sites. Route each one through "
                + "RoslynFormattingHelper.FormatAnnotatedSpansAsync / FormatWholeDocumentAsync:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Select(key => "  " + key)));
        }

        if (stale.Count > 0)
        {
            problems.Add("Stale allowlist entries (no longer present in the tree). Remove them so the list only shrinks:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale.Select(key => "  " + key)));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }

    [Test]
    public void DeletedWhitespaceHelpers_NeverReappear()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, root) in sources)
        {
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvokedMemberName(invocation);
                if (name is not null && DeletedWhitespaceHelpers.Contains(name))
                {
                    found.Add($"  {relativePath}::{EnclosingMemberName(invocation)} calls {name}");
                }
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (DeletedWhitespaceHelpers.Contains(method.Identifier.ValueText))
                {
                    found.Add($"  {relativePath} declares {method.Identifier.ValueText}");
                }
            }
        }

        Assert.That(found, Is.Empty,
            "A deleted NormalizeWhitespace-family helper is back. Route new formatting through "
            + "RoslynFormattingHelper.FormatAnnotatedSpansAsync / FormatNewRootKeepingTriviaAsync / FormatWholeDocumentAsync instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    // Parses every non-test production .cs file under RoslynSentinel.Common, Engines.* and Tools.*,
    // skipping bin, obj and Worktree folders. Returned in relative-path order for stable messages.
    private static List<(string RelativePath, SyntaxNode Root)> ParseProductionSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, SyntaxNode Root)>();
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            var name = Path.GetFileName(projectDir);
            if (name.Contains("Tests", StringComparison.Ordinal) || !IsProductionProject(name))
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

                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
                result.Add((relativePath, root));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return result;
    }

    private static bool IsProductionProject(string projectName) =>
        projectName == "RoslynSentinel.Common"
        || projectName.StartsWith("RoslynSentinel.Engines.", StringComparison.Ordinal)
        || projectName.StartsWith("RoslynSentinel.Tools.", StringComparison.Ordinal);

    private static bool IsRawFormatterCall(InvocationExpressionSyntax invocation)
    {
        var name = InvokedMemberName(invocation);
        if (name == "NormalizeWhitespace")
        {
            return true;
        }

        return name == "FormatAsync"
            && invocation.Expression is MemberAccessExpressionSyntax { Expression: var receiver }
            && IsFormatterReceiver(receiver);
    }

    private static bool IsFormatterReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "Formatter",
        MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == "Formatter",
        _ => false,
    };

    private static string? InvokedMemberName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    private static string EnclosingMemberName(SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            null => "<no member>",
            _ => "<" + member.Kind() + ">",
        };
    }
}

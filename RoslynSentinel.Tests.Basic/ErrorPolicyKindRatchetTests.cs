using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for error-policy kinds (plan_error_codes_follow_cause.md): every string-literal missing-target kind that a Common,
// Engines.Basic or Tools.Basic throw site passes to ToolNotFoundException or MemberRefactoringNotFound must either map through
// ErrorPolicyClassifier.MapKind or be listed as deliberately unmapped below. Non-literal arguments (variables, ?? expressions) are skipped.
// To add a kind, map it in ErrorPolicyClassifier SymbolKinds/FileKinds, or add it to MissingTargetKinds.UnmappedKinds (Common) with a reason.
[TestFixture]
[Category("ErrorPolicyKindRatchet")]
public class ErrorPolicyKindRatchetTests
{
    private const int MinimumLiteralSites = 20;
    private const string FactoryType = "MemberRefactoringNotFound";
    private const string FactoryFile = "RoslynSentinel.Engines.Basic/MemberRefactoringNotFound.cs";

    private static readonly string[] ScannedProjects = { "RoslynSentinel.Common", "RoslynSentinel.Engines.Basic", "RoslynSentinel.Tools.Basic" };

    // Statement-like and structural targets with no policy key. Compared case-insensitively.
    private static readonly IReadOnlySet<string> DeliberatelyUnmappedKinds = MissingTargetKinds.UnmappedKinds;

    // Files migrated to MissingTargetKinds constants are removed from this list; a listed file without literal kinds fails the stale check. The list only shrinks.
    private static readonly HashSet<string> NotYetMigratedFiles = new(StringComparer.Ordinal)
    {
        "RoslynSentinel.Engines.Basic/MemberRefactoringNotFound.cs",
        "RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs",
        "RoslynSentinel.Engines.Basic/StructuralRefinementEngine.cs",
        "RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs",
        "RoslynSentinel.Engines.Basic/SyntaxUpgradeEngine.cs",
        "RoslynSentinel.Engines.Basic/ThreadSafetyEngine.cs",
        "RoslynSentinel.Common/DocumentLookup.cs",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsScannedProject(string directoryName) => ScannedProjects.Contains(directoryName, StringComparer.Ordinal);

    // Parses every .cs file of the scanned projects, skipping bin, obj and Worktree folders. Returned in relative-path order for stable messages.
    // TODO share the production-source scan helpers with BareFileWriteRatchetTests
    private static List<(string RelativePath, SyntaxNode Root)> ParseScannedSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, SyntaxNode Root)>();
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            if (!IsScannedProject(Path.GetFileName(projectDir)))
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

    // The argument for a parameter: the named one when the call names it, otherwise the positional one at the given index.
    private static ExpressionSyntax? ArgumentAt(BaseArgumentListSyntax? list, int position, string name)
    {
        if (list is null)
        {
            return null;
        }

        var named = list.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == name);
        if (named is not null)
        {
            return named.Expression;
        }

        return list.Arguments.Where(argument => argument.NameColon is null).ElementAtOrDefault(position)?.Expression;
    }

    private static string? LiteralText(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    private static bool IsToolNotFoundType(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "ToolNotFoundException",
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == "ToolNotFoundException",
        _ => false,
    };

    // Matches MemberRefactoringNotFound.<method>(...) anywhere, and the unqualified <method>(...) inside the factory file itself.
    private static bool IsFactoryCall(InvocationExpressionSyntax invocation, string method, bool inFactoryFile)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax access)
        {
            return access.Expression is IdentifierNameSyntax receiver
                && receiver.Identifier.ValueText == FactoryType
                && access.Name.Identifier.ValueText == method;
        }

        return inFactoryFile && invocation.Expression is IdentifierNameSyntax call && call.Identifier.ValueText == method;
    }

    private static void AddSite(List<(string Kind, string Site)> sites, string relativePath, SyntaxNode node, string? kind)
    {
        if (kind is null)
        {
            return;
        }

        var line = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        sites.Add((kind, relativePath + ":" + line));
    }

    private static List<(string Kind, string Site)> CollectLiteralKindSites(IEnumerable<(string RelativePath, SyntaxNode Root)> sources)
    {
        var sites = new List<(string Kind, string Site)>();
        foreach (var (relativePath, root) in sources)
        {
            var inFactoryFile = relativePath == FactoryFile;
            foreach (var node in root.DescendantNodes())
            {
                if (node is ObjectCreationExpressionSyntax creation && IsToolNotFoundType(creation.Type))
                {
                    AddSite(sites, relativePath, creation, LiteralText(ArgumentAt(creation.ArgumentList, 1, "missingTargetKind")));
                }
                else if (node is ImplicitObjectCreationExpressionSyntax implicitCreation && inFactoryFile)
                {
                    AddSite(sites, relativePath, implicitCreation, LiteralText(ArgumentAt(implicitCreation.ArgumentList, 1, "missingTargetKind")));
                }
                else if (node is InvocationExpressionSyntax invocation)
                {
                    if (IsFactoryCall(invocation, "Declaration", inFactoryFile))
                    {
                        // The sixth parameter (missingTargetKind) wins; otherwise the kindLabel (first) argument is the kind.
                        var kind = LiteralText(ArgumentAt(invocation.ArgumentList, 5, "missingTargetKind"))
                            ?? LiteralText(ArgumentAt(invocation.ArgumentList, 0, "kindLabel"));
                        AddSite(sites, relativePath, invocation, kind);
                    }
                    else if (IsFactoryCall(invocation, "Unresolved", inFactoryFile))
                    {
                        AddSite(sites, relativePath, invocation, LiteralText(ArgumentAt(invocation.ArgumentList, 0, "missingTargetKind")));
                    }
                }
            }
        }

        return sites;
    }

    [Test]
    public void LiteralMissingTargetKinds_AreMappedOrDeliberatelyUnmapped()
    {
        var sources = ParseScannedSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no .cs files in the scanned projects; the repo root lookup is broken.");

        var sites = CollectLiteralKindSites(sources);
        Assert.That(
            sites.Count,
            Is.GreaterThanOrEqualTo(MinimumLiteralSites),
            $"The scan found {sites.Count} literal missing-target kind sites; expected at least {MinimumLiteralSites}. The scan is probably not matching the throw sites.");

        var offenders = sites
            .Where(site => !DeliberatelyUnmappedKinds.Contains(site.Kind) && ErrorPolicyClassifier.MapKind(ToolErrorCode.NotFound, site.Kind) is null)
            .OrderBy(site => site.Site, StringComparer.Ordinal)
            .Select(site => $"  '{site.Kind}' at {site.Site}")
            .ToList();

        Assert.That(
            offenders,
            Is.Empty,
            "Literal missing-target kinds with no policy mapping. A new kind must be added to ErrorPolicyClassifier SymbolKinds/FileKinds, "
                + "or to MissingTargetKinds.UnmappedKinds (Common):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Test]
    public void LiteralMissingTargetKinds_AreOnlyInNotYetMigratedFiles()
    {
        var sites = CollectLiteralKindSites(ParseScannedSources());
        var filesWithLiterals = sites
            .Select(site => site.Site.Substring(0, site.Site.LastIndexOf(':')))
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = filesWithLiterals
            .Where(file => !NotYetMigratedFiles.Contains(file))
            .Order(StringComparer.Ordinal)
            .Select(file => $"  {file}: literal missing-target kind; use MissingTargetKinds.<Name> instead of the string literal");
        var stale = NotYetMigratedFiles
            .Where(file => !filesWithLiterals.Contains(file))
            .Order(StringComparer.Ordinal)
            .Select(file => $"  {file}: no literal missing-target kind; remove from NotYetMigratedFiles");

        var problems = unlisted.Concat(stale).ToList();
        Assert.That(
            problems,
            Is.Empty,
            "NotYetMigratedFiles is out of step with the literal missing-target kind sites:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void DeliberatelyUnmappedKinds_MapToNull()
    {
        var mapped = DeliberatelyUnmappedKinds
            .Where(kind => ErrorPolicyClassifier.MapKind(ToolErrorCode.NotFound, kind) is not null)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.That(
            mapped,
            Is.Empty,
            "These kinds are listed as deliberately unmapped but now map to a policy key; remove them from MissingTargetKinds.UnmappedKinds (Common): "
                + string.Join(", ", mapped));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for hand-built line endings (plan_eol_policy_and_write_chokepoint.md Step 8z). Fails when a production .cs file in
// RoslynSentinel.Common or RoslynSentinel.Engines.* builds a line ending by hand: SyntaxFactory.LineFeed,
// SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.EndOfLine(...) or NormalizeWhitespace() with no arguments.
// Comments and string literals never match (the scan walks syntax, not text). Counts may only shrink.
[TestFixture]
[Category("NewlineLiteralRatchet")]
public class NewlineLiteralRatchetTests
{
    // Exempt from the scan: RoslynFormattingHelper.cs is where the resolved eol is applied, EolUtilities.cs defines detection.
    private static readonly HashSet<string> ExemptFiles = new(StringComparer.Ordinal)
    {
        "RoslynSentinel.Common/RoslynFormattingHelper.cs",
        "RoslynSentinel.Common/EolUtilities.cs",
    };

    // These literals were measured on 2026-10-26 (Step 8z of docs/current/plans/plan_eol_policy_and_write_chokepoint.md).
    // The write chokepoint converts every written file to the resolved EOL, so these only affect text returned in previews.
    // New code takes the eol parameter from IWorkspaceMutator.ResolveEol instead of building a line ending by hand.
    // Keyed "<relative file>::<enclosing member>" to the exact count; counts may only shrink.
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["RoslynSentinel.Common/ContentHasher.cs::ComputeHash"] = 1,
        ["RoslynSentinel.Common/ContextHelper.cs::WithAddedByComment"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::AlignPartialDeclaration"] = 2,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::ApplyExtractConstant"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::IntroduceParameterObjectAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::ReplaceConstructorWithFactoryAsync"] = 2,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::WithBlankLineSeparator"] = 1,
        ["RoslynSentinel.Engines.Advanced/AnnotatedInsertionHelper.cs::<FieldDeclaration>"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::ConvertToAsyncBridgeAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::GenerateAsyncOverloadAsync"] = 4,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::OptimizeIndependentAwaitsAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::OverloadLeadingTrivia"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::ReplaceOrAddAttributeOnOwnLine"] = 1,
        ["RoslynSentinel.Engines.Advanced/CodeStyleEngine.cs::FixDangerousLockAsync"] = 2,
        ["RoslynSentinel.Engines.Advanced/CodeStyleEngine.cs::UseTimeProviderAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/CommentingEngine.cs::AddAttribute"] = 1,
        ["RoslynSentinel.Engines.Advanced/DocumentationEngine.cs::GenerateXmlDocumentationStubsAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/LogicOptimizationEngine.cs::AddGuardClausesAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/ModernizationEngine.cs::ConvertToSourceGeneratedLoggingAsync"] = 2,
        ["RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::AddUsingDirectiveAsync"] = 2,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::EncapsulateFieldSafeAsync"] = 3,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::ExtractConstantSafeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::ExtractMethodSafeAsync"] = 4,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::KeepTrailingComment"] = 1,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::PreviewAddMissingUsingsAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs::ConvertToBackgroundServiceAsync"] = 3,
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

    // Scope: RoslynSentinel.Common and every RoslynSentinel.Engines.* production project (tests excluded).
    private static bool IsInScopeProject(string directoryName) =>
        directoryName == "RoslynSentinel.Common"
        || (directoryName.StartsWith("RoslynSentinel.Engines.", StringComparison.Ordinal)
            && !directoryName.Contains("Tests", StringComparison.Ordinal));

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

    // True for a node that builds a line ending by hand: SyntaxFactory.LineFeed, SyntaxFactory.CarriageReturnLineFeed,
    // SyntaxFactory.EndOfLine(...) or a NormalizeWhitespace() call with an empty argument list.
    private static bool IsHandBuiltLineEnding(SyntaxNode node)
    {
        switch (node)
        {
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "LineFeed" or "CarriageReturnLineFeed" } access:
                return access.Expression.ToString() == "SyntaxFactory";
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "EndOfLine" } endOfLine }:
                return endOfLine.Expression.ToString() == "SyntaxFactory";
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "NormalizeWhitespace" } } call:
                return call.ArgumentList.Arguments.Count == 0;
            default:
                return false;
        }
    }

    // Counts hand-built line endings per "<relative file>::<enclosing member>". Skips bin, obj and Worktree folders and the
    // exempt files. Attribution walks the syntax tree, so comments and string literals never match.
    private static (int FilesScanned, SortedDictionary<string, int> Actual) ScanHandBuiltLineEndings()
    {
        var repo = RepoRoot();
        var filesScanned = 0;
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var projectDir in Directory.GetDirectories(repo))
        {
            if (!IsInScopeProject(Path.GetFileName(projectDir)))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repo, file).Replace('\\', '/');
                if (relativePath.Split('/').Any(segment => segment is "bin" or "obj" or "Worktree") || ExemptFiles.Contains(relativePath))
                {
                    continue;
                }

                filesScanned++;
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
                foreach (var node in root.DescendantNodes())
                {
                    if (IsHandBuiltLineEnding(node))
                    {
                        var key = relativePath + "::" + EnclosingMemberName(node);
                        actual[key] = actual.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        return (filesScanned, actual);
    }

    [Test]
    public void HandBuiltLineEndings_MatchTheBaseline()
    {
        var (filesScanned, actual) = ScanHandBuiltLineEndings();
        Assert.That(filesScanned, Is.GreaterThan(0), "The scan found no production .cs files; the repo root lookup is broken.");

        var unexpected = actual.Keys.Where(key => !Baseline.ContainsKey(key)).ToList();
        var risen = Baseline
            .Where(entry => actual.TryGetValue(entry.Key, out var count) && count > entry.Value)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => "  " + entry.Key + ": baseline says " + entry.Value + ", tree has " + actual[entry.Key])
            .ToList();
        var lower = Baseline
            .Where(entry => actual.TryGetValue(entry.Key, out var count) && count < entry.Value)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => "  " + entry.Key + ": baseline says " + entry.Value + ", tree has " + actual[entry.Key])
            .ToList();
        var stale = Baseline.Keys.Where(key => !actual.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();

        var problems = new List<string>();
        if (unexpected.Count > 0)
        {
            problems.Add("New hand-built line ending call sites. Take the eol parameter from IWorkspaceMutator.ResolveEol instead of building "
                + "SyntaxFactory.LineFeed / CarriageReturnLineFeed / EndOfLine / NormalizeWhitespace() by hand:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Select(key => "  " + key + " (" + actual[key] + " call(s))")));
        }

        if (risen.Count > 0)
        {
            problems.Add("Hand-built line ending count rose above the baseline. Route the new site through the resolved eol instead:"
                + Environment.NewLine + string.Join(Environment.NewLine, risen));
        }

        if (lower.Count > 0)
        {
            problems.Add("Hand-built line ending count fell below the baseline. Lower the Baseline entry to match so the baseline tightens:"
                + Environment.NewLine + string.Join(Environment.NewLine, lower));
        }

        if (stale.Count > 0)
        {
            problems.Add("Stale Baseline entries (no longer present in the tree). Remove them so the baseline only shrinks:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale.Select(key => "  " + key)));
        }

        if (problems.Count > 0)
        {
            problems.Add("Current counts (paste into Baseline when the change is intended):" + Environment.NewLine
                + string.Join(Environment.NewLine, actual.Select(entry => "        [\"" + entry.Key + "\"] = " + entry.Value + ",")));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }
}

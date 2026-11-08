using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for case-insensitive path comparison (docs/current/issues/issue_path_comparison_case_sensitivity.md). FilePathWrapper's ==/Equals are
// OrdinalIgnoreCase, but Document.FilePath, FilePathWrapper.Absolute and string-converted wrappers are plain strings, so ==, string.Equals(a, b) and
// hand-rolled Documents scans silently become case-sensitive on a path. Kinds counted in production code (syntax nodes only, so comments and string
// literals never match):
//   a - ==/!= with a ".FilePath"/".Absolute" operand (other operand not null, "" or string.Empty)
//   b - string.Equals(x, y) / x.Equals(y) with no comparer where either side is a ".FilePath"/".Absolute" access
//   c - ".Documents" scans (Where/First/Any...) whose lambda reads ".FilePath", outside RoslynSentinel.Common/DocumentLookup.cs
// Counts may only shrink: a higher count or a new key fails; a lower count fails too so the baseline cannot go stale.
[TestFixture]
[Category("PathComparisonRatchet")]
public class PathComparisonRatchetTests
{
    // True for "x.FilePath" / "x.Absolute" (also "x?.FilePath" and parenthesized forms): a plain string that has lost FilePathWrapper's case-insensitivity.
    private static bool IsPathAccess(ExpressionSyntax? expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsPathAccess(parenthesized.Expression),
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FilePath" or "Absolute" } => true,
        ConditionalAccessExpressionSyntax { WhenNotNull: MemberBindingExpressionSyntax { Name.Identifier.ValueText: "FilePath" or "Absolute" } } => true,
        _ => false,
    };

    private static bool IsStringType(ExpressionSyntax expression) => expression.ToString() is "string" or "String" or "System.String";

    // null, "" and string.Empty are case-neutral, so comparing a path with them is not a case-sensitivity trap.
    private static bool IsNullOrEmptyLiteral(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsNullOrEmptyLiteral(parenthesized.Expression),
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression)
            || (literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.Length == 0),
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Empty" } access => IsStringType(access.Expression),
        _ => false,
    };

    // Kind a: ==/!= between a path string and anything that is not null / "" / string.Empty.
    private static bool IsCaseSensitivePathComparison(BinaryExpressionSyntax binary) =>
        binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
        && ((IsPathAccess(binary.Left) && !IsNullOrEmptyLiteral(binary.Right)) || (IsPathAccess(binary.Right) && !IsNullOrEmptyLiteral(binary.Left)));

    // Kind b: comparer-less string.Equals(x, y) or x.Equals(y) where either side is a path string. An overload that
    // passes a StringComparison/comparer has a different argument count and is never matched.
    private static bool IsCaseSensitivePathEquals(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Equals" } access)
        {
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        if (IsStringType(access.Expression))
        {
            return arguments.Count == 2 && (IsPathAccess(arguments[0].Expression) || IsPathAccess(arguments[1].Expression));
        }

        return arguments.Count == 1 && (IsPathAccess(access.Expression) || IsPathAccess(arguments[0].Expression));
    }

    // LINQ/list methods that scan a document collection with a predicate.
    private static readonly HashSet<string> ScanMethodNames = new(StringComparer.Ordinal)
    {
        "Where", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
        "Any", "All", "Count", "SkipWhile", "TakeWhile", "Exists", "Find", "FindAll",
    };

    // True when the subtree reads a member with this name ("x.Name" or the "?.Name" binding form).
    private static bool MentionsMember(SyntaxNode node, string memberName) => node.DescendantNodesAndSelf().Any(descendant =>
        descendant is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == memberName
        || descendant is MemberBindingExpressionSyntax binding && binding.Name.Identifier.ValueText == memberName);

    // Kind c: a scan method (Where/First/Any...) whose predicate reads ".FilePath" and whose receiver chain reaches a ".Documents"
    // member (directly, or inside e.g. Projects.SelectMany(p => p.Documents)). Each scan call in a chain counts once.
    private static bool IsDocumentsPathScan(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access
            || !ScanMethodNames.Contains(access.Name.Identifier.ValueText)
            || !MentionsMember(invocation.ArgumentList, "FilePath"))
        {
            return false;
        }

        var receiver = access.Expression;
        while (receiver is not null)
        {
            switch (receiver)
            {
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Documents" }:
                case MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Documents" }:
                    return true;
                case MemberAccessExpressionSyntax memberAccess:
                    receiver = memberAccess.Expression;
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    receiver = parenthesized.Expression;
                    break;
                case InvocationExpressionSyntax inner:
                    if (MentionsMember(inner.ArgumentList, "Documents"))
                    {
                        return true;
                    }

                    receiver = inner.Expression;
                    break;
                default:
                    return false;
            }
        }

        return false;
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

    private static bool IsProductionProject(string directoryName) =>
    directoryName.StartsWith("RoslynSentinel.", StringComparison.Ordinal)
    && !directoryName.Contains("Tests", StringComparison.Ordinal)
    && !directoryName.Contains("Samples", StringComparison.Ordinal)
    && !directoryName.Contains("DummyConsole", StringComparison.Ordinal)
    && !directoryName.Contains("TempCheck", StringComparison.Ordinal);

    // Parses every production .cs file in every RoslynSentinel.* project (see IsProductionProject), skipping bin, obj and Worktree folders.
    private static List<(string RelativePath, SyntaxNode Root)> ParseProductionSources()
    {
        var repo = RepoRoot();
        var result = new List<(string RelativePath, SyntaxNode Root)>();
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

                result.Add((relativePath, CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return result;
    }

    private const string DocumentLookupPath = "RoslynSentinel.Common/DocumentLookup.cs";

    // Counts per "<relative file>::<kind>" over all production sources. DocumentLookup itself is exempt from kind c only.
    private static SortedDictionary<string, int> MeasureCounts()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (relativePath, root) in sources)
        {
            void Count(string kind)
            {
                var key = relativePath + "::" + kind;
                actual[key] = actual.GetValueOrDefault(key) + 1;
            }

            foreach (var node in root.DescendantNodes())
            {
                if (node is BinaryExpressionSyntax binary && IsCaseSensitivePathComparison(binary))
                {
                    Count("a");
                }
                else if (node is InvocationExpressionSyntax invocation)
                {
                    if (IsCaseSensitivePathEquals(invocation))
                    {
                        Count("b");
                    }

                    if (relativePath != DocumentLookupPath && IsDocumentsPathScan(invocation))
                    {
                        Count("c");
                    }
                }
            }
        }

        return actual;
    }

    // Baseline keyed "<relative file>::<kind>" (kind a|b|c) to the exact number of sites that file may contain. Counts only shrink.
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        // Kind a: ==/!= on a .FilePath/.Absolute string (pre-existing; phase 5b removes these).
        ["RoslynSentinel.Engines.Advanced/DeadCodeEngine.cs::a"] = 1,
        ["RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs::a"] = 2,
        ["RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs::a"] = 4,
        ["RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs::a"] = 5,

        // Kind b: comparer-less string.Equals / x.Equals(y) on a path string (pre-existing; phase 5b removes these).
        ["RoslynSentinel.Engines.Basic/NamedArgumentsEngine.cs::b"] = 1,
        ["RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs::b"] = 1,

        // Kind c: hand-rolled .Documents scans on .FilePath outside DocumentLookup (pre-existing; phase 5b removes these).
        ["RoslynSentinel.Common/PersistentWorkspaceManager.cs::c"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::c"] = 4,
        ["RoslynSentinel.Engines.Advanced/ControlFlowEngine.cs::c"] = 1,
        ["RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::c"] = 2,
        ["RoslynSentinel.Engines.Basic/DiagnosticEngine.cs::c"] = 1,
        ["RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs::c"] = 1,
        ["RoslynSentinel.Engines.Basic/NamedArgumentsEngine.cs::c"] = 1,
        ["RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs::c"] = 1,
        ["RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs::c"] = 1,
        ["RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs::c"] = 2,
    };

    [Test]
    public void CaseSensitivePathComparisons_AreOnlyInTheBaseline()
    {
        var actual = MeasureCounts();

        // Above the baseline, or a key the baseline does not have: a new case-sensitive path comparison.
        var grown = actual
            .Where(entry => entry.Value > Baseline.GetValueOrDefault(entry.Key))
            .Select(entry => $"  [\"{entry.Key}\"] = {entry.Value}, (baseline {Baseline.GetValueOrDefault(entry.Key)})")
            .ToList();

        // Below the baseline (including keys that no longer occur): the baseline is stale and must be lowered.
        var shrunk = Baseline
            .Where(entry => actual.GetValueOrDefault(entry.Key) < entry.Value)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"  {entry.Key}: baseline {entry.Value}, tree has {actual.GetValueOrDefault(entry.Key)}")
            .ToList();

        var problems = new List<string>();
        if (grown.Count > 0)
        {
            problems.Add("New case-sensitive path comparison. Kind a/b: compare with PathComparison.Comparer / PathComparison.Comparison "
                + "(or keep both sides as FilePathWrapper) instead of ==, != or string.Equals on .FilePath/.Absolute. "
                + "Kind c: find the document with DocumentLookup instead of scanning .Documents for .FilePath:"
                + Environment.NewLine + string.Join(Environment.NewLine, grown));
        }

        if (shrunk.Count > 0)
        {
            problems.Add("ratchet can shrink: lower the baseline (delete the entry when the count reaches 0) to match the tree:"
                + Environment.NewLine + string.Join(Environment.NewLine, shrunk));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Architecture ratchet for silent first-match lookups. A name-keyed First/FirstOrDefault/Single/SingleOrDefault
/// returns the first match without reporting ambiguity, so production code must use the lookup helpers instead.
/// The baseline may only shrink.
/// </summary>
[TestFixture]
[Category("FirstMatchRatchet")]
public class FirstMatchRatchetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsProductionProject(string projectName) =>
    projectName == "RoslynSentinel.Common"
    || projectName.StartsWith("RoslynSentinel.Engines.", StringComparison.Ordinal)
    || projectName.StartsWith("RoslynSentinel.Tools.", StringComparison.Ordinal);

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

    // A pair is name-keyed when one side reads a name-like member and the other side is not a kind check
    // (a string literal, nameof(...) or an interpolated string without holes).
    private static bool IsNameKeyedPair(ExpressionSyntax left, ExpressionSyntax right)
    {
        static bool IsNameAccessor(ExpressionSyntax expression)
        {
            if (expression is InvocationExpressionSyntax call && InvokedMemberName(call) == "ToDisplayString")
            {
                return true;
            }

            var text = expression.ToString();
            return new[] { ".Name", ".MetadataName", ".FilePath", ".Identifier.Text", ".Identifier.ValueText", ".Identifier.Value", ".ValueText" }
                .Any(suffix => text.EndsWith(suffix, StringComparison.Ordinal));
        }

        static bool IsKindLiteral(ExpressionSyntax expression) => expression switch
        {
            LiteralExpressionSyntax => true,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } => true,
            InterpolatedStringExpressionSyntax interpolated => !interpolated.Contents.OfType<InterpolationSyntax>().Any(),
            _ => false,
        };

        return (IsNameAccessor(left) && !IsKindLiteral(right)) || (IsNameAccessor(right) && !IsKindLiteral(left));
    }

    // An == comparison, or an Equals/StartsWith/EndsWith call on a receiver, whose operands are name-keyed.
    private static bool IsNameKeyedComparison(SyntaxNode node)
    {
        if (node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression))
        {
            return IsNameKeyedPair(binary.Left, binary.Right);
        }

        if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var receiver } } call
            && InvokedMemberName(call) is ("Equals" or "StartsWith" or "EndsWith")
            && call.ArgumentList.Arguments.Count > 0)
        {
            return IsNameKeyedPair(receiver, call.ArgumentList.Arguments[0].Expression);
        }

        return false;
    }

    // Name-keyed picks: First, FirstOrDefault, Single or SingleOrDefault with a lambda argument whose body compares a
    // name-like member with a non-literal value via == or Equals/StartsWith/EndsWith.
    internal static IEnumerable<InvocationExpressionSyntax> FindNameKeyedPicks(SyntaxNode root)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = InvokedMemberName(invocation);
            if (name is not ("First" or "FirstOrDefault" or "Single" or "SingleOrDefault"))
            {
                continue;
            }

            if (invocation.ArgumentList.Arguments.Any(argument =>
                    argument.Expression is LambdaExpressionSyntax lambda
                    && lambda.Body.DescendantNodesAndSelf().Any(IsNameKeyedComparison)))
            {
                yield return invocation;
            }
        }
    }

    // Keys are "<relative file>::<enclosing member>"; values are the exact number of name-keyed picks that member may
    // contain. Only shrinks: a new name-keyed First/Single pick needs a lookup helper, not a new entry without a reason.
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        // TODO triage: unclassified first-match pick. Each entry needs a read of the site to decide between routing it
        // through ProjectLookup.FindProject / NameTargetLookup.RequireSingle / DocumentLookup.RequireDocument and a reasoned exemption.
        ["RoslynSentinel.Common/PersistentWorkspaceManager.cs::ApplyInMemoryDocumentUpdatesAsync"] = 1,
        ["RoslynSentinel.Common/ToolCallHint.cs::FindSpec"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::ApplyExtractConstant"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::ExtractMembersToPartialWithSourceAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::InlineMethodAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AdvancedStructuralEngine.cs::InlineParameterAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AntiPatternEngine.cs::FindMissingGenericConstraintsAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs::RewriteCallerAsyncBodyAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs::SemanticBridgeCallRewriteAsync"] = 2,
        ["RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs::TryTransformCallerAsync"] = 2,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::FlagCandidatesInProjectAsync"] = 4,
        ["RoslynSentinel.Engines.Advanced/AsyncOptimizationEngine.cs::RewriteObsoleteCallsInAsyncMethodAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/CloneDetectionEngine.cs::FindDuplicateBlocksInClassAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/CodeGenerationEngine.cs::BuildAutoProperty"] = 2,
        ["RoslynSentinel.Engines.Advanced/LogicOptimizationEngine.cs::ConvertStaticToExtensionAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/MappingEngine.cs::GenerateMappingAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/MetricsEngine.cs::AnalyzeTypeCohesionAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/ModernizationEngine.cs::RecordToClassAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/OutParamRefactoringEngine.cs::ConvertOutParamsToValueTupleAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/ResourceSafetyEngine.cs::DetectMemoryLeaksAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/StackOverflowEngine.cs::AnalyzeStackOverflowRisksAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/StackOverflowEngine.cs::DetectCrossFileInheritanceCyclesAsync"] = 1,
        ["RoslynSentinel.Engines.Advanced/StackOverflowEngine.cs::DetectInFileInheritanceCycles"] = 2,
        ["RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::ChangeSignatureAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs::MoveAllTypesToFilesForDocumentAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs::DescribeDuplicateDefinitionAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs::GetConstructorParametersAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs::MoveMembersToBaseTypeAsync"] = 2,
        ["RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs::RemoveConstructorParameterAsync"] = 3,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::EncapsulateFieldSafeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::ExtractConstantSafeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs::ExtractMethodSafeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs::FindNamespacePathMismatchesAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs::CollectSitesAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/SemanticReplaceEngine.cs::FindArgumentFor"] = 1,
        ["RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs::BuildCallGraphNodeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs::BuildReverseCallGraphNodeAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs::FindCallersAsync"] = 2,
        ["RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs::FindImplementationsForMemberAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs::TraceVariableLifetimeAsync"] = 3,
        ["RoslynSentinel.Engines.Basic/SyntaxUpgradeEngine.cs::UpgradeToPrimaryConstructorAsync"] = 1,
        ["RoslynSentinel.Engines.Basic/TestCategoryTextEditor.cs::FindInsertionLine"] = 1,
        ["RoslynSentinel.Engines.Basic/ThreadSafetyEngine.cs::MakeMethodThreadSafeAsync"] = 1,
        ["RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs::BuildRecoveryHintAsync"] = 1,
    };

    private static SyntaxNode ParseSnippet(string code) => CSharpSyntaxTree.ParseText(code).GetRoot();

    [Test]
    public void FindNameKeyedPicks_DetectsEqualityOnName()
    {
        var root = ParseSnippet("var r = projects.FirstOrDefault(p => p.Name == name);");
        Assert.That(FindNameKeyedPicks(root).Count(), Is.EqualTo(1));
    }

    [Test]
    public void FindNameKeyedPicks_DetectsMultiLineFilePathPick()
    {
        var root = ParseSnippet("var r = x\n    .FirstOrDefault(\n        d => d.FilePath == path);");
        Assert.That(FindNameKeyedPicks(root).Count(), Is.EqualTo(1));
    }

    [Test]
    public void FindNameKeyedPicks_IgnoresKindChecksAndNonPickCalls()
    {
        var root = ParseSnippet(
            "var hit = attrs.FirstOrDefault(a => a.AttributeClass?.Name == \"ObsoleteAttribute\");\n"
            + "var all = list.Where(d => d.Name == n);");
        Assert.That(FindNameKeyedPicks(root).ToList(), Is.Empty);
    }

    [Test]
    public void NameKeyedPicks_AreOnlyInTheBaseline()
    {
        var sources = ParseProductionSources();
        Assert.That(sources, Is.Not.Empty, "The scan found no production .cs files; the repo root lookup is broken.");

        // Name-keyed pick count per "<file>::<member>" key.
        var actual = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (relativePath, root) in sources)
        {
            foreach (var pick in FindNameKeyedPicks(root))
            {
                var key = relativePath + "::" + EnclosingMemberName(pick);
                actual[key] = actual.GetValueOrDefault(key) + 1;
            }
        }

        var unexpected = actual.Keys.Where(key => !Baseline.ContainsKey(key)).ToList();
        var stale = Baseline.Keys.Where(key => !actual.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        var countMismatch = Baseline
            .Where(entry => actual.TryGetValue(entry.Key, out var count) && count != entry.Value)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"  {entry.Key}: baseline says {entry.Value}, tree has {actual[entry.Key]}")
            .ToList();

        var problems = new List<string>();
        if (unexpected.Count > 0)
        {
            problems.Add("Unexpected name-keyed First/Single pick. A name-keyed First/Single pick returns the first match silently; "
                + "use ProjectLookup.FindProject, NameTargetLookup.RequireSingle or DocumentLookup.RequireDocument/FindDocumentOrNull, "
                + "or add a baseline entry with a reason:"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Select(key => "  " + key + " (" + actual[key] + " pick(s))")));
        }

        if (countMismatch.Count > 0)
        {
            problems.Add("Name-keyed pick count differs from the baseline for a baselined member. A higher count is a new silent "
                + "first-match pick to route through a lookup helper; a lower count means the baseline value must shrink to match:"
                + Environment.NewLine + string.Join(Environment.NewLine, countMismatch));
        }

        if (stale.Count > 0)
        {
            problems.Add("Stale baseline entries (no longer present in the tree). Remove them so the baseline only shrinks:"
                + Environment.NewLine + string.Join(Environment.NewLine, stale.Select(key => "  " + key)));
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine + Environment.NewLine, problems));
    }
}

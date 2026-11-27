using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.ErrorRecovery;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model unit tests for the Transcription slip scenarios: seed and golden text parsed with Roslyn, no files, no builds.
/// </summary>
[TestFixture]
public class TranscriptionScenarioTests
{
    private const string DictionaryFilePathType = "Dictionary<FilePath, string>";

    [Test]
    public void WidenDictionaryLeafFirstSlip_OrderCoversSeedMethodsAndRespectsForwarding()
    {
        var seeds = WidenDictionaryLeafFirstSlipScenario.Definition.SeedFiles;
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(seeds[WidenToReadOnlyDictionaryScenario.ChangeValidatorPath]),
            CSharpSyntaxTree.ParseText(seeds[WidenToReadOnlyDictionaryScenario.ChangeHelperPath]),
        };
        var order = WidenDictionaryLeafFirstSlipScenario.LeafFirstOrder
            .Select(t => (t.TypeName, t.MethodName, t.ParameterCount))
            .ToList();

        var expectedSet = order.ToHashSet();
        var methods = trees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(method => method.ParameterList.Parameters.Any(p => p.Type?.ToString().Trim() == DictionaryFilePathType))
            .ToList();
        var actualSet = methods
            .Select(method => (ContainingTypeName(method), method.Identifier.Text, method.ParameterList.Parameters.Count))
            .ToHashSet();

        Assert.That(
            actualSet.SetEquals(expectedSet),
            Is.True,
            $"Methods with a Dictionary<FilePath, string> parameter {Format(actualSet)} differ from LeafFirstOrder {Format(expectedSet)}.");

        var edgeCount = 0;
        var violations = new List<string>();
        foreach (var caller in methods)
        {
            var callerType = ContainingTypeName(caller);
            var callerName = caller.Identifier.Text;
            var callerIndex = order.FindIndex(k => k == (callerType, callerName, caller.ParameterList.Parameters.Count));
            Assert.That(callerIndex, Is.GreaterThanOrEqualTo(0), $"{callerType}.{callerName} is not listed in LeafFirstOrder.");

            foreach (var invocation in caller.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string calleeName;
                string? qualifier;
                switch (invocation.Expression)
                {
                    case IdentifierNameSyntax identifier:
                        calleeName = identifier.Identifier.Text;
                        qualifier = null;
                        break;
                    case MemberAccessExpressionSyntax access:
                        calleeName = access.Name.Identifier.Text;
                        qualifier = access.Expression.ToString().Trim();
                        break;
                    default:
                        continue;
                }

                var calleeType = qualifier ?? callerType;
                var argumentCount = invocation.ArgumentList.Arguments.Count;
                var calleeIndex = order.FindIndex(k => k == (calleeType, calleeName, argumentCount));
                if (calleeIndex < 0)
                {
                    continue;
                }

                edgeCount++;
                if (calleeIndex >= callerIndex)
                {
                    violations.Add(
                        $"{callerType}.{callerName} (index {callerIndex + 1}) calls {calleeType}.{calleeName} with {argumentCount} argument(s) (index {calleeIndex + 1}); the callee must come earlier.");
                }
            }
        }

        Assert.That(edgeCount, Is.GreaterThan(0), "No forwarding edge was resolved; the order check would pass vacuously.");
        Assert.That(violations, Is.Empty, "Forwarding edges out of leaf-first order: " + string.Join(" ", violations));
    }

    [Test]
    public void SweepNamedSitesSlip_SitesToConvertEqualsSeedCreationSites()
    {
        var seeds = SweepNamedSitesSlipScenario.Definition.SeedFiles;
        var seedSites = new HashSet<(string FileName, string Method)>();
        foreach (var path in SweepConstructionSitesScenario.TargetPaths)
        {
            seedSites.UnionWith(CreationSites(path, seeds[path]));
        }

        var expectedSites = SweepNamedSitesSlipScenario.SitesToConvert
            .Select(site => (site.FileName, site.Method))
            .ToHashSet();

        Assert.That(
            seedSites.SetEquals(expectedSites),
            Is.True,
            $"Seed creation sites {Format(seedSites)} differ from SitesToConvert {Format(expectedSites)}.");

        var golden = SweepNamedSitesSlipScenario.Definition.Steps[0].GoldenFiles;
        var goldenSites = new List<(string FileName, string Method)>();
        foreach (var path in SweepConstructionSitesScenario.TargetPaths)
        {
            goldenSites.AddRange(CreationSites(path, golden[path]));
        }

        Assert.That(
            goldenSites,
            Is.Empty,
            $"Golden files still create Dictionary<FilePath, string> at {Format(goldenSites)}.");
    }

    private static List<(string FileName, string Method)> CreationSites(string path, string text)
    {
        var sites = new List<(string FileName, string Method)>();
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (creation.Type.ToString().Trim() != DictionaryFilePathType)
            {
                continue;
            }

            var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            sites.Add((Path.GetFileName(path), method?.Identifier.Text ?? "<no method>"));
        }

        return sites;
    }

    private static string ContainingTypeName(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.Text;

    private static string Format<T>(IEnumerable<T> items) => "[" + string.Join(", ", items) + "]";

    private static IEnumerable<string> SlipFamilyIds()
    {
        return ImplementationScenarioRegistry.All
            .Where(scenario => scenario.Steps.Count > 0 && scenario.Steps[0].Sites is not null)
            .Select(scenario => scenario.Id);
    }

    private static (ImplementationScenario Scenario, SlipSiteSet Sites) ResolveSlipFamily(string familyId)
    {
        var scenario = ImplementationScenarioRegistry.All.Single(candidate => candidate.Id == familyId);
        var sites = scenario.Steps[0].Sites ?? throw new InvalidOperationException($"Scenario {familyId} has no slip sites on step 0.");
        return (scenario, sites);
    }

    [Test]
    [TestCaseSource(nameof(SlipFamilyIds))]
    public void SlipFamily_PassesValidation(string familyId)
    {
        var (_, sites) = ResolveSlipFamily(familyId);
        Assert.That(SlipFamilyBuilder.Validate(sites), Is.Empty);
    }

    [Test]
    [TestCaseSource(nameof(SlipFamilyIds))]
    public void SlipFamily_GoldenAllCorrect_SeedAllUnchanged(string familyId)
    {
        var (scenario, sites) = ResolveSlipFamily(familyId);

        var golden = SiteGrader.Grade(scenario.Steps[0].GoldenFiles[sites.FilePath], sites);
        Assert.That(golden.Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Correct));

        var seed = SiteGrader.Grade(scenario.SeedFiles[sites.FilePath], sites);
        Assert.That(seed.Where(grade => grade.Role == SiteRole.Changed).Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Unchanged));
        Assert.That(seed.Where(grade => grade.Role != SiteRole.Changed).Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Correct));
    }

    [Test]
    [TestCaseSource(nameof(SlipFamilyIds))]
    public void SlipFamily_FitsContextBudget(string familyId)
    {
        var (scenario, _) = ResolveSlipFamily(familyId);
        Assert.That(scenario.ContextCharacters(), Is.LessThanOrEqualTo(ImplementationScenario.ContextBudgetCharacters));
    }

    [Test]
    public void SlipFamily_EveryPlanScenarioHasSites()
    {
        var planScenarios = ImplementationScenarioRegistry.All
            .Where(scenario => scenario.Source.Contains("plan_slip_first_scenarios", StringComparison.Ordinal))
            .ToList();
        Assert.That(planScenarios, Is.Not.Empty);
        Assert.That(planScenarios.Where(scenario => scenario.Steps.Count == 0 || scenario.Steps[0].Sites is null).Select(scenario => scenario.Id), Is.Empty);
    }

    [Test]
    public void SiteVocabulary_MatchesEnums()
    {
        var repoRoot = RepoRootLocator.TryFind(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");
        var json = File.ReadAllText(Path.Combine(repoRoot, "scripts", "eval-review", "vocabulary.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(ReadVocabularyNames(root, "siteRoles"), Is.EqualTo(Enum.GetNames<SiteRole>()));
        Assert.That(ReadVocabularyNames(root, "siteOutcomes"), Is.EqualTo(Enum.GetNames<SiteOutcome>()));
    }

    private static string[] ReadVocabularyNames(System.Text.Json.JsonElement root, string property)
    {
        return root.GetProperty(property).EnumerateArray().Select(element => element.GetString() ?? string.Empty).ToArray();
    }
}

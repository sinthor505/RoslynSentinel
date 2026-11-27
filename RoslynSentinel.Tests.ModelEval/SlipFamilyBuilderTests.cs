using System;
using System.Collections.Generic;
using System.Linq;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model unit tests for SlipFamilyBuilder: separator expansion, change application, prompt and file rendering,
/// layout validation and the built scenario. No files, no builds, no models.
/// </summary>
[TestFixture]
public class SlipFamilyBuilderTests
{
    private const string MemberBody = "alpha one beta two gamma three delta four";

    [Test]
    public void Expand_InterleavesUniqueSeparators()
    {
        var set = ThreeSiteSet();

        var expanded = SlipFamilyBuilder.Expand(set);

        Assert.That(expanded.Count, Is.EqualTo(5));
        Assert.That(expanded[0], Is.SameAs(set.Sites[0]));
        Assert.That(expanded[2], Is.SameAs(set.Sites[1]));
        Assert.That(expanded[4], Is.SameAs(set.Sites[2]));
        var separators = new[] { expanded[1], expanded[3] };
        Assert.That(separators.Select(site => site.Role), Is.All.EqualTo(SiteRole.Separator));
        Assert.That(separators.Select(site => site.Name).Distinct().Count(), Is.EqualTo(2));
        Assert.That(separators.Select(site => site.SeedText).Distinct().Count(), Is.EqualTo(2));
        Assert.That(separators.Select(site => site.Name), Is.All.StartWith(SlipFamilyLayout.SeparatorPrefix));
    }

    [Test]
    public void ApplyChanges_FragmentNotUnique_Throws()
    {
        var site = new SlipSite("Msg01", "alpha beta alpha", [new SiteChange("alpha", "omega")], SiteRole.Changed);

        Assert.Throws<InvalidOperationException>(() => SlipFamilyBuilder.ApplyChanges(site));
    }

    [Test]
    public void ApplyChanges_ReplacesAtSeedPositions()
    {
        var site = new SlipSite(
            "Msg01",
            "A one B two C",
            [new SiteChange("one", "two three"), new SiteChange("two", "four")],
            SiteRole.Changed);

        var golden = SlipFamilyBuilder.ApplyChanges(site);

        Assert.That(golden, Is.EqualTo("A two three B four C"));
    }

    [Test]
    public void RenderPrompt_ContainsEachChangedSiteLineOnly()
    {
        var set = ThreeSiteSet();

        var prompt = SlipFamilyBuilder.RenderPrompt(set);

        foreach (var site in set.Sites.Where(site => site.Role == SiteRole.Changed))
        {
            Assert.That(prompt, Does.Contain(SlipFamilyBuilder.RenderChangeLine(site)));
        }

        Assert.That(prompt, Does.Not.Contain("Msg02"));
        Assert.That(prompt, Does.Not.Contain(SlipFamilyLayout.SeparatorPrefix + "01"));
        Assert.That(prompt, Does.Not.Contain(SlipFamilyLayout.SeparatorPrefix + "02"));
    }

    [Test]
    public void Validate_ValidLayout_ReturnsNothing()
    {
        var set = WithSites(ValidSites());

        Assert.That(SlipFamilyBuilder.Validate(set), Is.Empty);
    }

    [TestCase("ObservedCount")]
    [TestCase("ControlPositions")]
    [TestCase("SeparatorPrefixName")]
    [TestCase("DuplicateNames")]
    [TestCase("ChangeCount")]
    [TestCase("ControlHasChanges")]
    [TestCase("NonPlainFragment")]
    [TestCase("OldNotUnique")]
    [TestCase("OverlappingRanges")]
    [TestCase("SpreadTooSmall")]
    [TestCase("TooManyDocLines")]
    public void Validate_FlagsEachRule(string ruleName)
    {
        var set = BreakRule(ruleName);

        var violations = SlipFamilyBuilder.Validate(set);

        Assert.That(violations, Is.Not.Empty);
    }

    [Test]
    public void Render_GoldenAllCorrect_SeedAllUnchanged()
    {
        var set = ThreeSiteSet();

        var golden = SiteGrader.Grade(SlipFamilyBuilder.RenderFile(set, true), set);
        Assert.That(golden.Count, Is.EqualTo(5));
        Assert.That(golden.Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Correct));

        var seed = SiteGrader.Grade(SlipFamilyBuilder.RenderFile(set, false), set);
        Assert.That(seed.Where(grade => grade.Role == SiteRole.Changed).Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Unchanged));
        Assert.That(seed.Where(grade => grade.Role != SiteRole.Changed).Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Correct));
    }

    [Test]
    public void Build_SetsSitesAndSeedEqualsRenderedSeed()
    {
        var set = ThreeSiteSet();

        var scenario = SlipFamilyBuilder.Build("X", "t", "src", set);

        Assert.That(scenario.Steps[0].Sites, Is.SameAs(set));
        Assert.That(scenario.SeedFiles[set.FilePath], Is.EqualTo(SlipFamilyBuilder.RenderFile(set, false)));
        Assert.That(scenario.TurnCap, Is.EqualTo(SlipFamilyBuilder.TurnCap));
        Assert.That(scenario.PrimaryAxis, Is.EqualTo(EvalAxis.Transcription));
    }

    private static string SiteText(string name) => $"public static string {name}() => \"{MemberBody}\";";

    private static string SiteName(int position) => $"Msg{position:00}";

    private static SlipSite ChangedSite(string name) => new(
        name,
        SiteText(name),
        [new SiteChange("alpha", "omega"), new SiteChange("delta", "sigma")],
        SiteRole.Changed);

    private static SlipSite ControlSite(string name) => new(name, SiteText(name), [], SiteRole.Control);

    private static SlipSiteSet WithSites(IReadOnlyList<SlipSite> sites) =>
        new("src/Msgs.cs", "Sample.Messages", "Msgs", string.Empty, sites);

    private static SlipSiteSet ThreeSiteSet() =>
        WithSites(new[] { ChangedSite("Msg01"), ControlSite("Msg02"), ChangedSite("Msg03") });

    private static List<SlipSite> ValidSites()
    {
        var sites = new List<SlipSite>();
        for (var position = 1; position <= SlipFamilyLayout.ObservedSiteCount; position++)
        {
            var name = SiteName(position);
            sites.Add(SlipFamilyLayout.ControlPositions.Contains(position) ? ControlSite(name) : ChangedSite(name));
        }

        return sites;
    }

    private static SlipSiteSet BreakRule(string ruleName)
    {
        var sites = ValidSites();
        switch (ruleName)
        {
            case "ObservedCount":
                sites.RemoveAt(sites.Count - 1);
                break;
            case "ControlPositions":
                // Position 4 is a Control position; a Changed site there breaks only the role rule.
                sites[3] = ChangedSite(sites[3].Name);
                break;
            case "SeparatorPrefixName":
                sites[0] = ChangedSite("Calc01");
                break;
            case "DuplicateNames":
                sites[1] = ChangedSite(sites[0].Name);
                break;
            case "ChangeCount":
                sites[0] = new SlipSite(sites[0].Name, sites[0].SeedText, [new SiteChange("alpha", "omega")], SiteRole.Changed);
                break;
            case "ControlHasChanges":
                sites[3] = new SlipSite(sites[3].Name, sites[3].SeedText, [new SiteChange("alpha", "omega")], SiteRole.Control);
                break;
            case "NonPlainFragment":
                sites[0] = new SlipSite(
                    sites[0].Name,
                    sites[0].SeedText,
                    [new SiteChange("alpha", "omega$"), new SiteChange("delta", "sigma")],
                    SiteRole.Changed);
                break;
            case "OldNotUnique":
                // "a" occurs many times in the member text.
                sites[0] = new SlipSite(
                    sites[0].Name,
                    sites[0].SeedText,
                    [new SiteChange("a", "o"), new SiteChange("delta", "sigma")],
                    SiteRole.Changed);
                break;
            case "OverlappingRanges":
                // The first Old spans the second Old's start, and the starts are 37 apart so only the overlap rule fires.
                sites[0] = new SlipSite(
                    sites[0].Name,
                    sites[0].SeedText,
                    [new SiteChange(MemberBody, "omega"), new SiteChange("delta four", "sigma")],
                    SiteRole.Changed);
                break;
            case "SpreadTooSmall":
                // "alpha" starts at 33 and "one" at 39: six characters apart, no overlap.
                sites[0] = new SlipSite(
                    sites[0].Name,
                    sites[0].SeedText,
                    [new SiteChange("alpha", "omega"), new SiteChange("one", "sigma")],
                    SiteRole.Changed);
                break;
            case "TooManyDocLines":
                sites[0] = new SlipSite(
                    sites[0].Name,
                    "/// one\n/// two\n/// three\n/// four\n" + sites[0].SeedText,
                    sites[0].Changes,
                    SiteRole.Changed);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ruleName), ruleName, "No rule-breaking case for this rule name.");
        }

        return WithSites(sites);
    }
}

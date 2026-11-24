namespace RoslynSentinel.Tests;

[TestFixture]
[Category("ParameterSurface")]
public class ParameterSurfaceTests
{
    [TearDown]
    public void ResetSchemaOptions()
    {
#pragma warning disable CS0618 // obsolete from stage D Step 10; the reset still uses the legacy setter until then
        SchemaOptions.ConfigureHiddenParameters([]);
#pragma warning restore CS0618
        SchemaOptions.Configure([]);
    }

    private static ToolSurfaceTool Tool(string name, params string[] hidden) =>
        new(name, "TestClass", ToolSurfaceOrigin.Startup, null, [], hidden);

    private static ParameterVisibilityRule HideRule(string? tool, string parameter, ParameterRuleSource source, string origin = "test") =>
        new(tool, parameter, ParameterVisibility.Hide, source, origin);

    private static ParameterVisibilityRule ShowRule(string? tool, string parameter, ParameterRuleSource source, string origin = "test") =>
        new(tool, parameter, ParameterVisibility.Show, source, origin);

    private static ParameterSurface Surface(params ParameterVisibilityRule[] rules) =>
        new(new ToolParameterSurfaceOptions(rules));

    [Test]
    public void LaterLayerWins()
    {
        var profileHide = HideRule(null, "p", ParameterRuleSource.Profile);
        var harnessShow = ShowRule(null, "p", ParameterRuleSource.Harness);
        Assert.That(Surface(profileHide, harnessShow).ResolveHidden("AnyTool", "p"), Is.Null);

        var harnessHide = HideRule(null, "p", ParameterRuleSource.Harness);
        var profileShow = ShowRule(null, "p", ParameterRuleSource.Profile);
        Assert.That(Surface(harnessHide, profileShow).ResolveHidden("AnyTool", "p"), Is.SameAs(harnessHide));
    }

    [Test]
    public void ScopedRuleBeatsUnscopedInSameLayer()
    {
        // The scoped Show is listed first; precedence within a layer must not depend on list order.
        var scopedShow = ShowRule("ToolA", "p", ParameterRuleSource.EnvOrFlag);
        var unscopedHide = HideRule(null, "p", ParameterRuleSource.EnvOrFlag);
        var surface = Surface(scopedShow, unscopedHide);

        Assert.That(surface.ResolveHidden("ToolA", "p"), Is.Null);
        Assert.That(surface.ResolveHidden("ToolB", "p"), Is.SameAs(unscopedHide));
    }

    [Test]
    public void ShowStar_UnhidesLowerLayers()
    {
        var surface = Surface(
            HideRule(null, "a", ParameterRuleSource.Profile),
            HideRule(null, "b", ParameterRuleSource.Profile),
            ShowRule(null, "*", ParameterRuleSource.Harness));

        Assert.That(surface.ResolveHidden("AnyTool", "a"), Is.Null);
        Assert.That(surface.ResolveHidden("AnyTool", "b"), Is.Null);
    }

    [Test]
    public void ShowStar_ScopedToTool_LeavesOtherToolsHidden()
    {
        var surface = Surface(
            HideRule(null, "a", ParameterRuleSource.Profile),
            HideRule(null, "b", ParameterRuleSource.Profile),
            ShowRule("ToolA", "*", ParameterRuleSource.Harness));

        Assert.That(surface.ResolveHidden("ToolA", "a"), Is.Null);
        Assert.That(surface.ResolveHidden("ToolA", "b"), Is.Null);
        Assert.That(surface.ResolveHidden("ToolB", "a"), Is.Not.Null);
        Assert.That(surface.ResolveHidden("ToolB", "b"), Is.Not.Null);
    }

    [Test]
    public void ToolNameIgnoresCase_ParameterNameIsOrdinal()
    {
        var rule = HideRule("ReplaceSnippet", "validateOnApply", ParameterRuleSource.Profile);
        var surface = Surface(rule);

        Assert.That(surface.ResolveHidden("replacesnippet", "validateOnApply"), Is.SameAs(rule));
        Assert.That(surface.ResolveHidden("ReplaceSnippet", "VALIDATEONAPPLY"), Is.Null);
    }

    [Test]
    public void ParseHideList_ToolDotParam_ScopesToTool()
    {
        var rules = ToolParameterSurfaceOptions.ParseHideList("ReplaceSnippet.validateOnApply", ParameterRuleSource.EnvOrFlag, "--hide-params");

        Assert.That(rules.Count, Is.EqualTo(1));
        var rule = rules[0];
        Assert.That(rule.Tool, Is.EqualTo("ReplaceSnippet"));
        Assert.That(rule.Parameter, Is.EqualTo("validateOnApply"));
        Assert.That(rule.Action, Is.EqualTo(ParameterVisibility.Hide));
        Assert.That(rule.Source, Is.EqualTo(ParameterRuleSource.EnvOrFlag));
        Assert.That(rule.Origin, Is.EqualTo("--hide-params"));
    }

    [Test]
    public void ParseHideList_BlankOrSeparatorsOnly_GivesEmpty()
    {
        foreach (var raw in new string?[] { null, "", "  ", ",;, ;" })
        {
            var rules = ToolParameterSurfaceOptions.ParseHideList(raw, ParameterRuleSource.EnvOrFlag, "test");
            Assert.That(rules, Is.Empty, $"raw='{raw}'");
        }
    }

    [Test]
    public void ParseHideList_MalformedDots_StayUnscopedLiterals()
    {
        foreach (var raw in new[] { "a.b.c", ".x", "x." })
        {
            var rules = ToolParameterSurfaceOptions.ParseHideList(raw, ParameterRuleSource.EnvOrFlag, "test");
            Assert.That(rules.Count, Is.EqualTo(1), $"raw='{raw}'");
            Assert.That(rules[0].Tool, Is.Null, $"raw='{raw}'");
            Assert.That(rules[0].Parameter, Is.EqualTo(raw));
        }
    }

    [Test]
    [NonParallelizable]
    public void FromStartup_LeanAndExplicit_HidesExactlyEffectiveHiddenParameters()
    {
        SchemaOptions.Configure(new[] { "--schema-profile=lean", "--hide-params=dryRun,custom" });
        var surface = ParameterSurface.FromStartup();

        var candidates = new List<string>(SchemaOptions.LeanHiddenParameters) { "dryRun", "custom", "notHidden" };
#pragma warning disable CS0618 // obsolete from stage D Step 7; this test pins equivalence to it
        var effective = SchemaOptions.EffectiveHiddenParameters();
#pragma warning restore CS0618
        foreach (var name in candidates)
        {
            var isHidden = surface.ResolveHidden("AnyTool", name) is not null;
            Assert.That(isHidden, Is.EqualTo(effective.Contains(name)), $"name='{name}'");
        }
    }

    [Test]
    [NonParallelizable]
    public void FromStartup_DottedEntry_ScopesToTool()
    {
        SchemaOptions.Configure(new[] { "--schema-profile=full", "--hide-params=ReplaceSnippet.validateOnApply" });
        var surface = ParameterSurface.FromStartup();

        Assert.That(surface.ResolveHidden("ReplaceSnippet", "validateOnApply"), Is.Not.Null);
        Assert.That(surface.ResolveHidden("ApplyDiff", "validateOnApply"), Is.Null);
        Assert.That(surface.ResolveHidden("AnyTool", "ReplaceSnippet.validateOnApply"), Is.Null);
    }

    [Test]
    public void AttributeHidden_GroupsToolsByWinningRule()
    {
        var pRule = HideRule(null, "p", ParameterRuleSource.Profile, "origin-p");
        var qRule = HideRule(null, "q", ParameterRuleSource.Profile, "origin-q");
        var surface = Surface(pRule, qRule);

        var groups = surface.AttributeHidden([Tool("ToolB", "p"), Tool("ToolA", "p"), Tool("ToolC", "q")]);

        Assert.That(groups.Count, Is.EqualTo(2));
        Assert.That(groups[0].Parameter, Is.EqualTo("p"));
        Assert.That(groups[0].Source, Is.EqualTo(ParameterRuleSource.Profile));
        Assert.That(groups[0].Origin, Is.EqualTo("origin-p"));
        Assert.That(groups[0].Tools, Is.EqualTo(new[] { "ToolA", "ToolB" }));
        Assert.That(groups[1].Parameter, Is.EqualTo("q"));
        Assert.That(groups[1].Tools, Is.EqualTo(new[] { "ToolC" }));
    }

    [Test]
    public void AttributeHidden_EqualRulesFromTwoLayers_StaySeparateGroups()
    {
        // Record equality is by value and each name resolves to one winning instance, so the two
        // duplicates below cannot be told apart by grouping: this pins one group per winner, not
        // reference grouping itself. The layer-precedence half pins which winner is attributed.
        var first = HideRule(null, "p", ParameterRuleSource.Profile, "same");
        var second = HideRule(null, "p", ParameterRuleSource.Profile, "same");
        Assert.That(second, Is.EqualTo(first));
        Assert.That(second, Is.Not.SameAs(first));

        var duplicates = Surface(first, second).AttributeHidden([Tool("ToolA", "p"), Tool("ToolB", "p")]);
        Assert.That(duplicates.Count, Is.EqualTo(1));
        Assert.That(duplicates[0].Tools, Is.EqualTo(new[] { "ToolA", "ToolB" }));

        var profileP = HideRule(null, "p", ParameterRuleSource.Profile, "same");
        var envP = HideRule(null, "p", ParameterRuleSource.EnvOrFlag, "same");
        var layered = Surface(envP, profileP);
        Assert.That(layered.ResolveHidden("ToolA", "p"), Is.SameAs(envP));

        var layeredGroups = layered.AttributeHidden([Tool("ToolA", "p"), Tool("ToolB", "p")]);
        Assert.That(layeredGroups.Count, Is.EqualTo(1));
        Assert.That(layeredGroups[0].Source, Is.EqualTo(ParameterRuleSource.EnvOrFlag));
        Assert.That(layeredGroups[0].Tools, Is.EqualTo(new[] { "ToolA", "ToolB" }));
    }

    [Test]
    public void AttributeHidden_OmitsNamesWithNoWinningRule()
    {
        var surface = Surface(HideRule(null, "p", ParameterRuleSource.Profile));

        var groups = surface.AttributeHidden([Tool("ToolA", "p", "zzz")]);

        Assert.That(groups.Count, Is.EqualTo(1));
        Assert.That(groups[0].Parameter, Is.EqualTo("p"));
    }

    [Test]
    public void UnmatchedHideRules_ListsRuleThatWonNothing()
    {
        var typo = HideRule(null, "typo", ParameterRuleSource.Profile);
        var p = HideRule(null, "p", ParameterRuleSource.Profile);
        var show = ShowRule(null, "x", ParameterRuleSource.Harness);
        var surface = Surface(typo, p, show);

        var unmatched = surface.UnmatchedHideRules([Tool("ToolA", "p")]);

        Assert.That(unmatched.Count, Is.EqualTo(1));
        Assert.That(unmatched[0], Is.SameAs(typo));
    }

    [Test]
    public void HidesAnything_FalseForEmptyOrShowOnly()
    {
        Assert.That(new ParameterSurface(ToolParameterSurfaceOptions.Empty).HidesAnything, Is.False);
        Assert.That(Surface(ShowRule(null, "p", ParameterRuleSource.Harness)).HidesAnything, Is.False);
        Assert.That(Surface(HideRule(null, "p", ParameterRuleSource.Profile)).HidesAnything, Is.True);
    }
}

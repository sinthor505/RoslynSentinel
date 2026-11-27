using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model unit tests for SiteGrader: inline slip-site sets graded against end-state text, plus the shared step-1 sidecar fixture.
/// </summary>
[TestFixture]
public class SiteGraderTests
{
    private const string ClassName = "Target";
    private const string GreetSeed = "public static string Greet() => \"hello big world and more\";";
    private const string GreetGolden = "public static string Greet() => \"howdy big world and less\";";
    private const string FarewellSeed = "public static string Farewell() => \"hello big world and more\";";
    private const string FarewellGolden = "public static string Farewell() => \"howdy big world and less\";";
    private const string Calc01Golden = "public static int Calc01() => 1;";

    [Test]
    public void Grade_WhitespaceOnlyDifference_IsCorrect()
    {
        var end = FileOf(
            "public static string Greet()   =>   \"howdy big world and less\";",
            FarewellGolden,
            Calc01Golden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Correct));
    }

    [Test]
    public void Grade_MemberReflowedAcrossLines_IsCorrect()
    {
        var end = FileOf(
            "public static string Greet()\r\n    =>\r\n    \"howdy big world and less\";",
            FarewellGolden,
            Calc01Golden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Correct));
    }

    [Test]
    public void Grade_LiteralEscapeDifference_IsWrong()
    {
        const string escapedSeed = "public static string Greet() => \"hello big\\tworld and more\";";
        var set = SetOf(GreetSite(escapedSeed), FarewellSite());
        var end = FileOf(
            "public static string Greet() => \"howdy big\\nworld and less\";",
            FarewellGolden,
            Calc01Golden);

        var grades = SiteGrader.Grade(end, set);
        var greet = grades.Single(grade => grade.SiteName == "Greet");

        Assert.That(greet.Outcome, Is.EqualTo(SiteOutcome.Wrong));
        Assert.That(greet.EndStateText, Is.Not.Null);
    }

    [Test]
    public void Grade_SeedText_IsUnchanged()
    {
        var end = FileOf(GreetSeed, FarewellGolden, Calc01Golden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Unchanged));
    }

    [Test]
    public void Grade_MemberAbsent_IsMissing()
    {
        var end = FileOf(FarewellGolden, Calc01Golden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Missing));
    }

    [Test]
    public void Grade_MemberDuplicated_IsMissing()
    {
        var end = FileOf(GreetGolden, GreetGolden, FarewellGolden, Calc01Golden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Missing));
    }

    [Test]
    public void Grade_FileAbsent_AllMissing()
    {
        var grades = SiteGrader.Grade(null, GreetFarewellSet());

        Assert.That(grades, Has.Count.EqualTo(3));
        Assert.That(grades.Select(grade => grade.Outcome), Is.All.EqualTo(SiteOutcome.Missing));
    }

    [Test]
    public void Grade_DocCommentWordChanged_IsWrong()
    {
        const string docSeed = "/// <summary>one &lt;two&gt; three</summary>\r\n" + GreetSeed;
        var set = SetOf(GreetSite(docSeed), FarewellSite());
        var end = FileOf(
            "/// <summary>one &lt;four&gt; three</summary>\r\npublic static string Greet() => \"howdy big world and less\";",
            FarewellGolden,
            Calc01Golden);

        var grades = SiteGrader.Grade(end, set);

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Wrong));
    }

    [Test]
    public void Grade_DocCommentReflowed_IsCorrect()
    {
        const string docSeed = "/// <summary>one &lt;two&gt; three</summary>\r\n" + GreetSeed;
        var set = SetOf(GreetSite(docSeed), FarewellSite());
        var end = FileOf(
            "/// <summary>one &lt;two&gt;\r\n/// three</summary>\r\npublic static string Greet() => \"howdy big world and less\";",
            FarewellGolden,
            Calc01Golden);

        var grades = SiteGrader.Grade(end, set);

        Assert.That(OutcomeOf(grades, "Greet"), Is.EqualTo(SiteOutcome.Correct));
    }

    [Test]
    public void Grade_ControlEdited_IsWrong()
    {
        const string steadySeed = "public static string Steady() => \"unchanged text\";";
        var set = SetOf(GreetSite(GreetSeed), new SlipSite("Steady", steadySeed, [], SiteRole.Control));
        var end = FileOf(
            GreetGolden,
            Calc01Golden,
            "public static string Steady() => \"edited control text\";");

        var grades = SiteGrader.Grade(end, set);
        var steady = grades.Single(grade => grade.SiteName == "Steady");

        Assert.That(steady.Role, Is.EqualTo(SiteRole.Control));
        Assert.That(steady.Outcome, Is.EqualTo(SiteOutcome.Wrong));
    }

    [Test]
    public void Grade_SeparatorEdited_IsWrong()
    {
        var end = FileOf(
            GreetGolden,
            "public static int Calc01() => 2;",
            FarewellGolden);

        var grades = SiteGrader.Grade(end, GreetFarewellSet());
        var separator = grades.Single(grade => grade.SiteName == "Calc01");

        Assert.That(separator.Role, Is.EqualTo(SiteRole.Separator));
        Assert.That(separator.Outcome, Is.EqualTo(SiteOutcome.Wrong));
    }

    [Test]
    public void AllIntact_FalseWhenAnyGradeNotCorrect()
    {
        foreach (var outcome in Enum.GetValues<SiteOutcome>().Where(value => value != SiteOutcome.Correct))
        {
            var grades = new List<SiteGrade>
            {
                new("Greet", SiteRole.Changed, SiteOutcome.Correct, null),
                new("Farewell", SiteRole.Changed, outcome, "text"),
            };

            Assert.That(SiteGrader.AllIntact(grades), Is.False, $"AllIntact should be false when a grade is {outcome}.");
        }

        var intact = new List<SiteGrade>
        {
            new("Greet", SiteRole.Changed, SiteOutcome.Correct, null),
            new("Farewell", SiteRole.Changed, SiteOutcome.Correct, null),
        };

        Assert.That(SiteGrader.AllIntact(intact), Is.True);
    }

    [Test]
    public void ReadSidecar_SharedFixture_GivesExpectedGrades()
    {
        var repoRoot = RepoRootLocator.TryFind(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");
        var path = Path.Combine(repoRoot, "RoslynSentinel.Tests.ModelEval", "SiteSidecars", "step-1.sites.json");

        var actual = SiteGrader.ReadSidecar(path);

        Assert.That(actual, Is.EqualTo(ExpectedSharedFixtureGrades()));
    }

    [Test]
    public void WriteSidecar_RoundTripsGrades()
    {
        var directory = Path.Combine(ScratchRoot.Resolve(), "SiteGraderTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "step-1.sites.json");
        var grades = ExpectedSharedFixtureGrades();
        try
        {
            SiteGrader.WriteSidecar(path, grades);

            var actual = SiteGrader.ReadSidecar(path);

            Assert.That(actual, Is.EqualTo(grades));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static List<SiteGrade> ExpectedSharedFixtureGrades() =>
    [
        new("ChangedCorrectSite", SiteRole.Changed, SiteOutcome.Correct, null),
        new("ChangedUnchangedSite", SiteRole.Changed, SiteOutcome.Unchanged, "public static string ChangedUnchangedSite() => \"hello big world and more\";"),
        new("ChangedWrongSite", SiteRole.Changed, SiteOutcome.Wrong, "public static string ChangedWrongSite() => \"howdy big world and more\";"),
        new("ChangedMissingSite", SiteRole.Changed, SiteOutcome.Missing, null),
        new("ControlWrongSite", SiteRole.Control, SiteOutcome.Wrong, "public static string ControlWrongSite() => \"edited control text\";"),
        new("ControlMissingSite", SiteRole.Control, SiteOutcome.Missing, null),
        new("Calc01", SiteRole.Separator, SiteOutcome.Wrong, "public static int Calc01() => 2;"),
    ];

    private static SlipSite GreetSite(string seed) =>
        new("Greet", seed, [new SiteChange("hello", "howdy"), new SiteChange("more", "less")], SiteRole.Changed);

    private static SlipSite FarewellSite() =>
        new("Farewell", FarewellSeed, [new SiteChange("hello", "howdy"), new SiteChange("more", "less")], SiteRole.Changed);

    private static SlipSiteSet GreetFarewellSet() => SetOf(GreetSite(GreetSeed), FarewellSite());

    private static SlipSiteSet SetOf(params SlipSite[] sites) =>
        new("Target.cs", "N", ClassName, string.Empty, sites);

    private static string FileOf(params string[] members) =>
        "namespace N;\r\n\r\npublic static class " + ClassName + "\r\n{\r\n"
        + string.Concat(members.Select(member => "    " + member + "\r\n"))
        + "}\r\n";

    private static SiteOutcome OutcomeOf(IReadOnlyList<SiteGrade> grades, string siteName) =>
        grades.Single(grade => grade.SiteName == siteName).Outcome;
}

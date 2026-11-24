namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Model-free checks of the label: value Report block that follows a DONE:/BLOCKED: line.</summary>
[TestFixture]
public class ScenarioReportTests
{
    [Test]
    public void Parse_LabelValueLines()
    {
        var fields = ScenarioReport.Parse("DONE: finished\nFiles: a.cs\nNotes: all good");

        Assert.That(fields.Count, Is.EqualTo(2));
        Assert.That(fields["Files"], Is.EqualTo("a.cs"));
        Assert.That(fields["notes"], Is.EqualTo("all good"));
    }

    [Test]
    public void Parse_StripsBulletsPipesAndBackticks()
    {
        var fields = ScenarioReport.Parse("DONE: ok\n- `Files`: `a.cs`\n> * `Build`: 0 errors\n| Tests: | 3 passed |");

        Assert.That(fields["Files"], Is.EqualTo("a.cs"));
        Assert.That(fields["Build"], Is.EqualTo("0 errors"));
        Assert.That(fields["Tests"], Is.EqualTo("3 passed"));
    }

    [Test]
    public void Parse_FirstOccurrenceWins()
    {
        var fields = ScenarioReport.Parse("DONE: ok\nFiles: first\nfiles: second");

        Assert.That(fields.Count, Is.EqualTo(1));
        Assert.That(fields["Files"], Is.EqualTo("first"));
    }

    [Test]
    public void Parse_ValueKeepsLaterColons()
    {
        var fields = ScenarioReport.Parse("DONE: ok\nNote: time 12:30: retried");

        Assert.That(fields["Note"], Is.EqualTo("time 12:30: retried"));
    }

    [TestCase((string?)null)]
    [TestCase("")]
    [TestCase("DONE: finished")]
    public void Parse_NoLines_IsEmpty(string? message)
    {
        Assert.That(ScenarioReport.Parse(message), Is.Empty);
    }

    [Test]
    public void Parse_IgnoresTheTokenLine()
    {
        var fields = ScenarioReport.Parse("BLOCKED: stuck: reason\nFiles: a.cs");

        Assert.That(fields.ContainsKey("BLOCKED"), Is.False);
        Assert.That(fields.Count, Is.EqualTo(1));
        Assert.That(fields["Files"], Is.EqualTo("a.cs"));
    }
}

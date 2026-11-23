using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Model-free checks of ScenarioTask.Render: fixed field order, skipped optional fields,
/// required-field validation, line-ending normalisation and the endings repeat. Never calls a model.
/// </summary>
[TestFixture]
public sealed class ScenarioTaskRenderTests
{
    private static ScenarioTask FullTask() => new(
        Goal: "Fix the null check in Parser.Read",
        Files: "RoslynSentinel.Engines/Parser.cs",
        IfFails: "leave the file unchanged and list what you checked",
        DoneWhen: "RunTest passes for ParserTests",
        Conditions: "the Parser class exists",
        Report: "the changed method and the test result");

    private static ScenarioTask TaskWithBlank(string field) => new(
        Goal: field == "Goal" ? "   " : "goal text",
        Files: field == "Files" ? "" : "files text",
        IfFails: field == "IfFails" ? " " : "if-fails text",
        DoneWhen: field == "DoneWhen" ? "\t" : "done text");

    [Test]
    public void Render_AllFields_OneLabelledLineEachInOrder()
    {
        string rendered = FullTask().Render();

        Assert.That(rendered, Is.EqualTo(
            "Goal: Fix the null check in Parser.Read\n" +
            "Files: RoslynSentinel.Engines/Parser.cs\n" +
            "Conditions: the Parser class exists\n" +
            "If a condition fails: leave the file unchanged and list what you checked\n" +
            "Done when: RunTest passes for ParserTests\n" +
            "Report: the changed method and the test result"));
    }

    [Test]
    public void Render_EmptyOptionalFields_AreSkipped()
    {
        var task = new ScenarioTask(
            Goal: "g",
            Files: "f",
            IfFails: "i",
            DoneWhen: "d",
            Conditions: "",
            Report: "   ");

        Assert.That(task.Render(), Is.EqualTo(
            "Goal: g\n" +
            "Files: f\n" +
            "If a condition fails: i\n" +
            "Done when: d"));
    }

    [TestCase("Goal")]
    [TestCase("Files")]
    [TestCase("IfFails")]
    [TestCase("DoneWhen")]
    public void Render_BlankRequiredField_ThrowsNamingTheField(string field)
    {
        ScenarioTask task = TaskWithBlank(field);

        var ex = Assert.Throws<ArgumentException>(() => task.Render());

        Assert.That(ex!.Message, Does.Contain(field));
    }

    [Test]
    public void Render_ValueWithLineBreaks_KeepsThemAsLf()
    {
        var task = new ScenarioTask(
            Goal: "first line\r\nsecond line",
            Files: "f",
            IfFails: "i",
            DoneWhen: "d",
            Report: "r1\r\nr2");

        string rendered = task.Render();

        Assert.That(rendered, Does.Contain("Goal: first line\nsecond line\n"));
        Assert.That(rendered, Does.Not.Contain("\r"));
        Assert.That(rendered, Does.EndWith("Report: r1\nr2"));
    }

    [Test]
    public void Render_LongMultiSentenceGoal_IsNotTruncated()
    {
        string goal = "Rename the helper in Catalog.cs. Update every caller in the Tools project. " +
            "Keep the public signature of Catalog.Find unchanged. Then run the catalog tests and " +
            "report the count of passing tests together with any test that was skipped and why.";
        var task = new ScenarioTask(Goal: goal, Files: "f", IfFails: "i", DoneWhen: "d");

        Assert.That(task.Render(), Does.Contain("Goal: " + goal + "\n"));
    }

    [Test]
    public void Render_TrimsOuterWhitespace()
    {
        var task = new ScenarioTask(
            Goal: "   padded goal  ",
            Files: "\n  padded files \n",
            IfFails: "i",
            DoneWhen: "d");

        Assert.That(task.Render(), Is.EqualTo(
            "Goal: padded goal\n" +
            "Files: padded files\n" +
            "If a condition fails: i\n" +
            "Done when: d"));
    }

    [Test]
    public void Render_RepeatEndingsOff_IsTheDefault()
    {
        ScenarioTask task = FullTask();

        string defaultRender = task.Render();

        Assert.That(defaultRender, Is.EqualTo(task.Render(repeatEndings: false)));
        Assert.That(defaultRender, Does.Not.Contain("## Endings"));
    }

    [Test]
    public void Render_RepeatEndingsOn_AppendsEndingsAfterBlankLine()
    {
        ScenarioTask task = FullTask();
        string endings = AgentSystemPrompts.CodingAgentContractEndings.Replace("\r\n", "\n").Trim();

        string rendered = task.Render(repeatEndings: true);

        Assert.That(rendered, Is.EqualTo(task.Render() + "\n\n" + endings));
    }

    [Test]
    public void Render_KeepsFormatPlaceholder_SoStringFormatFillsIt()
    {
        var task = new ScenarioTask(
            Goal: "Replace the value {0} in Settings.cs",
            Files: "f",
            IfFails: "i",
            DoneWhen: "d");

        string rendered = task.Render();

        Assert.That(rendered, Does.Contain("{0}"));
        Assert.That(string.Format(rendered, "x"), Does.Contain("Replace the value x in Settings.cs"));
    }
}

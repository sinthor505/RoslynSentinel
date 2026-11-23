using NUnit.Framework;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class PlanStalenessReviewCheckTests
{
    private const string WriterPattern = @"(?<![A-Za-z])Writer";
    private const string CallerBPattern = "CallerB";

    // ---- Segment splitting ----

    [Test]
    public void SplitIntoSegments_KeepsTableRowWhole()
    {
        var segments = PlanStalenessReviewScenario.SplitIntoSegments("Intro sentence. Second sentence.\n| 2 | a. b. c |");

        Assert.That(segments, Has.Count.EqualTo(3));
        Assert.That(segments[2], Is.EqualTo("| 2 | a. b. c |"));
    }

    // ---- Writer table rows that must pass ----

    [TestCase("| 2 | `Writer.cs:9` | the `Apply` implementation | Line 9 is a blank line. The `Apply` implementation is now at **line 11** (doc comment at line 10). | **DOES NOT HOLD - new line 11** |")]
    [TestCase("| 2 | `Writer.cs:9` - the `Apply` implementation | **Does not hold.** Line 9 is blank. The `Apply` declaration is at **line 11** (after the class doc comment). |")]
    [TestCase("| 2 | `Writer.cs:9` - the `Apply` implementation | **STALE.** Line 9 is now a blank line. The `Apply` implementation has moved to **line 11** (`public void Apply(Dictionary<string, string> changes)`). It shifted down 2 lines because a class-level doc comment + the `StoredCount` property now sit above it. |")]
    public void MovedAnchorLineIsInContext_AcceptsWriterTableRow(string row)
    {
        Assert.That(Check(row, WriterPattern, 10, 12), Is.True);
    }

    // ---- CallerB table row that must pass ----

    [Test]
    public void MovedAnchorLineIsInContext_AcceptsCallerBTableRow()
    {
        const string row = "| 4 | `CallerB.cs:5` - the first call in `CallerB` | **STALE.** Line 5 is now the class opening brace `{`. The first call in `CallerB` is at **line 8** (`writer.Apply(new Dictionary<string, string> { [\"c.txt\"] = \"three\" });`). It moved down 3 lines as the method signature and body brace were added above the call. |";

        Assert.That(Check(row, CallerBPattern, 7, 9), Is.True);
    }

    // ---- Writer table rows that must fail ----

    [TestCase("| 2 | `Writer.cs:9` | the `Apply` implementation | **Still holds** - Line 9 contains public void Apply |")]
    [TestCase("| 2 | `Writer.cs:9` | the `Apply` implementation | Moved. The `Apply` implementation is now at **line 14**. |")]
    public void MovedAnchorLineIsInContext_RejectsWrongWriterTableRow(string row)
    {
        Assert.That(Check(row, WriterPattern, 10, 12), Is.False);
    }

    // ---- Regression: plain-line form ----

    [Test]
    public void MovedAnchorLineIsInContext_AcceptsPlainLineForm()
    {
        Assert.That(Check("2. Writer.cs:9 moved, Apply is now at Writer.cs:11.", WriterPattern, 10, 12), Is.True);
    }

    private static bool Check(string text, string anchorPattern, int fromLine, int toLine)
    {
        return PlanStalenessReviewScenario.MovedAnchorLineIsInContext(
            PlanStalenessReviewScenario.SplitIntoSegments(text), anchorPattern, fromLine, toLine);
    }
}

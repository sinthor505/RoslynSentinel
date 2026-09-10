using RoslynSentinel.Tools.PlanStepRunner;

namespace RoslynSentinel.Tests.PlanStepRunner;

/// <summary>
/// Covers <see cref="PlanStepFile"/>'s frontmatter parsing — the source of truth for whether a plan
/// step is allowed to change files. These flags are enforced by the runner before it commits, so a
/// parse that silently returns the wrong answer reintroduces exactly the failure the flags exist to
/// prevent: run 20260910-013550-398 let a read-only step perform two later steps' work because the
/// constraint lived only in prose. The unmarked-file case matters just as much as the marked one —
/// ten of the eleven step files have no frontmatter and must keep behaving as before.
/// </summary>
[TestFixture]
public class PlanStepFileTests
{
    private static PlanStepFile Parse(string text) =>
        PlanStepFile.Parse(1, "01-baseline.md", @"C:\plans\01-baseline.md", text);

    [Test]
    public void Parse_ReadOnlyFrontmatter_SetsFlagAndStripsBlockFromBody()
    {
        var step = Parse("---\nreadOnly: true\n---\n# Step 0 — Baseline\n\nRun the tests.\n");

        Assert.Multiple(() =>
        {
            Assert.That(step.ReadOnly, Is.True);
            Assert.That(step.BuildOptional, Is.False, "buildOptional was not specified, so it should stay false.");
            Assert.That(step.Body, Does.StartWith("# Step 0"), "The frontmatter block must not reach the model.");
            Assert.That(step.Body, Does.Not.Contain("readOnly"));
        });
    }

    [Test]
    public void Parse_NoFrontmatter_LeavesFlagsFalseAndBodyByteIdentical()
    {
        // The regression guard for the ten existing step files that were never marked up.
        const string text = "# Step 1.1 — Reshape `BuildResult`\n\n## Task\n\nDo the thing.\n";

        var step = Parse(text);

        Assert.Multiple(() =>
        {
            Assert.That(step.ReadOnly, Is.False);
            Assert.That(step.BuildOptional, Is.False);
            Assert.That(step.Body, Is.EqualTo(text), "An unmarked step's text must pass through untouched.");
        });
    }

    [Test]
    public void Parse_BuildOptionalParsesIndependentlyOfReadOnly()
    {
        var step = Parse("---\nbuildOptional: true\n---\n# Step\n");

        Assert.Multiple(() =>
        {
            Assert.That(step.BuildOptional, Is.True);
            Assert.That(step.ReadOnly, Is.False);
        });
    }

    [Test]
    public void Parse_BothFlagsTogether_AreBothHonoured()
    {
        var step = Parse("---\nreadOnly: true\nbuildOptional: true\n---\n# Step\n");

        Assert.Multiple(() =>
        {
            Assert.That(step.ReadOnly, Is.True);
            Assert.That(step.BuildOptional, Is.True);
        });
    }

    [Test]
    public void Parse_UnclosedFrontmatter_Throws()
    {
        // Must not fall back to "treat it as body" — that would drop the readOnly flag silently and
        // run a read-only step as if it were unmarked, which is the whole failure mode being fixed.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Parse("---\nreadOnly: true\n# Step 0 — Baseline\n\nRun the tests.\n"));

        Assert.That(ex!.Message, Does.Contain("never closed"));
    }

    [Test]
    public void Parse_NonBooleanFlagValue_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("---\nreadOnly: yes\n---\n# Step\n"));

        Assert.That(ex!.Message, Does.Contain("must be true or false"));
    }

    [Test]
    public void Parse_UnknownKeysAndComments_AreIgnored()
    {
        var step = Parse("---\n# a comment\nowner: someone\nreadOnly: true\n---\n# Step\n");

        Assert.That(step.ReadOnly, Is.True, "An unrecognized sibling key must not prevent readOnly from parsing.");
    }

    [Test]
    public void Parse_CrlfFrontmatter_IsRecognized()
    {
        // The step files are CRLF on disk in this repo, so this is the shape that actually ships.
        var step = Parse("---\r\nreadOnly: true\r\n---\r\n# Step 0\r\n");

        Assert.Multiple(() =>
        {
            Assert.That(step.ReadOnly, Is.True);
            Assert.That(step.Body, Does.StartWith("# Step 0"));
        });
    }
}

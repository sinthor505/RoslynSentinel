using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for TestCategoryTextEditor: the pure text edit that turns planned category edits (Add, RemoveStale; class and
/// method level) into new file text. Placement, indentation, EOL preservation, marked-only removal and skip reasons.
/// </summary>
[TestFixture]
public class TestCategoryTextEditorTests
{
    private const string Marker = "// sentinel:auto-category";

    private static string Lines(string eol, params string[] lines) => string.Join(eol, lines);

    // Line numbers (1-based): 1 using, 2 namespace, 3 {, 4 [TestFixture], 5 class, 6 {, 7 [Test], 8 T1, 9 blank, 10 [Test] T2 (shared line).
    private static string BasicSource(string eol = "\n") => Lines(eol,
        "using NUnit.Framework;",
        "namespace Fx",
        "{",
        "    [TestFixture]",
        "    public class Drawer",
        "    {",
        "        [Test]",
        "        public void T1() { }",
        "",
        "        [Test] public void T2() { }",
        "    }",
        "}",
        "");

    private static PlannedCategoryEdit AddMethod(int line, string method, string category) => new(
        TestCategoryEditKind.Add, TestCategoryLevel.Method, "Fixtures", @"C:\fake\Tests.cs", line,
        "Fx.Drawer", method, null, category, TestCategoryTaggingEngine.FormatAttribute(TestCategoryFramework.NUnit, category));

    private static PlannedCategoryEdit AddClass(int line, string category) => new(
        TestCategoryEditKind.Add, TestCategoryLevel.Class, "Fixtures", @"C:\fake\Tests.cs", line,
        "Fx.Drawer", null, null, category, TestCategoryTaggingEngine.FormatAttribute(TestCategoryFramework.NUnit, category));

    private static PlannedCategoryEdit Remove(TestCategoryLevel level, int line, string method, string category, string? text = null) => new(
        TestCategoryEditKind.RemoveStale, level, "Fixtures", @"C:\fake\Tests.cs", line,
        "Fx.Drawer", method, null, category, text ?? $"[Category(\"{category}\")] {Marker}", StaleCategoryReason.NoLongerReferenced);

    [Test]
    [Description("A method-level add goes on its own line directly above the method, after the existing attribute, indented like the method")]
    public void AddMethodLevel_InsertsOwnLineAfterExistingAttributeWithMatchingIndent()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddMethod(8, "T1", "Alpha")]);

        Assert.That(result.MethodLevelAdded, Is.EqualTo(1));
        Assert.That(result.Skipped, Is.Empty);
        Assert.That(result.NewText, Is.EqualTo(Lines("\n",
            "using NUnit.Framework;",
            "namespace Fx",
            "{",
            "    [TestFixture]",
            "    public class Drawer",
            "    {",
            "        [Test]",
            "        [Category(\"Alpha\")] " + Marker,
            "        public void T1() { }",
            "",
            "        [Test] public void T2() { }",
            "    }",
            "}",
            "")));
    }

    [Test]
    [Description("A class-level add goes directly above the class declaration, after the existing [TestFixture], with the class's indent")]
    public void AddClassLevel_InsertsAfterExistingAttributeAboveClass()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddClass(5, "Alpha")]);

        Assert.That(result.ClassLevelAdded, Is.EqualTo(1));
        var lines = result.NewText.Split('\n');
        Assert.That(lines[3], Is.EqualTo("    [TestFixture]"));
        Assert.That(lines[4], Is.EqualTo("    [Category(\"Alpha\")] " + Marker));
        Assert.That(lines[5], Is.EqualTo("    public class Drawer"));
    }

    [Test]
    [Description("When the attribute shares its line with the declaration the new line goes above that whole line, leaving the list unbroken")]
    public void AddMethodLevel_WhenAttributeSharesLineWithMethod_InsertsAboveThatLine()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddMethod(10, "T2", "Beta")]);

        var lines = result.NewText.Split('\n');
        Assert.That(lines[9], Is.EqualTo("        [Category(\"Beta\")] " + Marker));
        Assert.That(lines[10], Is.EqualTo("        [Test] public void T2() { }"));
    }

    [Test]
    [Description("Several adds on the same method land in category order above it")]
    public void AddMethodLevel_MultipleCategories_AreInsertedInOrder()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddMethod(8, "T1", "Beta"), AddMethod(8, "T1", "Alpha")]);

        var lines = result.NewText.Split('\n');
        Assert.That(result.MethodLevelAdded, Is.EqualTo(2));
        Assert.That(lines[7], Is.EqualTo("        [Category(\"Alpha\")] " + Marker));
        Assert.That(lines[8], Is.EqualTo("        [Category(\"Beta\")] " + Marker));
        Assert.That(lines[9], Is.EqualTo("        public void T1() { }"));
    }

    [Test]
    [Description("A method without attributes gets the line directly above its declaration")]
    public void AddMethodLevel_NoExistingAttribute_InsertsAboveDeclaration()
    {
        var source = Lines("\n",
            "using NUnit.Framework;",
            "public class C",
            "{",
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source, [AddMethod(4, "T1", "Alpha")]);

        Assert.That(result.NewText.Split('\n')[3], Is.EqualTo("    [Category(\"Alpha\")] " + Marker));
        Assert.That(result.NewText.Split('\n')[4], Is.EqualTo("    public void T1() { }"));
    }

    [Test]
    [Description("A doc comment above the method stays above the new attribute line")]
    public void AddMethodLevel_KeepsDocCommentAboveTheAttributes()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    /// <summary>Doc.</summary>",
            "    [Test]",
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source, [AddMethod(5, "T1", "Alpha")]);

        var lines = result.NewText.Split('\n');
        Assert.That(lines[2], Is.EqualTo("    /// <summary>Doc.</summary>"));
        Assert.That(lines[3], Is.EqualTo("    [Test]"));
        Assert.That(lines[4], Is.EqualTo("    [Category(\"Alpha\")] " + Marker));
    }

    [TestCase("\r\n")]
    [TestCase("\n")]
    [Description("Inserted lines use the file's own line ending, so the EOL style never changes")]
    public void Add_PreservesLineEnding(string eol)
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(eol), [AddMethod(8, "T1", "Alpha")]);

        var withoutCrlf = result.NewText.Replace("\r\n", string.Empty);
        if (eol == "\r\n")
        {
            Assert.That(withoutCrlf, Does.Not.Contain("\n"), "no bare LF may appear in a CRLF file");
        }
        else
        {
            Assert.That(result.NewText, Does.Not.Contain("\r"));
        }

        Assert.That(result.NewText, Does.Contain("[Category(\"Alpha\")] " + Marker + eol));
    }

    [Test]
    [Description("Tabs used for indentation are reused for the inserted line")]
    public void Add_ReusesTabIndentation()
    {
        var source = "public class C\n{\n\t[Test]\n\tpublic void T1() { }\n}\n";

        var result = TestCategoryTextEditor.Apply(source, [AddMethod(4, "T1", "Alpha")]);

        Assert.That(result.NewText, Does.Contain("\t[Category(\"Alpha\")] " + Marker + "\n\tpublic void T1()"));
    }

    [Test]
    [Description("A stale marked attribute's whole line is removed, leaving no blank line, and the other attributes are untouched")]
    public void RemoveStale_DeletesWholeMarkedLineOnly()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    [Test]",
            "    [Category(\"Old\")] " + Marker,
            "    [Category(\"Hand\")]",
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source, [Remove(TestCategoryLevel.Method, 4, "T1", "Old")]);

        Assert.That(result.RemovedStale, Is.EqualTo(1));
        Assert.That(result.NewText, Is.EqualTo(Lines("\n",
            "public class C",
            "{",
            "    [Test]",
            "    [Category(\"Hand\")]",
            "    public void T1() { }",
            "}",
            "")));
    }

    [Test]
    [Description("A hand-written (unmarked) attribute is never removed: a removal pointing at it is skipped with a reason")]
    public void RemoveStale_OnUnmarkedAttribute_IsSkippedAndTextUnchanged()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    [Test]",
            "    [Category(\"Hand\")]",
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source, [Remove(TestCategoryLevel.Method, 5, "T1", "Hand", "[Category(\"Hand\")]")]);

        Assert.That(result.NewText, Is.EqualTo(source));
        Assert.That(result.RemovedStale, Is.EqualTo(0));
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        Assert.That(result.Skipped[0].Reason, Does.Contain("No marked"));
    }

    [Test]
    [Description("A marked list that also holds other attributes is not rewritten: it is skipped with a reason and the text is unchanged")]
    public void RemoveStale_MarkedListSharedWithOtherAttributes_IsSkippedWithReason()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    [Test, Category(\"Old\")] " + Marker,
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source,
            [Remove(TestCategoryLevel.Method, 3, "T1", "Old", "[Test, Category(\"Old\")] " + Marker)]);

        Assert.That(result.NewText, Is.EqualTo(source));
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        Assert.That(result.Skipped[0].Reason, Does.Contain("other attributes"));
    }

    [Test]
    [Description("A marked list sharing its line with another attribute list is skipped, never half-deleted")]
    public void RemoveStale_MarkedListSharingLineWithCode_IsSkippedWithReason()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    [Test] [Category(\"Old\")] " + Marker,
            "    public void T1() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source,
            [Remove(TestCategoryLevel.Method, 3, "T1", "Old", "[Category(\"Old\")] " + Marker)]);

        Assert.That(result.NewText, Is.EqualTo(source));
        Assert.That(result.Skipped.Single().Reason, Does.Contain("shares its line"));
    }

    [Test]
    [Description("A plan that no longer matches the file (drift) is skipped with a re-run hint instead of mis-applied")]
    public void StalePlan_NoDeclarationAtLine_IsSkipped()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddMethod(99, "T1", "Alpha")]);

        Assert.That(result.NewText, Is.EqualTo(BasicSource()));
        Assert.That(result.Skipped.Single().Reason, Does.Contain("Re-run the dry run"));
    }

    [Test]
    [Description("Removal and add in one file land together: line numbers of the plan stay valid because edits apply in one pass")]
    public void MixedRemoveAndAdd_AppliesInOnePass()
    {
        var source = Lines("\n",
            "public class C",
            "{",
            "    [Test]",
            "    [Category(\"Old\")] " + Marker,
            "    public void T1() { }",
            "",
            "    [Test]",
            "    public void T2() { }",
            "}",
            "");

        var result = TestCategoryTextEditor.Apply(source,
        [
            Remove(TestCategoryLevel.Method, 4, "T1", "Old"),
            AddMethod(5, "T1", "New"),
            AddMethod(8, "T2", "Other"),
        ]);

        Assert.That(result.RemovedStale, Is.EqualTo(1));
        Assert.That(result.MethodLevelAdded, Is.EqualTo(2));
        Assert.That(result.NewText, Is.EqualTo(Lines("\n",
            "public class C",
            "{",
            "    [Test]",
            "    [Category(\"New\")] " + Marker,
            "    public void T1() { }",
            "",
            "    [Test]",
            "    [Category(\"Other\")] " + Marker,
            "    public void T2() { }",
            "}",
            "")));
    }

    [Test]
    [Description("The editor's own output carries marked attributes the planner treats as existing, so a re-plan yields no Add (idempotency)")]
    public void AddedAttribute_UsesTheEngineFormat()
    {
        var result = TestCategoryTextEditor.Apply(BasicSource(), [AddMethod(8, "T1", "Alpha")]);

        Assert.That(result.NewText, Does.Contain(TestCategoryTaggingEngine.FormatAttribute(TestCategoryFramework.NUnit, "Alpha")));
    }

    [TestCase("a\r\nb\r\nc\n", "\r\n")]
    [TestCase("a\nb\nc\r\n", "\n")]
    [TestCase("a\nb", "\n")]
    [Description("DetectEol picks the dominant line ending")]
    public void DetectEol_PicksDominantEnding(string text, string expected) =>
        Assert.That(TestCategoryTextEditor.DetectEol(text), Is.EqualTo(expected));
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Regression tests for the MoveMember whole-file reformatting blocker.
/// See docs/current/blockers/blocking_error_movemember_reformats_entire_source_and_caller_files.md.
/// 
/// These tests verify that MoveMember preserves untouched content exactly (blank lines,
/// multi-line formatting, line endings, final newlines) and only changes the intended spans
/// (removed member, inserted member, rewritten call sites, updated crefs).
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
[Category("MoveMemberResult")] // sentinel:auto-category
public class MoveMemberPreservesUntouchedTextTests
{
    [Test]
    public async Task StaticMethodToExistingClass_WithCRLF_PreservesUntouchedContent()
    {
        // Source file with CRLF, multi-line record, wrapped signature, ternary, doc comment with cref
        const string sourceWithCRLF = "using System;\r\n" +
            "\r\n" +
            "namespace Example;\r\n" +
            "\r\n" +
            "public record CallerInfo(\r\n" +
            "    int Line,\r\n" +
            "    string Name);\r\n" +
            "\r\n" +
            "public static class A\r\n" +
            "{\r\n" +
            "    /// <summary>\r\n" +
            "    /// Normalizes a type name. See also <see cref=\"Norm\"/> for direct calls.\r\n" +
            "    /// </summary>\r\n" +
            "    public static string Norm(\r\n" +
            "        string input,\r\n" +
            "        bool strict = false)\r\n" +
            "    {\r\n" +
            "        var normalized = strict ? input.ToLower() : input.Trim();\r\n" +
            "        return normalized ?? \"default\";\r\n" +
            "    }\r\n" +
            "\r\n" +
            "    /// <summary>Other method references Norm.</summary>\r\n" +
            "    /// <remarks>See also <see cref=\"A.Norm\"/> for the moved member.</remarks>\r\n" +
            "    public static int Process(string x) => x.Length;\r\n" +
            "}\r\n";

        const string targetWithCRLF = "namespace Example;\r\n" +
            "\r\n" +
            "public record TargetInfo(\r\n" +
            "    string Result,\r\n" +
            "    int Value);\r\n" +
            "\r\n" +
            "public static class B\r\n" +
            "{\r\n" +
            "    /// <summary>Existing member on target.</summary>\r\n" +
            "    public static int Count() => 0;\r\n" +
            "\r\n" +
            "    /// <summary>Another existing member.</summary>\r\n" +
            "    public static string Describe() => \"target\";\r\n" +
            "}\r\n";

        const string callerWithCRLF = "using Example;\r\n" +
            "\r\n" +
            "public class Caller\r\n" +
            "{\r\n" +
            "    /// <summary>First method with wrapped call.</summary>\r\n" +
            "    public void Run()\r\n" +
            "    {\r\n" +
            "        var result = A.Norm(\r\n" +
            "            \"test\",\r\n" +
            "            strict: true);\r\n" +
            "    }\r\n" +
            "\r\n" +
            "    /// <summary>Another method, also uses the moved member.</summary>\r\n" +
            "    /// <remarks>See also <see cref=\"A.Norm\"/> when available.</remarks>\r\n" +
            "    public void Process()\r\n" +
            "    {\r\n" +
            "        var x = new CallInfo(A.Norm(\"data\", false));\r\n" +
            "    }\r\n" +
            "\r\n" +
            "    public record CallInfo(string Value);\r\n" +
            "}\r\n";

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", sourceWithCRLF),
            ("Target.cs", targetWithCRLF),
            ("Caller.cs", callerWithCRLF));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Norm" },
            "B",
            workspace.PathOf("Target.cs"),
            CancellationToken.None);

        Assert.That(result.Changes, Is.Not.Null, "Changes should be present");
        
        var sourceKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Source.cs", StringComparison.OrdinalIgnoreCase));
        var targetKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Target.cs", StringComparison.OrdinalIgnoreCase));
        var callerKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase));

        var sourceNewText = result.Changes[sourceKey];
        var targetNewText = result.Changes[targetKey];
        var callerNewText = result.Changes[callerKey];

        // Verify CRLF is preserved in all files
        Assert.That(sourceNewText, Does.Contain("\r\n"), "Source should preserve CRLF");
        Assert.That(targetNewText, Does.Contain("\r\n"), "Target should preserve CRLF");
        Assert.That(callerNewText, Does.Contain("\r\n"), "Caller should preserve CRLF");

        // Verify final newlines are preserved
        Assert.That(sourceNewText.EndsWith("\n"), "Source should keep final newline");
        Assert.That(targetNewText.EndsWith("\n"), "Target should keep final newline");
        Assert.That(callerNewText.EndsWith("\n"), "Caller should keep final newline");

        // Verify source file: only Norm member (with its doc comment) should be removed
        AssertOnlySpansChanged(sourceWithCRLF, sourceNewText, new[]
        {
            new ContentSpan(
                "    /// <summary>\r\n    /// Normalizes a type name. See also <see cref=\"Norm\"/> for direct calls.\r\n    /// </summary>\r\n    public static string Norm(\r\n        string input,\r\n        bool strict = false)\r\n    {\r\n        var normalized = strict ? input.ToLower() : input.Trim();\r\n        return normalized ?? \"default\";\r\n    }\r\n\r\n", string.Empty),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"B.Norm\"/>")
        });

        // Verify target file: only the inserted Norm member should be added
        AssertOnlySpansChanged(targetWithCRLF, targetNewText, new[]
        {
            new ContentSpan(
                "    public static string Describe() => \"target\";\r\n",
                "    public static string Describe() => \"target\";\r\n\r\n    /// <summary>\r\n    /// Normalizes a type name. See also <see cref=\"Norm\"/> for direct calls.\r\n    /// </summary>\r\n    public static string Norm(\r\n        string input,\r\n        bool strict = false)\r\n    {\r\n        var normalized = strict ? input.ToLower() : input.Trim();\r\n        return normalized ?? \"default\";\r\n    }\r\n")
        });

        // Verify caller file: only A.Norm references should change to B.Norm
        AssertOnlySpansChanged(callerWithCRLF, callerNewText, new[]
        {
            new ContentSpan("A.Norm(", "B.Norm("),
            new ContentSpan("A.Norm(", "B.Norm("),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"B.Norm\"/>")
        });

        // Verify crefs were updated
        Assert.That(callerNewText, Does.Contain("cref=\"B.Norm\""),
            "Caller's cref should be updated from A.Norm to B.Norm");
        Assert.That(sourceNewText, Does.Contain("cref=\"B.Norm\""),
            "Source's remaining cref on Process method should be updated to B.Norm");

        // Verify no cref attribute spacing corruption (no 'cref = ')
        Assert.That(sourceNewText, Does.Not.Contain("cref = "),
            "cref attributes must not have spaces around equals");
        Assert.That(callerNewText, Does.Not.Contain("cref = "),
            "cref attributes must not have spaces around equals");
    }

    [Test]
    public async Task StaticMethodToExistingClass_WithLF_PreservesLineEndings()
    {
        const string sourceWithLF = "using System;\n" +
            "\n" +
            "namespace Example;\n" +
            "\n" +
            "public record CallerInfo(\n" +
            "    int Line,\n" +
            "    string Name);\n" +
            "\n" +
            "public static class A\n" +
            "{\n" +
            "    public static string Norm(string input) => input.ToLower();\n" +
            "\n" +
            "    /// <summary>Other method with cref.</summary>\n" +
            "    /// <remarks>See <see cref=\"A.Norm\"/> elsewhere.</remarks>\n" +
            "    public static int Process(string x) => x.Length;\n" +
            "}\n";

        const string targetWithLF = "namespace Example;\n" +
            "\n" +
            "public record TargetInfo(\n" +
            "    string Result);\n" +
            "\n" +
            "public static class B\n" +
            "{\n" +
            "    public static int Count() => 0;\n" +
            "\n" +
            "    public static string Describe() => \"target\";\n" +
            "}\n";

        const string callerWithLF = "using Example;\n" +
            "\n" +
            "public class Caller\n" +
            "{\n" +
            "    /// <summary>Method with wrapped call.</summary>\n" +
            "    /// <remarks>Uses <see cref=\"A.Norm\"/> for normalization.</remarks>\n" +
            "    public void Run()\n" +
            "    {\n" +
            "        var result = A.Norm(\n" +
            "            \"test\");\n" +
            "    }\n" +
            "\n" +
            "    public void Other() => Console.WriteLine(A.Norm(\"x\"));\n" +
            "}\n";

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", sourceWithLF),
            ("Target.cs", targetWithLF),
            ("Caller.cs", callerWithLF));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Norm" },
            "B",
            workspace.PathOf("Target.cs"),
            CancellationToken.None);

        var sourceKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Source.cs", StringComparison.OrdinalIgnoreCase));
        var targetKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Target.cs", StringComparison.OrdinalIgnoreCase));
        var callerKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase));

        var sourceNewText = result.Changes[sourceKey];
        var targetNewText = result.Changes[targetKey];
        var callerNewText = result.Changes[callerKey];

        // Verify LF is preserved (not converted to CRLF)
        Assert.That(sourceNewText, Does.Not.Contain("\r\n"),
            "Source should preserve LF and not add CRLF - " +
            "the whole-tree normalizer changes LF to the default EOL, which is the bug we are catching");
        Assert.That(targetNewText, Does.Not.Contain("\r\n"),
            "Target should preserve LF and not add CRLF");
        Assert.That(callerNewText, Does.Not.Contain("\r\n"),
            "Caller should preserve LF and not add CRLF");

        // Verify final newlines are preserved
        Assert.That(sourceNewText.EndsWith("\n"), "Source should keep final newline");
        Assert.That(targetNewText.EndsWith("\n"), "Target should keep final newline");
        Assert.That(callerNewText.EndsWith("\n"), "Caller should keep final newline");

        // Verify source file: only Norm member should be removed, untouched content byte-identical
        AssertOnlySpansChanged(sourceWithLF, sourceNewText, new[]
        {
            new ContentSpan("    public static string Norm(string input) => input.ToLower();\n\n", string.Empty),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"B.Norm\"/>")
        });

        // Verify target file: only inserted Norm member should be added
        AssertOnlySpansChanged(targetWithLF, targetNewText, new[]
        {
            new ContentSpan(
                "    public static string Describe() => \"target\";\n",
                "    public static string Describe() => \"target\";\n\n    public static string Norm(string input) => input.ToLower();\n")
        });

        // Verify caller file: only A.Norm references should change
        AssertOnlySpansChanged(callerWithLF, callerNewText, new[]
        {
            new ContentSpan("A.Norm(", "B.Norm("),
            new ContentSpan("A.Norm(", "B.Norm("),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"B.Norm\"/>")
        });

        // Verify crefs were updated without spacing corruption
        Assert.That(callerNewText, Does.Contain("cref=\"B.Norm\""),
            "Caller's cref should be updated to B.Norm with no spaces");
        Assert.That(sourceNewText, Does.Contain("cref=\"B.Norm\""),
            "Source's cref should be updated to B.Norm");
        Assert.That(sourceNewText, Does.Not.Contain("cref = "),
            "cref attributes must not have spaces around equals");
        Assert.That(callerNewText, Does.Not.Contain("cref = "),
            "cref attributes must not have spaces around equals");
    }

    [Test]
    public async Task StaticMethodToExistingClass_WithoutFinalNewline_PreservesMissing()
    {
        // Source file WITHOUT final newline - guards the property that the normalizer
        // unconditionally adds a final newline, which is the bug we are catching.
        const string sourceNoFinalNewline = "namespace Example;\n" +
            "\n" +
            "public static class A\n" +
            "{\n" +
            "    public static string Norm(string x) => x;\n" +
            "\n" +
            "    public static int Other() => 1;\n" +
            "}"; // No final newline

        const string targetWithNewline = "namespace Example;\n" +
            "\n" +
            "public static class B\n" +
            "{\n" +
            "    public static int Count() => 0;\n" +
            "}\n";

        const string callerWithNewline = "using Example;\n" +
            "\n" +
            "class Caller\n" +
            "{\n" +
            "    void Run() => A.Norm(\"x\");\n" +
            "}\n";

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", sourceNoFinalNewline),
            ("Target.cs", targetWithNewline),
            ("Caller.cs", callerWithNewline));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Norm" },
            "B",
            workspace.PathOf("Target.cs"),
            CancellationToken.None);

        var sourceKey = result.Changes.Keys.Single(k => k.ToString().EndsWith("Source.cs", StringComparison.OrdinalIgnoreCase));
        var sourceNewText = result.Changes[sourceKey];

        // Verify source file WITHOUT final newline stays without one
        Assert.That(sourceNewText.EndsWith("\n"), Is.False,
            "Source must NOT gain a final newline if it didn't have one - " +
            "the whole-tree normalizer adds one, which is the bug we are catching");
    }

    [Test]
    public async Task MethodToBaseType_WithCRLF_PreservesUntouchedContent()
    {
        const string eol = "\r\n";
        var source = Lines(eol,
            "using System;",
            "",
            "namespace Example;",
            "",
            "public record CallerInfo(",
            "    int Line,",
            "    string Name);",
            "",
            "public class Derived : Base",
            "{",
            "    /// <summary>",
            "    /// Normalizes a type name.",
            "    /// </summary>",
            "    public string Normalize(",
            "        string input,",
            "        bool strict = false)",
            "    {",
            "        var normalized = strict ? input.ToLower() : input.Trim();",
            "        return normalized ?? \"default\";",
            "    }",
            "",
            "    /// <summary>Other method, see <see cref=\"Derived.Normalize\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var baseFile = Lines(eol,
            "namespace Example;",
            "",
            "public record BaseInfo(",
            "    string Result,",
            "    int Value);",
            "",
            "public class Base",
            "{",
            "    /// <summary>Existing member on base.</summary>",
            "    public int Count() => 0;",
            "",
            "    /// <summary>Another existing member.</summary>",
            "    public string Describe() => \"base\";",
            "}");
        var caller = Lines(eol,
            "using Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Wrapped call.</summary>",
            "    /// <remarks>See <see cref=\"Derived.Normalize\"/>.</remarks>",
            "    public void Run()",
            "    {",
            "        var d = new Derived();",
            "        var result = d.Normalize(",
            "            \"test\",",
            "            strict: true);",
            "    }",
            "}");
        var movedMember = Lines(eol,
            "    /// <summary>",
            "    /// Normalizes a type name.",
            "    /// </summary>",
            "    public string Normalize(",
            "        string input,",
            "        bool strict = false)",
            "    {",
            "        var normalized = strict ? input.ToLower() : input.Trim();",
            "        return normalized ?? \"default\";",
            "    }");
        var pulledUpMember = movedMember.Replace("public string Normalize(", "public virtual string Normalize(");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Base.cs", baseFile),
            ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "Derived",
            new[] { "Normalize" },
            "Base",
            workspace.PathOf("Base.cs"),
            CancellationToken.None);

        var sourceNewText = result.Changes[result.Changes.Keys.Single(k => k.ToString().EndsWith("Source.cs", StringComparison.OrdinalIgnoreCase))];
        var baseNewText = result.Changes[result.Changes.Keys.Single(k => k.ToString().EndsWith("Base.cs", StringComparison.OrdinalIgnoreCase))];

        // Call sites keep working through inheritance, so the caller must not be rewritten at all.
        Assert.That(result.Changes.Keys.Any(k => k.ToString().EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase)), Is.False,
            "Pulling a member up to a base type must not touch caller files");

        Assert.That(sourceNewText, Does.Contain(eol));
        Assert.That(baseNewText, Does.Contain(eol));
        Assert.That(sourceNewText.Replace(eol, string.Empty), Does.Not.Contain("\n"), "Source must not mix in bare LF");
        Assert.That(baseNewText.Replace(eol, string.Empty), Does.Not.Contain("\n"), "Base must not mix in bare LF");
        Assert.That(sourceNewText.EndsWith("\n"), "Source should keep final newline");
        Assert.That(baseNewText.EndsWith("\n"), "Base should keep final newline");

        // Source: only the member (with its doc comment and one blank line) is removed; the cref outside it is untouched.
        AssertOnlySpansChanged(source, sourceNewText, new[]
        {
            new ContentSpan(movedMember + eol, string.Empty)
        });
        Assert.That(sourceNewText, Does.Contain("<see cref=\"Derived.Normalize\"/>"));
        Assert.That(sourceNewText, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");

        // Base: only the pulled-up member (now virtual) is inserted after the last existing member.
        AssertOnlySpansChanged(baseFile, baseNewText, new[]
        {
            new ContentSpan(
                "    public string Describe() => \"base\";" + eol,
                "    public string Describe() => \"base\";" + eol + eol + pulledUpMember)
        });
    }

    [Test]
    public async Task StaticMethodToNewClass_WithCRLF_PreservesUntouchedContentAndWritesCleanNewFile()
    {
        const string eol = "\r\n";
        var source = Lines(eol,
            "using System;",
            "",
            "namespace Example;",
            "",
            "public record CallerInfo(",
            "    int Line,",
            "    string Name);",
            "",
            "public static class A",
            "{",
            "    /// <summary>",
            "    /// Normalizes a type name.",
            "    /// </summary>",
            "    public static string Norm(",
            "        string input,",
            "        bool strict = false)",
            "    {",
            "        var normalized = strict ? input.ToLower() : input.Trim();",
            "        return normalized ?? \"default\";",
            "    }",
            "",
            "    /// <summary>Other method.</summary>",
            "    /// <remarks>See <see cref=\"A.Norm\"/> for the moved member.</remarks>",
            "    public static int Process(string x) => x.Length;",
            "}");
        var caller = Lines(eol,
            "using Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>First method with wrapped call.</summary>",
            "    public void Run()",
            "    {",
            "        var result = A.Norm(",
            "            \"test\",",
            "            strict: true);",
            "    }",
            "",
            "    /// <summary>Second method.</summary>",
            "    /// <remarks>See also <see cref=\"A.Norm\"/> when available.</remarks>",
            "    public void Process()",
            "    {",
            "        var x = A.Norm(\"data\", false);",
            "    }",
            "}");
        var movedMember = Lines(eol,
            "    /// <summary>",
            "    /// Normalizes a type name.",
            "    /// </summary>",
            "    public static string Norm(",
            "        string input,",
            "        bool strict = false)",
            "    {",
            "        var normalized = strict ? input.ToLower() : input.Trim();",
            "        return normalized ?? \"default\";",
            "    }");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Norm" },
            "NewHelper",
            null,
            CancellationToken.None);

        Assert.That(result.Changes.Count, Is.EqualTo(3), "Expected the source, the caller and the new file");
        var sourceNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Source.cs")];
        var callerNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Caller.cs")];
        var newFileText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "NewHelper.cs")];

        foreach (var text in new[] { sourceNewText, callerNewText, newFileText })
        {
            Assert.That(text, Does.Contain(eol));
            Assert.That(text.Replace(eol, string.Empty), Does.Not.Contain("\n"), "No bare LF may be mixed into a CRLF file");
            Assert.That(text.EndsWith(eol), "Every file should end with a final newline");
        }

        // New file: written cleanly, matching the source file's usings, namespace style and line endings.
        var expectedNewFile = Lines(eol, "using System;", "", "namespace Example;", "", "public class NewHelper", "{") + movedMember + Lines(eol, "}");
        Assert.That(newFileText, Is.EqualTo(expectedNewFile));

        // Source: only the member (with its doc comment and one blank line) is removed and the cref outside it retargeted.
        AssertOnlySpansChanged(source, sourceNewText, new[]
        {
            new ContentSpan(movedMember + eol, string.Empty),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"NewHelper.Norm\"/>")
        });

        // Caller: only the qualifiers change.
        AssertOnlySpansChanged(caller, callerNewText, new[]
        {
            new ContentSpan("A.Norm(", "NewHelper.Norm("),
            new ContentSpan("A.Norm(", "NewHelper.Norm("),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"NewHelper.Norm\"/>")
        });
        Assert.That(sourceNewText, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
        Assert.That(callerNewText, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
    }

    [Test]
    public async Task StaticMethodToNewClass_WithLFAndBlockNamespace_PreservesUntouchedContentAndWritesCleanNewFile()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "using System;",
            "",
            "namespace Example",
            "{",
            "    public record CallerInfo(",
            "        int Line,",
            "        string Name);",
            "",
            "    public static class A",
            "    {",
            "        public static string Norm(string input) => input.ToLower();",
            "",
            "        /// <summary>Other method with cref.</summary>",
            "        /// <remarks>See <see cref=\"A.Norm\"/> elsewhere.</remarks>",
            "        public static int Process(string x) => x.Length;",
            "    }",
            "}");
        var caller = Lines(eol,
            "using Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Method with wrapped call.</summary>",
            "    /// <remarks>Uses <see cref=\"A.Norm\"/> for normalization.</remarks>",
            "    public void Run()",
            "    {",
            "        var result = A.Norm(",
            "            \"test\");",
            "    }",
            "}");
        var movedMember = Lines(eol, "        public static string Norm(string input) => input.ToLower();");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Norm" },
            "NewHelper",
            null,
            CancellationToken.None);

        Assert.That(result.Changes.Count, Is.EqualTo(3), "Expected the source, the caller and the new file");
        var sourceNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Source.cs")];
        var callerNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Caller.cs")];
        var newFileText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "NewHelper.cs")];

        foreach (var text in new[] { sourceNewText, callerNewText, newFileText })
        {
            Assert.That(text, Does.Not.Contain("\r"), "LF files must stay LF");
            Assert.That(text.EndsWith(eol), "Every file should end with a final newline");
        }

        var expectedNewFile = Lines(eol, "using System;", "", "namespace Example", "{", "    public class NewHelper", "    {") + movedMember + Lines(eol, "    }", "}");
        Assert.That(newFileText, Is.EqualTo(expectedNewFile));

        AssertOnlySpansChanged(source, sourceNewText, new[]
        {
            new ContentSpan(movedMember + eol, string.Empty),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"NewHelper.Norm\"/>")
        });

        AssertOnlySpansChanged(caller, callerNewText, new[]
        {
            new ContentSpan("A.Norm(", "NewHelper.Norm("),
            new ContentSpan("<see cref=\"A.Norm\"/>", "<see cref=\"NewHelper.Norm\"/>")
        });
        Assert.That(sourceNewText, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
        Assert.That(callerNewText, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
    }

    [Test]
    public async Task InstanceMethodToExistingClass_WithCRLF_PreservesUntouchedContent()
    {
        const string eol = "\r\n";
        var (source, target, caller, movedMember) = InstanceMoveCrlfSources();

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target),
            ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Count" },
            "B",
            null,
            CancellationToken.None);

        Assert.That(result.SkippedCallSites, Is.Empty, "The single B field makes the call site resolvable automatically");
        Assert.That(result.Changes.Count, Is.EqualTo(3), "Expected the source, the target and the caller");
        var sourceNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Source.cs")];
        var targetNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Target.cs")];
        var callerNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Caller.cs")];

        foreach (var text in new[] { sourceNewText, targetNewText, callerNewText })
        {
            Assert.That(text, Does.Contain(eol));
            Assert.That(text.Replace(eol, string.Empty), Does.Not.Contain("\n"), "No bare LF may be mixed into a CRLF file");
            Assert.That(text.EndsWith(eol), "Every file should keep its final newline");
            Assert.That(text, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
        }

        // Source: only the member (with its doc comment and one blank line) is removed; the cref outside it is untouched.
        AssertOnlySpansChanged(source, sourceNewText, new[]
        {
            new ContentSpan(movedMember + eol, string.Empty)
        });

        // Target: only the moved member is appended after the last existing member.
        AssertOnlySpansChanged(target, targetNewText, new[]
        {
            new ContentSpan(
                "    public string Describe() => \"target\";" + eol,
                "    public string Describe() => \"target\";" + eol + eol + movedMember)
        });

        // Caller: only the receiver expression of the call site changes.
        AssertOnlySpansChanged(caller, callerNewText, new[]
        {
            new ContentSpan("a.Count(", "_b.Count(")
        });
    }

    [Test]
    public async Task InstanceMethodToClassInSameFile_WithLFAndBlockNamespace_PreservesUntouchedContent()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "using System;",
            "",
            "namespace Example",
            "{",
            "    public record CallerInfo(",
            "        int Line,",
            "        string Name);",
            "",
            "    public class A",
            "    {",
            "        public int Count(string input) => input.Length;",
            "",
            "        /// <summary>Other method, see <see cref=\"A.Count\"/>.</summary>",
            "        public int Process(string x) => x.Length;",
            "    }",
            "",
            "    public class B",
            "    {",
            "        public int Size() => 0;",
            "    }",
            "}");
        var caller = Lines(eol,
            "using Example;",
            "",
            "public class Caller",
            "{",
            "    private readonly B _b = new B();",
            "",
            "    /// <summary>Uses <see cref=\"A.Count\"/>.</summary>",
            "    public int Run()",
            "    {",
            "        var a = new A();",
            "        return a.Count(",
            "            \"test\");",
            "    }",
            "}");
        var movedMember = Lines(eol, "        public int Count(string input) => input.Length;");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Caller.cs", caller));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            new[] { "Count" },
            "B",
            null,
            CancellationToken.None);

        Assert.That(result.SkippedCallSites, Is.Empty, "The single B field makes the call site resolvable automatically");
        Assert.That(result.Changes.Count, Is.EqualTo(2), "Expected the source file (holding both classes) and the caller");
        var sourceNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Source.cs")];
        var callerNewText = result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Caller.cs")];

        foreach (var text in new[] { sourceNewText, callerNewText })
        {
            Assert.That(text, Does.Not.Contain("\r"), "LF files must stay LF");
            Assert.That(text.EndsWith(eol), "Every file should keep its final newline");
            Assert.That(text, Does.Not.Contain("cref = "), "cref attributes must not gain spaces around equals");
        }

        // Source file: the member is cut out of A and appended to B, nothing else moves.
        AssertOnlySpansChanged(source, sourceNewText, new[]
        {
            new ContentSpan(movedMember + eol, string.Empty),
            new ContentSpan(
                "        public int Size() => 0;" + eol,
                "        public int Size() => 0;" + eol + eol + movedMember)
        });

        AssertOnlySpansChanged(caller, callerNewText, new[]
        {
            new ContentSpan("a.Count(", "_b.Count(")
        });
    }

    [Test]
    [Category("PreviewCallSite")] // sentinel:auto-category
    public async Task InstanceMovePreview_TrialTextEqualsAppliedTextAndCallSiteStillResolves()
    {
        const string eol = "\r\n";
        var (source, target, caller, movedMember) = InstanceMoveCrlfSources();
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target),
            ("Caller.cs", caller));

        // The call-site preview still classifies the wrapped call as automatically resolvable.
        var rows = await engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "B", null, CancellationToken.None);
        var callSite = rows.Single(r => r.CallExpression == "a.Count");
        Assert.That(callSite.Status, Is.EqualTo(CallSiteStatus.Valid));
        Assert.That(callSite.SuggestedFix, Is.EqualTo("_b"));

        // The preview's trial compile text is built by the same helpers (and the same arguments) the apply path
        // uses, so for the documents the preview touches it must equal what apply writes, byte for byte.
        var solution = await workspace.Manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var sourceDocument = solution.GetDocumentIdsWithFilePath(workspace.PathOf("Source.cs")).Select(solution.GetDocument).First()!;
        var targetDocument = solution.GetDocumentIdsWithFilePath(workspace.PathOf("Target.cs")).Select(solution.GetDocument).First()!;
        var root = (CompilationUnitSyntax)(await sourceDocument.GetSyntaxRootAsync())!;
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == "A");
        var targetRoot = (await targetDocument.GetSyntaxRootAsync())!;
        var targetClassNode = targetRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c => c.Identifier.Text == "B");
        var membersToMove = classNode.Members.Where(m => m is MethodDeclarationSyntax method && method.Identifier.Text == "Count").ToList();

        var previewEdits = new Dictionary<DocumentId, List<TextChange>>();
        var (previewTexts, previewNewFiles) = await MemberRefactoringEngine.AddInstanceMoveStructuralEditsAsync(previewEdits, sourceDocument, root, classNode, membersToMove, "B", targetDocument, targetClassNode, false, CancellationToken.None);
        var preview = await MemberRefactoringEngine.MaterializeDocumentEditsAsync(solution, previewEdits, previewTexts, CancellationToken.None);

        var applied = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "B", null, CancellationToken.None);

        Assert.That(previewNewFiles, Is.Empty, "The preview never synthesizes a new class file");
        Assert.That(preview.Count, Is.EqualTo(2), "The preview touches only the source and target documents - never callers");
        foreach (var fileName in new[] { "Source.cs", "Target.cs" })
        {
            var previewText = preview[preview.Keys.Single(k => Path.GetFileName(k.ToString()) == fileName)];
            var appliedText = applied.Changes[applied.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == fileName)];
            Assert.That(previewText, Is.EqualTo(appliedText), $"Preview text for {fileName} must equal the applied text");
        }

        // And the preview text carries no collateral changes either.
        AssertOnlySpansChanged(source, preview[preview.Keys.Single(k => Path.GetFileName(k.ToString()) == "Source.cs")], new[]
        {
            new ContentSpan(movedMember + eol, string.Empty)
        });
        AssertOnlySpansChanged(target, preview[preview.Keys.Single(k => Path.GetFileName(k.ToString()) == "Target.cs")], new[]
        {
            new ContentSpan(
                "    public string Describe() => \"target\";" + eol,
                "    public string Describe() => \"target\";" + eol + eol + movedMember)
        });
    }

    /// <summary>
    /// CRLF fixture for the instance-member move tests: an instance method with a wrapped signature on class A,
    /// a target class B, and a caller that holds exactly one B field (so the call site resolves automatically).
    /// </summary>
    private static (string Source, string Target, string Caller, string MovedMember) InstanceMoveCrlfSources()
    {
        const string eol = "\r\n";
        var source = Lines(eol,
            "using System;",
            "",
            "namespace Example;",
            "",
            "public record CallerInfo(",
            "    int Line,",
            "    string Name);",
            "",
            "public class A",
            "{",
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(",
            "        string input,",
            "        bool trim = false)",
            "    {",
            "        var text = trim ? input.Trim() : input;",
            "        return text.Length;",
            "    }",
            "",
            "    /// <summary>Other method, see <see cref=\"A.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public record TargetInfo(",
            "    string Result,",
            "    int Value);",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>Another existing member.</summary>",
            "    public string Describe() => \"target\";",
            "}");
        var caller = Lines(eol,
            "using Example;",
            "",
            "public class Caller",
            "{",
            "    private readonly B _b = new B();",
            "",
            "    /// <summary>Wrapped call.</summary>",
            "    /// <remarks>See <see cref=\"A.Count\"/>.</remarks>",
            "    public int Run()",
            "    {",
            "        var a = new A();",
            "        var total = a.Count(",
            "            \"test\",",
            "            true);",
            "        return total;",
            "    }",
            "}");
        var movedMember = Lines(eol,
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(",
            "        string input,",
            "        bool trim = false)",
            "    {",
            "        var text = trim ? input.Trim() : input;",
            "        return text.Length;",
            "    }");
        return (source, target, caller, movedMember);
    }

    /// <summary>Joins lines with <paramref name="eol"/> and terminates the last line with it too.</summary>
    private static string Lines(string eol, params string[] lines) => string.Join(eol, lines) + eol;

    /// <summary>
    /// Assertion helper: compares original and new text, strips allowed edits,
    /// and asserts the remainder is byte-identical. Reports the first changed line on failure.
    /// </summary>
    private static void AssertOnlySpansChanged(
        string before,
        string after,
        ContentSpan[] allowedEdits)
    {
        var workingBefore = before;
        var workingAfter = after;

        // Apply allowed edits in reverse order (to preserve indices)
        var sortedEdits = allowedEdits.OrderByDescending(e => before.IndexOf(e.OldContent, StringComparison.Ordinal)).ToList();

        foreach (var edit in sortedEdits)
        {
            var beforeIdx = workingBefore.IndexOf(edit.OldContent, StringComparison.Ordinal);
            if (beforeIdx < 0)
            {
                Assert.Fail($"Expected span not found in 'before' text: {edit.OldContent.Substring(0, Math.Min(50, edit.OldContent.Length))}...");
            }

            var afterIdx = workingAfter.IndexOf(edit.NewContent, StringComparison.Ordinal);
            if (afterIdx < 0)
            {
                Assert.Fail($"Expected replacement span not found in 'after' text: {edit.NewContent.Substring(0, Math.Min(50, edit.NewContent.Length))}...");
            }

            // Strip the edit from both versions
            workingBefore = workingBefore.Remove(beforeIdx, edit.OldContent.Length);
            workingAfter = workingAfter.Remove(afterIdx, edit.NewContent.Length);
        }

        if (workingBefore != workingAfter)
        {
            var beforeLines = workingBefore.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var afterLines = workingAfter.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < Math.Min(beforeLines.Length, afterLines.Length); i++)
            {
                if (beforeLines[i] != afterLines[i])
                {
                    Assert.Fail(
                        $"First differing line (after stripping allowed edits) at line {i + 1}:\n" +
                        $"Before: {BeforeTruncated(beforeLines[i])}\n" +
                        $"After:  {BeforeTruncated(afterLines[i])}");
                }
            }

            if (beforeLines.Length != afterLines.Length)
            {
                Assert.Fail(
                    $"Line count mismatch (after stripping allowed edits): " +
                    $"before has {beforeLines.Length} lines, after has {afterLines.Length} lines");
            }

            Assert.Fail("Remaining text differs after stripping allowed edits (no line-by-line diff available)");
        }
    }

    private static string BeforeTruncated(string s) =>
        s.Length > 70 ? s.Substring(0, 67) + "..." : s;

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine) CreateInMemoryTestFixture(
        params (string relativePath, string content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = new MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine);
        return (workspace, engine);
    }

    private readonly record struct ContentSpan(string OldContent, string NewContent)
    {
        public ContentSpan(string content) : this(content, content) { }
    }
}

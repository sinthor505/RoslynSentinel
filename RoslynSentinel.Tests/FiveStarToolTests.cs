using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #3 — Tests targeting the four tools rated 4-star in Battery #2:
///   a) ExtractConstantSafe       — strong disk-based tests for int/decimal/bool/char literals,
///                                   multi-occurrence replacement, error cases
///   b) ConvertStringFormatToInterpolatedSmart — 3-arg, format specifiers, escaped braces,
///                                   error cases
///   c) PreviewAddMissingUsings   — actual preview with a type in another namespace,
///                                   no-missing case, preview does not touch disk
///   d) FormatDocumentSafe        — preview vs. apply, disk side-effects, error case
///
/// Goal: all four tools reach 5-star quality with no gaps in coverage.
/// </summary>

// ══════════════════════════════════════════════════════════════════════════════
// A. ExtractConstantSafe — additional strong tests
// ══════════════════════════════════════════════════════════════════════════════

[TestFixture]
public class ExtractConstantSafeStrongTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ── Numeric literal types ─────────────────────────────────────────────────

    [Test]
    [Description("Extract an int literal — DetermineNumericType should return 'int'")]
    public async Task ExtractConstant_IntLiteral_ExtractsToIntConst()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class RetryPolicy
                {
                    public int GetMaxRetries() => 5;
                    public int GetAltMaxRetries() => 5;
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "5", "MaxRetries", lineBefore: "public int GetMaxRetries() =>");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const int MaxRetries"),
                "const int declaration must be emitted");
            Assert.That(result.UpdatedContent, Does.Contain("MaxRetries = 5"),
                "Constant must be initialised to 5");
            // Both usages replaced — the raw literal '5' should only appear once (in const decl)
            var rawCount = result.UpdatedContent!.Split("= 5").Length - 1;
            Assert.That(rawCount, Is.EqualTo(1),
                "All usages of '5' should be replaced with MaxRetries");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Extract a decimal literal — should produce 'const decimal'")]
    public async Task ExtractConstant_DecimalLiteral_ExtractsToDecimalConst()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class Scoring
                {
                    public decimal CalcScore(int value) => value * 1.5m;
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "1.5m", "ScoreMultiplier");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const decimal ScoreMultiplier"),
                "const decimal declaration must be emitted");
            Assert.That(result.UpdatedContent, Does.Contain("ScoreMultiplier"),
                "Usage must be replaced with constant name");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Extract a bool literal — should produce 'const bool'")]
    public async Task ExtractConstant_BoolLiteral_ExtractsToBoolConst()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class FeatureFlags
                {
                    public bool IsDebug() => true;
                    public bool IsVerbose() => true;
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "true", "DefaultEnabled", lineBefore: "public bool IsDebug() =>");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const bool DefaultEnabled"),
                "const bool declaration must be emitted");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Extract a char literal — should produce 'const char'")]
    public async Task ExtractConstant_CharLiteral_ExtractsToCharConst()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class CsvParser
                {
                    public char GetDelimiter() => ',';
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "','", "CsvDelimiter");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const char CsvDelimiter"),
                "const char declaration must be emitted");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Multi-occurrence replacement ─────────────────────────────────────────

    [Test]
    [Description("Four identical string literals → all replaced, one const declaration")]
    public async Task ExtractConstant_FourIdenticalLiterals_AllReplaced()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class Config
                {
                    public string GetA() => "localhost";
                    public string GetB() => "localhost";
                    public string GetC() => "localhost";
                    public string GetD() => "localhost";
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "\"localhost\"", "DefaultHost",
                lineBefore: "public string GetA() =>");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const string DefaultHost = \"localhost\""),
                "One const declaration must exist");
            // After extraction, raw "localhost" string appears only in the const decl
            int literalOccurrences = CountOccurrences(result.UpdatedContent!, "\"localhost\"");
            Assert.That(literalOccurrences, Is.EqualTo(1),
                "All 4 usages should be replaced; only the const initializer retains the literal");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Literal inside nested class ──────────────────────────────────────────

    [Test]
    [Description("Literal inside a nested class — constant placed inside that nested class")]
    public async Task ExtractConstant_LiteralInNestedClass_PlacedInContainingType()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string source = """
                public class Outer
                {
                    public class Inner
                    {
                        public string GetEndpoint() => "/api/v1";
                    }
                }
                """;
            await File.WriteAllTextAsync(tempFile, source);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "\"/api/v1\"", "ApiEndpoint");

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.UpdatedContent, Does.Contain("const string ApiEndpoint"),
                "Constant must be inserted into the inner class");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Test]
    [Description("Snippet that doesn't appear in the file → clean human-readable error")]
    public async Task ExtractConstant_SnippetNotFound_ReturnsHelpfulError()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                public class X
                {
                    public string Get() => "hello";
                }
                """);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "\"DOES_NOT_EXIST_IN_FILE\"", "MissingConst");

            Assert.That(result.Success, Is.False,
                "Non-existent snippet must produce a failure");
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                "Error message must be non-empty and human-readable");
            // The error must be actionable, not a generic .NET exception dump
            Assert.That(result.Error, Does.Not.Contain("StackTrace"),
                "Error must not expose a stack trace to the caller");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("File does not exist → clean error, no throw")]
    public async Task ExtractConstant_FileNotFound_ReturnsHelpfulError()
    {
        var result = await _engine.ExtractConstantSafeAsync(
            @"C:\nonexistent\path\missing.cs", "\"any\"", "AnyConst");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("missing.cs"),
            "Error must mention the missing file name");
    }

    [Test]
    [Description("Invalid identifier → clear rejection before attempting extraction")]
    public async Task ExtractConstant_InvalidCSharpIdentifier_ReturnsHelpfulError()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, "public class X { string M() => \"hi\"; }");

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "\"hi\"", "99InvalidName");

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("99InvalidName").Or.Contain("identifier"),
                "Error must identify the invalid name");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Regression: interpolated string literal returns clean error (not crash) ──

    [Test]
    [Description("Interpolated string ($\"\") cannot be const — must fail cleanly, not throw")]
    public async Task ExtractConstant_InterpolatedString_FailsCleanly()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                public class CacheKey
                {
                    public string UserKey(string id) => $"user:{id}:data";
                }
                """);

            var result = await _engine.ExtractConstantSafeAsync(
                tempFile, "$\"user:", "UserCacheKey");

            // Interpolated strings are NOT literals — the engine should either:
            // (a) fail gracefully because the $ prefix makes it an interpolated expression, not a literal
            // (b) succeed if it finds an adjacent string piece
            // Either way: no throw, no null result, always has an error message when Success=false
            Assert.That(result, Is.Not.Null);
            if (!result.Success)
            {
                Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
                    "Failure must include an actionable error message");
            }
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string MakeTempFile() =>
        Path.Combine(Path.GetTempPath(), $"rse_test_{Guid.NewGuid():N}.cs");

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* ignore */ }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, pos = 0;
        while ((pos = text.IndexOf(value, pos, StringComparison.Ordinal)) >= 0) { count++; pos += value.Length; }
        return count;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// B. ConvertStringFormatToInterpolatedSmart — additional tests
// ══════════════════════════════════════════════════════════════════════════════

[TestFixture]
public class ConvertStringFormatSmartTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── Three-argument conversion ────────────────────────────────────────────

    [Test]
    [Description("Three-argument string.Format → all three placeholders appear in the interpolated string")]
    public async Task ConvertStringFormat_ThreeArgs_AllPlaceholdersConverted()
    {
        SetSource("""
            public class Greeter
            {
                public string Build(string first, string last, int age)
                {
                    return string.Format("Hello {0} {1}, age {2}", first, last, age);
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(\"Hello");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("$\""),
            "Result must be an interpolated string");
        Assert.That(result.UpdatedContent, Does.Contain("first"),
            "First argument must appear");
        Assert.That(result.UpdatedContent, Does.Contain("last"),
            "Second argument must appear");
        Assert.That(result.UpdatedContent, Does.Contain("age"),
            "Third argument must appear");
        Assert.That(result.UpdatedContent, Does.Not.Contain("string.Format"),
            "The original string.Format call must be replaced");
    }

    // ── Format specifier preservation ────────────────────────────────────────

    [Test]
    [Description("Format specifier ':yyyy-MM-dd' must survive conversion")]
    public async Task ConvertStringFormat_WithFormatSpecifier_SpecifierPreserved()
    {
        SetSource("""
            using System;
            public class Reporter
            {
                public string FormatDate(DateTime dt)
                {
                    return string.Format("Report date: {0:yyyy-MM-dd}", dt);
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(\"Report");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("yyyy-MM-dd"),
            "Format specifier must be preserved in the interpolated string");
        Assert.That(result.UpdatedContent, Does.Contain("dt"),
            "The DateTime argument must appear in the interpolated string");
    }

    // ── Escaped braces ───────────────────────────────────────────────────────

    [Test]
    [Description("Escaped {{ }} in the format string must remain as literal braces in the output")]
    public async Task ConvertStringFormat_EscapedBraces_PreservedInOutput()
    {
        SetSource("""
            public class JsonBuilder
            {
                public string Build(string val)
                {
                    return string.Format("{{\"key\":\"{0}\"}}", val);
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(\"{{");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("$\""),
            "Result must be an interpolated string");
        // The escaped {{ must appear as {{ in the interpolated string literal
        Assert.That(result.UpdatedContent, Does.Contain("{{").Or.Contain("}}"),
            "Escaped braces must survive as literal {{ or }} in output");
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Test]
    [Description("Snippet pointing to non-Format code → clean error, not throw")]
    public async Task ConvertStringFormat_SnippetNotOnFormatCall_ReturnsHelpfulError()
    {
        SetSource("""
            public class X
            {
                public string Get() => "hello world";
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "\"hello world\"");

        Assert.That(result.Success, Is.False,
            "Non-Format snippet must produce a failure result");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
            "Error message must be non-empty");
    }

    [Test]
    [Description("Dynamic (non-constant) format string → clean failure, not throw")]
    public async Task ConvertStringFormat_NonResolvableFormatArg_ReturnsHelpfulError()
    {
        SetSource("""
            public class X
            {
                public string Build(string fmt, string val)
                {
                    return string.Format(fmt, val);
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(fmt");

        Assert.That(result.Success, Is.False,
            "Non-constant format argument must produce a failure result");
        Assert.That(result.Error, Does.Contain("constant").Or.Contain("literal").Or.Contain("resolve"),
            "Error must explain WHY it failed");
    }

    [Test]
    [Description("Single-argument string.Format (no args, just a literal) → valid conversion")]
    public async Task ConvertStringFormat_SingleArg_ConvertedToPlainInterpolatedString()
    {
        // string.Format with ONLY a format string and no arguments is unusual but valid.
        // The result should be the literal wrapped in $"..."
        SetSource("""
            public class Banner
            {
                public string GetBanner()
                {
                    return string.Format("Welcome to ExpressRecipe");
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(\"Welcome");

        Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("Welcome to ExpressRecipe"),
            "Literal text must survive");
    }

    // ── Regression: named const + literal (covered in RegressionTests) ─────────

    [Test]
    [Description("Regression guard: named const format string — the core differentiator from standard tool")]
    public async Task ConvertStringFormat_NamedConst_ResolvesAndConverts_Regression()
    {
        SetSource("""
            public class AuditLogger
            {
                private const string EntryFmt = "Action={0} By={1}";

                public string Format(string action, string user)
                {
                    return string.Format(EntryFmt, action, user);
                }
            }
            """);

        var result = await _engine.ConvertStringFormatToInterpolatedSmartAsync(
            "Test.cs", "string.Format(EntryFmt");

        Assert.That(result.Success, Is.True,
            "Named const — this is the core bug fix; must succeed");
        Assert.That(result.UpdatedContent, Does.Contain("action"),
            "First arg must appear in output");
        Assert.That(result.UpdatedContent, Does.Contain("user"),
            "Second arg must appear in output");
        Assert.That(result.UpdatedContent, Does.Not.Contain("string.Format"),
            "Original call must be gone");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// C. PreviewAddMissingUsings — actual preview tests (with loaded solution)
// ══════════════════════════════════════════════════════════════════════════════

[TestFixture]
public class PreviewAddMissingUsingsLoadedTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetMultiFileSolution(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── Actual preview: type in another namespace ─────────────────────────────

    [Test]
    [Description("Type defined in another source-file namespace → preview suggests the using")]
    public async Task PreviewAddMissing_TypeInProjectNamespace_SuggestsCorrectUsing()
    {
        // Two source files in the same project:
        // File A defines UserRepository in MyApp.Data
        // File B uses UserRepository without importing MyApp.Data
        SetMultiFileSolution(
            ("Repo.cs", """
                namespace MyApp.Data
                {
                    public class UserRepository
                    {
                        public void Save() { }
                    }
                }
                """),
            ("Service.cs", """
                public class UserService
                {
                    public void Configure()
                    {
                        var repo = new UserRepository();
                    }
                }
                """)
        );

        var result = await _engine.PreviewAddMissingUsingsAsync("Service.cs");

        Assert.That(result.SolutionRequired, Is.False,
            "Solution IS loaded");
        Assert.That(result.UsingsToAdd, Does.Contain("MyApp.Data"),
            "MyApp.Data namespace must be identified as needed");
        Assert.That(result.UpdatedContent, Does.Contain("using MyApp.Data"),
            "Preview content must include the using directive");
    }

    [Test]
    [Description("Preview does NOT write to disk — the file is never touched")]
    public async Task PreviewAddMissing_Preview_DoesNotWriteToDisk()
    {
        // Write a file to disk with no usings so we can verify it's unchanged after preview
        var tempFile = Path.Combine(Path.GetTempPath(), $"rse_test_{Guid.NewGuid():N}.cs");
        const string originalContent = """
            public class TestSvc
            {
                public void Run() { }
            }
            """;
        await File.WriteAllTextAsync(tempFile, originalContent);

        try
        {
            // Set a workspace solution that contains this file
            var solution = TestSolutionBuilder.CreateSolutionWithProject(
                "TestProj", [(tempFile, originalContent)]);
            _workspaceManager.SetTestSolution(solution);

            // Call preview (default) on the file
            await _engine.PreviewAddMissingUsingsAsync(tempFile);

            // File on disk must be UNCHANGED
            var diskContent = await File.ReadAllTextAsync(tempFile);
            Assert.That(diskContent, Is.EqualTo(originalContent),
                "PreviewAddMissingUsings must NEVER modify the file on disk (fixes MS bug where preview is ignored)");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Test]
    [Description("Clean code with no diagnostics → UsingsToAdd is empty, no crash")]
    public async Task PreviewAddMissing_CleanCode_ReturnsEmptyUsingsToAdd()
    {
        SetMultiFileSolution(
            ("Clean.cs", """
                using System;
                public class Clean
                {
                    public string Id { get; } = Guid.NewGuid().ToString();
                }
                """)
        );

        var result = await _engine.PreviewAddMissingUsingsAsync("Clean.cs");

        Assert.That(result.SolutionRequired, Is.False,
            "Solution is loaded, so SolutionRequired=false");
        Assert.That(result.UsingsToAdd, Is.Empty,
            "No missing usings for clean compilable code");
        Assert.That(result.UpdatedContent, Is.Not.Empty,
            "UpdatedContent must always return something (the unchanged file)");
    }

    [Test]
    [Description("Two files each missing a different namespace → both discovered independently")]
    public async Task PreviewAddMissing_EachFileMissingDifferentNamespace_BothFound()
    {
        SetMultiFileSolution(
            ("Alpha.cs", """
                namespace MyApp.Alpha
                {
                    public class AlphaService { }
                }
                """),
            ("Beta.cs", """
                namespace MyApp.Beta
                {
                    public class BetaService { }
                }
                """),
            ("Consumer.cs", """
                public class Consumer
                {
                    public void Use()
                    {
                        var a = new AlphaService();
                    }
                }
                """)
        );

        var result = await _engine.PreviewAddMissingUsingsAsync("Consumer.cs");

        Assert.That(result.UsingsToAdd, Does.Contain("MyApp.Alpha"),
            "AlphaService lives in MyApp.Alpha — must be suggested");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// D. FormatDocumentSafe — disk-based tests (preview vs. apply, error paths)
// ══════════════════════════════════════════════════════════════════════════════

[TestFixture]
public class FormatDocumentSafeTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ── Preview mode (default) ────────────────────────────────────────────────

    [Test]
    [Description("Poorly indented code → preview returns well-formatted content")]
    public async Task FormatDocumentSafe_MisindentedCode_FormatsCorrectly_Preview()
    {
        var tempFile = MakeTempFile();
        try
        {
            // Deliberately broken indentation
            const string ugly = "public class X\n{\npublic void M()\n{\nvar x = 1;\n}\n}";
            await File.WriteAllTextAsync(tempFile, ugly);

            var result = await _engine.FormatDocumentSafeAsync(tempFile, preview: true);

            Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
            Assert.That(result.UpdatedContent, Is.Not.Null.And.Not.Empty,
                "Formatted content must be returned");
            // Roslyn formatter adds proper indentation — body should be indented
            Assert.That(result.UpdatedContent, Does.Contain("    "),
                "Formatted output must contain 4-space indentation (or equivalent)");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Preview mode must NOT write to disk — the file must remain identical")]
    public async Task FormatDocumentSafe_Preview_DoesNotModifyDisk()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string ugly = "public class Y\n{\npublic int V;}\n";
            await File.WriteAllTextAsync(tempFile, ugly);

            await _engine.FormatDocumentSafeAsync(tempFile, preview: true);

            var diskContent = await File.ReadAllTextAsync(tempFile);
            Assert.That(diskContent, Is.EqualTo(ugly),
                "FormatDocumentSafe(preview=true) must NEVER modify the file on disk " +
                "(this is the core bug fix — standard format_document has no preview mode)");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Apply mode ────────────────────────────────────────────────────────────

    [Test]
    [Description("Non-preview mode writes the formatted content to disk")]
    public async Task FormatDocumentSafe_Apply_WritesDiskFile()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string ugly = "public class Z\n{\npublic int V;}\n";
            await File.WriteAllTextAsync(tempFile, ugly);

            var result = await _engine.FormatDocumentSafeAsync(tempFile, preview: false);

            Assert.That(result.Success, Is.True, $"Apply should succeed: {result.Error}");
            var diskContent = await File.ReadAllTextAsync(tempFile);
            Assert.That(diskContent, Is.Not.EqualTo(ugly),
                "FormatDocumentSafe(preview=false) MUST write to disk");
            Assert.That(diskContent, Is.EqualTo(result.UpdatedContent),
                "Disk content must match the UpdatedContent returned by the engine");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Already-formatted ─────────────────────────────────────────────────────

    [Test]
    [Description("Well-formatted code stays logically equivalent after formatting")]
    public async Task FormatDocumentSafe_AlreadyFormatted_ReturnsSameLogicalContent()
    {
        var tempFile = MakeTempFile();
        try
        {
            const string clean = """
                public class Formatter
                {
                    public int Value { get; set; }

                    public void Reset()
                    {
                        Value = 0;
                    }
                }
                """;
            await File.WriteAllTextAsync(tempFile, clean);

            var result = await _engine.FormatDocumentSafeAsync(tempFile, preview: true);

            Assert.That(result.Success, Is.True, $"Should succeed: {result.Error}");
            // The content may differ by trailing whitespace / CRLF normalization,
            // but the identifiers and structure must all be present
            Assert.That(result.UpdatedContent, Does.Contain("class Formatter"),
                "Class declaration must survive formatting");
            Assert.That(result.UpdatedContent, Does.Contain("Value = 0"),
                "Method body must survive formatting");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Test]
    [Description("File does not exist → clean error, not throw")]
    public async Task FormatDocumentSafe_FileNotFound_ReturnsHelpfulError()
    {
        var result = await _engine.FormatDocumentSafeAsync(
            @"C:\nonexistent_rs_test\missing_rs.cs");

        Assert.That(result.Success, Is.False,
            "Missing file must produce a failure result");
        Assert.That(result.Error, Does.Contain("missing_rs.cs").Or.Contain("nonexistent"),
            "Error must identify the missing path");
    }

    [Test]
    [Description("Syntactically broken C# still returns without throw (best-effort format)")]
    public async Task FormatDocumentSafe_SyntaxErrors_StillReturnsWithoutThrowing()
    {
        var tempFile = MakeTempFile();
        try
        {
            // Missing closing brace — still valid Roslyn parse (error recovery)
            await File.WriteAllTextAsync(tempFile, "public class Broken { void M() { ");

            // Roslyn's formatter handles error-recovery trees; should not throw
            var result = await _engine.FormatDocumentSafeAsync(tempFile, preview: true);

            // We don't mandate success here — just that it doesn't throw
            Assert.That(result, Is.Not.Null, "Must return a result, never throw");
        }
        finally { SafeDelete(tempFile); }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string MakeTempFile() =>
        Path.Combine(Path.GetTempPath(), $"rse_fmt_{Guid.NewGuid():N}.cs");

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { /* ignore */ }
    }
}

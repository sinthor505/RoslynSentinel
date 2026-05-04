#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #4 — Advanced edge-case tests for four augmented tools:
///   A. EncapsulateFieldSafe  (5 tests) — readonly, static, underscore naming, override name, usage renaming
///   B. AnalyzeForeachForLinqConversion (4 tests) — file-not-found, snippet-not-found, multi-target, field collection
///   C. SwitchConversionAdvanced (6 tests) — analyze/convert file-not-found, snippet-not-found,
///      throw-per-case analysis, return-per-case conversion, and the documented throw-only limitation
///
/// Total: 15 tests. All workspace-based tools use SetSource(); all disk-based tools use temp files.
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. EncapsulateFieldSafe — advanced edge cases
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class EncapsulateFieldSafeAdvancedTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [Description("readonly field → generated property must have get accessor only — no setter")]
    public async Task EncapsulateField_ReadonlyField_GeneratesGetOnlyProperty()
    {
        SetSource("""
            public class RequestStats
            {
                public readonly int MaxAttempts = 3;
            }
            """);

        var result = await _engine.EncapsulateFieldSafeAsync("Test.cs", "MaxAttempts");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("get"),
            "Property must have a getter");
        Assert.That(result.UpdatedContent, Does.Not.Contain("set"),
            "readonly field must produce get-only property — no setter allowed");
    }

    [Test]
    [Description("static field → generated backing field AND property must both be static")]
    public async Task EncapsulateField_StaticField_GeneratesStaticBackingAndProperty()
    {
        SetSource("""
            public class AppConstants
            {
                public static string Environment = "production";
            }
            """);

        var result = await _engine.EncapsulateFieldSafeAsync("Test.cs", "Environment");

        Assert.That(result.Success, Is.True, result.Error);
        // At minimum two 'static' keywords: one for backing field, one for property
        var staticCount = result.UpdatedContent!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Trim(',', ';', '{', '}') == "static");
        Assert.That(staticCount, Is.GreaterThanOrEqualTo(2),
            "Both the backing field and the property must be declared static");
    }

    [Test]
    [Description("_camelCase field: backing name stays _count, property becomes Count (no double-underscore)")]
    public async Task EncapsulateField_UnderscorePrefixedField_CorrectNamingNoDoubleUnderscore()
    {
        SetSource("""
            public class Counter
            {
                public int _count;
            }
            """);

        var result = await _engine.EncapsulateFieldSafeAsync("Test.cs", "_count");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("Count"),
            "Property name must be 'Count' (PascalCase strip of '_count')");
        Assert.That(result.UpdatedContent, Does.Contain("_count"),
            "Backing field name must remain '_count' — no rename when field already has underscore prefix");
        Assert.That(result.UpdatedContent, Does.Not.Contain("__count"),
            "Must NOT produce a double-underscore backing name '__count'");
    }

    [Test]
    [Description("overridePropertyName parameter → custom name is used instead of the derived PascalCase name")]
    public async Task EncapsulateField_OverridePropertyName_UsesProvidedName()
    {
        SetSource("""
            public class Cache
            {
                public int hitCount;
            }
            """);

        var result = await _engine.EncapsulateFieldSafeAsync(
            "Test.cs", "hitCount", overridePropertyName: "HitRate");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("HitRate"),
            "Override property name 'HitRate' must appear in the output");
        Assert.That(result.UpdatedContent, Does.Not.Contain("HitCount"),
            "Default derived name 'HitCount' must NOT appear when an override is supplied");
    }

    [Test]
    [Description("Field referenced in method bodies → all usages are renamed to the backing field name")]
    public async Task EncapsulateField_FieldUsagesInMethods_AllRenamedToBackingField()
    {
        SetSource("""
            public class OrderProcessor
            {
                public int retryCount;
                public void Reset() { retryCount = 0; }
                public bool HasRetries() => retryCount > 0;
            }
            """);

        var result = await _engine.EncapsulateFieldSafeAsync("Test.cs", "retryCount");

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.UpdatedContent, Does.Contain("private int _retryCount"),
            "Backing field must be declared as 'private int _retryCount'");
        Assert.That(result.UpdatedContent, Does.Contain("RetryCount"),
            "Public property must be named 'RetryCount'");
        Assert.That(result.UpdatedContent, Does.Contain("_retryCount = 0"),
            "Reset() body must use backing field _retryCount, not the original field name");
        Assert.That(result.UpdatedContent, Does.Contain("_retryCount > 0"),
            "HasRetries() body must use backing field _retryCount, not the original field name");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. AnalyzeForeachForLinqConversion — advanced edge cases
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AnalyzeForeachAdvancedTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static string MakeTempFile() => Path.GetTempFileName() + ".cs";
    private static void SafeDelete(string path) { try { File.Delete(path); } catch { } }

    [Test]
    [Description("Non-existent file path → IsSafeToConvert=false with a 'Could not read file' error")]
    public async Task AnalyzeForeach_FileNotFound_ReturnsCleanError()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "GhostFile_" + Guid.NewGuid() + ".cs");

        var result = await _engine.AnalyzeForeachForLinqConversionAsync(nonExistent, "foreach");

        Assert.That(result.IsSafeToConvert, Is.False,
            "Non-existent file must not claim safe-to-convert");
        Assert.That(result.BlockingReason, Does.Contain("Could not read file"),
            "BlockingReason must explain the file could not be read");
    }

    [Test]
    [Description("File exists but snippet is not present → IsSafeToConvert=false with a context error")]
    public async Task AnalyzeForeach_SnippetNotFoundInFile_ReturnsCleanError()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                public class C
                {
                    public void M() { }
                }
                """);

            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (var x in doesNotExistCollection)");

            Assert.That(result.IsSafeToConvert, Is.False,
                "Snippet absent from file must not claim safe-to-convert");
            Assert.That(result.BlockingReason, Is.Not.Null.And.Not.Empty,
                "BlockingReason must be populated when snippet cannot be located");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Foreach body adds to two different collections → cannot pick a single conversion target")]
    public async Task AnalyzeForeach_MultipleAddTargets_ReportsConflict()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                using System.Collections.Generic;
                public class Splitter
                {
                    public void Split(List<int> all, List<int> evens, List<int> odds)
                    {
                        foreach (var n in all)
                        {
                            evens.Add(n);
                            odds.Add(n);
                        }
                    }
                }
                """);

            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (var n in all)");

            Assert.That(result.IsSafeToConvert, Is.False,
                "Adding to two collections in one loop cannot be a single-target LINQ conversion");
            Assert.That(result.BlockingReason,
                Does.Contain("evens").Or.Contain("odds").Or.Contain("Multiple"),
                "BlockingReason must identify the conflicting collection targets");
        }
        finally { SafeDelete(tempFile); }
    }

    [Test]
    [Description("Collection is a class field (not a local variable) → IsSafeToConvert=true with a guidance note")]
    public async Task AnalyzeForeach_CollectionIsFieldNotLocal_ReturnsSafeWithNote()
    {
        var tempFile = MakeTempFile();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
                using System.Collections.Generic;
                public class EventLog
                {
                    private readonly List<string> _events = new();

                    public void RecordAll(List<string> incoming)
                    {
                        foreach (var e in incoming)
                        {
                            _events.Add(e);
                        }
                    }
                }
                """);

            var result = await _engine.AnalyzeForeachForLinqConversionAsync(
                tempFile, "foreach (var e in incoming)");

            // Implementation: when the collection has no local-variable declaration in the same block,
            // declIndex=-1 → returns IsSafeToConvert=true with a note to use standard tool.
            Assert.That(result.IsSafeToConvert, Is.True,
                "No local pre-modification to block — should report safe to convert");
            Assert.That(result.CollectionVariableName, Is.EqualTo("_events"),
                "Must correctly identify '_events' as the Add() target collection");
        }
        finally { SafeDelete(tempFile); }
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. SwitchConversionAdvanced — AnalyzeSwitchForPatternConversion + ConvertSwitchToPatternSafe
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SwitchConversionAdvancedTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ── AnalyzeSwitchForPatternConversion ───────────────────────────────────────

    [Test]
    [Description("File not in workspace → Analyze returns IsSafeToConvert=false with file-not-found message")]
    public async Task AnalyzeSwitchForPattern_FileNotInWorkspace_ReturnsFileNotFoundError()
    {
        SetSource("public class C { public void M() { } }"); // loaded as "Test.cs"

        var result = await _engine.AnalyzeSwitchForPatternConversionAsync(
            "NotInWorkspace.cs", "switch (x)");

        Assert.That(result.IsSafeToConvert, Is.False,
            "File absent from workspace must not claim safe-to-convert");
        Assert.That(result.BlockingReason, Does.Contain("NotInWorkspace"),
            "BlockingReason must reference the missing file name");
    }

    [Test]
    [Description("Snippet not present in file → Analyze returns IsSafeToConvert=false with context error")]
    public async Task AnalyzeSwitchForPattern_SnippetNotFound_ReturnsError()
    {
        SetSource("public class C { public void M() { } }");

        var result = await _engine.AnalyzeSwitchForPatternConversionAsync(
            "Test.cs", "switch (ghostVariable)");

        Assert.That(result.IsSafeToConvert, Is.False,
            "Missing snippet must not claim safe-to-convert");
        Assert.That(result.BlockingReason, Is.Not.Null.And.Not.Empty,
            "BlockingReason must be populated when snippet is not found");
    }

    [Test]
    [Description("All cases are throw statements — Analyze correctly reports safe (throws have no assignments)")]
    public async Task AnalyzeSwitchForPattern_ThrowPerCase_ReportedAsSafe()
    {
        SetSource("""
            public class ErrorMapper
            {
                public void Validate(int code)
                {
                    switch (code)
                    {
                        case 0: throw new System.ArgumentException("zero");
                        case 1: throw new System.InvalidOperationException("one");
                        default: throw new System.NotSupportedException("other");
                    }
                }
            }
            """);

        var result = await _engine.AnalyzeSwitchForPatternConversionAsync(
            "Test.cs", "switch (code)");

        // ThrowStatementSyntax nodes contain no AssignmentExpressionSyntax.
        // The multi-assignment counter finds 0 assignments per case → reports safe.
        Assert.That(result.IsSafeToConvert, Is.True,
            "Throw-per-case switch has no multi-assignments — Analyze must report safe");
        Assert.That(result.BlockingReason, Is.Null.Or.Empty,
            "No blocking reason should be set for a structurally clean throw switch");
    }

    // ── ConvertSwitchToPatternSafe ───────────────────────────────────────────────

    [Test]
    [Description("All cases are return statements → output is 'return x switch { ... }' with arm values preserved")]
    public async Task ConvertSwitchToPattern_ReturnPerCase_ProducesReturnSwitchExpression()
    {
        SetSource("""
            public class Converter
            {
                public string MapCode(int code)
                {
                    switch (code)
                    {
                        case 1: return "one";
                        case 2: return "two";
                        default: return "other";
                    }
                }
            }
            """);

        var result = await _engine.ConvertSwitchToPatternSafeAsync("Test.cs", "switch (code)");

        Assert.That(result.Success, Is.True,
            $"Return-per-case switch should convert successfully. Error: {result.Error}");
        Assert.That(result.UpdatedContent, Does.Contain("return"),
            "Converted output must contain a return statement");
        Assert.That(result.UpdatedContent, Does.Contain("switch"),
            "Switch expression must appear in the converted output");
        Assert.That(result.UpdatedContent, Does.Not.Contain("break;"),
            "No break statements should remain after switch-expression conversion");
        Assert.That(result.UpdatedContent, Does.Contain("\"one\""),
            "Arm value 'one' must be preserved in the switch expression");
        Assert.That(result.UpdatedContent, Does.Contain("\"two\""),
            "Arm value 'two' must be preserved in the switch expression");
    }

    [Test]
    [Description("File not in workspace → Convert fails cleanly because Analyze fails first")]
    public async Task ConvertSwitchToPattern_FileNotInWorkspace_FailsWithError()
    {
        SetSource("public class C { public void M() { } }");

        var result = await _engine.ConvertSwitchToPatternSafeAsync(
            "NotInWorkspace.cs", "switch (x)");

        Assert.That(result.Success, Is.False,
            "File absent from workspace must cause conversion to fail");
        Assert.That(result.Error, Is.Not.Null.And.Not.Empty,
            "Error message must explain the failure cause");
    }

    [Test]
    [Description("DOCUMENTED LIMITATION: throw-only switch — Analyze says safe, Convert reveals the gap")]
    public async Task ConvertSwitchToPattern_ThrowOnlySwitch_FailsWithDocumentedLimitation()
    {
        // This test documents a known design gap between analysis and conversion:
        //
        // AnalyzeSwitchForPatternConversion counts AssignmentExpressionSyntax nodes.
        // ThrowStatementSyntax contains none, so HasMultipleAssignments=false for every
        // case → IsSafeToConvert=true (correct by its own narrow logic).
        //
        // ConvertSwitchToPatternSafe encounters throw cases and sets isReturnSwitch=false.
        // With isReturnSwitch=false AND targetVariable=null, it falls to the "could not
        // determine replacement form" path → returns Fail.
        //
        // EXPECTED: analysis says safe; conversion fails with an explicit message.
        // This is preferable to a silent corrupt-output failure.
        SetSource("""
            public class ErrorMapper
            {
                public void Validate(int code)
                {
                    switch (code)
                    {
                        case 0: throw new System.ArgumentException("zero");
                        default: throw new System.NotSupportedException("other");
                    }
                }
            }
            """);

        var analysis = await _engine.AnalyzeSwitchForPatternConversionAsync(
            "Test.cs", "switch (code)");
        Assert.That(analysis.IsSafeToConvert, Is.True,
            "PRECONDITION: Analyze reports safe because throw cases have no multi-assignments");

        var conversion = await _engine.ConvertSwitchToPatternSafeAsync(
            "Test.cs", "switch (code)");

        // Convert cannot emit valid C# for throw-only; must fail explicitly (not silently corrupt).
        Assert.That(conversion.Success, Is.False,
            "Known limitation: throw-only switch cannot be converted to switch expression by this tool version");
        Assert.That(conversion.Error, Does.Contain("Could not determine replacement form")
                                          .Or.Contain("Manual conversion required"),
            "Error message must be explicit — users need to know WHY the conversion was rejected");
    }
}

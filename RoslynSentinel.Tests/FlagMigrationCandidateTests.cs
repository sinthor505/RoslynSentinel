using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for <see cref="AsyncOptimizationEngine.FlagMigrationCandidateAsync"/> and
/// <see cref="AsyncOptimizationEngine.FindMigrationCandidatesAsync"/>.
///
/// Verifies:
///   - [MigrationCandidate] is added to the target method with correct args.
///   - MigrationCandidateAttribute.cs is injected when the class is absent.
///   - MigrationCandidateAttribute.cs is NOT injected when the class already exists.
///   - Re-flagging the same pattern is idempotent (old attribute replaced, not doubled).
///   - Flagging a second pattern leaves the first intact.
///   - convert_to_async_bridge strips [MigrationCandidate] automatically.
///   - FindMigrationCandidatesAsync returns flagged methods with correct fields.
///   - FindMigrationCandidatesAsync pattern filter works correctly.
///   - Error cases: file not found, method not found.
/// </summary>
[TestFixture]
public class FlagMigrationCandidateTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AsyncOptimizationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AsyncOptimizationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Service.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetSources(params (string fileName, string source)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Attribute injection
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_AttributeClassAbsent_InjectsAttributeFile()
    {
        SetSource(@"
namespace Avaal.Service
{
    public class TripService
    {
        public System.Data.DataTable GetTrips(int id) => null;
    }
}", "TripService.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "TripService.cs", "GetTrips", "AsyncBridge");
        var changes = result.Changes;

        Assert.That(result.AttributeClassInjected, Is.True,
            "AttributeClassInjected should be true when the attribute class was absent.");
        // Should have two entries: the updated file and the new attribute class file.
        Assert.That(changes.Count, Is.EqualTo(2),
            "Should return exactly two changes: target file + MigrationCandidateAttribute.cs.");

        var attrEntry = changes.Keys.FirstOrDefaultByFileNameSuffix("MigrationCandidateAttribute.cs");
        Assert.That(attrEntry, Is.Not.Null,
            "One change should be the injected MigrationCandidateAttribute.cs.");
        Assert.That(changes[attrEntry!], Does.Contain("class MigrationCandidateAttribute"),
            "Injected file should define the attribute class.");
        Assert.That(changes[attrEntry!], Does.Contain("internal sealed"),
            "Injected attribute class should be internal sealed.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_AttributeClassAbsent_NamespaceMatchesTargetFile()
    {
        SetSource(@"
namespace Avaal.Service
{
    public class TripService
    {
        public int GetCount() => 0;
    }
}", "TripService.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "TripService.cs", "GetCount", "AsyncBridge");
        var changes = result.Changes;

        var attrKey = changes.Keys.FirstOrDefaultByFileNameSuffix("MigrationCandidateAttribute.cs");
        Assert.That(attrKey, Is.Not.Null);
        Assert.That(changes[attrKey!], Does.Contain("namespace Avaal.Service"),
            "Injected attribute class should use the namespace of the target file.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_AttributeClassPresent_DoesNotInjectAgain()
    {
        // Pre-define MigrationCandidateAttribute in the solution.
        SetSources(
            ("TripService.cs", @"
namespace Avaal.Service
{
    public class TripService
    {
        public int GetCount() => 0;
    }
}"),
            ("MigrationCandidateAttribute.cs", @"
using System;
namespace Avaal.Service
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    internal sealed class MigrationCandidateAttribute : Attribute
    {
        public MigrationCandidateAttribute(string pattern) => Pattern = pattern;
        public string Pattern { get; }
        public int Score { get; set; }
        public string Reason { get; set; }
        public string FlaggedDate { get; set; }
    }
}"));

        var result = await _engine.FlagMigrationCandidateAsync(
            "TripService.cs", "GetCount", "AsyncBridge");
        var changes = result.Changes;

        Assert.That(result.AttributeClassInjected, Is.False,
            "AttributeClassInjected should be false when the class was already present.");
        Assert.That(changes.Count, Is.EqualTo(1),
            "Should only update the target file — no new attribute class injection needed.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Attribute content
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_AddsPatternToMethod()
    {
        SetSource(@"
public class Svc
{
    public int Search(string q) => 0;
}", "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync("Svc.cs", "Search", "AsyncBridge");
        var changes = result.Changes;

        Assert.That(result.WasAlreadyFlagged, Is.False, "Fresh flag should not report WasAlreadyFlagged.");
        var targetSrc = changes["Svc.cs"];
        Assert.That(targetSrc, Does.Contain("[MigrationCandidate(\"AsyncBridge\""),
            "Target file should have [MigrationCandidate(\"AsyncBridge\"...)] on the method.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_WithScore_IncludesScoreNamedArg()
    {
        SetSource(@"
public class Svc { public int Search(string q) => 0; }", "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "Svc.cs", "Search", "AsyncBridge", score: 18);
        var changes = result.Changes;

        Assert.That(changes["Svc.cs"], Does.Contain("Score = 18"),
            "Score named argument should appear when score != 0.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_WithReason_IncludesReasonNamedArg()
    {
        SetSource(@"
public class Svc { public int Search(string q) => 0; }", "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "Svc.cs", "Search", "HandlerExtract", reason: "3 non-thin handlers");
        var changes = result.Changes;

        Assert.That(changes["Svc.cs"], Does.Contain("Reason = \"3 non-thin handlers\""),
            "Reason named argument should appear when reason is provided.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_ZeroScore_OmitsScoreArg()
    {
        SetSource(@"
public class Svc { public int Search(string q) => 0; }", "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "Svc.cs", "Search", "AsyncBridge", score: 0);
        var changes = result.Changes;

        Assert.That(changes["Svc.cs"], Does.Not.Contain("Score"),
            "Score named argument should be omitted when score is 0.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_AlwaysIncludesFlaggedDate()
    {
        SetSource(@"
public class Svc { public int Search(string q) => 0; }", "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync("Svc.cs", "Search", "AsyncBridge");
        var changes = result.Changes;

        Assert.That(changes["Svc.cs"], Does.Contain("FlaggedDate = \""),
            "FlaggedDate named argument should always be included.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Idempotency
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_SamePatternTwice_OnlyOneAttributeOnMethod()
    {
        // Pre-apply first flag directly so the method already has [MigrationCandidate("AsyncBridge")].
        const string preFlagged = @"
public class Svc
{
    [MigrationCandidate(""AsyncBridge"", Score = 5, FlaggedDate = ""2026-01-01"")]
    public int Search(string q) => 0;
}
internal sealed class MigrationCandidateAttribute : System.Attribute
{
    public MigrationCandidateAttribute(string p) {}
    public int Score { get; set; }
    public string Reason { get; set; }
    public string FlaggedDate { get; set; }
}";
        SetSource(preFlagged, "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "Svc.cs", "Search", "AsyncBridge", score: 20);
        var changes = result.Changes;

        Assert.That(result.WasAlreadyFlagged, Is.True,
            "WasAlreadyFlagged should be true when the same pattern is applied a second time.");
        Assert.That(result.PreviousPattern, Is.EqualTo("AsyncBridge"),
            "PreviousPattern should reflect the replaced attribute's pattern.");
        // Count occurrences of [MigrationCandidate in the target source.
        var src = changes["Svc.cs"];
        var count = StringCountHelper.CountOccurrences(src, "[MigrationCandidate(");
        Assert.That(count, Is.EqualTo(1),
            "Re-flagging the same pattern should replace, not duplicate, the attribute.");
        Assert.That(src, Does.Contain("Score = 20"),
            "New score should be reflected after re-flagging.");
    }

    [Test, CancelAfter(5000)]
    public async Task FlagMigrationCandidate_DifferentPattern_BothAttributesPreserved()
    {
        const string preFlagged = @"
public class Svc
{
    [MigrationCandidate(""AsyncBridge"", FlaggedDate = ""2026-01-01"")]
    public int Search(string q) => 0;
}
internal sealed class MigrationCandidateAttribute : System.Attribute
{
    public MigrationCandidateAttribute(string p) {}
    public int Score { get; set; }
    public string Reason { get; set; }
    public string FlaggedDate { get; set; }
}";
        SetSource(preFlagged, "Svc.cs");

        var result = await _engine.FlagMigrationCandidateAsync(
            "Svc.cs", "Search", "HandlerExtract");
        var changes = result.Changes;

        Assert.That(result.WasAlreadyFlagged, Is.False,
            "WasAlreadyFlagged should be false when adding a new (different) pattern.");
        var src = changes["Svc.cs"];
        Assert.That(src, Does.Contain("[MigrationCandidate(\"AsyncBridge\""),
            "Original AsyncBridge flag should be preserved.");
        Assert.That(src, Does.Contain("[MigrationCandidate(\"HandlerExtract\""),
            "New HandlerExtract flag should be added alongside the existing one.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Bridge conversion strips the flag
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task ConvertToAsyncBridge_StripesMigrationCandidateAttribute()
    {
        SetSource(@"
public class Svc
{
    [MigrationCandidate(""AsyncBridge"", Score = 18, FlaggedDate = ""2026-05-27"")]
    public int Search(string q) { return 0; }
}
internal sealed class MigrationCandidateAttribute : System.Attribute
{
    public MigrationCandidateAttribute(string p) {}
    public int Score { get; set; }
    public string Reason { get; set; }
    public string FlaggedDate { get; set; }
}", "Svc.cs");

        var result = await _engine.ConvertToAsyncBridgeAsync("Svc.cs", "Search");

        Assert.That(result, Does.Not.Contain("[MigrationCandidate("),
            "[MigrationCandidate] should be removed from the bridge wrapper after conversion.");
        Assert.That(result, Does.Contain("[Obsolete("),
            "[Obsolete] attribute should still be present on the bridge wrapper.");
        Assert.That(result, Does.Contain("SearchAsync"),
            "Async overload should still be created.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FindMigrationCandidates
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public async Task FindMigrationCandidates_ReturnsFlaggedMethods()
    {
        SetSources(
            ("Svc.cs", @"
public class Svc
{
    [MigrationCandidate(""AsyncBridge"", Score = 15, Reason = ""I/O"", FlaggedDate = ""2026-05-27"")]
    public int Search(string q) => 0;

    public void NotFlagged() {}
}
internal sealed class MigrationCandidateAttribute : System.Attribute
{
    public MigrationCandidateAttribute(string p) {}
    public int Score { get; set; }
    public string Reason { get; set; }
    public string FlaggedDate { get; set; }
}"));

        var findings = await _engine.FindMigrationCandidatesAsync();

        Assert.That(findings.Count, Is.EqualTo(1), "Only one method is flagged.");
        var f = findings[0];
        Assert.That(f.MethodName, Is.EqualTo("Search"));
        Assert.That(f.ClassName, Is.EqualTo("Svc"));
        Assert.That(f.Pattern, Is.EqualTo("AsyncBridge"));
        Assert.That(f.Score, Is.EqualTo(15));
        Assert.That(f.Reason, Is.EqualTo("I/O"));
        Assert.That(f.FlaggedDate, Is.EqualTo("2026-05-27"));
        Assert.That(f.Line, Is.GreaterThan(0));
    }

    [Test, CancelAfter(5000)]
    public async Task FindMigrationCandidates_PatternFilter_ReturnsOnlyMatchingPattern()
    {
        SetSources(
            ("Svc.cs", @"
public class Svc
{
    [MigrationCandidate(""AsyncBridge"",   FlaggedDate = ""2026-05-27"")]
    [MigrationCandidate(""HandlerExtract"",FlaggedDate = ""2026-05-27"")]
    public int Search(string q) => 0;
}
internal sealed class MigrationCandidateAttribute : System.Attribute
{
    public MigrationCandidateAttribute(string p) {}
    public int Score { get; set; }
    public string Reason { get; set; }
    public string FlaggedDate { get; set; }
}"));

        var bridgeFindings = await _engine.FindMigrationCandidatesAsync(pattern: "AsyncBridge");
        var handlerFindings = await _engine.FindMigrationCandidatesAsync(pattern: "HandlerExtract");
        var allFindings = await _engine.FindMigrationCandidatesAsync();

        Assert.That(bridgeFindings.Count, Is.EqualTo(1), "AsyncBridge filter should return 1.");
        Assert.That(handlerFindings.Count, Is.EqualTo(1), "HandlerExtract filter should return 1.");
        Assert.That(allFindings.Count, Is.EqualTo(2), "No filter should return both.");
    }

    [Test, CancelAfter(5000)]
    public async Task FindMigrationCandidates_EmptySolution_ReturnsEmptyList()
    {
        SetSource(@"
public class Svc { public int GetCount() => 0; }", "Svc.cs");

        var findings = await _engine.FindMigrationCandidatesAsync();

        Assert.That(findings, Is.Empty, "No flagged methods should return an empty list.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Error cases
    // ══════════════════════════════════════════════════════════════════════════

    [Test, CancelAfter(5000)]
    public void FlagMigrationCandidate_FileNotFound_Throws()
    {
        SetSource(@"public class Svc {}", "Svc.cs");

        Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await _engine.FlagMigrationCandidateAsync("Missing.cs", "Foo", "AsyncBridge"),
            "Should throw when the file is not in the loaded solution.");
    }

    [Test, CancelAfter(5000)]
    public void FlagMigrationCandidate_MethodNotFound_Throws()
    {
        SetSource(@"public class Svc { public int GetCount() => 0; }", "Svc.cs");

        Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await _engine.FlagMigrationCandidateAsync("Svc.cs", "NonExistent", "AsyncBridge"),
            "Should throw when the named method does not exist.");
    }
}

/// <summary>
/// Extension helpers for test assertions over file path dictionaries.
/// </summary>
file static class DictionaryTestExtensions
{
    /// <summary>
    /// Returns the first key whose file name (last path segment) ends with
    /// <paramref name="suffix"/> (case-insensitive), or <c>null</c> if none match.
    /// </summary>
    internal static string? FirstOrDefaultByFileNameSuffix(
        this IEnumerable<string> keys, string suffix)
        => keys.FirstOrDefault(k =>
            System.IO.Path.GetFileName(k)
                  .EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase));
}

/// <summary>Local helper used in idempotency tests.</summary>
file static class StringCountHelper
{
    internal static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(pattern, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}

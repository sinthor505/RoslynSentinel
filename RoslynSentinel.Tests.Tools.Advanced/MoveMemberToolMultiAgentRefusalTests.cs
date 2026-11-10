using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

/// <summary>
/// Tool-level proof that MoveMemberTool refuses a real (non-dry-run) move that leaves call sites unresolved when the
/// process runs in MultiAgent mode, before anything is written. The test-only seam adds one pending ledger entry to the
/// real move result. Dry runs and SingleAgent mode keep their existing behaviour.
/// </summary>
[TestFixture]
// AgentOperatingModeOptions is a process-wide static: the mode selected by one fixture would leak into parallel fixtures.
[NonParallelizable]
public class MoveMemberToolMultiAgentRefusalTests
{
    private const string Eol = "\r\n";
    private const string FileName = "Refactor.cs";

    private const string MultiAgentMarker = "MultiAgent mode";

    private static readonly string OriginalText = Lines(
        "namespace Example;",
        "",
        "public class Src",
        "{",
        "    public static int Foo(int x) { return x + 1; }",
        "}",
        "",
        "public class Target",
        "{",
        "}");

    private IWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    private void SetRefactorSource()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(FileName, OriginalText)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private CoreRefactoringTools CreateSeamTools()
    {
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new PendingLedgerRefactoringEngine(_workspaceManager, symbolNav, validation);
        return CreateTools(engine, symbolNav, validation);
    }

    private CoreRefactoringTools CreateTools(MemberRefactoringEngine memberEngine, SymbolNavigationEngine symbolNav, ValidationEngine validation)
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        return new CoreRefactoringTools(
            Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            memberEngine,
            symbolNav,
            _workspaceManager,
            validation,
            NullLogger<CoreRefactoringTools>.Instance);
    }

    private async Task<string> ReadCommittedRefactorText()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == FileName);
        return (await doc.GetTextAsync()).ToString();
    }

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MultiAgent_RealApplyWithUnresolvedCallSites_IsRefusedBeforeAnythingIsWritten()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=multi-agent" });
        SetRefactorSource();
        var tools = CreateSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.True, "MultiAgent mode must refuse a real move that leaves call sites unresolved");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.UnresolvedCallSites), $"{result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        Assert.That(result.ErrorData?.Message, Does.Contain(MultiAgentMarker), "The refusal must name the MultiAgent mode");
        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), FileName)), Is.False,
            "A refused move must not write Refactor.cs to disk");
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(OriginalText), "A refused move must not change the committed text");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MultiAgent_DryRun_IsNotRefused()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=multi-agent" });
        SetRefactorSource();
        var tools = CreateSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", dryRun: true, oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.ErrorData?.Message ?? string.Empty, Does.Not.Contain(MultiAgentMarker), "A dry run must not be refused by the MultiAgent check");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SingleAgent_RealApplyWithUnresolvedCallSites_IsNotRefusedByTheModeCheck()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });
        SetRefactorSource();
        var tools = CreateSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.ErrorData?.Message ?? string.Empty, Does.Not.Contain(MultiAgentMarker), "SingleAgent mode must not be refused by the MultiAgent check");
    }

    /// <summary>
    /// Test-only seam: simulates a move that succeeds but leaves one unresolved call site. The real move is untouched;
    /// only PendingLedgerEntries is added to its result.
    /// </summary>
    private sealed class PendingLedgerRefactoringEngine : MemberRefactoringEngine
    {
        public PendingLedgerRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ValidationEngine validationEngine)
            : base(workspaceManager, symbolNavigationEngine, validationEngine)
        {
        }

        public override async Task<MoveMemberResult> MoveMemberAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default, bool autoResolveCallSites = true, Dictionary<string, string>? callSiteFixups = null, IReadOnlyDictionary<string, string>? fieldMap = null, MoveMemberOldMember oldMember = MoveMemberOldMember.remove, string? forwarderReceiver = null)
        {
            var result = await base.MoveMemberAsync(filePath, className, memberNames, targetClassName, targetFilePath, cancellationToken, autoResolveCallSites, callSiteFixups, fieldMap, oldMember, forwarderReceiver);
            return result with { PendingLedgerEntries = [SeamEntry(FileName)] };
        }
    }

    private static CallSiteLedgerEntry SeamEntry(string filePath) => new CallSiteLedgerEntry
    {
        EntryId = "seam0001",
        FilePath = filePath,
        Line = 1,
        BrokenExpression = "Foo",
        OldStaticType = "Src",
        Status = CallSiteStatus.NoCandidateIntroducible,
    };
}

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

/// <summary>
/// Tool-level proof that MoveMemberTool surfaces the collateral-change refusal unmasked. The test-only engine
/// subclass runs a real keepDelegator move, then normalizes the source file's text (drops the blank line after the
/// namespace line), a change the move did not make. The guard must refuse it with CollateralChangeRefused and write
/// nothing. Control: the plain engine passes the same call, so the refusal is the normalizer's doing.
/// </summary>
[TestFixture]
// Runs exclusively: the leak guard wipes top-level *.cs in the shared working directory, which would delete files parallel fixtures in this assembly are using (AssemblyInfo.cs sets ParallelScope.Fixtures).
[NonParallelizable]
public class MoveMemberToolCollateralRefusalTests
{
    private const string Eol = "\r\n";
    private const string FileName = "Refactor.cs";

    private const string CallerFileName = "Caller.cs";

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

    private static readonly string InstanceSourceText = Lines(
    "namespace Example;",
    "",
    "public class A",
    "{",
    "    public int Count(string s, bool b) => s.Length;",
    "}");

    private static readonly string CallerText = Lines(
    "namespace Example;",
    "",
    "public class C",
    "{",
    "    int M() { var a = new A(); return a.Count(\"test\", true); }",
    "}");

    private IWorkspaceManager _workspaceManager;
    private string _projectDir;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (_projectDir != null && System.IO.Directory.Exists(_projectDir))
        {
            System.IO.Directory.Delete(_projectDir, recursive: true);
        }
        _projectDir = null;
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    private void SetRefactorSource()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(FileName, OriginalText)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetInstanceSolution()
    {
        // Absolute document paths under the project directory, so the engine derives Counter.cs's path under
        // the project and FindContainingProject can place it. The files are written to disk for the on-disk pre-image.
        _projectDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RoslynSentinelMoveMember", System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_projectDir);
        var refactorPath = System.IO.Path.Combine(_projectDir, FileName);
        var callerPath = System.IO.Path.Combine(_projectDir, CallerFileName);
        System.IO.File.WriteAllText(refactorPath, InstanceSourceText);
        System.IO.File.WriteAllText(callerPath, CallerText);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            System.IO.Path.Combine(_projectDir, "TestProj.csproj"),
            [(FileName, InstanceSourceText, refactorPath), (CallerFileName, CallerText, callerPath)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private AdvancedRefactoringTools CreateTools(MemberRefactoringEngine memberEngine, SymbolNavigationEngine symbolNav, ValidationEngine validation)
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        return new AdvancedRefactoringTools(
            Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager),
            Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager),
            memberEngine,
            Battery.Basic.TestEngines.SemanticRefactoringEngine(_workspaceManager),
            Battery.Advanced.TestEnginesAdvanced.MappingEngine(_workspaceManager),
            Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, config),
            Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager),
            Battery.Advanced.TestEnginesAdvanced.CodeGenerationEngine(_workspaceManager),
            symbolNav,
            _workspaceManager,
            validation,
            config,
            NullLogger<AdvancedRefactoringTools>.Instance);
    }

    private async Task<string> ReadCommittedRefactorText()
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == FileName);
        return (await doc.GetTextAsync()).ToString();
    }

    private async Task<string> ReadCommittedText(string fileName)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var doc = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);
        return (await doc.GetTextAsync()).ToString();
    }

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NormalizedSourceFile_IsRefusedUnmaskedAndWritesNothing()
    {
        SetRefactorSource();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new NormalizingMemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.True, "The guard must refuse the normalized source text");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.CollateralChangeRefused), $"{result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        Assert.That(result.ErrorData?.Message, Does.Contain(FileName), "The refusal must name the file it refused");
        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), FileName)), Is.False,
            "A refused move must not write Refactor.cs to disk");
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(OriginalText), "A refused move must not change the committed text");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_PlainEngine_SameCallSucceeds()
    {
        SetRefactorSource();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_DryRun_ReturnsEditSpansForTheTouchedFilesAndWritesNothing()
    {
        SetRefactorSource();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", dryRun: true, oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.EditSpans, Is.Not.Null.And.Not.Empty, "A dry run must report the edit spans it validated");
        Assert.That(summary.ChangedContent, Is.Not.Null, "A dry run must report the proposed file contents");

        var touchedFiles = summary.ChangedContent!.Keys.Select(f => f.ToString()).ToList();
        foreach (var span in summary.EditSpans!)
        {
            Assert.That(touchedFiles, Does.Contain(span.FilePath), "Every span must belong to a touched file");
            Assert.That(span.Start, Is.GreaterThanOrEqualTo(0));
            Assert.That(span.Length, Is.GreaterThanOrEqualTo(0));
            Assert.That(span.Start + span.Length, Is.LessThanOrEqualTo(OriginalText.Length), "A span must lie inside the original text");
            Assert.That(span.NewTextTruncated, Is.False, "The forwarder text is short, so it must not be truncated");
        }

        // The move touches only Refactor.cs. Replaying the spans against the ORIGINAL text, last span first,
        // must reproduce the proposed content.
        Assert.That(summary.EditSpans!.All(s => s.FilePath == FileName), Is.True, "This single-file move must only report spans for Refactor.cs");
        var proposed = summary.ChangedContent!.Single(kv => System.IO.Path.GetFileName(kv.Key.ToString()) == FileName).Value;
        var replayed = OriginalText;
        foreach (var span in summary.EditSpans!.OrderByDescending(s => s.Start))
        {
            replayed = replayed.Remove(span.Start, span.Length).Insert(span.Start, span.NewText);
        }
        Assert.That(replayed, Is.EqualTo(proposed), "Applying the spans to the original text must reproduce the proposed content");

        Assert.That(System.IO.File.Exists(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), FileName)), Is.False,
            "A dry run must not write Refactor.cs to disk");
        Assert.That(await ReadCommittedRefactorText(), Is.EqualTo(OriginalText), "A dry run must not change the committed text");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_RealApply_ReturnsNoEditSpans()
    {
        SetRefactorSource();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.False);
        Assert.That(summary.EditSpans, Is.Null, "A real apply must not report edit spans");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_InstanceToNewClass_LocalReceiverCaller_IsRefusedWithUnresolvedCallSitesAndWritesNothing()
    {
        SetInstanceSolution();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", System.IO.Path.Combine(_projectDir, FileName), "A", ["Count"], "Counter", oldMember: MoveMemberOldMember.remove);

        Assert.That(result.IsError, Is.True, "A local-receiver call site with no callSiteFixups must be refused");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.UnresolvedCallSites),
            $"{result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        Assert.That(result.ErrorData?.Message, Does.Contain("callSiteFixups"), "The refusal must tell the caller to retry with callSiteFixups");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(_projectDir, FileName)), Is.EqualTo(InstanceSourceText), "A refused move must not change Refactor.cs on disk");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(_projectDir, CallerFileName)), Is.EqualTo(CallerText), "A refused move must not change Caller.cs on disk");
        Assert.That(await ReadCommittedText(FileName), Is.EqualTo(InstanceSourceText), "A refused move must not change the committed source text");
        Assert.That(await ReadCommittedText(CallerFileName), Is.EqualTo(CallerText), "A refused move must not change the committed caller text");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_InstanceToNewClass_LocalReceiverCaller_RetryWithNewFixupSucceeds()
    {
        SetInstanceSolution();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);
        var fixups = new Dictionary<string, string> { ["*"] = "new" };

        var result = await tools.MoveMemberTool(reason: "test message", System.IO.Path.Combine(_projectDir, FileName), "A", ["Count"], "Counter", callSiteFixups: fixups, oldMember: MoveMemberOldMember.remove);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var callerText = System.IO.File.ReadAllText(System.IO.Path.Combine(_projectDir, CallerFileName));
        Assert.That(callerText, Does.Contain("new Counter().Count(\"test\", true)"), "The caller must call the moved member through a new zero-argument receiver");
        Assert.That(callerText, Does.Not.Contain("a.Count("), "The local-receiver call must no longer go through the source instance");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SkippedCallSiteAndNormalizedText_CollateralRefusalIsNotMaskedByUnresolvedCallSites()
    {
        SetInstanceSolution();
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new NormalizingMemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", System.IO.Path.Combine(_projectDir, FileName), "A", ["Count"], "Counter", oldMember: MoveMemberOldMember.remove);

        Assert.That(engine.LastResult?.SkippedCallSites.Count, Is.GreaterThan(0), "The engine must record the local-receiver call site as skipped for this test to pin the exemption");
        Assert.That(result.IsError, Is.True, "The guard must refuse the normalized source text");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.CollateralChangeRefused),
            $"A collateral refusal must not be replaced by UnresolvedCallSites: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        Assert.That(result.ErrorData?.Message, Does.Contain(FileName), "The refusal must name the file it refused");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(_projectDir, FileName)), Is.EqualTo(InstanceSourceText), "A refused move must not change Refactor.cs on disk");
        Assert.That(System.IO.File.ReadAllText(System.IO.Path.Combine(_projectDir, CallerFileName)), Is.EqualTo(CallerText), "A refused move must not change Caller.cs on disk");
        Assert.That(await ReadCommittedText(FileName), Is.EqualTo(InstanceSourceText), "A refused move must not change the committed source text");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_PendingLedgerRealApply_OpensLedgerThatBlocksOnlyUnrelatedFiles()
    {
        SetRefactorSource();
        var tools = CreateLedgerSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("Opened a scoped operation ledger with 1 unresolved call site(s)"));

        var ledger = (IScopedOperationLedger)_workspaceManager;
        Assert.That(ledger.GetOpenEntries(), Has.Count.EqualTo(1), "The opened ledger must hold the one pending entry");
        Assert.That(ledger.IsBlocked(FileName, out _), Is.False, "The moved member's own file must stay writable");
        Assert.That(ledger.IsBlocked(CallerFileName, out var reason), Is.True, "An unrelated file must be refused while the ledger is open");
        Assert.That(reason, Is.Not.Null);
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_PendingLedgerDryRun_OpensNoLedger()
    {
        SetRefactorSource();
        var tools = CreateLedgerSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", dryRun: true, oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Not.Contain("scoped operation ledger"), "A dry run must not mention opening a ledger");
        Assert.That(((IScopedOperationLedger)_workspaceManager).GetOpenEntries(), Is.Empty, "A dry run must not open a ledger");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_PendingLedgerWhileLedgerAlreadyOpen_TakesTheWarningBranch()
    {
        SetRefactorSource();
        var ledger = (IScopedOperationLedger)_workspaceManager;
        Assert.That(ledger.TryOpen("Preexisting", [SeamEntry(FileName)], out _), Is.True, "Precondition: a ledger is already open");
        var tools = CreateLedgerSeamTools();

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("WARNING:").And.Contain("a ledger could not be opened"));
        Assert.That(ledger.GetOpenEntries().Single().FilePath, Is.EqualTo(FileName), "The pre-existing ledger must stay the open one");
    }

    private AdvancedRefactoringTools CreateLedgerSeamTools()
    {
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = Battery.Basic.TestEngines.ValidationEngine(_workspaceManager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new PendingLedgerRefactoringEngine(_workspaceManager, symbolNav, validation);
        return CreateTools(engine, symbolNav, validation);
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

    /// <summary>
    /// Test-only seam: the real move runs, then the source file's text is normalized (the blank line after the
    /// namespace line is removed), an effect the move did not make. AppliedEdits is passed through untouched.
    /// </summary>
    private sealed class NormalizingMemberRefactoringEngine : MemberRefactoringEngine
    {
        public NormalizingMemberRefactoringEngine(IWorkspaceManager workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ValidationEngine validationEngine)
            : base(workspaceManager, symbolNavigationEngine, validationEngine)
        {
        }

        public MoveMemberResult? LastResult { get; private set; }

        public override async Task<MoveMemberResult> MoveMemberAsync(FilePathWrapper filePath, string className, string[] memberNames, string targetClassName, FilePathWrapper? targetFilePath = null, CancellationToken cancellationToken = default, bool autoResolveCallSites = true, Dictionary<string, string>? callSiteFixups = null, IReadOnlyDictionary<string, string>? fieldMap = null, MoveMemberOldMember oldMember = MoveMemberOldMember.remove, string? forwarderReceiver = null)
        {
            var result = await base.MoveMemberAsync(filePath, className, memberNames, targetClassName, targetFilePath, cancellationToken, autoResolveCallSites, callSiteFixups, fieldMap, oldMember, forwarderReceiver);
            var changes = new Dictionary<FilePathWrapper, string>(result.Changes);
            foreach (var key in changes.Keys.ToList())
            {
                if (System.IO.Path.GetFileName(key.ToString()) == FileName)
                {
                    changes[key] = StripBlankLineAfterNamespace(changes[key]);
                }
            }

            LastResult = result with { Changes = changes };
            return LastResult;
        }

        private static string StripBlankLineAfterNamespace(string text)
        {
            const string Marker = "namespace Example;" + Eol + Eol;
            var index = text.IndexOf(Marker, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException("The engine text must keep the blank line after the namespace line");
            }

            return text.Substring(0, index) + "namespace Example;" + Eol + text.Substring(index + Marker.Length);
        }
    }

    /// <summary>
    /// Test-only seam: simulates a move that succeeds but leaves one unresolved call site, so the tool's ledger-open
    /// branch runs. The real move is untouched; only PendingLedgerEntries is added to its result.
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
}

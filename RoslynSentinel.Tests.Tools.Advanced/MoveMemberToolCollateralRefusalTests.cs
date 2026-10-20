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
        DeleteLeakedRelativeFiles();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
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
        DeleteLeakedRelativeFiles();
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
        var config = new SentinelConfiguration();
        return new AdvancedRefactoringTools(
            new BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new AdvancedRefactoringEngine(_workspaceManager),
            new StructuralRefactoringEngine(_workspaceManager),
            memberEngine,
            new SemanticRefactoringEngine(_workspaceManager),
            new MappingEngine(_workspaceManager),
            new SyntaxModernizationEngine(_workspaceManager, config),
            new MsToolAugmentEngine(_workspaceManager),
            new CodeGenerationEngine(_workspaceManager),
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

    /// <summary>
    /// Fixture documents use bare relative names, so applied edits land in the working directory. Clearing the
    /// working directory's .cs files before and after each test keeps the on-disk pre-image check honest.
    /// </summary>
    private static void DeleteLeakedRelativeFiles()
    {
        foreach (var leaked in System.IO.Directory.GetFiles(System.IO.Directory.GetCurrentDirectory(), "*.cs", System.IO.SearchOption.TopDirectoryOnly))
        {
            System.IO.File.Delete(leaked);
        }
    }

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NormalizedSourceFile_IsRefusedUnmaskedAndWritesNothing()
    {
        SetRefactorSource();
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
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
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
        var tools = CreateTools(engine, symbolNav, validation);

        var result = await tools.MoveMemberTool(reason: "test message", FileName, "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message} | Detail: {result.ErrorData?.Detail}");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_DryRun_ReturnsEditSpansForTheTouchedFilesAndWritesNothing()
    {
        SetRefactorSource();
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
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
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
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
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
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
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validation);
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
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
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
}

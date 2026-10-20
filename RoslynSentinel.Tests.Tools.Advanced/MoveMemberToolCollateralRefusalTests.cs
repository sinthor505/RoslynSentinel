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
        DeleteLeakedRelativeFiles();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        DeleteLeakedRelativeFiles();
    }

    private void SetRefactorSource()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(FileName, OriginalText)]);
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
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.CollateralChangeRefused), result.ErrorData?.Message);
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

        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
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

            return result with { Changes = changes };
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

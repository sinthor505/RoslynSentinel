using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class EditAttributionRevalidationTests
{
    private const string AOriginal = "namespace T;\r\npublic class A\r\n{\r\n    public int M() => 1;\r\n    public int N() => 2;\r\n}\r\n";
    private const string BOriginal = "namespace T;\r\npublic class B\r\n{\r\n    public int C() => 0;\r\n}\r\n";
    private const string ANoM = "namespace T;\r\npublic class A\r\n{\r\n    public int N() => 2;\r\n}\r\n";
    private const string BCallsM = "namespace T;\r\npublic class B\r\n{\r\n    public int C() => new A().M();\r\n}\r\n";
    private const string BOk = "namespace T;\r\npublic class B\r\n{\r\n    public int C() => new A().N();\r\n}\r\n";
    private const string BMissing = "namespace T;\r\npublic class B\r\n{\r\n    public int C() => new Missing().M();\r\n}\r\n";

    private string _dir = null!;
    private string _aPath = null!;
    private string _bPath = null!;
    private PersistentWorkspaceManager _manager = null!;
    private EditAttributionLedger _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "EditAttributionRevalidation", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _aPath = Path.Combine(_dir, "A.cs");
        _bPath = Path.Combine(_dir, "B.cs");
        File.WriteAllText(_aPath, AOriginal);
        File.WriteAllText(_bPath, BOriginal);

        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "Revalidate", "Revalidate", LanguageNames.CSharp,
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }))
            .AddDocument(DocumentId.CreateNewId(projectId), "A.cs", SourceText.From(AOriginal), filePath: _aPath)
            .AddDocument(DocumentId.CreateNewId(projectId), "B.cs", SourceText.From(BOriginal), filePath: _bPath);

        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _manager.EolModeOverride = EolMode.Crlf;
        _manager.SetTestSolution(solution);
        _ledger = ((IEditAttributionLedgerProvider)_manager).EditAttributions;
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _manager.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private ToolCallContext Context(string? agent, bool gatePassed)
    {
        var ctx = new ToolCallContext(agent, "implementer", "ReplaceSnippet", "race test", _ledger.CurrentSeq);
        if (gatePassed)
        {
            ctx.MarkGatePassed();
        }

        return ctx;
    }

    private ValidationEngine Validation() =>
        Battery.Basic.TestEngines.ValidationEngine(_manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);

    [Test]
    public async Task GatePassedThenForeignWriteBreaksIt_IsRefused()
    {
        var caller = Context("agent-b", true);
        ToolCallContext.Set(Context("agent-a", false));
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        ToolCallContext.Set(caller);
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BCallsM });
        Assert.That(result.IsError, Is.True);
        Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.ConcurrentEditRefused));
        Assert.That(result.Summary, Does.Contain("agent-a").And.Contain("MULTI-AGENT:"));
        Assert.That(File.ReadAllText(_bPath), Is.EqualTo(BOriginal));
    }

    [Test]
    public async Task ForeignWriteButStillCompiles_Proceeds()
    {
        var caller = Context("agent-b", true);
        ToolCallContext.Set(Context("agent-a", false));
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        ToolCallContext.Set(caller);
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BOk });
        Assert.That(result.IsError, Is.False);
        Assert.That(File.ReadAllText(_bPath), Is.EqualTo(BOk));
    }

    [Test]
    public async Task NoForeignWrite_DoesNotRevalidate()
    {
        ToolCallContext.Set(Context("agent-b", true));
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BMissing });
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task GateNotPassed_DoesNotRevalidate()
    {
        var caller = Context("agent-b", false);
        ToolCallContext.Set(Context("agent-a", false));
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        ToolCallContext.Set(caller);
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BCallsM });
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task OwnEarlierWrite_DoesNotRevalidate()
    {
        var caller = Context("agent-b", true);
        ToolCallContext.Set(caller);
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BCallsM });
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task CallerWithoutIdentity_DoesNotRevalidate()
    {
        var caller = Context(null, true);
        ToolCallContext.Set(Context("agent-a", false));
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        ToolCallContext.Set(caller);
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BCallsM });
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task AnonymousForeignWriter_DoesNotRevalidate()
    {
        var caller = Context("agent-b", true);
        ToolCallContext.Set(Context(null, false));
        var first = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_aPath] = ANoM });
        Assert.That(first.IsError, Is.False);

        ToolCallContext.Set(caller);
        var result = await _manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [_bPath] = BCallsM });
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    public async Task GatePassed_IsMarkedByHelper()
    {
        ToolCallContext.Set(Context("agent-b", false));
        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            Validation(), _manager, NullLogger.Instance,
            new Dictionary<FilePathWrapper, string> { [_bPath] = BOk }, "RevalidationTest");

        Assert.That(outcome.Error, Is.Null);
        Assert.That(ToolCallContext.Current?.GatePassed, Is.True);
    }

    [Test]
    public async Task GateFailure_DoesNotMark()
    {
        ToolCallContext.Set(Context("agent-b", false));
        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            Validation(), _manager, NullLogger.Instance,
            new Dictionary<FilePathWrapper, string> { [_bPath] = BMissing }, "RevalidationTest");

        Assert.That(outcome.Error, Is.Not.Null);
        Assert.That(ToolCallContext.Current?.GatePassed, Is.False);
    }

    [Test]
    public void LatestForeignSince_PicksNewestForeignAndSkipsOthers()
    {
        var ledger = new EditAttributionLedger();
        var pathA = Path.Combine(_dir, "A.cs");
        var pathB = Path.Combine(_dir, "B.cs");

        ledger.Append(pathA, new ToolCallContext("a", "implementer", "ReplaceSnippet", "t", 0), EditAttributionOp.Write, "hash");
        ledger.Append(pathB, new ToolCallContext("b", "implementer", "ReplaceSnippet", "t", 0), EditAttributionOp.Write, "hash");
        ledger.Append(pathA, new ToolCallContext("a", "implementer", "ReplaceSnippet", "t", 0), EditAttributionOp.Write, "hash");
        ledger.Append(pathA, new ToolCallContext(null, "implementer", "ReplaceSnippet", "t", 0), EditAttributionOp.Write, "hash");

        Assert.That(ledger.LatestForeignSince(0, "a")?.AgentId, Is.EqualTo("b"));
        Assert.That(ledger.LatestForeignSince(2, "a"), Is.Null);
        Assert.That(ledger.LatestForeignSince(0, "b")?.Seq, Is.EqualTo(3));
    }
}

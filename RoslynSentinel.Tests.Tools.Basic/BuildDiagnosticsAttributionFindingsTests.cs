using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]

public class BuildDiagnosticsAttributionFindingsTests
{
    private const string BrokenSource = "namespace T;\r\npublic class C { Missing m; }\r\n";

    private string? _dir;
    private PersistentWorkspaceManager? _manager;

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _manager?.Dispose();
        _manager = null;
        if (_dir != null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        _dir = null;
    }

    private WorkspaceBuildTestImpl CreateImpl(string? foreignAgentId)
    {
        _dir = Path.Combine(Path.GetTempPath(), "BuildDiagnosticsAttributionFindings", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "C.cs");
        File.WriteAllText(file, BrokenSource);
        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "Broken", "Broken", LanguageNames.CSharp,
                metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }))
            .AddDocument(DocumentId.CreateNewId(projectId), "C.cs", SourceText.From(BrokenSource), filePath: file);
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _manager.EolModeOverride = EolMode.Crlf;
        _manager.SetTestSolution(solution);
        if (foreignAgentId != null)
        {
            ((IEditAttributionLedgerProvider)_manager).EditAttributions.Append(
                file, new ToolCallContext(foreignAgentId, "implementer", "ReplaceSnippet", "other agent edit", 0), EditAttributionOp.Write, "hash");
        }

        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "Build", "build attribution check", 0));
        return new WorkspaceBuildTestImpl(_manager,
            Battery.Basic.TestEngines.DiagnosticEngine(_manager),
            Battery.Basic.TestEngines.BuildEngine(_manager),
            Battery.Basic.TestEngines.TestRunEngine(_manager),
            NullLogger<WorkspaceBuildTestImpl>.Instance);
    }

    [Test]
    public async Task GetDiagnostics_ErrorFileLastWrittenByOtherAgent_CarriesFinding()
    {
        var impl = CreateImpl("agent-b");

        var result = await impl.GetDiagnostics("diagnostics attribution check", ToolScope.solution);

        var attribution = result.Findings.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(attribution, Has.Count.EqualTo(1));
        Assert.That(attribution[0].Message, Does.Contain("agent-b"));
    }

    [Test]
    public async Task GetDiagnostics_NoForeignEntry_HasNoFinding()
    {
        var impl = CreateImpl(null);

        var result = await impl.GetDiagnostics("diagnostics attribution check", ToolScope.solution);

        Assert.That(result.Findings.Any(f => f.Source == "EditAttribution"), Is.False);
    }

    [Test]
    public async Task Build_FailedOnFileLastWrittenByOtherAgent_CarriesFinding()
    {
        var impl = CreateImpl("agent-b");

        var result = await impl.Build("build attribution check", BuildVerifyLevel.quickBuild, ToolScope.solution);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Findings.Any(f => f.Source == "EditAttribution" && f.Message.Contains("agent-b")), Is.True);
    }

    [Test]
    public async Task Build_FailedWithNoForeignEntry_HasNoFinding()
    {
        var impl = CreateImpl(null);

        var result = await impl.Build("build attribution check", BuildVerifyLevel.quickBuild, ToolScope.solution);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Findings.Any(f => f.Source == "EditAttribution"), Is.False);
    }

}

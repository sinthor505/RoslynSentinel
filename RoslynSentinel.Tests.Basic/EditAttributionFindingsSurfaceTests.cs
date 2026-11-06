using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class EditAttributionFindingsSurfaceTests
{
    [TearDown]
    public void ResetIdentity()
    {
        ToolCallContext.Set(null);
    }

    private static async Task<ApplyOutcome> GateFailureAsync(string? foreignAgentId)
    {
        const string source = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
        var dir = Path.Combine(Path.GetTempPath(), "EditAttributionFindingsSurface", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, source);
            var projectId = ProjectId.CreateNewId();
            var solution = new AdhocWorkspace().CurrentSolution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "Gate", "Gate", LanguageNames.CSharp,
                    metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }))
                .AddDocument(DocumentId.CreateNewId(projectId), "A.cs", SourceText.From(source), filePath: file);
            using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            manager.EolModeOverride = EolMode.Crlf;
            manager.SetTestSolution(solution);

            if (foreignAgentId != null)
            {
                ((IEditAttributionLedgerProvider)manager).EditAttributions.Append(
                    file, new ToolCallContext(foreignAgentId, "implementer", "ReplaceSnippet", "other agent edit", 0), EditAttributionOp.Write, "hash");
            }

            ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "ReplaceSnippet", "gate failure check", 0));
            var validation = Battery.Basic.TestEngines.ValidationEngine(manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
            var changes = new Dictionary<FilePathWrapper, string> { [file] = source.Replace("=> 1", "=> \"bad\"") };
            return await ValidateAndApplyHelper.ValidateAndApplyAsync(
                validation, manager, NullLogger.Instance, changes, "EditAttributionFindingsSurfaceTest");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Getter_ErrorFindings_AreLiftedAndJsonIgnoredInsideErrorData()
    {
        var finding = new Finding("EditAttribution", "m", FindingSeverity.Info);
        var other = new Finding("EditAttribution", "other", FindingSeverity.Info);
        var error = new ResultError("X", "m") { Findings = [finding] };

        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = error };
        Assert.That(result.Findings, Has.Member(finding));

        var json = JsonSerializer.Serialize(error);
        Assert.That(json, Does.Not.Contain("Findings").And.Not.Contain("findings"));

        var explicitResult = new SentinelCallToolResult<object> { IsError = true, ErrorData = error, Findings = [other] };
        Assert.That(explicitResult.Findings, Has.Count.EqualTo(1));
        Assert.That(explicitResult.Findings[0], Is.EqualTo(other));
    }

    [Test]
    public void Getter_NoFindingsAnywhere_IsEmpty()
    {
        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError("X", "m") };
        Assert.That(result.Findings, Is.Empty);
    }

    [Test]
    public async Task GateFailure_OnFileLastWrittenByOtherAgent_CarriesAttributionFinding()
    {
        var outcome = await GateFailureAsync("agent-b");

        Assert.That(outcome.Error, Is.Not.Null);
        var findings = outcome.Error!.Findings;
        Assert.That(findings, Is.Not.Null);
        var attribution = findings!.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(attribution, Has.Count.EqualTo(1));
        Assert.That(attribution[0].Message, Does.Contain("agent-b"));

        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = outcome.Error };
        Assert.That(result.Findings.Any(f => f.Source == "EditAttribution"), Is.True);
    }

    [Test]
    public async Task GateFailure_WithNoForeignEntry_HasNoAttributionFinding()
    {
        var outcome = await GateFailureAsync(null);

        Assert.That(outcome.Error, Is.Not.Null);
        var findings = outcome.Error!.Findings ?? Array.Empty<Finding>();
        Assert.That(findings.Any(f => f.Source == "EditAttribution"), Is.False);
    }
}

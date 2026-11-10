using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitStatusAttributionFindingsTests
{
    private string _repoDir = "";
    private PersistentWorkspaceManager _manager = null!;
    private GitTools _gitTools = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitAttribution_" + Guid.NewGuid());
        Directory.CreateDirectory(_repoDir);
        RunGit(_repoDir, "init");

        // GitTool finds the git root from the manager's solution root, so point the real manager at the temp repo.
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _manager.SolutionPath = Path.Combine(_repoDir, "Fake.sln");
        _gitTools = new GitTools(_manager, NullLogger<GitTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _manager.Dispose();
        Directory.Delete(_repoDir, recursive: true);
    }

    [Test]
    public async Task Status_DirtyFileLastWrittenByOtherAgent_CarriesFinding()
    {
        var filePath = Path.Combine(_repoDir, "File.cs");
        File.WriteAllText(filePath, "class File { }");
        ((IEditAttributionLedgerProvider)_manager).EditAttributions.Append(
            filePath,
            new ToolCallContext("agent-b", "implementer", "ReplaceSnippet", "other agent edit", 0),
            EditAttributionOp.Write,
            "hash");
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "ReplaceSnippet", "caller edit", 0));

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var attributed = (result.Findings ?? []).Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(attributed, Has.Count.EqualTo(1));
        Assert.That(attributed[0].Message, Does.Contain("agent-b"));
    }

    [Test]
    public async Task Status_NoForeignEntry_HasNoFinding()
    {
        File.WriteAllText(Path.Combine(_repoDir, "File.cs"), "class File { }");
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "ReplaceSnippet", "caller edit", 0));

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That((result.Findings ?? []).Where(f => f.Source == "EditAttribution"), Is.Empty);
    }

    [Test]
    public async Task Status_WithRepoPath_IsNotAttributed()
    {
        var filePath = Path.Combine(_repoDir, "File.cs");
        File.WriteAllText(filePath, "class File { }");
        ((IEditAttributionLedgerProvider)_manager).EditAttributions.Append(
            filePath,
            new ToolCallContext("agent-b", "implementer", "ReplaceSnippet", "other agent edit", 0),
            EditAttributionOp.Write,
            "hash");
        ToolCallContext.Set(new ToolCallContext("agent-a", "implementer", "ReplaceSnippet", "caller edit", 0));

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status, repoPath: _repoDir);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That((result.Findings ?? []).Where(f => f.Source == "EditAttribution"), Is.Empty);
    }
}

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Tests for EditAttributionFindings, the edit attribution finding helper.</summary>
[TestFixture]
public class EditAttributionFindingsTests
{
    private const string PathA = "C:/repo/A.cs";

    [Test]
    public void ForPaths_ForeignWriter_ReportsAgentToolAndReason()
    {
        var ledger = new EditAttributionLedger();
        ledger.Append(PathA, new ToolCallContext("agent-2", "coder", "WriteFile", "fix the thing", 0), EditAttributionOp.Write, "h1");

        var findings = EditAttributionFindings.ForPaths(ledger, "agent-1", new[] { PathA }, DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Source, Is.EqualTo("EditAttribution"));
        Assert.That(findings[0].Message, Does.Contain("agent-2").And.Contain("WriteFile").And.Contain("fix the thing"));
    }

    [Test]
    public void ForPaths_SameAgentAsCaller_ReturnsEmpty()
    {
        var ledger = new EditAttributionLedger();
        ledger.Append(PathA, new ToolCallContext("agent-1", "coder", "WriteFile", null, 0), EditAttributionOp.Write, "h1");

        var findings = EditAttributionFindings.ForPaths(ledger, "agent-1", new[] { PathA }, DateTime.UtcNow);

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void ForPaths_NullCallerId_ReturnsEmpty()
    {
        var ledger = new EditAttributionLedger();
        ledger.Append(PathA, new ToolCallContext("agent-2", "coder", "WriteFile", null, 0), EditAttributionOp.Write, "h1");

        var findings = EditAttributionFindings.ForPaths(ledger, null, new[] { PathA }, DateTime.UtcNow);

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void ForPaths_UnknownPath_ReturnsEmpty()
    {
        var ledger = new EditAttributionLedger();

        var findings = EditAttributionFindings.ForPaths(ledger, "agent-1", new[] { PathA }, DateTime.UtcNow);

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void ForPaths_SlashStylePath_MatchesBackslashKeyedEntry()
    {
        var ledger = new EditAttributionLedger();
        ledger.Append("C:\\repo\\B.cs", new ToolCallContext("agent-2", null, "ReplaceSnippet", null, 0), EditAttributionOp.Write, "h1");

        var findings = EditAttributionFindings.ForPaths(ledger, "agent-1", new[] { "C:/repo/B.cs" }, DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("agent-2"));
    }

    [Test]
    public void ForPaths_MoreThanFiveForeignFiles_ReturnsFivePlusSummary()
    {
        var ledger = new EditAttributionLedger();
        var paths = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var path = $"C:/repo/f{i}.cs";
            paths.Add(path);
            ledger.Append(path, new ToolCallContext("agent-2", null, "WriteFile", null, 0), EditAttributionOp.Write, "h");
        }

        var findings = EditAttributionFindings.ForPaths(ledger, "agent-1", paths, DateTime.UtcNow);

        Assert.That(findings, Has.Count.EqualTo(6));
        Assert.That(findings[5].Source, Is.EqualTo("EditAttribution"));
        Assert.That(findings[5].Message, Does.Contain("+1 more"));
    }

    [Test]
    public void ForCurrentCaller_ManagerWithoutLedgerProvider_ReturnsEmpty()
    {
        ToolCallContext.Set(new ToolCallContext("agent-1", "coder", "WriteFile", null, 0));
        try
        {
            var findings = EditAttributionFindings.ForCurrentCaller(new object(), new[] { PathA });

            Assert.That(findings, Is.Empty);
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }
}

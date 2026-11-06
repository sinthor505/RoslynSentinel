using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Tests for the in-memory edit attribution ledger. These are not tests of the scoped operation ledger.</summary>
[TestFixture]
public class EditAttributionLedgerTests
{
    private const string PathA = "C:/repo/A.cs";
    private const string PathB = "C:/repo/B.cs";

    [Test]
    public void Append_RecordsContextFields()
    {
        var ledger = new EditAttributionLedger();
        var context = new ToolCallContext("agent-1", "coder", "WriteFile", "fix the thing", 0);

        var entry = ledger.Append(PathA, context, EditAttributionOp.Write, "hash-1");

        Assert.That(entry.AgentId, Is.EqualTo("agent-1"));
        Assert.That(entry.AgentType, Is.EqualTo("coder"));
        Assert.That(entry.Tool, Is.EqualTo("WriteFile"));
        Assert.That(entry.Reason, Is.EqualTo("fix the thing"));
        Assert.That(entry.Op, Is.EqualTo(EditAttributionOp.Write));
        Assert.That(entry.HashAfter, Is.EqualTo("hash-1"));
        Assert.That(entry.Seq, Is.EqualTo(1L));
    }

    [Test]
    public void Append_NullContext_RecordsNullIdentity()
    {
        var ledger = new EditAttributionLedger();

        var entry = ledger.Append(PathA, null, EditAttributionOp.Create, null);

        Assert.That(entry.AgentId, Is.Null);
        Assert.That(entry.AgentType, Is.Null);
        Assert.That(entry.Tool, Is.Null);
        Assert.That(entry.Reason, Is.Null);
        Assert.That(ledger.GetHistory(PathA), Has.Count.EqualTo(1));
    }

    [Test]
    public void Seq_IncreasesAcrossFiles()
    {
        var ledger = new EditAttributionLedger();

        var first = ledger.Append(PathA, null, EditAttributionOp.Write, null);
        var second = ledger.Append(PathB, null, EditAttributionOp.Write, null);

        Assert.That(first.Seq, Is.EqualTo(1L));
        Assert.That(second.Seq, Is.EqualTo(2L));
        Assert.That(ledger.CurrentSeq, Is.EqualTo(2L));
    }

    [Test]
    public void GetLatest_ReturnsLastEntry_AndNullWhenUnknown()
    {
        var ledger = new EditAttributionLedger();
        Assert.That(ledger.GetLatest(PathA), Is.Null);

        ledger.Append(PathA, null, EditAttributionOp.Write, "h1");
        ledger.Append(PathA, null, EditAttributionOp.Write, "h2");

        var latest = ledger.GetLatest(PathA);
        Assert.That(latest, Is.Not.Null);
        Assert.That(latest!.HashAfter, Is.EqualTo("h2"));
        Assert.That(latest.Seq, Is.EqualTo(2L));
    }

    [Test]
    public void GetHistory_IsBounded_DropsOldest()
    {
        var ledger = new EditAttributionLedger();
        for (var i = 0; i < 10; i++)
        {
            ledger.Append(PathA, null, EditAttributionOp.Write, null);
        }

        var history = ledger.GetHistory(PathA);

        Assert.That(history, Has.Count.EqualTo(EditAttributionLedger.MaxEntriesPerFile));
        for (var i = 0; i < history.Count; i++)
        {
            Assert.That(history[i].Seq, Is.EqualTo((long)(i + 3)));
        }

        Assert.That(ledger.CurrentSeq, Is.EqualTo(10L));
    }

    [Test]
    public void GetHistory_UnknownPath_IsEmpty()
    {
        var ledger = new EditAttributionLedger();

        Assert.That(ledger.GetHistory(PathA), Is.Empty);
    }

    [Test]
    public void Reason_IsSanitizedAndCapped()
    {
        var ledger = new EditAttributionLedger();

        var withControl = ledger.Append(PathA, new ToolCallContext(null, null, "T", "a\r\nb", 0), EditAttributionOp.Write, null);
        Assert.That(withControl.Reason, Is.EqualTo("a  b"));

        var accented = ledger.Append(PathA, new ToolCallContext(null, null, "T", "caf" + (char)0xE9, 0), EditAttributionOp.Write, null);
        Assert.That(accented.Reason, Is.EqualTo("caf?"));

        var tooLong = ledger.Append(PathA, new ToolCallContext(null, null, "T", new string('x', 300), 0), EditAttributionOp.Write, null);
        Assert.That(tooLong.Reason, Has.Length.EqualTo(EditAttributionLedger.MaxReasonLength));
    }

    [Test]
    public void Append_Concurrent_GivesUniqueSeqsAndKeepsTheBound()
    {
        var ledger = new EditAttributionLedger();

        Parallel.For(0, 200, _ => { ledger.Append(PathA, null, EditAttributionOp.Write, null); });

        Assert.That(ledger.CurrentSeq, Is.EqualTo(200L));
        var history = ledger.GetHistory(PathA);
        Assert.That(history, Has.Count.EqualTo(EditAttributionLedger.MaxEntriesPerFile));
        for (var i = 1; i < history.Count; i++)
        {
            Assert.That(history[i].Seq, Is.GreaterThan(history[i - 1].Seq));
        }
    }
}

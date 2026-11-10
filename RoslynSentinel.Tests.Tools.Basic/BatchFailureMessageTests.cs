using System;
using System.Linq;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Battery.Basic;

// Pure string tests of the batch failure text rule: first FullDetailCount entries in full, later entries one line each,
// whole text under TotalCapChars. No workspace or tool is involved.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class BatchFailureMessageTests
{
    private const string Header = "Batch failed; no edit was applied.";

    [Test]
    public void Compose_TwoShortEntries_MatchesPlainJoin()
    {
        var failures = new[] { "batchEdits[0] (A.cs): first problem", "batchEdits[1] (B.cs): second problem" };

        string result = BatchFailureMessage.Compose(Header, failures);

        Assert.That(result, Is.EqualTo(Header + "\n" + string.Join("\n", failures)));
    }

    [Test]
    public void Compose_FifteenLargeEntries_StaysUnderCapAndNamesEveryIndex()
    {
        var failures = BuildEntries(15, 2300);

        string result = BatchFailureMessage.Compose(Header, failures);

        Assert.That(result.Length, Is.LessThan(12800));
        for (int i = 0; i < 15; i++)
        {
            Assert.That(result, Does.Contain($"batchEdits[{i}]"));
        }
    }

    [Test]
    public void Compose_FifteenLargeEntries_TailEntriesAreOneLineEachAndNoteCountsTwelve()
    {
        var failures = BuildEntries(15, 2300);

        string result = BatchFailureMessage.Compose(Header, failures);
        string[] lines = result.Split('\n');

        for (int i = 3; i < 15; i++)
        {
            string line = lines.Single(l => l.StartsWith($"batchEdits[{i}]", StringComparison.Ordinal));
            Assert.That(line.Length, Is.LessThanOrEqualTo(BatchFailureMessage.OneLineMaxChars + 3));
        }

        Assert.That(result, Does.Contain("(12 more failing edit(s) shown as one line each"));
    }

    [Test]
    public void Compose_ThreeEntriesOfOneThousandChars_AllStayWhole()
    {
        var failures = BuildEntries(3, 1000);

        string result = BatchFailureMessage.Compose(Header, failures);

        Assert.That(result, Is.EqualTo(Header + "\n" + string.Join("\n", failures)));
    }

    [Test]
    public void Compose_SingleEntryOverCap_IsCutWithOmittedSuffix()
    {
        var failures = BuildEntries(1, 9000);

        string result = BatchFailureMessage.Compose(Header, failures);

        Assert.That(result, Does.Contain(" chars omitted]"));
        Assert.That(result.Length, Is.LessThanOrEqualTo(BatchFailureMessage.TotalCapChars));
    }

    private static string[] BuildEntries(int count, int length)
    {
        return Enumerable.Range(0, count)
            .Select(i => $"batchEdits[{i}] (File.cs): ".PadRight(length, 'x'))
            .ToArray();
    }
}

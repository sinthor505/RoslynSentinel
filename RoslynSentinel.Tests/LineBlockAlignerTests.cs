namespace RoslynSentinel.Tests;

[Parallelizable(ParallelScope.All)]
public class LineBlockAlignerTests
{
    [Test]
    public void FindBestPrefixAlignment_FullMatch_ReturnsExpectedPositionAndIsFullMatch()
    {
        var lines = new[] { "a", "b", "c", "d", "e", "f" };
        var needle = new[] { "c", "d" };

        var best = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 0, 1, 2, 3, 4 });

        Assert.That(best.Position, Is.EqualTo(2));
        Assert.That(best.MatchedCount, Is.EqualTo(2));
        Assert.That(best.IsFullMatch, Is.True);
    }

    [Test]
    public void FindBestPrefixAlignment_PartialMatch_ReportsLongestMatchAndItsPosition()
    {
        var lines = new[] { "x", "a", "b", "q", "a", "z" };
        var needle = new[] { "a", "b", "c" };

        var best = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 0, 1, 4 });

        Assert.That(best.Position, Is.EqualTo(1));
        Assert.That(best.MatchedCount, Is.EqualTo(2));
        Assert.That(best.IsFullMatch, Is.False);
    }

    [Test]
    public void FindBestPrefixAlignment_EqualAlignments_EarlierCandidateInOrderWins()
    {
        var lines = new[] { "a", "b", "a", "b" };
        var needle = new[] { "a", "b", "c" };

        var listedLaterFirst = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 2, 0 });
        var listedEarlierFirst = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 0, 2 });

        Assert.That(listedLaterFirst.Position, Is.EqualTo(2));
        Assert.That(listedEarlierFirst.Position, Is.EqualTo(0));
    }

    [Test]
    public void FindBestPrefixAlignment_NoCandidateMatchesFirstNeedleLine_ReportsZeroMatchedCount()
    {
        var lines = new[] { "a", "b" };
        var needle = new[] { "z", "b" };

        var best = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 0, 1 });

        Assert.That(best.MatchedCount, Is.EqualTo(0));
        Assert.That(best.IsFullMatch, Is.False);
    }

    [Test]
    public void DescribeDivergence_PartialMatch_NamesFirstDivergentLine()
    {
        var lines = new[] { "alpha", "beta", "gamma", "delta", "epsilon" };
        var needle = new[] { "alpha", "beta", "XYZ" };
        var best = LineBlockAligner.FindBestPrefixAlignment(lines, needle, new[] { 0 });

        var message = LineBlockAligner.DescribeDivergence(lines, needle, best);

        Assert.That(message, Does.Contain("expected \"XYZ\" at line 3"));
        Assert.That(message, Does.Contain("found \"gamma\""));
    }
}

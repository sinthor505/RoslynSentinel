using NUnit.Framework;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Stage 1 of the multi-file move planner (MoveSetPlanner.ExpandAndCheckAsync) on real files: expansion of file and folder
/// pairs, per-file eligibility with all-or-nothing refusal, the per-call cap, and the reason-to-error-code map.
/// </summary>
[TestFixture]
public sealed class MoveSetPlannerEligibilityTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = MoveSetFixture.CreateRoot(nameof(MoveSetPlannerEligibilityTests));

    [TearDown]
    public void TearDown() => MoveSetFixture.DeleteRoot(_root);

    [Test]
    public async Task Expand_TwoFilePairs_ReturnsTwoPlannedMoves()
    {
        var a = MoveSetFixture.WriteText(Path.Combine(_root, "src", "A.txt"), "alpha");
        var b = MoveSetFixture.WriteText(Path.Combine(_root, "src", "B.txt"), "beta");

        var plan = await MoveSetPlanner.ExpandAndCheckAsync(
            Request((a, Path.Combine(_root, "dst", "A.txt")), (b, Path.Combine(_root, "dst", "B.txt"))),
            CancellationToken.None);

        Assert.That(plan.Moves.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task Expand_FolderPair_SubfoldersPreserved_NonCsFileIncluded()
    {
        var source = Path.Combine(_root, "src");
        var destination = Path.Combine(_root, "dst");
        MoveSetFixture.WriteText(Path.Combine(source, "Top.cs"), "class Top {}");
        MoveSetFixture.WriteText(Path.Combine(source, "Nested", "Inner.cs"), "class Inner {}");
        MoveSetFixture.WriteText(Path.Combine(source, "Nested", "Deep", "notes.txt"), "notes");

        var plan = await MoveSetPlanner.ExpandAndCheckAsync(Request((source, destination)), CancellationToken.None);

        var expected = new[]
        {
            Path.Combine(destination, "Top.cs"),
            Path.Combine(destination, "Nested", "Inner.cs"),
            Path.Combine(destination, "Nested", "Deep", "notes.txt"),
        };
        Assert.That(plan.Moves.Select(move => move.Destination.Absolute), Is.EquivalentTo(expected));
    }

    [Test]
    public async Task Expand_FolderPairIntoExistingFolder_SameNameFileRefuses()
    {
        var source = Path.Combine(_root, "src");
        var destination = Path.Combine(_root, "dst");
        MoveSetFixture.WriteText(Path.Combine(source, "Same.txt"), "from source");
        MoveSetFixture.WriteText(Path.Combine(source, "Other.txt"), "other");
        var existing = MoveSetFixture.WriteText(Path.Combine(destination, "Same.txt"), "already here");

        var ex = await Assert.ThrowsAsync<MoveSetRefusedException>(async () =>
            await MoveSetPlanner.ExpandAndCheckAsync(Request((source, destination)), CancellationToken.None));

        Assert.That(ex.Offenders.Count, Is.EqualTo(1));
        Assert.That(ex.Offenders[0].Reason, Is.EqualTo(MoveOffenderReason.DestinationExists));
        Assert.That(ex.Offenders[0].Detail, Does.Contain(existing));
    }

    [Test]
    public async Task Expand_OneBinaryOneNonUtf8OneExistingDestination_ListsAllThreeOffenders_DiskUntouched()
    {
        var binary = MoveSetFixture.WriteBinary(Path.Combine(_root, "src", "Blob.dat"));
        var nonUtf8 = MoveSetFixture.WriteNonUtf8(Path.Combine(_root, "src", "Latin.txt"));
        var clash = MoveSetFixture.WriteText(Path.Combine(_root, "src", "Clash.txt"), "clash source");
        var clashDestination = MoveSetFixture.WriteText(Path.Combine(_root, "dst", "Clash.txt"), "existing");
        var good = MoveSetFixture.WriteText(Path.Combine(_root, "src", "Good.txt"), "good");
        var before = MoveSetFixture.Snapshot(_root);

        var ex = await Assert.ThrowsAsync<MoveSetRefusedException>(async () =>
            await MoveSetPlanner.ExpandAndCheckAsync(
                Request(
                    (binary, Path.Combine(_root, "dst", "Blob.dat")),
                    (nonUtf8, Path.Combine(_root, "dst", "Latin.txt")),
                    (clash, clashDestination),
                    (good, Path.Combine(_root, "dst", "Good.txt"))),
                CancellationToken.None));

        Assert.That(
            ex.Offenders.Select(offender => offender.Reason),
            Is.EquivalentTo(new[] { MoveOffenderReason.Binary, MoveOffenderReason.NotUtf8, MoveOffenderReason.DestinationExists }));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        AssertSameBytes(before, MoveSetFixture.Snapshot(_root));
    }

    [Test]
    public async Task Expand_MoreThanMaxFilesPerCall_RefusesOverCap_BoundaryAccepted()
    {
        var overSource = Path.Combine(_root, "over");
        WriteFiles(overSource, MoveSetPlanner.MaxFilesPerCall + 1);

        var ex = await Assert.ThrowsAsync<MoveSetRefusedException>(async () =>
            await MoveSetPlanner.ExpandAndCheckAsync(Request((overSource, Path.Combine(_root, "over-dst"))), CancellationToken.None));

        Assert.That(ex.Offenders.Count, Is.EqualTo(1));
        Assert.That(ex.Offenders[0].Reason, Is.EqualTo(MoveOffenderReason.OverCap));

        var exactSource = Path.Combine(_root, "exact");
        WriteFiles(exactSource, MoveSetPlanner.MaxFilesPerCall);

        var plan = await MoveSetPlanner.ExpandAndCheckAsync(
            Request((exactSource, Path.Combine(_root, "exact-dst"))),
            CancellationToken.None);

        Assert.That(plan.Moves.Count, Is.EqualTo(MoveSetPlanner.MaxFilesPerCall));
    }

    [TestCase(MoveOffenderReason.DuplicateDestination)]
    [TestCase(MoveOffenderReason.SamePath)]
    [TestCase(MoveOffenderReason.Missing)]
    public async Task Expand_DuplicateDestination_SamePath_Missing_EachMapsToItsErrorCode(MoveOffenderReason reason)
    {
        var request = SingleReasonRequest(reason);

        var ex = await Assert.ThrowsAsync<MoveSetRefusedException>(async () =>
            await MoveSetPlanner.ExpandAndCheckAsync(request, CancellationToken.None));

        Assert.That(ex.ErrorCode, Is.EqualTo(MoveOffender.ErrorCodeOf(reason)));
        Assert.That(ex.Offenders.Select(offender => offender.Reason), Does.Contain(reason));
    }

    /// <summary>One request whose only refusal reason is <paramref name="reason"/>.</summary>
    private MoveSetRequest SingleReasonRequest(MoveOffenderReason reason)
    {
        switch (reason)
        {
            case MoveOffenderReason.DuplicateDestination:
            {
                // Two existing sources share one destination that does not exist yet: the second pair is refused.
                var first = MoveSetFixture.WriteText(Path.Combine(_root, "src", "A.txt"), "alpha");
                var second = MoveSetFixture.WriteText(Path.Combine(_root, "src", "B.txt"), "beta");
                var shared = Path.Combine(_root, "dst", "Same.txt");
                return Request((first, shared), (second, shared));
            }

            case MoveOffenderReason.SamePath:
            {
                // Source and destination resolve to the same existing file.
                var same = MoveSetFixture.WriteText(Path.Combine(_root, "src", "A.txt"), "alpha");
                return Request((same, same));
            }

            case MoveOffenderReason.Missing:
            {
                // The source file is never written.
                return Request((Path.Combine(_root, "src", "Absent.txt"), Path.Combine(_root, "dst", "Absent.txt")));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }
    }

    private static MoveSetRequest Request(params (string Source, string Destination)[] pairs)
    {
        return new MoveSetRequest(
            pairs.Select(pair => new FileMovePair { SourcePath = pair.Source, DestinationPath = pair.Destination }).ToList());
    }

    private static void WriteFiles(string folder, int count)
    {
        for (var i = 0; i < count; i++)
        {
            MoveSetFixture.WriteText(Path.Combine(folder, $"f{i:D3}.txt"), "x");
        }
    }

    private static void AssertSameBytes(IReadOnlyDictionary<string, byte[]> before, IReadOnlyDictionary<string, byte[]> after)
    {
        Assert.That(after.Keys.OrderBy(key => key, StringComparer.Ordinal), Is.EqualTo(before.Keys.OrderBy(key => key, StringComparer.Ordinal)));
        foreach (var (path, bytes) in before)
        {
            Assert.That(after[path], Is.EqualTo(bytes), path);
        }
    }
}

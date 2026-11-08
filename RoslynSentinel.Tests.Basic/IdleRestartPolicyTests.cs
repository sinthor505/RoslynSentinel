using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class IdleRestartPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string HeadSha = new string('a', 40);
    private static readonly string OwnSha = new string('c', 40);

    private static IdleRestartCheckRequest AllClear() => new(
        Enabled: true,
        NowUtc: T0,
        LastActivityUtc: T0.AddMinutes(-30),
        IdleThreshold: TimeSpan.FromMinutes(15),
        InFlightCount: 0,
        SessionHalted: false,
        OpenLedgerOperation: null,
        BuildGateBusy: false,
        Latest: new LatestBuildRecord(HeadSha, "bbbbbbbb", "built", false, null),
        DevHeadSha: HeadSha,
        OwnBuildSha: OwnSha);

    [Test]
    public void AllClearRequest_ReturnsNull()
    {
        Assert.That(IdleRestartPolicy.FirstUnmetGuard(AllClear()), Is.Null);
    }

    [Test]
    public void Disabled_ReturnsGuardDisabled()
    {
        var request = AllClear() with { Enabled = false };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardDisabled));
    }

    [Test]
    public void ToolCallInFlight_ReturnsGuardInFlight()
    {
        var request = AllClear() with { InFlightCount = 1 };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardInFlight));
    }

    [Test]
    public void ActivityFiveMinutesAgo_ReturnsGuardRecentActivity()
    {
        var request = AllClear() with { LastActivityUtc = T0.AddMinutes(-5) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardRecentActivity));
    }

    [Test]
    public void ActivityExactlyAtThreshold_IsNotRecentActivity()
    {
        var request = AllClear() with { LastActivityUtc = T0.AddMinutes(-15) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.Null);
    }

    [Test]
    public void ActivityOneSecondInsideThreshold_ReturnsGuardRecentActivity()
    {
        var request = AllClear() with { LastActivityUtc = T0.AddMinutes(-15).AddSeconds(1) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardRecentActivity));
    }

    [Test]
    public void SessionHalted_ReturnsGuardSessionHalted()
    {
        var request = AllClear() with { SessionHalted = true };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardSessionHalted));
    }

    [Test]
    public void OpenLedgerOperation_ReturnsGuardLedgerOpen()
    {
        var request = AllClear() with { OpenLedgerOperation = "MoveMember" };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardLedgerOpen));
    }

    [Test]
    public void BuildGateBusy_ReturnsGuardBuildGateBusy()
    {
        var request = AllClear() with { BuildGateBusy = true };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardBuildGateBusy));
    }

    [Test]
    public void LatestBuildNull_ReturnsGuardNoLatestBuild()
    {
        var request = AllClear() with { Latest = null };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardNoLatestBuild));
    }

    [Test]
    public void LatestBuildEmptyBuildSha8_ReturnsGuardNoLatestBuild()
    {
        var request = AllClear() with { Latest = new LatestBuildRecord(HeadSha, "", "built", false, null) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardNoLatestBuild));
    }

    [Test]
    public void LatestBuildFailed_ReturnsGuardLatestBuildFailed()
    {
        var request = AllClear() with { Latest = new LatestBuildRecord(HeadSha, "bbbbbbbb", "built", true, null) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardLatestBuildFailed));
    }

    [Test]
    public void DevHeadShaNull_ReturnsGuardUnknownHead()
    {
        var request = AllClear() with { DevHeadSha = null };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardUnknownHead));
    }

    [Test]
    public void OwnBuildShaEmpty_ReturnsGuardUnknownHead()
    {
        var request = AllClear() with { OwnBuildSha = "" };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardUnknownHead));
    }

    [Test]
    public void DevHeadDiffersFromLatestHead_ReturnsGuardPrebuildPending()
    {
        var request = AllClear() with { DevHeadSha = new string('d', 40) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardPrebuildPending));
    }

    [Test]
    public void OwnBuildStartsWithLatestBuildSha8IgnoringCase_ReturnsGuardAlreadyOnLatestBuild()
    {
        var request = AllClear() with { OwnBuildSha = "BBBBBBBB" + new string('c', 32) };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardAlreadyOnLatestBuild));
    }

    [Test]
    public void InFlightAndSessionHalted_ReturnsInFlight_BecauseItIsCheckedFirst()
    {
        var request = AllClear() with { InFlightCount = 1, SessionHalted = true };

        Assert.That(IdleRestartPolicy.FirstUnmetGuard(request), Is.EqualTo(IdleRestartPolicy.GuardInFlight));
    }
}

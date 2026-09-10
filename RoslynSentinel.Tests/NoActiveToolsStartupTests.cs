// Startup handling when the resolved arguments would activate no tool classes at all.
//
// Launching with no --mode (and no --include-tools) registers zero tool classes. Before this
// guard the failure surfaced from deep inside DI: the DEBUG smoke check demanded
// SentinelWorkspaceTools unconditionally and threw "Tool type not resolvable:
// SentinelWorkspaceTools" — a type the operator never mentioned, with no hint that a flag was
// missing. Found while probing the refreshed bin-vscode stdio server with a bare
// --transport=stdio.
//
// These are plain unit tests: the helpers are static and take the parsed argument sets directly,
// so no host or server process is needed.

using Microsoft.Extensions.DependencyInjection;

namespace RoslynSentinel.Tests;

[TestFixture]
public class NoActiveToolsStartupTests
{
    private static HashSet<string> Names(params string[] values) =>
        new(values, StringComparer.OrdinalIgnoreCase);

    private static string? Describe(
        string modeArg = "",
        IEnumerable<string>? activeModes = null,
        IEnumerable<string>? includeTools = null,
        IEnumerable<string>? excludeTools = null) =>
        ServerStartupHelpers.DescribeNoActiveToolsFailure(
            modeArg,
            ToolClassRegistry.AdvancedModeToToolClasses,
            new HashSet<string>(activeModes ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(includeTools ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(excludeTools ?? [], StringComparer.OrdinalIgnoreCase));

    [Test]
    public void BothOmitted_IsReportedAndNamesBothFlags()
    {
        // The exact case that crashed: no --mode, no --include-tools.
        var failure = Describe();

        Assert.That(failure, Is.Not.Null, "a launch that would expose zero tools must be reported");
        Assert.Multiple(() =>
        {
            Assert.That(failure, Does.Contain("--mode"));
            Assert.That(failure, Does.Contain("--include-tools"));
            Assert.That(failure, Does.Contain("--mode=all"), "must state the one-flag fix");
            Assert.That(failure, Does.Contain("--list-tools"), "must state how to check a combination");
            Assert.That(failure, Does.Not.Contain("SentinelWorkspaceTools"),
                "must not name an internal type the operator never asked for — that was the old message");
        });
    }

    [Test]
    public void BothEmptyRatherThanAbsent_IsTreatedTheSame()
    {
        // --mode= with an empty value parses to an empty mode set, indistinguishable from absent.
        // Worth pinning separately because the user asked for "omitted/empty", and an empty string
        // reaching modeArg must not be reported as an unrecognised mode name.
        var failure = Describe(modeArg: "");

        Assert.That(failure, Is.Not.Null);
        Assert.That(failure, Does.Contain("neither --mode nor --include-tools"));
    }

    [Test]
    public void ModeSupplied_IsNotReported()
    {
        var failure = Describe(modeArg: "Workspace", activeModes: ["Workspace"]);

        Assert.That(failure, Is.Null, "a mode that resolves to real classes must start normally");
    }

    [Test]
    public void IncludeToolsAloneWithNoMode_IsNotReported()
    {
        // --include-tools without --mode is a supported way to run a narrow surface, so it must
        // not trip the guard.
        var failure = Describe(includeTools: ["SentinelGitTools"]);

        Assert.That(failure, Is.Null);
    }

    [Test]
    public void UnrecognisedModeName_ReportsItAndListsTheKnownModes()
    {
        // Distinguished from the both-omitted case: the operator did supply something, so the fix
        // is a correct name rather than a missing flag.
        var failure = Describe(modeArg: "Wokspace", activeModes: ["Wokspace"]);

        Assert.That(failure, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Does.Contain("Wokspace"), "must echo what was actually passed");
            Assert.That(failure, Does.Contain("Workspace"), "must list the known modes");
            Assert.That(failure, Does.Contain("Sentinel"), "must explain the implied prefix");
            Assert.That(failure, Does.Not.Contain("neither --mode nor"),
                "a supplied-but-wrong mode is a different diagnosis from an omitted one");
        });
    }

    [Test]
    public void ExcludeToolsRemovingEverything_IsReportedAsAnExcludeProblem()
    {
        // Third distinct route to zero. Exclude wins and is applied last, so the operator needs to
        // be told that rather than being pointed at --mode.
        var failure = Describe(
            modeArg: "Admin",
            activeModes: ["Admin"],
            excludeTools: ["SentinelAdminTools"]);

        Assert.That(failure, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(failure, Does.Contain("--exclude-tools"));
            Assert.That(failure, Does.Contain("SentinelAdminTools"), "must name what was excluded");
            Assert.That(failure, Does.Contain("Exclude always wins"));
        });
    }

    [Test]
    public void ModeAll_IsNotReported()
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);
        var failure = Describe(modeArg: "all", activeModes: allModes);

        Assert.That(failure, Is.Null);
    }

    [Test]
    public void SmokeResolve_SkipsTypesTheRunDidNotActivate()
    {
        // The other half of the fix. An empty service provider stands in for "nothing registered":
        // if the check still tried to resolve a type that wasn't in the active set it would throw,
        // so completing without exception is the assertion.
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.That(
            () => ServerStartupHelpers.SmokeResolveToolTypes(
                services,
                [typeof(SentinelWorkspaceTools)],
                Names("SentinelGitTools")),
            Throws.Nothing,
            "a type outside the active set must not be resolved, let alone reported as broken");
    }

    [Test]
    public void SmokeResolve_ActiveButUnregisteredType_NamesTheClassAndPointsAtItsDependencies()
    {
        // When a class really is active and really can't be built, the message should say so and
        // send the reader to the dependency rather than stopping at the outer type name.
        // DEBUG-only via [Conditional], which is how these suites run.
        var services = new ServiceCollection().BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ServerStartupHelpers.SmokeResolveToolTypes(
                services,
                [typeof(SentinelWorkspaceTools)],
                Names("SentinelWorkspaceTools")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("SentinelWorkspaceTools"));
            Assert.That(ex.Message, Does.Contain("dependencies"), "must point at the real cause");
            Assert.That(ex.InnerException, Is.Not.Null, "the underlying resolution failure must be preserved");
        });
    }
}

// --include-tools / --exclude-tools name resolution.
//
// NormalizeToolClassNames used to prepend "Sentinel" to every name unconditionally, but tool classes
// are now registered unprefixed (WorkspaceTools, GitTools; only SymbolNavigationTools keeps the
// prefix). So "--exclude-tools=GitTools" became "SentinelGitTools" and silently removed nothing,
// and "--include-tools=WorkspaceTools" silently added nothing. Found while implementing the
// SubAgent recursion guard, which depends on --exclude-tools actually excluding.
//
// Plain unit tests: ParseArgs and ResolveActiveToolClasses are static and take raw args, so no host
// or server process is needed.

using RoslynSentinel.Server.Basic;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ToolClassNameResolutionTests
{
    private static HashSet<string> ResolveFor(params string[] args)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.AdvancedModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);
        ServerStartupHelpers.ParseArgs(
            args, allModes,
            out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);

        return ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes, ToolClassRegistry.AdvancedModeToToolClasses, includeTools, excludeTools);
    }

    [Test]
    public void ExcludeTools_UnprefixedClassName_RemovesThatClass()
    {
        var active = ResolveFor("--mode=Workspace", "--exclude-tools=GitTools");

        Assert.Multiple(() =>
        {
            Assert.That(active, Does.Not.Contain("GitTools"), "the excluded class must actually be removed");
            Assert.That(active, Does.Contain("WorkspaceTools"), "exclusion must not touch other classes");
        });
    }

    [Test]
    public void ExcludeTools_LegacyPrefixedSpelling_StillRemovesTheUnprefixedClass()
    {
        var active = ResolveFor("--mode=Workspace", "--exclude-tools=SentinelGitTools");

        Assert.That(active, Does.Not.Contain("GitTools"));
    }

    [Test]
    public void ExcludeTools_ClassThatKeepsItsPrefix_IsRemovedByShortenedName()
    {
        // SymbolNavigationTools is the one registered class that keeps the prefix; the shortened form
        // older launch scripts type must still reach it.
        var active = ResolveFor("--mode=Workspace", "--exclude-tools=SymbolNavigationTools");

        Assert.That(active, Does.Not.Contain("SymbolNavigationTools"));
    }

    [Test]
    public void IncludeTools_UnprefixedClassName_ActivatesThatClass()
    {
        var active = ResolveFor("--include-tools=WorkspaceTools");

        Assert.That(active, Is.EquivalentTo(new[] { "WorkspaceTools" }));
    }

    [Test]
    public void IncludeTools_ShortenedPrefixedClassName_ActivatesThePrefixedClass()
    {
        var active = ResolveFor("--include-tools=SymbolNavigationTools");

        Assert.That(active, Is.EquivalentTo(new[] { "SymbolNavigationTools" }));
    }

    [Test]
    public void ExcludeTools_UnknownName_IsLeftAloneAndRemovesNothing()
    {
        var withUnknown = ResolveFor("--mode=Workspace", "--exclude-tools=NoSuchTools");
        var baseline = ResolveFor("--mode=Workspace");

        Assert.That(withUnknown, Is.EquivalentTo(baseline));
    }
}

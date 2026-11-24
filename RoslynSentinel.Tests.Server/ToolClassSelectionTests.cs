// ToolClassRegistry.SelectStartupToolTypes pins: which table rows a launch selects, in table order.
//
// A row is selected when its class is always on (ServerStatusTools), or when it is in the active class set and is either
// not a claude-lean-only class (ToolsetControlTools, DeclarationTools, ParameterEditTools) or the launch is claude-lean.
// Only those four classes plus WorkspaceTools are probed here: they are the ones the rule treats differently, and the
// rest of the table behaves like WorkspaceTools.

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ToolClassSelectionTests
{
    private static readonly string[] Probed =
    [
        "ServerStatusTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools", "WorkspaceTools",
    ];

    private static IEnumerable<TestCaseData> ActiveSetCases()
    {
        string[] everything = ["ToolsetControlTools", "DeclarationTools", "ParameterEditTools", "WorkspaceTools"];

        yield return new TestCaseData(Array.Empty<string>(), false, new[] { "ServerStatusTools" })
            .SetName("NothingActive_NotClaudeLean_SelectsOnlyTheAlwaysOnClass");
        yield return new TestCaseData(Array.Empty<string>(), true, new[] { "ServerStatusTools" })
            .SetName("NothingActive_ClaudeLean_SelectsOnlyTheAlwaysOnClass");
        yield return new TestCaseData(new[] { "WorkspaceTools" }, false, new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("OrdinaryClassActive_NotClaudeLean_IsSelected");
        yield return new TestCaseData(new[] { "WorkspaceTools" }, true, new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("OrdinaryClassActive_ClaudeLean_IsSelected");
        yield return new TestCaseData(everything, false, new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("ClaudeLeanOnlyClassesActive_NotClaudeLean_AreDropped");
        yield return new TestCaseData(everything, true,
                new[] { "ServerStatusTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools", "WorkspaceTools" })
            .SetName("ClaudeLeanOnlyClassesActive_ClaudeLean_AreSelectedInTableOrder");
        yield return new TestCaseData(new[] { "ToolsetControlTools", "ParameterEditTools", "WorkspaceTools" }, true,
                new[] { "ServerStatusTools", "ToolsetControlTools", "ParameterEditTools", "WorkspaceTools" })
            .SetName("ClaudeLean_ClassMissingFromTheActiveSet_IsNotSelected");
        yield return new TestCaseData(new[] { "DeclarationTools" }, true, new[] { "ServerStatusTools", "DeclarationTools" })
            .SetName("ClaudeLeanOnlyClassAlone_ClaudeLean_IsSelected");
    }

    [TestCaseSource(nameof(ActiveSetCases))]
    public void SelectStartupToolTypes_ActiveSet_SelectsTheExpectedProbedClasses(string[] active, bool claudeLean, string[] expected)
    {
        var activeSet = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);

        var selected = ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.BasicToolClassTypes, activeSet, claudeLean);

        Assert.That(ProbedNames(selected), Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> LaunchArgumentCases()
    {
        yield return new TestCaseData(new[] { "--mode=claude-lean" },
                new[] { "ServerStatusTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools", "WorkspaceTools" })
            .SetName("ClaudeLean_SelectsTheClaudeLeanOnlyClasses");
        yield return new TestCaseData(new[] { "--mode=claude-lean", "--exclude-tools=DeclarationTools" },
                new[] { "ServerStatusTools", "ToolsetControlTools", "ParameterEditTools", "WorkspaceTools" })
            .SetName("ClaudeLean_ExcludeDeclarationTools_DropsOnlyThatClass");
        yield return new TestCaseData(new[] { "--mode=claude-lean", "--exclude-tools=ServerStatusTools" },
                new[] { "ServerStatusTools", "ToolsetControlTools", "DeclarationTools", "ParameterEditTools", "WorkspaceTools" })
            .SetName("ClaudeLean_ExcludeAlwaysOnClass_StillSelectsIt");
        yield return new TestCaseData(new[] { "--mode=Workspace", "--include-tools=ParameterEditTools" },
                new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("OutsideClaudeLean_IncludeParameterEditTools_IsDropped");
        yield return new TestCaseData(new[] { "--mode=Workspace", "--include-tools=DeclarationTools,ToolsetControlTools" },
                new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("OutsideClaudeLean_IncludeClaudeLeanOnlyClasses_AreDropped");
        yield return new TestCaseData(new[] { "--mode=Workspace" },
                new[] { "ServerStatusTools", "WorkspaceTools" })
            .SetName("WorkspaceMode_SelectsTheOrdinaryClassOnly");
    }

    [TestCaseSource(nameof(LaunchArgumentCases))]
    public void SelectStartupToolTypes_LaunchArguments_SelectsTheExpectedProbedClasses(string[] args, string[] expected)
    {
        var allModes = new HashSet<string>(ToolClassRegistry.ModeToToolClasses.Keys, StringComparer.OrdinalIgnoreCase);
        ServerStartupHelpers.ParseArgs(
            args, allModes,
            out _, out var activeModes, out _, out _, out var includeTools, out var excludeTools, out _);
        var active = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes, ToolClassRegistry.ModeToToolClasses, includeTools, excludeTools);

        var selected = ToolClassRegistry.SelectStartupToolTypes(
            ToolClassRegistry.BasicToolClassTypes, active, activeModes.Contains(ToolClassRegistry.ClaudeLeanMode));

        Assert.That(ProbedNames(selected), Is.EqualTo(expected));
    }

    [Test]
    public void SelectStartupToolTypes_AdvancedTable_FollowsTheSameRule()
    {
        var active = new HashSet<string>(["SubAgentTools", "DeclarationTools"], StringComparer.OrdinalIgnoreCase);

        var selected = ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.AdvancedToolClassTypes, active, claudeLean: false);

        Assert.That(selected.Select(t => t.Name), Is.EqualTo(new[] { "SubAgentTools" }),
            "ServerStatusTools is not in the Advanced table, and DeclarationTools is a Basic table class.");
    }

    [Test]
    public void SelectStartupToolTypes_KeepsTableOrderAndNeverReordersByActiveSet()
    {
        var active = new HashSet<string>(ToolClassRegistry.BasicToolClassTypes.Reverse().Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var selected = ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.BasicToolClassTypes, active, claudeLean: true);

        Assert.That(selected, Is.EqualTo(ToolClassRegistry.BasicToolClassTypes));
    }

    private static string[] ProbedNames(IReadOnlyList<Type> selected) =>
        selected.Select(t => t.Name).Where(n => Probed.Contains(n, StringComparer.Ordinal)).ToArray();
}

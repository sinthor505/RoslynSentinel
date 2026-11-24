using System.Reflection;
using ModelContextProtocol.Server;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ToolClassTableTests
{
    private const BindingFlags AllMethods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Deliberately broken table entry: a class with no tool methods, used to check that the problem text names it.
    /// </summary>
    private sealed class NoToolMethods
    {
    }

    [Test]
    public void Tables_AreConsistentWithTheRegistry()
    {
        var problems = ToolClassRegistry.DescribeToolClassTableProblems(
            ToolClassRegistry.ModeToToolClasses,
            ToolClassRegistry.BasicToolClassTypes,
            ToolClassRegistry.AdvancedToolClassTypes);

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryToolTypeInTheToolsAssemblies_HasARow()
    {
        // Every RoslynSentinel.Tools.* assembly Server.Advanced references, found by file name in the output folder:
        // a project reference copies its assembly there even when no code names a type in it, so a new Tools project
        // cannot slip past the way it would with typeof() or GetReferencedAssemblies() (which drops an unused reference).
        // A Tools assembly that deliberately holds unregistered tool classes would need an explicit allow-list here;
        // none does today (Tools.Experimental's CodeTransformTools is in the Advanced table).
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "RoslynSentinel.Tools.*.dll")
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path)))
            .Where(assembly => assembly.GetName().Name!.StartsWith("RoslynSentinel.Tools.", StringComparison.Ordinal))
            .ToList();

        Assert.That(
            assemblies.Select(a => a.GetName().Name),
            Is.SupersetOf(new[] { "RoslynSentinel.Tools.Basic", "RoslynSentinel.Tools.Advanced", "RoslynSentinel.Tools.Experimental" }),
            "The output folder must hold every Tools assembly Server.Advanced references.");

        var toolTypes = new List<Type>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (IsToolType(type))
                {
                    toolTypes.Add(type);
                }
            }
        }

        Assert.That(toolTypes, Is.Not.Empty, "No [McpServerToolType] classes with [McpServerTool] methods were found in the tools assemblies.");

        var offenders = new List<string>();
        foreach (var type in toolTypes)
        {
            var rows = ToolClassRegistry.BasicToolClassTypes.Count(t => t == type)
                + ToolClassRegistry.AdvancedToolClassTypes.Count(t => t == type);
            if (rows != 1)
            {
                offenders.Add($"{type.FullName} ({rows} rows)");
            }
        }

        Assert.That(offenders, Is.Empty, "Tool types without exactly one table row: " + string.Join(", ", offenders));
    }

    [Test]
    public void BrokenInputs_AreReportedByName()
    {
        var modes = new Dictionary<string, string[]>(ToolClassRegistry.ModeToToolClasses, StringComparer.OrdinalIgnoreCase)
        {
            ["GhostMode"] = new[] { "GhostTools" },
        };
        var ghostProblems = ToolClassRegistry.DescribeToolClassTableProblems(
            modes,
            ToolClassRegistry.BasicToolClassTypes,
            ToolClassRegistry.AdvancedToolClassTypes);

        var advancedWithHelper = new List<Type>(ToolClassRegistry.AdvancedToolClassTypes) { typeof(NoToolMethods) };
        var helperProblems = ToolClassRegistry.DescribeToolClassTableProblems(
            ToolClassRegistry.ModeToToolClasses,
            ToolClassRegistry.BasicToolClassTypes,
            advancedWithHelper);

        var advancedWithDuplicate = new List<Type>(ToolClassRegistry.AdvancedToolClassTypes) { typeof(WorkspaceTools) };
        var duplicateProblems = ToolClassRegistry.DescribeToolClassTableProblems(
            ToolClassRegistry.ModeToToolClasses,
            ToolClassRegistry.BasicToolClassTypes,
            advancedWithDuplicate);

        Assert.Multiple(() =>
        {
            Assert.That(ghostProblems.Any(line => line.Contains("GhostTools", StringComparison.Ordinal)), Is.True,
                "A mode naming a class with no table row must be reported by that class name. Problems:" + Environment.NewLine
                + string.Join(Environment.NewLine, ghostProblems));
            Assert.That(helperProblems.Any(line => line.Contains("NoToolMethods", StringComparison.Ordinal)), Is.True,
                "A table type with no tool methods must be reported by name. Problems:" + Environment.NewLine
                + string.Join(Environment.NewLine, helperProblems));
            Assert.That(duplicateProblems.Any(line => line.Contains("WorkspaceTools", StringComparison.Ordinal)), Is.True,
                "A type in both tables must be reported by name. Problems:" + Environment.NewLine
                + string.Join(Environment.NewLine, duplicateProblems));
        });
    }

    private static bool IsToolType(Type type)
    {
        if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
        {
            return false;
        }

        return type.GetMethods(AllMethods).Any(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}

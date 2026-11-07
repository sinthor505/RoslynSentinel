using System.Reflection;
using ModelContextProtocol.Server;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Server;

// Ratchet for untyped tool return shapes (plan_wire_result_marshaller.md Step 11). A [McpServerTool] method whose
// declared return type, after unwrapping Task<T>/ValueTask<T>, is object or string, or SentinelCallToolResult over
// object or string, is an offender and must be listed in the baseline. New offenders fail, and a baseline line that
// no longer offends fails too, so the baseline only ratchets down.
// Refresh after a migration: set ROSLYNSENTINEL_UPDATE_TOOL_RETURN_BASELINE=1 and run Scan_MatchesBaseline.
[TestFixture]
public class ToolReturnShapeRatchetTests
{
    private const string BaselineRelativePath = "docs/known-untyped-tool-returns.txt";
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_TOOL_RETURN_BASELINE";

    private static readonly string[] ToolAssemblyNames =
    {
        "RoslynSentinel.Tools.Basic", "RoslynSentinel.Tools.Advanced", "RoslynSentinel.Tools.Experimental",
    };

    private readonly record struct Offender(string DeclaringType, string Method, string Category)
    {
        public string Line => $"{DeclaringType}.{Method} | {Category}";
    }

    [Test]
    public void Scan_MatchesBaseline()
    {
        var repoRoot = FindRepoRoot();
        var baselinePath = Path.Combine(repoRoot, BaselineRelativePath);
        var (scanned, offenders) = Collect(LoadedToolTypes());
        Assert.That(scanned, Is.GreaterThan(0), "no [McpServerTool] methods discovered: a tool assembly was not loaded");

        TestContext.Out.WriteLine(Summarize(scanned, offenders));

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            WriteBaseline(baselinePath, offenders);
            return;
        }

        var baseline = LoadBaseline(baselinePath);
        var current = offenders.Select(offender => offender.Line).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var line in current.Where(line => !baseline.Contains(line)).OrderBy(line => line, StringComparer.Ordinal))
        {
            problems.Add("new offender (not in baseline): " + line);
        }

        foreach (var line in baseline.Where(line => !current.Contains(line)).OrderBy(line => line, StringComparer.Ordinal))
        {
            problems.Add("baseline entry no longer offends (remove it): " + line);
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void Collect_ClassifiesEachUntypedCategoryAndNothingElse()
    {
        var (scanned, offenders) = Collect(new[] { typeof(SyntheticTools) });

        // Ten attributed methods; the unattributed one is not a tool and must not be scanned.
        Assert.That(scanned, Is.EqualTo(10));
        Assert.That(offenders.Select(offender => offender.Line), Is.EquivalentTo(new[]
        {
            "SyntheticTools.ObjectTool | object",
            "SyntheticTools.StringTool | string",
            "SyntheticTools.AsyncObjectTool | object",
            "SyntheticTools.AsyncStringTool | string",
            "SyntheticTools.ObjectResult | SentinelCallToolResult<object>",
            "SyntheticTools.StringResult | SentinelCallToolResult<string>",
            "SyntheticTools.ObjectErrorResult | SentinelCallToolResult<object, ResultError>",
            "SyntheticTools.StringErrorResult | SentinelCallToolResult<string, ResultError>",
        }));
    }

    private static IEnumerable<Type> LoadedToolTypes()
    {
        foreach (var name in ToolAssemblyNames)
        {
            foreach (var type in Assembly.Load(name).GetTypes())
            {
                if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                {
                    yield return type;
                }
            }
        }
    }

    private static (int Scanned, List<Offender> Offenders) Collect(IEnumerable<Type> toolTypes)
    {
        var scanned = 0;
        var offenders = new List<Offender>();
        foreach (var type in toolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!Attribute.IsDefined(method, typeof(McpServerToolAttribute)))
                {
                    continue;
                }

                scanned++;
                var category = Classify(method.ReturnType);
                if (category is not null)
                {
                    offenders.Add(new Offender(type.Name, method.Name, category));
                }
            }
        }

        return (scanned, offenders);
    }

    // Returns the category label for an untyped declared return, or null when the return is typed.
    private static string? Classify(Type declaredReturn)
    {
        var type = declaredReturn;
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                type = type.GetGenericArguments()[0];
            }
        }

        if (type == typeof(object))
        {
            return "object";
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (!type.IsGenericType)
        {
            return null;
        }

        var generic = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        var payload = arguments[0] == typeof(object) ? "object" : arguments[0] == typeof(string) ? "string" : null;
        if (payload is null)
        {
            return null;
        }

        if (generic == typeof(SentinelCallToolResult<>))
        {
            return $"SentinelCallToolResult<{payload}>";
        }

        if (generic == typeof(SentinelCallToolResult<,>))
        {
            return $"SentinelCallToolResult<{payload}, {arguments[1].Name}>";
        }

        return null;
    }

    private static string Summarize(int scanned, List<Offender> offenders)
    {
        var perCategory = offenders
            .GroupBy(offender => offender.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");
        return $"scanned {scanned} tool methods; untyped offenders {offenders.Count}: {string.Join(", ", perCategory)}";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("RoslynSentinel.slnx not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> LoadBaseline(string path)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static void WriteBaseline(string path, List<Offender> offenders)
    {
        var lines = new List<string>
        {
            "# Known untyped tool return shapes: ratchet baseline for ToolReturnShapeRatchetTests.",
            "# A [McpServerTool] method returning object or string, or SentinelCallToolResult over object or string,",
            "# is listed here. This file may only shrink; migrate a tool to a typed DTO and remove its line.",
            "# Refresh after a migration: set ROSLYNSENTINEL_UPDATE_TOOL_RETURN_BASELINE=1 and run",
            "# ToolReturnShapeRatchetTests.Scan_MatchesBaseline. Line format: <DeclaringType>.<Method> | <category>",
        };
        lines.AddRange(offenders
            .Select(offender => offender.Line)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal));
        File.WriteAllLines(path, lines);
    }

    // Synthetic tools covering each classifier branch. Methods are public so reflection lists them; the class is
    // private so it never reaches the real tool surface.
    private sealed class SyntheticTools
    {
        [McpServerTool] public object ObjectTool() => null!;
        [McpServerTool] public string StringTool() => null!;
        [McpServerTool] public Task<object> AsyncObjectTool() => Task.FromResult<object>(null!);
        [McpServerTool] public ValueTask<string> AsyncStringTool() => ValueTask.FromResult<string>(null!);
        [McpServerTool] public SentinelCallToolResult<object> ObjectResult() => null!;
        [McpServerTool] public Task<SentinelCallToolResult<string>> StringResult() => Task.FromResult<SentinelCallToolResult<string>>(null!);
        [McpServerTool] public SentinelCallToolResult<object, ResultError> ObjectErrorResult() => null!;
        [McpServerTool] public SentinelCallToolResult<string, ResultError> StringErrorResult() => null!;
        [McpServerTool] public SentinelCallToolResult<int> TypedResult() => null!;
        [McpServerTool] public Task VoidTask() => Task.CompletedTask;
        public object Unattributed() => null!;
    }
}

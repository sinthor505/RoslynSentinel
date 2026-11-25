using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace RoslynSentinel.Common;

public sealed record ConfigurationProblem(ConfigurationProblemKind Kind, string? SettingName,
    string? Flag, string? EnvName, string? RawValue, string? Expected);

public sealed record ConfigurationReport(IReadOnlyList<StartupSetting> Settings,
    IReadOnlyList<ConfigurationProblem> Problems, IReadOnlyList<string> UndeclaredEnv, bool Halted);

public sealed class ConfigurationInvalidException(IReadOnlyList<ConfigurationProblem> problems)
    : Exception(StartupSettings.DescribeProblems(problems))
{
    public IReadOnlyList<ConfigurationProblem> Problems { get; } = problems;
}

public sealed class StartupSettingDeclarationException(string message) : Exception(message);

public static class StartupSettings
{
    /// <summary>Step 26 sets true (Q16); readonly, not const, to avoid CS0162.</summary>
    public static readonly bool HaltOnErrors = false;

    /// <summary>Q24 hint text, facts and options only.</summary>
    public const string UndeclaredEnvHint = "Undeclared ROSLYNSENTINEL_* names are listed with settings: nonDefault or settings: all.";

    public static Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

    public static Func<IEnumerable<string>> EnvironmentNames { get; set; }
        = () => Environment.GetEnvironmentVariables().Keys.Cast<string>();

    /// <summary>Empty, Halted false, before ConfigureAll.</summary>
    public static ConfigurationReport LastReport { get; private set; }
        = new ConfigurationReport(Array.Empty<StartupSetting>(), Array.Empty<ConfigurationProblem>(), Array.Empty<string>(), false);

    private const string EnvPrefix = "ROSLYNSENTINEL_";
    private const string AssemblyPrefix = "RoslynSentinel.";

    // Discovery cache, guarded by GroupLock: one entry per group type so Name is set once and every path
    // (Discover, ConfigureAll, ConfigureGroup) sees the same setting instances. SettingGroupType maps a setting
    // back to its group so ConfigureAll can run the touched groups' PostConfigure from a flat setting list.
    private static readonly object GroupLock = new();
    private static readonly Dictionary<Type, IReadOnlyList<StartupSetting>> GroupSettings = new();
    private static readonly Dictionary<Type, MethodInfo?> GroupPostConfigure = new();
    private static readonly ConditionalWeakTable<StartupSetting, Type> SettingGroupType = new();

    /// <summary>Root + transitive RoslynSentinel.* references.</summary>
    public static IReadOnlyList<StartupSetting> Discover(Assembly root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var seen = new HashSet<string>(StringComparer.Ordinal) { root.GetName().Name ?? string.Empty };
        var pending = new Queue<Assembly>();
        var assemblies = new List<Assembly>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            assemblies.Add(assembly);
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null
                    || !reference.Name.StartsWith(AssemblyPrefix, StringComparison.Ordinal)
                    || !seen.Add(reference.Name))
                {
                    continue;
                }

                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
                {
                    // An unloadable reference cannot hold groups the running process could use; skip it.
                }
            }
        }

        var groupTypes = new List<Type>();
        foreach (var assembly in assemblies)
        {
            groupTypes.AddRange(GetLoadableTypes(assembly)
                .Where(t => t.IsDefined(typeof(StartupSettingGroupAttribute), inherit: false))
                .OrderBy(t => t.FullName, StringComparer.Ordinal));
        }

        return Discover(groupTypes);
    }

    public static IReadOnlyList<StartupSetting> Discover(IEnumerable<Type> groupTypes)
    {
        ArgumentNullException.ThrowIfNull(groupTypes);

        var all = new List<StartupSetting>();
        foreach (var type in groupTypes.Distinct())
        {
            var group = GetGroupSettings(type);
            if (group is not null)
            {
                all.AddRange(group);
            }
        }

        CheckDeclarations(all);
        return all;
    }

    /// <summary>Sets LastReport.</summary>
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, IReadOnlyList<StartupSetting> settings, SettingScope scopes, bool haltOnErrors)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(settings);

        var parsed = StartupArgs.Parse(args);
        var getEnv = EnvironmentReader;
        var problems = new List<ConfigurationProblem>();
        var touchedGroups = new List<Type>();

        foreach (var setting in settings)
        {
            if ((setting.Scope & scopes) == 0)
            {
                continue;
            }

            var problem = setting.Apply(parsed, getEnv);
            if (problem is not null)
            {
                problems.Add(problem);
            }

            if (SettingGroupType.TryGetValue(setting, out var groupType) && !touchedGroups.Contains(groupType))
            {
                touchedGroups.Add(groupType);
            }
        }

        foreach (var groupType in touchedGroups)
        {
            problems.AddRange(RunPostConfigure(groupType));
        }

        AddUnknownFlags(parsed.Tokens, settings, problems);
        var undeclaredEnv = FindUndeclaredEnv(settings);

        var report = new ConfigurationReport(settings, problems, undeclaredEnv, haltOnErrors && problems.Count > 0);
        foreach (var problem in problems)
        {
            Console.Error.WriteLine(DescribeProblems(new[] { problem }));
        }

        foreach (var name in undeclaredEnv)
        {
            Console.Error.WriteLine($"{name}: environment variable not declared by any setting");
        }

        LastReport = report;
        return report;
    }

    /// <summary>Equals ConfigureAll(args, Discover(root), scopes, HaltOnErrors).</summary>
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, Assembly root, SettingScope scopes)
        => ConfigureAll(args, Discover(root), scopes, HaltOnErrors);

    /// <summary>Does not touch LastReport.</summary>
    public static IReadOnlyList<ConfigurationProblem> ConfigureGroup(Type groupType, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(groupType);
        ArgumentNullException.ThrowIfNull(args);

        var settings = GetGroupSettings(groupType)
            ?? throw new StartupSettingDeclarationException(
                $"{groupType.Name} is not marked with [StartupSettingGroup]; it declares no startup settings.");

        var parsed = StartupArgs.Parse(args);
        var getEnv = EnvironmentReader;
        var problems = new List<ConfigurationProblem>();
        foreach (var setting in settings)
        {
            var problem = setting.Apply(parsed, getEnv);
            if (problem is not null)
            {
                problems.Add(problem);
            }
        }

        problems.AddRange(RunPostConfigure(groupType));
        return problems;
    }

    /// <summary>Throws ConfigurationInvalidException when Problems is non-empty.</summary>
    public static void ThrowIfInvalid(ConfigurationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Problems.Count > 0)
        {
            throw new ConfigurationInvalidException(report.Problems);
        }
    }

    /// <summary>One "&lt;SettingName&gt; (&lt;Flag&gt;, &lt;EnvName&gt;): &lt;Kind&gt;, raw '&lt;RawValue&gt;', expected &lt;Expected&gt;" line per problem, null parts omitted; always contains the flag and env names.</summary>
    public static string DescribeProblems(IReadOnlyList<ConfigurationProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return string.Join(Environment.NewLine, problems.Select(DescribeProblem));
    }

    private static string DescribeProblem(ConfigurationProblem problem)
    {
        var line = new System.Text.StringBuilder();
        if (problem.SettingName is not null)
        {
            line.Append(problem.SettingName);
        }

        var names = new[] { problem.Flag, problem.EnvName }.Where(n => n is not null).ToArray();
        if (names.Length > 0)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append('(').Append(string.Join(", ", names)).Append(')');
        }

        line.Append(line.Length > 0 ? ": " : string.Empty).Append(problem.Kind);
        if (problem.RawValue is not null)
        {
            line.Append(", raw '").Append(problem.RawValue).Append('\'');
        }

        if (problem.Expected is not null)
        {
            line.Append(", expected ").Append(problem.Expected);
        }

        return line.ToString();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Keep the types that did load; a partially loadable assembly can still hold groups.
            return ex.Types.OfType<Type>().ToArray();
        }
    }

    /// <summary>
    /// The settings of one group, discovered once and cached; null when the type carries no
    /// [StartupSettingGroup]. Reading the fields runs the class's static initializer.
    /// </summary>
    private static IReadOnlyList<StartupSetting>? GetGroupSettings(Type type)
    {
        lock (GroupLock)
        {
            if (GroupSettings.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var attribute = type.GetCustomAttribute<StartupSettingGroupAttribute>(inherit: false);
            if (attribute is null)
            {
                return null;
            }

            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (TypeInitializationException ex) when (ex.InnerException is StartupSettingDeclarationException declaration)
            {
                // A factory rejected a declaration inside the static initializer; surface it unwrapped.
                throw new StartupSettingDeclarationException($"{type.Name}: {declaration.Message}");
            }

            MethodInfo? postConfigure = null;
            if (!string.IsNullOrEmpty(attribute.PostConfigure))
            {
                postConfigure = type.GetMethod(attribute.PostConfigure,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (postConfigure is null || !typeof(IReadOnlyList<ConfigurationProblem>).IsAssignableFrom(postConfigure.ReturnType))
                {
                    throw new StartupSettingDeclarationException(
                        $"{type.Name}: PostConfigure '{attribute.PostConfigure}' must be a parameterless static method returning IReadOnlyList<ConfigurationProblem>.");
                }
            }

            var settings = new List<StartupSetting>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!field.IsInitOnly || !typeof(StartupSetting).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                if (field.GetValue(null) is not StartupSetting setting)
                {
                    continue;
                }

                var fieldName = field.Name.Length > "Setting".Length && field.Name.EndsWith("Setting", StringComparison.Ordinal)
                    ? field.Name[..^"Setting".Length]
                    : field.Name;
                setting.Name = type.Name + "." + fieldName;
                settings.Add(setting);
                SettingGroupType.AddOrUpdate(setting, type);
            }

            GroupSettings[type] = settings;
            GroupPostConfigure[type] = postConfigure;
            return settings;
        }
    }

    /// <summary>A flag or env name declared twice with overlapping Scope throws; disjoint scopes are allowed.</summary>
    private static void CheckDeclarations(List<StartupSetting> all)
    {
        CheckNames(all, s => s.Flag, "Flag");
        CheckNames(all, s => s.EnvName, "Env name");
    }

    private static void CheckNames(List<StartupSetting> all, Func<StartupSetting, string?> nameOf, string label)
    {
        var byName = new Dictionary<string, List<StartupSetting>>(StringComparer.Ordinal);
        foreach (var setting in all)
        {
            var name = nameOf(setting);
            if (name is null)
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var list))
            {
                byName[name] = list = new List<StartupSetting>();
            }

            list.Add(setting);
        }

        foreach (var (name, list) in byName)
        {
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var overlap = list[i].Scope & list[j].Scope;
                    if (overlap != 0 && !ReferenceEquals(list[i], list[j]))
                    {
                        throw new StartupSettingDeclarationException(
                            $"{label} '{name}' is declared by both {list[i].Name} and {list[j].Name} with overlapping scope ({overlap}).");
                    }
                }
            }
        }
    }

    /// <summary>Runs the group's PostConfigure; any problem is re-kinded PostConfigure and resets the whole group to defaults.</summary>
    private static IReadOnlyList<ConfigurationProblem> RunPostConfigure(Type groupType)
    {
        MethodInfo? method;
        IReadOnlyList<StartupSetting> settings;
        lock (GroupLock)
        {
            if (!GroupPostConfigure.TryGetValue(groupType, out method) || !GroupSettings.TryGetValue(groupType, out settings!))
            {
                return Array.Empty<ConfigurationProblem>();
            }
        }

        if (method is null)
        {
            return Array.Empty<ConfigurationProblem>();
        }

        object? result;
        try
        {
            result = method.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        var returned = (IReadOnlyList<ConfigurationProblem>?)result;
        if (returned is null || returned.Count == 0)
        {
            return Array.Empty<ConfigurationProblem>();
        }

        foreach (var setting in settings)
        {
            setting.Reset();
        }

        return returned.Select(p => p with { Kind = ConfigurationProblemKind.PostConfigure }).ToArray();
    }

    /// <summary>
    /// Every `--token` no setting in the list declares (any scope) is an UnknownFlag. A token is skipped as a value
    /// when it follows a declared flag written `--f v`: a value flag consumes any next token not starting with
    /// `--`, a bool flag only a bool literal. `--f=v` consumes nothing; a bare `--` is not a flag.
    /// </summary>
    private static void AddUnknownFlags(IReadOnlyList<string> tokens, IReadOnlyList<StartupSetting> settings, List<ConfigurationProblem> problems)
    {
        var declared = settings.Where(s => s.Flag is not null).ToLookup(s => s.Flag!, StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token == "--")
            {
                continue;
            }

            var equals = token.IndexOf('=');
            var name = equals < 0 ? token : token[..equals];
            if (!declared.Contains(name))
            {
                problems.Add(new ConfigurationProblem(ConfigurationProblemKind.UnknownFlag, null, name, null,
                    equals < 0 ? null : token[(equals + 1)..], null));
                continue;
            }

            if (equals < 0 && i + 1 < tokens.Count && ConsumesNext(declared[name], tokens[i + 1]))
            {
                i++;
            }
        }
    }

    private static bool ConsumesNext(IEnumerable<StartupSetting> declarations, string next)
    {
        if (next.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        // The same flag may be declared with disjoint scopes of different types; any value declaration consumes.
        return declarations.Any(s => !s.IsBool) || IsBoolLiteral(next);
    }

    private static bool IsBoolLiteral(string raw)
        => raw.Trim().ToLowerInvariant() is "true" or "false" or "1" or "0" or "on" or "off" or "yes" or "no";

    private static IReadOnlyList<string> FindUndeclaredEnv(IReadOnlyList<StartupSetting> settings)
    {
        var declared = settings.Where(s => s.EnvName is not null).Select(s => s.EnvName!).ToHashSet(StringComparer.Ordinal);
        return EnvironmentNames()
            .Where(n => n is not null && n.StartsWith(EnvPrefix, StringComparison.Ordinal) && !declared.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}

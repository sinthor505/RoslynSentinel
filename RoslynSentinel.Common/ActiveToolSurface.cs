namespace RoslynSentinel.Common;

public record ActiveToolSurface
{
    /// <summary>Snapshot of what --mode/--include-tools/--exclude-tools resolved to at startup,
    /// captured once so an always-active tool (see SentinelServerStatusTools.McpServerStatus in
    /// RoslynSentinel.Tools.Basic) can report the same values the startup guidance message in
    /// ServerStartupHelpers.DescribeNoActiveToolsFailure is built from, without needing a restart
    /// or a second implementation.</summary>
    public string ModeArg
    {
        get;
    }
    public IReadOnlySet<string> ActiveModes
    {
        get;
    }
    public IReadOnlySet<string> IncludeTools
    {
        get;
    }
    public IReadOnlySet<string> ExcludeTools
    {
        get;
    }
    public IReadOnlySet<string> ActiveToolClasses
    {
        get;
    }
    /// <summary>Tool class name -> the --mode values that enable it (empty when unknown). Lets
    /// McpServerStatus tell a caller how to enable a gated tool; the registry itself lives in
    /// Server.Advanced, which Tools.Basic cannot reference.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ClassModes
    {
        get;
    }

    /// <summary>Per-tool allow-list in effect (the exclusive claude-lean mode), or null when every
    /// tool of every active class is registered. A tool is active only if its class is active AND,
    /// when this is non-null, its name is listed here.</summary>
    public IReadOnlySet<string>? AllowedToolNames
    {
        get;
    }

    public bool IsToolAllowed(string toolName) => AllowedToolNames is null || AllowedToolNames.Contains(toolName);

    /// <summary>True when a call to <paramref name="toolName"/> can be served by this server: a class declaring
    /// that tool is in <see cref="ActiveToolClasses"/> AND <see cref="IsToolAllowed"/> accepts the name. This is
    /// the rule McpServerStatus uses for ActiveForThisMode (ServerStatusTools.McpServerStatusTool), not a second
    /// one; two differences are deliberate: it does not see tools a caller switched on at runtime through
    /// McpToolsetControl, and it does not special-case the always-on ServerStatusTools class. Both only make it
    /// answer false for a tool that is in fact callable, so a hint built from it errs by leaving a tool name out.
    /// Error and hint text uses it to avoid telling a model to call a tool it cannot see.</summary>
    public bool IsCallable(string toolName)
    {
        if (!IsToolAllowed(toolName))
        {
            return false;
        }

        // A tool name can be declared by more than one class (a facade and the split class behind it); one
        // active declaring class is enough, as in McpServerStatus.
        return DeclaringClassesByTool().TryGetValue(toolName, out var classes) && classes.Any(ActiveToolClasses.Contains);
    }

    private static readonly object DeclaringClassesLock = new();
    private static int _declaringClassesAssemblyCount = -1;
    private static IReadOnlyDictionary<string, string[]> _declaringClassesByTool = new Dictionary<string, string[]>();

    /// <summary>Tool name -> declaring class names, reflected once per set of loaded RoslynSentinel assemblies
    /// (the same discovery McpServerStatus runs); rebuilt only if an assembly loads later.</summary>
    private static IReadOnlyDictionary<string, string[]> DeclaringClassesByTool()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
            .ToArray();
        lock (DeclaringClassesLock)
        {
            if (assemblies.Length != _declaringClassesAssemblyCount)
            {
                _declaringClassesByTool = McpToolSchemaPatcher.DiscoverAllDeclaredTools(assemblies)
                    .GroupBy(t => t.ToolName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.ClassName).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
                _declaringClassesAssemblyCount = assemblies.Length;
            }

            return _declaringClassesByTool;
        }
    }

    public ActiveToolSurface(
        string modeArg,
        IReadOnlySet<string> activeModes,
        IReadOnlySet<string> includeTools,
        IReadOnlySet<string> excludeTools,
        IReadOnlySet<string> activeToolClasses,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? classModes = null,
        IReadOnlySet<string>? allowedToolNames = null)
    {
        AllowedToolNames = allowedToolNames;
        ModeArg = modeArg;
        ActiveModes = activeModes;
        IncludeTools = includeTools;
        ExcludeTools = excludeTools;
        ActiveToolClasses = activeToolClasses;
        ClassModes = classModes ?? new Dictionary<string, IReadOnlyList<string>>();
    }
}

namespace RoslynSentinel.Common;

public record ActiveToolSurface
{
    /// <summary>Snapshot of what --mode/--include-tools/--exclude-tools resolved to at startup,
    /// captured once so an always-active tool (see SentinelServerStatusTools.McpServerStatus in
    /// RoslynSentinel.Server.Basic) can report the same values the startup guidance message in
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
    /// Server.Basic, which Tools.Basic cannot reference.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ClassModes
    {
        get;
    }

    public ActiveToolSurface(
        string modeArg,
        IReadOnlySet<string> activeModes,
        IReadOnlySet<string> includeTools,
        IReadOnlySet<string> excludeTools,
        IReadOnlySet<string> activeToolClasses,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? classModes = null)
    {
        ModeArg = modeArg;
        ActiveModes = activeModes;
        IncludeTools = includeTools;
        ExcludeTools = excludeTools;
        ActiveToolClasses = activeToolClasses;
        ClassModes = classModes ?? new Dictionary<string, IReadOnlyList<string>>();
    }
}

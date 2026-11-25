using System.Reflection;

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
    public const string UndeclaredEnvHint = "TODO placeholder (Step 4 writes the final text)";

    public static Func<string, string?> EnvironmentReader { get; set; } = Environment.GetEnvironmentVariable;

    public static Func<IEnumerable<string>> EnvironmentNames { get; set; }
        = () => Environment.GetEnvironmentVariables().Keys.Cast<string>();

    /// <summary>Empty, Halted false, before ConfigureAll.</summary>
    public static ConfigurationReport LastReport { get; private set; }
        = new ConfigurationReport(Array.Empty<StartupSetting>(), Array.Empty<ConfigurationProblem>(), Array.Empty<string>(), false);

    /// <summary>Root + transitive RoslynSentinel.* references.</summary>
    public static IReadOnlyList<StartupSetting> Discover(Assembly root) => throw new NotImplementedException(); // TODO implement (Step 4)

    public static IReadOnlyList<StartupSetting> Discover(IEnumerable<Type> groupTypes) => throw new NotImplementedException(); // TODO implement (Step 4)

    /// <summary>Sets LastReport.</summary>
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, IReadOnlyList<StartupSetting> settings, SettingScope scopes, bool haltOnErrors) => throw new NotImplementedException(); // TODO implement (Step 4)

    /// <summary>Equals ConfigureAll(args, Discover(root), scopes, HaltOnErrors).</summary>
    public static ConfigurationReport ConfigureAll(IReadOnlyList<string> args, Assembly root, SettingScope scopes) => throw new NotImplementedException(); // TODO implement (Step 4)

    /// <summary>Does not touch LastReport.</summary>
    public static IReadOnlyList<ConfigurationProblem> ConfigureGroup(Type groupType, IReadOnlyList<string> args) => throw new NotImplementedException(); // TODO implement (Step 4)

    /// <summary>Throws ConfigurationInvalidException when Problems is non-empty.</summary>
    public static void ThrowIfInvalid(ConfigurationReport report) => throw new NotImplementedException(); // TODO implement (Step 4)

    /// <summary>One "&lt;SettingName&gt; (&lt;Flag&gt;, &lt;EnvName&gt;): &lt;Kind&gt;, raw '&lt;RawValue&gt;', expected &lt;Expected&gt;" line per problem, null parts omitted; always contains the flag and env names.</summary>
    public static string DescribeProblems(IReadOnlyList<ConfigurationProblem> problems) => throw new NotImplementedException(); // TODO implement (Step 4)
}

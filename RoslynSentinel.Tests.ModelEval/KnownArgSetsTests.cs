using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Server.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// T11 and T15 of plan_startup_configuration_registry (Q16, Q23). T11: every arg set this repo ships produces zero
/// configuration problems against the server registry, so enabling the halt cannot refuse a known launch. T15: the
/// ModelEval assembly's registry discovers without a declaration error and a clean environment reports zero problems.
/// </summary>
/// <remarks>
/// NonParallelizable: <see cref="StartupSettings"/> holds static state (environment reader, environment names, last
/// report) and <c>ConfigureAll</c> writes the static option classes. The fixture fakes an empty environment and, in
/// TearDown, restores the real reader and re-applies the real environment with no flags, so nothing leaks into
/// other tests in the process.
/// <para>
/// Launcher script scan limits (scripts/roslynsentinel-mcp-launch.ps1): a line-based text scan, not a PowerShell
/// parse. It reads each non-comment line that is not log text (Write-LaunchLog, Write-StartupLog), a <c>-match</c>
/// regex test or a git/dotnet invocation (Invoke-NativeCaptured). From a remaining line it takes every quoted
/// string that starts with <c>--flag</c> (a <c>$var</c> or <c>$(...)</c> inside it becomes the value "placeholder"),
/// plus the bare <c>--flag[=value]</c> tokens of the line that starts with <c>&amp; $exePath</c>. Flags the script
/// builds from pieces, or passes through from its own caller (<c>$ServerArgs</c>), are not seen.
/// </para>
/// <para>
/// ErrorPolicyWiring.ChildServerArgs is exercised for every mode with a policy file, and for Off without one; Pointer
/// or Inline without a file is not a launch the product produces (ComposePrompt throws for it), so it is not a known set.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class KnownArgSetsTests
{
    private const string Placeholder = "placeholder";
    private const SettingScope ServerScopes = SettingScope.Server | SettingScope.ServerStdio;
    private const SettingScope ModelEvalScopes = SettingScope.Server | SettingScope.AgentLoop | SettingScope.TestHarness;

    private static readonly Lazy<IReadOnlyList<StartupSetting>> ServerSettings = new(() => StartupSettings.Discover(typeof(ServerStdio).Assembly));

    private static readonly Regex SkippedScriptLine = new(@"Write-LaunchLog|Write-StartupLog|-match|Invoke-NativeCaptured", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedString = new("\"([^\"]*)\"|'([^']*)'", RegexOptions.CultureInvariant);
    private static readonly Regex FlagStart = new("^--[a-z][a-z0-9-]*(=|$)", RegexOptions.CultureInvariant);
    private static readonly Regex ScriptVariable = new(@"\$\([^)]*\)|\$\{[^}]*\}|\$\w+", RegexOptions.CultureInvariant);
    private static readonly Regex BareFlag = new(@"(?<=\s)--[a-z][a-z0-9-]*(=\S*)?", RegexOptions.CultureInvariant);

    private Func<string, string?> originalReader = null!;
    private Func<IEnumerable<string>> originalNames = null!;

    [SetUp]
    public void SetUp()
    {
        originalReader = StartupSettings.EnvironmentReader;
        originalNames = StartupSettings.EnvironmentNames;
        StartupSettings.EnvironmentReader = _ => null;
        StartupSettings.EnvironmentNames = () => [];
    }

    [TearDown]
    public void TearDown()
    {
        StartupSettings.EnvironmentReader = originalReader;
        StartupSettings.EnvironmentNames = originalNames;

        // ConfigureAll wrote the static option classes (for example --llm-model=some-model) and LastReport; put the
        // real environment back with no flags, as an entry point with an empty command line would.
        StartupSettings.ConfigureAll(
            Array.Empty<string>(),
            StartupSettings.Discover(typeof(KnownArgSetsTests).Assembly),
            ServerScopes | ModelEvalScopes,
            haltOnErrors: false);
    }

    public static IEnumerable<TestCaseData> ShippedArgSets()
    {
        yield return new TestCaseData("launcher script", LauncherScriptArgs().ToArray()).SetName("ShippedArgSet_LauncherScript_HasNoProblems");

        // launchSettings.json is gitignored, so it exists only on developer machines; a clean checkout gets one ignored case instead.
        var launchSettingsPath = Path.Combine(RepoRoot(), "RoslynSentinel.Server.Advanced", "Properties", "launchSettings.json");
        if (File.Exists(launchSettingsPath))
        {
            foreach (var (profile, commandLine) in LaunchSettingsProfiles())
            {
                yield return new TestCaseData($"launchSettings profile '{profile}'", SplitCommandLine(commandLine).ToArray())
                    .SetName($"ShippedArgSet_LaunchSettings_{profile}_HasNoProblems");
            }
        }
        else
        {
            yield return new TestCaseData("launchSettings.json (absent)", Array.Empty<string>())
                .SetName("ShippedArgSet_LaunchSettings_FileAbsent")
                .Ignore("launchSettings.json is gitignored and absent in this checkout; its profiles are not checked here");
        }

        yield return new TestCaseData(
            "SubAgentChildServerLauncher.BuildServerArguments",
            SubAgentChildServerLauncher.BuildServerArguments(@"C:\wt", "some-model", @"C:\logs", "run-1", 200, 200, 6000, 6000).ToArray())
            .SetName("ShippedArgSet_SubAgentChildServerArguments_HasNoProblems");

        foreach (var mode in Enum.GetValues<ErrorPolicyMode>())
        {
            yield return new TestCaseData(
                $"ErrorPolicyWiring.ChildServerArgs({mode}, file)",
                ErrorPolicyWiring.ChildServerArgs(mode, ErrorPolicyFile.OutputPath).ToArray())
                .SetName($"ShippedArgSet_ErrorPolicyChildArgs_{mode}WithFile_HasNoProblems");
        }

        yield return new TestCaseData(
            "ErrorPolicyWiring.ChildServerArgs(Off, no file)",
            ErrorPolicyWiring.ChildServerArgs(ErrorPolicyMode.Off, null).ToArray())
            .SetName("ShippedArgSet_ErrorPolicyChildArgs_OffNoFile_HasNoProblems");
    }

    [TestCaseSource(nameof(ShippedArgSets))]
    public void ShippedArgSet_HasNoProblems(string setName, string[] args)
    {
        var report = StartupSettings.ConfigureAll(args, ServerSettings.Value, ServerScopes, haltOnErrors: false);

        Assert.That(
            report.Problems,
            Is.Empty,
            () => $"Arg set '{setName}' ({string.Join(" ", args)}) produced {report.Problems.Count} problem(s):{Environment.NewLine}{StartupSettings.DescribeProblems(report.Problems)}");
    }

    [Test]
    public void LauncherScriptScan_FindsTheKnownServerFlags()
    {
        var args = LauncherScriptArgs().ToList();

        Assert.That(args, Does.Contain("--transport=stdio"));
        Assert.That(args, Does.Contain("--eol=crlf"));
        foreach (var flag in new[] { "--working-dir=", "--builds-root=", "--tab-state-dir=", "--solution=" })
        {
            Assert.That(args.Any(a => a.StartsWith(flag, StringComparison.Ordinal)), Is.True, $"The launcher script scan did not find {flag}. Found: {string.Join(" ", args)}");
        }

        Assert.That(args, Does.Not.Contain("--detach"), "The scan picked up git arguments.");
        Assert.That(args, Does.Not.Contain("--force"), "The scan picked up git arguments.");
    }

    [Test]
    public void SplitCommandLine_FollowsWindowsRules()
    {
        Assert.That(
            SplitCommandLine("--a=\"x y\" --b=\\\"q\\\" --c=\"C:\\\\dir\\\\\" --d=1\r\n"),
            Is.EqualTo(new[] { "--a=x y", "--b=\"q\"", "--c=C:\\\\dir\\", "--d=1" }));
    }

    [Test]
    public void ModelEvalScopes_DiscoverWithoutDuplicates_CleanEnvHasNoProblems()
    {
        // Replays the call in ModelEvalAssemblySetUp (LiveModel namespace, [Explicit] fixtures only) with a clean environment.
        IReadOnlyList<StartupSetting> settings = [];
        try
        {
            settings = StartupSettings.Discover(typeof(KnownArgSetsTests).Assembly);
        }
        catch (StartupSettingDeclarationException ex)
        {
            Assert.Fail($"Discover(ModelEval assembly) threw {nameof(StartupSettingDeclarationException)}: {ex.Message}");
        }

        var args = ErrorPolicyFile.ResolveConfigureArgs(
            StartupSettings.EnvironmentReader(ErrorPolicyOptions.ModeSetting.EnvName!),
            StartupSettings.EnvironmentReader(ErrorPolicyOptions.FileSetting.EnvName!));
        var report = StartupSettings.ConfigureAll(args, settings, ModelEvalScopes, haltOnErrors: false);

        Assert.That(
            report.Problems,
            Is.Empty,
            () => $"A clean environment produced {report.Problems.Count} problem(s):{Environment.NewLine}{StartupSettings.DescribeProblems(report.Problems)}");
    }

    private static string RepoRoot() =>
        RepoRootLocator.TryFind(AppContext.BaseDirectory) ?? throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");

    private static IEnumerable<string> LauncherScriptArgs()
    {
        var path = Path.Combine(RepoRoot(), "scripts", "roslynsentinel-mcp-launch.ps1");
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || SkippedScriptLine.IsMatch(line))
            {
                continue;
            }

            foreach (Match match in QuotedString.Matches(line))
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (FlagStart.IsMatch(value))
                {
                    yield return ScriptVariable.Replace(value, Placeholder);
                }
            }

            if (line.StartsWith('&') && line.Contains("$exePath", StringComparison.Ordinal))
            {
                foreach (Match match in BareFlag.Matches(line))
                {
                    yield return match.Value;
                }
            }
        }
    }

    private static IEnumerable<(string Profile, string CommandLine)> LaunchSettingsProfiles()
    {
        var path = Path.Combine(RepoRoot(), "RoslynSentinel.Server.Advanced", "Properties", "launchSettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateObject())
        {
            if (profile.Value.TryGetProperty("commandLineArgs", out var commandLine) && commandLine.ValueKind == JsonValueKind.String)
            {
                yield return (profile.Name, commandLine.GetString()!);
            }
        }
    }

    /// <summary>Windows command-line rules: double quotes group, backslashes are special only before a quote (2n backslashes + quote = n backslashes and a toggle, 2n+1 = n backslashes and a literal quote); tokens are trimmed and empty ones dropped.</summary>
    private static List<string> SplitCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        void Flush()
        {
            var token = current.ToString().Trim();
            if (token.Length > 0)
            {
                tokens.Add(token);
            }

            current.Clear();
        }

        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    count++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', count);
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                i++;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                Flush();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        Flush();
        return tokens;
    }
}

using System.Text;
using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// T10 of plan_startup_configuration_registry: the drift guard for environment variables. An in-process source scan
/// from the repo root checks that every whole <c>ROSLYNSENTINEL_*</c> string literal is a name some
/// <see cref="StartupSetting"/> declares, that product code spells each name exactly once, and that product code
/// reads the process environment directly only where the registry (and the git layer, Q8) may.
/// <para>
/// Scan limits: a small lexer (not Roslyn) walks each file. It skips <c>//</c> and block comments and char literals.
/// A "whole literal" is a regular <c>"..."</c> string that is not verbatim, interpolated or raw and is not joined to a
/// neighbour with <c>+</c>; those forms and concatenated fragments are fragments, not names, so a test that needs a
/// deliberately undeclared name builds it by concatenation (<c>"ROSLYNSENTINEL" + "_TEST_UNDECLARED"</c>). Escape
/// sequences are not decoded (a name never contains one). Preprocessor-disabled code is scanned like live code.
/// Directories named bin, obj or Worktree, and every directory whose name starts with a dot, are not scanned.
/// </para>
/// </summary>
[TestFixture]
public class EnvVarDeclarationGuardTests
{
    // A prefix literal ending in an underscore is not a name, so the pattern must end in a letter or digit.
    private static readonly Regex NamePattern = new("^ROSLYNSENTINEL_[A-Z0-9_]*[A-Z0-9]$", RegexOptions.CultureInvariant);

    // Matches calls and method-group uses (StartupSettings keeps a default reader that is a method group).
    private static readonly Regex EnvReadPattern = new(@"\bEnvironment\s*\.\s*GetEnvironmentVariables?\b", RegexOptions.CultureInvariant);

    // Relative to the repo root, with forward slashes.
    private static readonly string[] EnvReadAllowedFolders = ["RoslynSentinel.Common/Configuration/", "RoslynSentinel.Git/"];

    private const string TestProjectFolderPrefix = "RoslynSentinel.Tests";

    private static readonly Lazy<IReadOnlyList<ScannedFile>> ScannedFiles = new(ScanRepository);
    private static readonly Lazy<HashSet<string>> DeclaredNames = new(DiscoverDeclaredNames);

    private sealed record NameLiteral(string Value, int Line);

    private sealed record ScannedFile(string RelativePath, bool IsTestProject, IReadOnlyList<NameLiteral> NameLiterals, IReadOnlyList<int> EnvReadLines);

    [Test]
    public void Discovery_ReachesHarnessAndPlanStepRunnerGroups()
    {
        var settings = StartupSettings.Discover(typeof(EnvVarDeclarationGuardTests).Assembly);

        Assert.That(settings, Does.Contain(TestHarnessSettings.TestKeepSetting), "The Tests.Harness TestHarnessSettings group is not reached by Discover.");
        Assert.That(settings.Any(s => (s.Scope & SettingScope.PlanStepRunner) != 0), Is.True, "No PlanStepRunner-scope setting is reached by Discover.");
        Assert.That(settings.Any(s => (s.Scope & SettingScope.AgentLoop) != 0), Is.True, "No AgentLoop-scope setting is reached by Discover.");
    }

    [Test]
    public void Product_Literals_AreDeclaredAndUnique()
    {
        var occurrences = ScannedFiles.Value
            .Where(f => !f.IsTestProject)
            .SelectMany(f => f.NameLiterals.Select(l => (File: f.RelativePath, l.Line, l.Value)))
            .ToList();
        Assert.That(occurrences, Is.Not.Empty, "The scan found no ROSLYNSENTINEL_* literal in product code, so it is not scanning the right files.");

        var offenders = new List<string>();
        foreach (var occurrence in occurrences)
        {
            if (!DeclaredNames.Value.Contains(occurrence.Value))
            {
                offenders.Add($"{occurrence.File}:{occurrence.Line}: {occurrence.Value} is not a declared setting name");
            }
        }

        foreach (var group in occurrences.GroupBy(o => o.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            foreach (var occurrence in group)
            {
                offenders.Add($"{occurrence.File}:{occurrence.Line}: {group.Key} occurs {group.Count()} times in product code (expected once)");
            }
        }

        AssertNoOffenders(offenders, "Product code must spell each ROSLYNSENTINEL_* name once, and the name must be declared by a StartupSetting.");
    }

    [Test]
    public void Product_EnvReads_OnlyInAllowedFolders()
    {
        var offenders = new List<string>();
        foreach (var file in ScannedFiles.Value.Where(f => !f.IsTestProject))
        {
            if (EnvReadAllowedFolders.Any(folder => file.RelativePath.StartsWith(folder, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var line in file.EnvReadLines)
            {
                offenders.Add($"{file.RelativePath}:{line}: direct Environment.GetEnvironmentVariable(s) read");
            }
        }

        AssertNoOffenders(offenders, $"Product code may read the environment directly only under {string.Join(" and ", EnvReadAllowedFolders)}; elsewhere use a StartupSetting.");
    }

    [Test]
    public void Test_Literals_AreDeclared()
    {
        var occurrences = ScannedFiles.Value
            .Where(f => f.IsTestProject)
            .SelectMany(f => f.NameLiterals.Select(l => (File: f.RelativePath, l.Line, l.Value)))
            .ToList();
        Assert.That(occurrences, Is.Not.Empty, "The scan found no ROSLYNSENTINEL_* literal in test code, so it is not scanning the right files.");

        var offenders = occurrences
            .Where(o => !DeclaredNames.Value.Contains(o.Value))
            .Select(o => $"{o.File}:{o.Line}: {o.Value} is not a declared setting name")
            .ToList();

        AssertNoOffenders(offenders, "Test code may use a ROSLYNSENTINEL_* literal only for a declared name (declare it in Tests.Harness TestHarnessSettings, or build a deliberately undeclared name by concatenation).");
    }

    private static void AssertNoOffenders(List<string> offenders, string rule)
    {
        offenders.Sort(StringComparer.Ordinal);
        Assert.That(offenders, Is.Empty, () => $"{rule}{Environment.NewLine}{offenders.Count} offender(s):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static HashSet<string> DiscoverDeclaredNames() =>
        StartupSettings.Discover(typeof(EnvVarDeclarationGuardTests).Assembly)
            .Where(s => s.EnvName is not null)
            .Select(s => s.EnvName!)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<ScannedFile> ScanRepository()
    {
        var root = RepoRootLocator.TryFind(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");

        var files = new List<ScannedFile>();
        foreach (var path in EnumerateSourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var topFolder = relative.Split('/')[0];
            var (literals, code) = Lex(File.ReadAllText(path));
            var envReadLines = EnvReadPattern.Matches(code).Select(m => LineOf(code, m.Index)).ToList();
            files.Add(new ScannedFile(relative, topFolder.StartsWith(TestProjectFolderPrefix, StringComparison.Ordinal), literals, envReadLines));
        }

        if (files.Count < 100)
        {
            throw new InvalidOperationException($"Scanned only {files.Count} .cs files under {root}; the scan is not seeing the repository.");
        }

        return files;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.')
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Worktree", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(child))
            {
                yield return file;
            }
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>Returns the whole name-shaped literals with their 1-based lines, and the file text with comments, char literals and string contents blanked (same offsets and newlines).</summary>
    private static (List<NameLiteral> Literals, string Code) Lex(string text)
    {
        var literals = new List<NameLiteral>();
        var code = new StringBuilder(text.Length);
        var line = 1;
        var lastSignificant = '\0';

        void Emit(int start, int end, bool blank)
        {
            for (var k = start; k < end; k++)
            {
                var ch = text[k];
                if (ch == '\n')
                {
                    line++;
                    code.Append('\n');
                }
                else if (ch == '\r')
                {
                    code.Append('\r');
                }
                else
                {
                    code.Append(blank ? ' ' : ch);
                }
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                Emit(i, end, blank: true);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + 2;
                Emit(i, end, blank: true);
                i = end;
            }
            else if (c == '\'')
            {
                var end = CharLiteralEnd(text, i);
                Emit(i, end, blank: true);
                lastSignificant = '\'';
                i = end;
            }
            else if (c == '"')
            {
                var verbatim = false;
                var interpolated = false;
                for (var back = i - 1; back >= 0 && back >= i - 3; back--)
                {
                    if (text[back] == '@')
                    {
                        verbatim = true;
                    }
                    else if (text[back] == '$')
                    {
                        interpolated = true;
                    }
                    else
                    {
                        break;
                    }
                }

                var startLine = line;
                var isRaw = i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                var whole = false;
                int end;
                if (isRaw)
                {
                    end = RawStringEnd(text, i);
                }
                else if (verbatim)
                {
                    end = VerbatimEnd(text, i);
                }
                else if (interpolated)
                {
                    end = InterpolatedEnd(text, i);
                }
                else
                {
                    end = NormalEnd(text, i, out whole);
                }

                if (whole)
                {
                    var value = text.Substring(i + 1, end - i - 2);
                    var joinedBefore = lastSignificant == '+';
                    var joinedAfter = NextSignificant(text, end) == '+';
                    if (!joinedBefore && !joinedAfter && NamePattern.IsMatch(value))
                    {
                        literals.Add(new NameLiteral(value, startLine));
                    }
                }

                Emit(i, end, blank: true);
                lastSignificant = '"';
                i = end;
            }
            else
            {
                Emit(i, i + 1, blank: false);
                if (!char.IsWhiteSpace(c))
                {
                    lastSignificant = c;
                }

                i++;
            }
        }

        return (literals, code.ToString());
    }

    private static char NextSignificant(string text, int from)
    {
        for (var k = from; k < text.Length; k++)
        {
            if (!char.IsWhiteSpace(text[k]))
            {
                return text[k];
            }
        }

        return '\0';
    }

    private static int CharLiteralEnd(string text, int start)
    {
        var j = start + 1;
        while (j < text.Length && text[j] != '\'' && text[j] != '\n')
        {
            if (text[j] == '\\')
            {
                j++;
            }

            j++;
        }

        return j < text.Length && text[j] == '\'' ? j + 1 : Math.Min(j, text.Length);
    }

    // End index (exclusive). terminated is false for an unclosed string, which stops at the end of its line.
    private static int NormalEnd(string text, int start, out bool terminated)
    {
        var j = start + 1;
        while (j < text.Length)
        {
            var ch = text[j];
            if (ch == '\\')
            {
                j += 2;
                continue;
            }

            if (ch == '"')
            {
                terminated = true;
                return j + 1;
            }

            if (ch == '\n')
            {
                terminated = false;
                return j;
            }

            j++;
        }

        terminated = false;
        return text.Length;
    }

    private static int VerbatimEnd(string text, int start)
    {
        var j = start + 1;
        while (j < text.Length)
        {
            if (text[j] == '"')
            {
                if (j + 1 < text.Length && text[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return text.Length;
    }

    private static int RawStringEnd(string text, int start)
    {
        var opening = 0;
        while (start + opening < text.Length && text[start + opening] == '"')
        {
            opening++;
        }

        var j = start + opening;
        while (j < text.Length)
        {
            if (text[j] != '"')
            {
                j++;
                continue;
            }

            var run = 0;
            while (j + run < text.Length && text[j + run] == '"')
            {
                run++;
            }

            if (run >= opening)
            {
                return j + run;
            }

            j += run;
        }

        return text.Length;
    }

    private static int InterpolatedEnd(string text, int start)
    {
        var j = start + 1;
        var depth = 0;
        while (j < text.Length)
        {
            var ch = text[j];
            var next = j + 1 < text.Length ? text[j + 1] : '\0';
            if (depth == 0)
            {
                if (ch == '\\' || (ch == '{' && next == '{') || (ch == '}' && next == '}'))
                {
                    j += 2;
                }
                else if (ch == '"')
                {
                    return j + 1;
                }
                else if (ch == '{')
                {
                    depth = 1;
                    j++;
                }
                else if (ch == '\n')
                {
                    return j;
                }
                else
                {
                    j++;
                }
            }
            else if (ch == '"')
            {
                j = NormalEnd(text, j, out _);
            }
            else
            {
                depth += ch == '{' ? 1 : ch == '}' ? -1 : 0;
                j++;
            }
        }

        return text.Length;
    }
}

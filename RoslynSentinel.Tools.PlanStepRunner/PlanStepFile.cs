using System.Text.RegularExpressions;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>One step file under plan-eval-defect-remediation-v2-steps, e.g. "04-phase1-tests.md".</summary>
/// <param name="Number">The step's leading "NN-" number, used for ordering and --start-step/--end-step.</param>
/// <param name="FileName">Bare file name, e.g. "01-baseline.md".</param>
/// <param name="FilePath">Absolute path under the plan directory (the root checkout's copy, not a worktree's).</param>
/// <param name="Body">
/// The step's markdown with any frontmatter block removed — what the model is shown. Read at load
/// time so the runner can inline it into the prompt rather than handing over a path the model has
/// to re-resolve; see Program.cs's prompt construction for why that indirection was removed.
/// </param>
/// <param name="ReadOnly">
/// True when the step is not permitted to change any file (e.g. 01-baseline.md, which only records
/// a test count). Enforced by the runner before commit, independently of whatever the step's prose
/// says — run 20260910-013550-398 had a read-only step perform two other steps' work because the
/// constraint existed only as prose.
/// </param>
/// <param name="BuildOptional">
/// True when the step's own Gate section explicitly documents leaving the solution non-compiling on
/// purpose, so a red build shouldn't halt the run. Normally false and normally should stay so: the
/// next step's worktree is built from this one's committed tip, so a step that advances red hands
/// the following step a broken tree. No current step sets it.
/// </param>
public sealed record PlanStepFile(
    int Number,
    string FileName,
    string FilePath,
    string Body,
    bool ReadOnly,
    bool BuildOptional)
{
    private static readonly Regex NumberPrefix = new(@"^(\d+)-", RegexOptions.Compiled);

    /// <summary>
    /// Loads every "NN-*.md" file in <paramref name="planDir"/> whose leading number falls within
    /// [startStep, endStep], sorted by that number. "00-index.md" is excluded — it's the plan's own
    /// table of contents, not a step to execute.
    /// </summary>
    public static List<PlanStepFile> LoadRange(string planDir, int startStep, int endStep)
    {
        if (!Directory.Exists(planDir))
        {
            throw new ArgumentException($"Plan directory not found: {planDir}");
        }

        var steps = new List<PlanStepFile>();
        foreach (var path in Directory.GetFiles(planDir, "*.md"))
        {
            var fileName = Path.GetFileName(path);
            var match = NumberPrefix.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var number = int.Parse(match.Groups[1].Value);
            if (number == 0)
            {
                continue; // 00-index.md is the table of contents, not an executable step.
            }

            if (number >= startStep && number <= endStep)
            {
                steps.Add(Parse(number, fileName, path, File.ReadAllText(path)));
            }
        }

        steps.Sort((a, b) => a.Number.CompareTo(b.Number));
        return steps;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into its optional frontmatter flags and its body. Exposed
    /// (rather than inlined into <see cref="LoadRange"/>) so the parse can be tested directly
    /// against literal step text without staging files on disk.
    /// </summary>
    public static PlanStepFile Parse(int number, string fileName, string filePath, string text)
    {
        var (readOnly, buildOptional, body) = ParseFrontmatter(text, fileName);
        return new PlanStepFile(number, fileName, filePath, body, readOnly, buildOptional);
    }

    /// <summary>
    /// Reads a leading "---" delimited block of "key: value" lines. Deliberately not a YAML parser:
    /// only two boolean keys are recognized, and pulling in a YAML dependency to read them would be
    /// far more machinery than the format warrants. Unknown keys are ignored so the block stays
    /// extensible, but an unterminated block throws rather than being treated as body — a step
    /// whose readOnly marker silently failed to parse would run as if unmarked, which is exactly
    /// the failure this flag exists to prevent.
    /// </summary>
    private static (bool ReadOnly, bool BuildOptional, string Body) ParseFrontmatter(string text, string fileName)
    {
        // Normalize only for the delimiter check — the body is returned with its original line
        // endings intact, since it goes to the model verbatim.
        if (!text.StartsWith("---\n", StringComparison.Ordinal) &&
            !text.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            return (false, false, text);
        }

        var lines = text.Split('\n');
        var closingIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---")
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            throw new InvalidOperationException(
                $"Plan step '{fileName}' opens with a '---' frontmatter block that is never closed. " +
                "Add a closing '---' line, or remove the opening one if the file has no frontmatter — " +
                "an unparsed block would silently drop flags like readOnly.");
        }

        var readOnly = false;
        var buildOptional = false;
        for (var i = 1; i < closingIndex; i++)
        {
            var line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                throw new InvalidOperationException(
                    $"Plan step '{fileName}' has a frontmatter line that is not 'key: value': '{line}'.");
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            switch (key.ToLowerInvariant())
            {
                case "readonly":
                    readOnly = ParseBool(fileName, key, value);
                    break;
                case "buildoptional":
                    buildOptional = ParseBool(fileName, key, value);
                    break;
                default:
                    // Unknown keys are ignored on purpose — see the remarks above.
                    break;
            }
        }

        return (readOnly, buildOptional, string.Join('\n', lines.Skip(closingIndex + 1)));
    }

    private static bool ParseBool(string fileName, string key, string value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Plan step '{fileName}' frontmatter key '{key}' must be true or false, but was '{value}'.");
}

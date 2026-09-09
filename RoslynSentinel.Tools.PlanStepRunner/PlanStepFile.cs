using System.Text.RegularExpressions;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>One step file under plan-eval-defect-remediation-v2-steps, e.g. "04-phase1-tests.md".</summary>
public sealed record PlanStepFile(int Number, string FileName, string FilePath)
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
                steps.Add(new PlanStepFile(number, fileName, path));
            }
        }

        steps.Sort((a, b) => a.Number.CompareTo(b.Number));
        return steps;
    }
}

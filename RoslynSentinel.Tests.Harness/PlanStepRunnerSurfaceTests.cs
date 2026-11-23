using System.Text.RegularExpressions;
using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Utilities.PlanStepRunner;

namespace RoslynSentinel.Tests.PlanStepRunner;

/// <summary>
/// Keeps the PlanStepRunner's tool surface consistent across the four places that name it: the allow-list
/// (<see cref="AgentToolAllowLists"/>), the server include default (<see cref="RunnerOptions.DefaultIncludeTools"/>),
/// the planstep script default, and the prompts the model reads. Drift between them is what let the model see tools
/// the server did not register, or the server register classes the model never sees.
/// </summary>
[TestFixture]
public class PlanStepRunnerSurfaceTests
{
    private static readonly Regex Identifier = new(@"^[A-Za-z][A-Za-z0-9]*$");

    private static readonly string[] FallbackToolNames =
    [
        "LoadSolution", "ListWorkspaceSolutions", "Git", "GitRemote", "WriteFile", "ApplyDiff", "ApplyUnifiedDiff",
        "ProjectDoc", "FindReferences", "SearchSolutionText", "ListAll", "InspectSymbol", "McpServerStatus",
        "McpServerControl", "McpToolsetControl",
    ];

    [Test]
    public void Options_DefaultIncludeTools_IsUsedWhenFlagOmitted()
    {
        var options = RunnerOptions.Parse(["--plan-dir", "p", "--repo", "r"]);
        var classes = options.IncludeTools.Split(',');

        Assert.Multiple(() =>
        {
            Assert.That(options.IncludeTools, Is.EqualTo(RunnerOptions.DefaultIncludeTools));
            Assert.That(classes, Is.SupersetOf(new[]
            {
                "WorkspaceTools", "WorkspaceReadNavigationTools", "WholeFileWriteTools", "RefactoringStructuralTools",
                "RefactoringSignatureTools", "RefactoringExtractionDocsTools", "CoreRefactoringTools",
            }));
            Assert.That(classes, Does.Not.Contain("RefactoringTools"), "RefactoringTools is not a server tool class.");
            Assert.That(classes, Does.Not.Contain("SymbolNavigationTools"));
            Assert.That(classes, Does.Not.Contain("CommentingTools"));
        });
    }

    // Mutates the process environment variable ROSLYNSENTINEL_SCRATCH_ROOT, so it must not run in parallel with other tests.
    [Test]
    [NonParallelizable]
    public void Options_RelativeScratchRoot_ThrowsArgumentExceptionNamingTheVariable()
    {
        var previous = Environment.GetEnvironmentVariable(ScratchRoot.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ScratchRoot.EnvironmentVariable, "relative-scratch");
            var ex = Assert.Throws<ArgumentException>(() => RunnerOptions.Parse(["--plan-dir", "p", "--repo", "r"]));
            Assert.That(ex.Message, Is.Not.Empty);
            Assert.That(ex.Message, Does.Contain(ScratchRoot.EnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScratchRoot.EnvironmentVariable, previous);
        }
    }

    [Test]
    public void DefaultIncludeTools_CoversTheClassOfEveryAllowListedTool()
    {
        var rows = ReadToolRows(RequireRepoFile(Path.Combine("docs", "generated", "architecture_tools.md")));
        var defaultClasses = RunnerOptions.DefaultIncludeTools.Split(',').ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var tool in AgentToolAllowLists.PlanStepRunnerSurface)
        {
            var classes = rows.Where(r => r.Tool == tool).Select(r => r.Class).Distinct().ToList();
            if (classes.Count == 0)
            {
                offenders.Add($"{tool}: no row in architecture_tools.md");
            }
            else if (!classes.Any(c => defaultClasses.Contains(c)))
            {
                offenders.Add($"{tool}: class {string.Join("/", classes)} is not in DefaultIncludeTools");
            }
        }

        Assert.That(offenders, Is.Empty, "Allow-listed tools whose server class is not included by default: " + string.Join("; ", offenders));
    }

    [Test]
    public void PlanStepScript_IncludeToolsDefault_MatchesRunnerOptions()
    {
        var text = File.ReadAllText(RequireRepoFile(Path.Combine("scripts", "roslynsentinel-planstep.ps1")));
        var match = Regex.Match(text, @"\[string\]\$IncludeTools\s*=\s*'([^']*)'");

        Assert.That(match.Success, Is.True, "scripts/roslynsentinel-planstep.ps1 has no [string]$IncludeTools default in single quotes.");
        Assert.That(match.Groups[1].Value, Is.EqualTo(RunnerOptions.DefaultIncludeTools));
    }

    [Test]
    public void Prompts_NameNoToolOutsideTheAllowList()
    {
        var prompts = new (string Label, string Text)[]
        {
            ("StepPrompts.BuildUserPrompt", StepPrompts.BuildUserPrompt(
                PlanStepFile.Parse(1, "01-sample.md", "01-sample.md", "Rename Foo to Bar in Foo.cs."))),
            ("ErrorPolicyWiring.ComposePrompt(CodingAgent)", ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgent)),
            ("ErrorPolicyWiring.ComposePrompt(CodingAgentContract)", ErrorPolicyWiring.ComposePrompt(AgentSystemPrompts.CodingAgentContract)),
        };

        var candidates = new SortedSet<string>(FallbackToolNames, StringComparer.Ordinal);
        var root = FindRepoRoot();
        if (root is not null)
        {
            var doc = Path.Combine(root, "docs", "generated", "architecture_tools.md");
            if (File.Exists(doc))
            {
                foreach (var row in ReadToolRows(doc))
                {
                    if (Identifier.IsMatch(row.Tool))
                    {
                        candidates.Add(row.Tool);
                    }
                }
            }
        }

        var surface = new HashSet<string>(AgentToolAllowLists.PlanStepRunnerSurface, StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var (label, text) in prompts)
        {
            foreach (var name in candidates)
            {
                if (name.Length >= 5 && !surface.Contains(name) && Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b"))
                {
                    offenders.Add($"({label}, {name})");
                }
            }
        }

        Assert.That(offenders, Is.Empty, "Prompts name tools outside PlanStepRunnerSurface: " + string.Join(", ", offenders));
    }

    /// <summary>Walks up from AppContext.BaseDirectory to the folder holding RoslynSentinel.slnx; null when absent.</summary>
    private static string? FindRepoRoot() => RepoRootLocator.TryFind(AppContext.BaseDirectory);

    /// <summary>Full path of a repo-relative file; Assert.Ignore when the repo or the file is not present.</summary>
    private static string RequireRepoFile(string relativePath)
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            Assert.Ignore("RoslynSentinel.slnx not found above the test directory; this test needs a repo checkout.");
        }

        var path = Path.Combine(root!, relativePath);
        if (!File.Exists(path))
        {
            Assert.Ignore($"{relativePath} is missing; this test needs it.");
        }

        return path;
    }

    /// <summary>
    /// Reads (class, tool) pairs from a markdown table row such as
    /// "| WorkspaceTools | ReadFile | Claude, claude-lean, Workspace | - | description |". Header and separator rows
    /// yield non-matching names and are harmless to callers that look up specific tools.
    /// </summary>
    private static List<(string Class, string Tool)> ReadToolRows(string path)
    {
        var rows = new List<(string Class, string Tool)>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith('|'))
            {
                continue;
            }

            // A row "| A | B | ..." splits to "", "A", "B", ...
            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length >= 3)
            {
                rows.Add((cells[1], cells[2]));
            }
        }

        return rows;
    }
}

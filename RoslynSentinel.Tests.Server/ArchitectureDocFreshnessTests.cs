namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ArchitectureDocFreshnessTests
{
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_GENERATED_DOCS";

    private const string RegenerateHint =
        "Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness\n" +
        "Or run: scripts/Generate-ArchitectureMap.ps1";

    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var slnxPath = Path.Combine(current, "RoslynSentinel.slnx");
            if (File.Exists(slnxPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException("Could not find RoslynSentinel.slnx - unable to locate repo root");
    }

    private static bool IsUpdateMode()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UpdateEnvVar));
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static string Clip(string? line, int maxLength)
    {
        if (line is null)
        {
            return "<no such line>";
        }

        return line.Length <= maxLength ? line : line.Substring(0, maxLength) + "...";
    }

    /// <summary>
    /// A simple positional line diff after line-ending normalization: the first <paramref name="maxLines"/>
    /// differing lines as "line N / expected / actual", each clipped to <paramref name="maxLineLength"/>,
    /// plus a count of the rest. Positional, so an inserted or removed row shifts every later line; the
    /// first differing line is still the place to look.
    /// </summary>
    internal static string DescribeDifferences(string expected, string actual, int maxLines = 20, int maxLineLength = 200)
    {
        var expectedLines = NormalizeLineEndings(expected).Split('\n');
        var actualLines = NormalizeLineEndings(actual).Split('\n');
        var lineCount = Math.Max(expectedLines.Length, actualLines.Length);

        var details = new System.Text.StringBuilder();
        var total = 0;
        var shown = 0;

        for (var i = 0; i < lineCount; i++)
        {
            var expectedLine = i < expectedLines.Length ? expectedLines[i] : null;
            var actualLine = i < actualLines.Length ? actualLines[i] : null;
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                continue;
            }

            total++;
            if (shown >= maxLines)
            {
                continue;
            }

            shown++;
            details.Append("  line ").Append(i + 1).Append(":\n");
            details.Append("    expected: ").Append(Clip(expectedLine, maxLineLength)).Append('\n');
            details.Append("    actual:   ").Append(Clip(actualLine, maxLineLength)).Append('\n');
        }

        if (total > shown)
        {
            details.Append("  ... and ").Append(total - shown).Append(" more differing line(s)\n");
        }

        return $"{total} differing line(s) (expected = freshly generated, actual = committed file):\n{details}";
    }

    private static void RegenerateWhenRequested(string fileName, string generatedContent)
    {
        if (!IsUpdateMode())
        {
            Assert.Ignore($"Regeneration skipped: set {UpdateEnvVar}=1 to rewrite docs/generated/{fileName}. " +
                          "This test writes a file and asserts nothing, so a green run would not prove it ran.");
        }

        var docsGenDir = Path.Combine(GetRepoRoot(), "docs", "generated");
        Directory.CreateDirectory(docsGenDir);
        File.WriteAllText(Path.Combine(docsGenDir, fileName), generatedContent, System.Text.Encoding.UTF8);
    }

    private static void AssertCommittedFileIsFresh(string fileName, string generatedContent)
    {
        // Skip if update env var set (regen mode)
        if (IsUpdateMode())
        {
            Assert.Ignore("Update mode active - regenerating instead of asserting");
        }

        var path = Path.Combine(GetRepoRoot(), "docs", "generated", fileName);

        if (!File.Exists(path))
        {
            Assert.Fail($"Architecture documentation stale or missing: {path}\n{RegenerateHint}");
        }

        var committedContent = NormalizeLineEndings(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var freshContent = NormalizeLineEndings(generatedContent);

        if (!string.Equals(committedContent, freshContent, StringComparison.Ordinal))
        {
            Assert.Fail($"Architecture documentation is stale: {path}\n" +
                        DescribeDifferences(freshContent, committedContent) +
                        RegenerateHint);
        }
    }

    /// <summary>The comma-separated Modes cell of one row of the generated tools table, or null if the row is absent.</summary>
    private static string[]? FindModes(string toolsMarkdown, string className, string toolName)
    {
        var rowPrefix = $"| {className} | {toolName} | ";
        foreach (var line in NormalizeLineEndings(toolsMarkdown).Split('\n'))
        {
            if (!line.StartsWith(rowPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|');
            return cells[3].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return null;
    }

    [Test]
    public void ToolsArchitectureDocFreshness_WhenUpdateEnvVarSet_RegeneratesFile()
    {
        RegenerateWhenRequested("architecture_tools.md", ArchitectureDocGenerator.GenerateToolsMarkdown());
    }

    [Test]
    public void ToolsArchitectureDocFreshness_WhenCommittedFileExists_MatchesRegeneratedContent()
    {
        AssertCommittedFileIsFresh("architecture_tools.md", ArchitectureDocGenerator.GenerateToolsMarkdown());
    }

    [Test]
    public void ProjectsArchitectureDocFreshness_WhenUpdateEnvVarSet_RegeneratesFile()
    {
        RegenerateWhenRequested("architecture_projects.md", ArchitectureDocGenerator.GenerateProjectsMarkdown());
    }

    [Test]
    public void ProjectsArchitectureDocFreshness_WhenCommittedFileExists_MatchesRegeneratedContent()
    {
        AssertCommittedFileIsFresh("architecture_projects.md", ArchitectureDocGenerator.GenerateProjectsMarkdown());
    }

    [Test]
    public void ToolsMarkdown_ClaudeLeanModeIsPerTool_NotPerClass()
    {
        var markdown = ArchitectureDocGenerator.GenerateToolsMarkdown();

        var build = FindModes(markdown, "WorkspaceTools", "Build");
        var listAll = FindModes(markdown, "WorkspaceTools", "ListAll");
        var bridge = FindModes(markdown, "AsyncifyTools", "BridgeAsyncMethods");
        var parameterEdit = FindModes(markdown, "ParameterEditTools", "ParameterEdit");
        var modifyEnum = FindModes(markdown, "RefactoringStructuralTools", "ModifyEnum");

        Assert.That(build, Is.Not.Null, "WorkspaceTools|Build row missing");
        Assert.That(listAll, Is.Not.Null, "WorkspaceTools|ListAll row missing");
        Assert.That(bridge, Is.Not.Null, "AsyncifyTools|BridgeAsyncMethods row missing");
        Assert.That(parameterEdit, Is.Not.Null, "ParameterEditTools|ParameterEdit row missing");
        Assert.That(modifyEnum, Is.Not.Null, "RefactoringStructuralTools|ModifyEnum row missing");

        // Core allow-list tool: plain claude-lean, other class modes untouched.
        Assert.That(build, Does.Contain(ArchitectureDocGenerator.ClaudeLeanCoreLabel));
        Assert.That(build, Does.Not.Contain(ArchitectureDocGenerator.ClaudeLeanOnDemandLabel));
        Assert.That(build, Does.Contain("Workspace"));

        // Same class as BuildTool, but not on the allow-list and in no toolset: the class is loaded, the tool is hidden.
        Assert.That(listAll!.Any(mode => mode.StartsWith("claude-lean", StringComparison.Ordinal)), Is.False);
        Assert.That(listAll, Does.Contain("Workspace"));

        // Advanced tool outside the allow-list.
        Assert.That(bridge!.Any(mode => mode.StartsWith("claude-lean", StringComparison.Ordinal)), Is.False);

        // Toolset members: on-demand marker only.
        Assert.That(parameterEdit, Does.Contain(ArchitectureDocGenerator.ClaudeLeanOnDemandLabel));
        Assert.That(parameterEdit, Does.Not.Contain(ArchitectureDocGenerator.ClaudeLeanCoreLabel));
        Assert.That(modifyEnum, Does.Contain(ArchitectureDocGenerator.ClaudeLeanOnDemandLabel));
        Assert.That(modifyEnum, Does.Contain("Refactor"));
    }

    [Test]
    public void DescribeDifferences_ShowsLineNumbersExpectedAndActual_AndCapsTheOutput()
    {
        var expected = string.Join("\r\n", Enumerable.Range(1, 30).Select(i => $"row {i}"));
        var actual = string.Join("\n", Enumerable.Range(1, 30).Select(i => i % 2 == 0 ? $"changed {i}" : $"row {i}"));

        var message = DescribeDifferences(expected, actual, maxLines: 3);

        Assert.That(message, Does.StartWith("15 differing line(s)"));
        Assert.That(message, Does.Contain("line 2:"));
        Assert.That(message, Does.Contain("expected: row 2"));
        Assert.That(message, Does.Contain("actual:   changed 2"));
        Assert.That(message, Does.Contain("line 6:"));
        Assert.That(message, Does.Not.Contain("line 8:"));
        Assert.That(message, Does.Contain("... and 12 more differing line(s)"));

        Assert.That(DescribeDifferences("a\r\nb", "a\nb"), Does.StartWith("0 differing line(s)"));
    }
}

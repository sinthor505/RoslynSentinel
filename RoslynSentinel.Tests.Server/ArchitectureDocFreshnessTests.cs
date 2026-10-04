namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class ArchitectureDocFreshnessTests
{
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

    private static string NormalizeLine(string line)
    {
        return line.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    [Test]
    public void ToolsArchitectureDocFreshness_WhenUpdateEnvVarSet_RegeneratesFile()
    {
        var repoRoot = GetRepoRoot();
        var docsGenDir = Path.Combine(repoRoot, "docs", "generated");
        var toolsPath = Path.Combine(docsGenDir, "architecture_tools.md");

        // Generate fresh content
        var generatedContent = ArchitectureDocGenerator.GenerateToolsMarkdown();

        // Ensure directory exists
        Directory.CreateDirectory(docsGenDir);

        // Write if env var set
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_UPDATE_GENERATED_DOCS")))
        {
            File.WriteAllText(toolsPath, generatedContent, System.Text.Encoding.UTF8);
        }
    }

    [Test]
    public void ToolsArchitectureDocFreshness_WhenCommittedFileExists_MatchesRegeneratedContent()
    {
        var repoRoot = GetRepoRoot();
        var docsGenDir = Path.Combine(repoRoot, "docs", "generated");
        var toolsPath = Path.Combine(docsGenDir, "architecture_tools.md");

        // Skip if update env var set (regen mode)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_UPDATE_GENERATED_DOCS")))
        {
            Assert.Ignore("Update mode active - regenerating instead of asserting");
        }

        // If file doesn't exist, fail with helpful message
        if (!File.Exists(toolsPath))
        {
            var msg = $"Architecture documentation stale or missing: {toolsPath}\n" +
                      $"Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness\n" +
                      $"Or run: scripts/Generate-ArchitectureMap.ps1";
            Assert.Fail(msg);
        }

        // Read committed file and normalize line endings
        var committedContent = File.ReadAllText(toolsPath, System.Text.Encoding.UTF8);
        committedContent = string.Join("\n", committedContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

        // Generate fresh content
        var generatedContent = ArchitectureDocGenerator.GenerateToolsMarkdown();
        generatedContent = string.Join("\n", generatedContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

        // Compare with helpful message on failure
        if (committedContent != generatedContent)
        {
            var msg = $"Architecture documentation is stale: {toolsPath}\n" +
                      $"Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness\n" +
                      $"Or run: scripts/Generate-ArchitectureMap.ps1";
            Assert.Fail(msg);
        }

        Assert.That(committedContent, Is.EqualTo(generatedContent));
    }

    [Test]
    public void ProjectsArchitectureDocFreshness_WhenUpdateEnvVarSet_RegeneratesFile()
    {
        var repoRoot = GetRepoRoot();
        var docsGenDir = Path.Combine(repoRoot, "docs", "generated");
        var projectsPath = Path.Combine(docsGenDir, "architecture_projects.md");

        // Generate fresh content
        var generatedContent = ArchitectureDocGenerator.GenerateProjectsMarkdown();

        // Ensure directory exists
        Directory.CreateDirectory(docsGenDir);

        // Write if env var set
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_UPDATE_GENERATED_DOCS")))
        {
            File.WriteAllText(projectsPath, generatedContent, System.Text.Encoding.UTF8);
        }
    }

    [Test]
    public void ProjectsArchitectureDocFreshness_WhenCommittedFileExists_MatchesRegeneratedContent()
    {
        var repoRoot = GetRepoRoot();
        var docsGenDir = Path.Combine(repoRoot, "docs", "generated");
        var projectsPath = Path.Combine(docsGenDir, "architecture_projects.md");

        // Skip if update env var set (regen mode)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROSLYNSENTINEL_UPDATE_GENERATED_DOCS")))
        {
            Assert.Ignore("Update mode active - regenerating instead of asserting");
        }

        // If file doesn't exist, fail with helpful message
        if (!File.Exists(projectsPath))
        {
            var msg = $"Architecture documentation stale or missing: {projectsPath}\n" +
                      $"Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness\n" +
                      $"Or run: scripts/Generate-ArchitectureMap.ps1";
            Assert.Fail(msg);
        }

        // Read committed file and normalize line endings
        var committedContent = File.ReadAllText(projectsPath, System.Text.Encoding.UTF8);
        committedContent = string.Join("\n", committedContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

        // Generate fresh content
        var generatedContent = ArchitectureDocGenerator.GenerateProjectsMarkdown();
        generatedContent = string.Join("\n", generatedContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

        // Compare with helpful message on failure
        if (committedContent != generatedContent)
        {
            var msg = $"Architecture documentation is stale: {projectsPath}\n" +
                      $"Regenerate with: $env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; dotnet test RoslynSentinel.Tests.Server --filter ArchitectureDocFreshness\n" +
                      $"Or run: scripts/Generate-ArchitectureMap.ps1";
            Assert.Fail(msg);
        }

        Assert.That(committedContent, Is.EqualTo(generatedContent));
    }
}

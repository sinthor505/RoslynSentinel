using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public partial class DependencyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public DependencyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Returns all project and NuGet dependencies for a specific project.
    /// </summary>
    public async Task<ProjectDependencyReport> GetProjectDependenciesAsync(string projectName)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            throw new InvalidOperationException($"Project '{projectName}' not found.");
        }

        var projectRefs = project.ProjectReferences
            .Select(r => solution.Projects.First(p => p.Id == r.ProjectId).Name)
            .ToList();

        var packageRefs = new List<string>();
        if (project.FilePath != null && File.Exists(project.FilePath))
        {
            var content = await File.ReadAllTextAsync(project.FilePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(content, "<PackageReference\\s+Include=\"([^\"]+)\"");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                packageRefs.Add(match.Groups[1].Value);
            }
        }

        return new ProjectDependencyReport(projectRefs, packageRefs);
    }

    /// <summary>
    /// Scans project files to find unused NuGet package references.
    /// </summary>
    public async Task<List<string>> FindUnusedReferencesAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project == null)
        {
            throw new InvalidOperationException($"Project '{projectName}' not found.");
        }

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            throw new InvalidOperationException($"Failed to get compilation for project '{projectName}'.");
        }

        var usedAssemblies = new HashSet<string>();
        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                continue;
            }

            var nodes = (await document.GetSyntaxRootAsync(cancellationToken))?.DescendantNodes();
            if (nodes == null)
            {
                continue;
            }

            foreach (var node in nodes)
            {
                var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol != null && symbol.ContainingAssembly != null)
                {
                    usedAssemblies.Add(symbol.ContainingAssembly.Name);
                }
            }
        }

        var unused = new List<string>();
        foreach (var reference in compilation.References)
        {
            if (reference is CompilationReference compRef && compRef.Compilation.AssemblyName != null && !usedAssemblies.Contains(compRef.Compilation.AssemblyName))
            {
                unused.Add(compRef.Compilation.AssemblyName);
            }
        }

        return unused;
    }

    /// <summary>
    /// Checks for version mismatches of the same package across multiple projects.
    /// </summary>
    public async Task<List<string>> CheckPackageInconsistencyAsync(CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var packageVersions = new Dictionary<string, List<(string Project, string Version)>>();

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null)
            {
                continue;
            }

            var xml = await File.ReadAllTextAsync(project.FilePath, cancellationToken);
            var matches = System.Text.RegularExpressions.Regex.Matches(xml, @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)""");

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var id = match.Groups[1].Value;
                var version = match.Groups[2].Value;
                if (!packageVersions.TryGetValue(id, out List<(string Project, string Version)>? value))
                {
                    value = new List<(string, string)>();
                    packageVersions[id] = value;
                }

                value.Add((project.Name, version));
            }
        }

        return packageVersions
            .Where(kvp => kvp.Value.Select(v => v.Version).Distinct().Count() > 1)
            .Select(kvp => $"Package '{kvp.Key}' has multiple versions: {string.Join(", ", kvp.Value.Select(v => $"{v.Project} ({v.Version})"))}")
            .ToList();
    }
}

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Engines.Basic;

/// <summary>What applying one test project's planned edits did. A failed project carries <see cref="Error"/> and wrote nothing.</summary>
/// <param name="ProjectName">The test project.</param>
/// <param name="ClassLevelAdded">Class-level attributes written.</param>
/// <param name="MethodLevelAdded">Method-level attributes written.</param>
/// <param name="RemovedStale">Marked stale attributes removed.</param>
/// <param name="SkippedExisting">Edits the planner skipped because the category was already present or covered.</param>
/// <param name="Failed">Planned edits that did not land: the whole project's edits when its batch was rejected, else the edits skipped at apply time.</param>
/// <param name="AffectedFiles">Files written.</param>
/// <param name="ChangeId">Undo id of this project's batch, null when nothing was written.</param>
/// <param name="Error">Why the project's batch was rejected (for example compile errors), null on success.</param>
/// <param name="SkippedEdits">Edits skipped at apply time, each with a reason.</param>
public sealed record TestCategoryProjectApplyResult(
    string ProjectName,
    int ClassLevelAdded,
    int MethodLevelAdded,
    int RemovedStale,
    int SkippedExisting,
    int Failed,
    IReadOnlyList<string> AffectedFiles,
    string? ChangeId,
    ResultError? Error,
    IReadOnlyList<TestCategoryEditSkip> SkippedEdits)
{
    public bool Succeeded => Error is null;
}

/// <summary>Per-project outcomes of an apply run, in plan order.</summary>
public sealed record TestCategoryApplyResult(IReadOnlyList<TestCategoryProjectApplyResult> Projects);

/// <summary>
/// Applies a <see cref="TestCategoryPlan"/>: turns each test project's planned edits into new file text
/// (<see cref="TestCategoryTextEditor"/>) and writes them as ONE <see cref="ValidateAndApplyHelper"/> batch per project, so
/// the write chokepoint, EOL guard, compile gate, rollback and undo record all apply. A project whose batch is rejected is
/// reported and left unchanged; the other projects still apply.
/// </summary>
public sealed class TestCategoryApplyEngine
{
    private const string OperationName = "TagTestCategories";

    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;

    public TestCategoryApplyEngine(IWorkspaceManager workspaceManager, ValidationEngine validationEngine)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
    }

    public async Task<TestCategoryApplyResult> ApplyAsync(TestCategoryPlan plan, ILogger logger, CancellationToken cancellationToken = default)
    {
        var results = new List<TestCategoryProjectApplyResult>();
        foreach (var project in plan.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ApplyProjectAsync(project, logger, cancellationToken));
        }

        return new TestCategoryApplyResult(results);
    }

    private async Task<TestCategoryProjectApplyResult> ApplyProjectAsync(TestProjectPlan project, ILogger logger, CancellationToken cancellationToken)
    {
        if (project.Edits.Count == 0)
        {
            return new TestCategoryProjectApplyResult(project.ProjectName, 0, 0, 0, project.SkippedExisting, 0, [], null, null, []);
        }

        // Re-read the committed solution per project: an earlier project's write has already resynced it.
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var wanted = new HashSet<string>(project.Edits.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not null && wanted.Contains(document.FilePath) && !texts.ContainsKey(document.FilePath))
            {
                texts[document.FilePath] = (await document.GetTextAsync(cancellationToken)).ToString();
            }
        }

        var changes = new Dictionary<FilePathWrapper, string>();
        var skipped = new List<TestCategoryEditSkip>();
        var classAdded = 0;
        var methodAdded = 0;
        var removed = 0;

        foreach (var group in project.Edits.GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!texts.TryGetValue(group.Key, out var text))
            {
                skipped.AddRange(group.Select(e => new TestCategoryEditSkip(e, "The file is not part of the loaded solution; reload the solution and re-run the dry run.")));
                continue;
            }

            var edited = TestCategoryTextEditor.Apply(text, group.ToList());
            skipped.AddRange(edited.Skipped);
            if (!string.Equals(edited.NewText, text, StringComparison.Ordinal))
            {
                changes[new FilePathWrapper(group.Key)] = edited.NewText;
                classAdded += edited.ClassLevelAdded;
                methodAdded += edited.MethodLevelAdded;
                removed += edited.RemovedStale;
            }
        }

        if (changes.Count == 0)
        {
            return new TestCategoryProjectApplyResult(project.ProjectName, 0, 0, 0, project.SkippedExisting, skipped.Count, [], null, null, skipped);
        }

        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, logger, changes, OperationName,
            dryRun: false, cancellationToken: cancellationToken);

        if (outcome.Error is not null)
        {
            return new TestCategoryProjectApplyResult(
                project.ProjectName, 0, 0, 0, project.SkippedExisting, project.Edits.Count, [], null, outcome.Error, skipped);
        }

        return new TestCategoryProjectApplyResult(
            project.ProjectName, classAdded, methodAdded, removed, project.SkippedExisting, skipped.Count,
            changes.Keys.Select(k => k.Absolute).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            outcome.ChangeId, null, skipped);
    }
}

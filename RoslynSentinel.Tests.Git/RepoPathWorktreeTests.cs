using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The optional <c>repoPath</c> of Build and RunTest (TODO punchlist #15): <see cref="RepoWorktrees"/> validation, path mapping and
/// HEAD read against real git worktrees, the tool-level refusals, and one real worktree build that must leave the loaded tree's
/// bin/obj untouched. The loaded tree is <c>main</c>; <c>old</c> is a linked worktree created before the project file was committed,
/// <c>new</c> one created after it (and then given its own commit).
/// </summary>
[TestFixture]
[Category("OutOfProcess")] // starts git (and, for one test, dotnet) processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class RepoPathWorktreeTests
{
    private const string ProjectXml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>";

    private string _root = null!;
    private string _main = null!;
    private string _old = null!;
    private string _new = null!;
    private string _newHead = null!;
    private string _plain = null!;
    private string _other = null!;
    private PersistentWorkspaceManager? _manager;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        // Short directory names: a linked-worktree git dir under a long name can pass MAX_PATH.
        _root = Path.Combine(Path.GetTempPath(), "rs-rp-" + Guid.NewGuid().ToString("N")[..12]);
        _main = Path.Combine(_root, "main");
        InitRepo(_main);
        File.WriteAllText(Path.Combine(_main, "App.sln"), "placeholder");
        CommitAll(_main, "first");
        _old = AddLinkedWorktree(_main, _root, "old");

        var projectDirectory = Path.Combine(_main, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "App.csproj"), ProjectXml);
        CommitAll(_main, "second");
        _new = AddLinkedWorktree(_main, _root, "new");
        _newHead = CommitAll(_new, "worktree commit");

        _plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(_plain);
        _other = Path.Combine(_root, "other");
        InitRepo(_other);
        File.WriteAllText(Path.Combine(_other, "x.txt"), "x");
        CommitAll(_other, "other first");
    }

    [TearDown]
    public void TearDown()
    {
        _manager?.Dispose();
        _manager = null;
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // Git object files are read-only; Directory.Delete would refuse them.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private WorkspaceBuildTestImpl CreateImpl()
    {
        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "App", "App", LanguageNames.CSharp,
                filePath: Path.Combine(_main, "src", "App", "App.csproj")));
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance, Path.Combine(_main, "App.sln"));
        _manager.SetTestSolution(solution);
        return new WorkspaceBuildTestImpl(_manager,
            Battery.Basic.TestEngines.DiagnosticEngine(_manager),
            Battery.Basic.TestEngines.BuildEngine(_manager),
            Battery.Basic.TestEngines.TestRunEngine(_manager),
            NullLogger<WorkspaceBuildTestImpl>.Instance);
    }

    [Test]
    public void TryResolve_RelativePath_IsRefused()
    {
        var ok = RepoWorktrees.TryResolve(Path.Combine("sub", "dir"), _main, out var root, out var error);

        Assert.That(ok, Is.False);
        Assert.That(root, Is.Empty);
        Assert.That(error, Does.Contain("not an absolute path"));
    }

    [Test]
    public void TryResolve_NonExistingDirectory_IsRefused()
    {
        var ok = RepoWorktrees.TryResolve(Path.Combine(_root, "missing"), _main, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("not an existing directory"));
    }

    [Test]
    public void TryResolve_PlainDirectory_IsRefused()
    {
        var ok = RepoWorktrees.TryResolve(_plain, _main, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("no .git entry"));
    }

    [Test]
    public void TryResolve_SubdirectoryOfAWorktree_IsRefused()
    {
        var ok = RepoWorktrees.TryResolve(Path.Combine(_new, "src"), _main, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("no .git entry"));
    }

    [Test]
    public void TryResolve_AnotherRepository_IsRefused()
    {
        var ok = RepoWorktrees.TryResolve(_other, _main, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("different repository"));
    }

    [Test]
    public void TryResolve_LinkedWorktree_IsAcceptedAndNamesItsRoot()
    {
        var ok = RepoWorktrees.TryResolve(_new + Path.DirectorySeparatorChar, _main, out var root, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(root, Is.EqualTo(_new).IgnoreCase);
    }

    [Test]
    public void TryResolve_MainWorkingTree_IsAcceptedFromALinkedWorktreeAsTheLoadedTree()
    {
        // The loaded solution may itself live in a linked worktree; the main tree is then one of its siblings.
        var ok = RepoWorktrees.TryResolve(_main, _new, out var root, out var error);

        Assert.That(ok, Is.True, error);
        Assert.That(root, Is.EqualTo(_main).IgnoreCase);
    }

    [Test]
    public void ReadHeadCommit_ReturnsTheWorktreesOwnHead()
    {
        var head = RepoWorktrees.ReadHeadCommit(_new);

        Assert.That(head, Is.EqualTo(_newHead));
        Assert.That(head, Is.Not.EqualTo(RepoWorktrees.ReadHeadCommit(_main)), "the worktree has a commit the main tree does not");
    }

    [Test]
    public void MapToWorktree_ReRootsTheRepositoryRelativePath()
    {
        var mapped = RepoWorktrees.MapToWorktree(Path.Combine(_main, "src", "App", "App.csproj"), _new);

        Assert.That(mapped, Is.EqualTo(Path.Combine(_new, "src", "App", "App.csproj")).IgnoreCase);
    }

    [Test]
    public void MapToWorktree_PathOutsideAnyRepository_IsNull()
    {
        Assert.That(RepoWorktrees.MapToWorktree(Path.Combine(_plain, "App.csproj"), _new), Is.Null);
    }

    [Test]
    public void TryMapExisting_FileMissingInTheWorktree_NamesTheRepositoryRelativePath()
    {
        var ok = RepoWorktrees.TryMapExisting(Path.Combine(_main, "src", "App", "App.csproj"), _old, out _, out var error);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("src/App/App.csproj"));
    }

    [Test]
    public async Task Build_RelativeRepoPath_ReturnsInvalidArgumentAsync()
    {
        var impl = CreateImpl();

        var result = await impl.Build("repoPath relative check", BuildVerifyLevel.fullBuildThrowaway, repoPath: Path.Combine("sub", "dir"));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("not an absolute path"));
    }

    [Test]
    public async Task Build_PlainDirectoryRepoPath_ReturnsInvalidArgumentAsync()
    {
        var impl = CreateImpl();

        var result = await impl.Build("repoPath plain directory check", BuildVerifyLevel.fullBuildThrowaway, repoPath: _plain);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("no .git entry"));
    }

    [Test]
    public async Task Build_QuickBuildWithRepoPath_ReturnsInvalidArgumentAsync()
    {
        var impl = CreateImpl();

        var result = await impl.Build("repoPath with quickBuild check", BuildVerifyLevel.quickBuild, repoPath: _new);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain("full builds only"));
    }

    [Test]
    public async Task RunTest_PlainDirectoryRepoPath_ReturnsInvalidArgumentAsync()
    {
        var impl = CreateImpl();

        var result = await impl.RunTest("repoPath plain directory check", ToolScope.project, "App", repoPath: _plain);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    // Engine seam: the project is re-rooted onto the worktree before any dotnet process starts, so a worktree that lacks it is
    // refused with the repository-relative path (no process, no build).
    [Test]
    public async Task Build_WorktreeLackingTheProject_FailsNamingTheMappedPathAsync()
    {
        var impl = CreateImpl();

        var result = await impl.Build("worktree without the project", BuildVerifyLevel.fullBuildThrowaway, ToolScope.project, "App", repoPath: _old);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("src/App/App.csproj"));
        Assert.That(result.ErrorData.Message, Does.Contain(_old));
    }

    [Test]
    public async Task RunTest_WorktreeLackingTheProject_FailsNamingTheMappedPathAsync()
    {
        var impl = CreateImpl();

        var result = await impl.RunTest("worktree without the project", ToolScope.project, "App", repoPath: _old);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("src/App/App.csproj"));
    }

    // A real `dotnet build` (empty library): fullBuild with repoPath writes the worktree's own obj/ and names its HEAD; main stays untouched.
    [Test]
    public async Task Build_FullBuildWithRepoPath_BuildsInTheWorktreeAndReportsItsHeadAsync()
    {
        var impl = CreateImpl();

        var result = await impl.Build("worktree full build", BuildVerifyLevel.fullBuild, ToolScope.project, "App", repoPath: _new);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var build = result.SuccessData as BuildResult;
        Assert.That(build, Is.Not.Null);
        Assert.That(build!.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
        Assert.That(build.WorktreeCommit, Is.EqualTo(_newHead));
        Assert.That(Directory.Exists(Path.Combine(_new, "src", "App", "obj")), Is.True, "the worktree's own obj/ is written");
        Assert.That(Directory.Exists(Path.Combine(_main, "src", "App", "obj")), Is.False, "the loaded tree's obj/ is untouched");
        Assert.That(Directory.Exists(Path.Combine(_main, "src", "App", "bin")), Is.False, "the loaded tree's bin/ is untouched");
    }
}

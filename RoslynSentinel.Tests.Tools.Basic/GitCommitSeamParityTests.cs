using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The reads and checks <c>GitImpl.CommitAsync</c> makes around <c>git commit</c> that plan 5a slice 3 moved onto the reader seam:
/// <see cref="IGitReader.ResolveCommitAbbreviation"/> (the summary line's abbreviation to a full hash), <see cref="IGitReader.GetCommitMessage"/> (the committed message),
/// <see cref="IGitReader.GetUpstreamContainingHead"/> (the amend-published check), <see cref="IGitReader.GetInProgressOperation"/> and <see cref="IGitReader.HasStagedChanges"/>
/// (the failure-path advice and the nothing-staged classification), plus the remaining-staged list through <see cref="IGitReader.GetNameStatus"/>.
/// Reader level: the CLI-only and libgit2-only readers agree with git on repositories of every shape the flow meets, the composite SERVES the plain cases from libgit2 (counter delta,
/// no decline, no recorded failure) and DECLINES the ones libgit2 cannot answer like git (an ambiguous or ref-shadowed abbreviation, a tag prefix, a re-encoded message, an
/// unusual upstream, a detached HEAD) with exactly the CLI answer. Tool level: the Git tool's commit DTO (success and every error text) and the repository state are the same
/// whether the composite or the CLI-only reader backs <see cref="GitImpl"/>. Spawn level: with git's own trace2 log on, a plain commit, an amend with and without an upstream and a
/// commit with trailers each start exactly one top-level git process, <c>git commit</c> itself.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitSeamParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _root = Path.Combine(Path.GetTempPath(), "rs-commitseam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private readonly List<string> _traceFiles = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var traceFile in _traceFiles)
        {
            try
            {
                File.Delete(traceFile);
            }
            catch (IOException)
            {
                // A git process of another test that inherited the variable may still hold it; a small temp file is harmless.
            }
        }

        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static FallbackGitReader Composite() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private static void Write(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A repository with its config pinned repo-locally (no autocrlf, no signing, no gc, hooks only from its own .git/hooks) and, optionally, one commit with README.md and a.txt.</summary>
    private string NewRepo(string name, bool initialCommit = true, bool trailers = false)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        RunGit(directory, "config", "maintenance.auto", "false");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        if (trailers)
        {
            RunGit(directory, "config", GitCommitTrailers.ConfigKey, "true");
        }

        if (initialCommit)
        {
            Write(directory, "README.md", "hello\n");
            Write(directory, "a.txt", "a\n");
            CommitAll(directory, "initial commit");
        }

        return directory;
    }

    private static string Head(string directory) => RunGit(directory, "rev-parse", "HEAD");

    private static string BranchName(string directory) => RunGit(directory, "rev-parse", "--abbrev-ref", "HEAD");

    /// <summary>A linked worktree of <paramref name="main"/> on a new branch (absolute paths even when the machine's git config asks for relative ones, which libgit2 cannot open).</summary>
    private string AddLinkedWorktree(string main, string name, string? branch = null)
    {
        var worktree = Path.Combine(_root, name);
        RunGit(main, "-c", "worktree.useRelativePaths=false", "worktree", "add", "--quiet", worktree, "-b", branch ?? name);
        return worktree;
    }

    /// <summary>A branch with one commit changing a.txt, then the original branch with a conflicting change to a.txt, then the conflicted merge (exit 1, MERGE_HEAD written).</summary>
    private static string StartConflictedMerge(string directory)
    {
        var branch = BranchName(directory);
        RunGit(directory, "checkout", "--quiet", "-b", "other");
        Write(directory, "a.txt", "other\n");
        var other = CommitAll(directory, "other change");
        RunGit(directory, "checkout", "--quiet", branch);
        Write(directory, "a.txt", "main\n");
        CommitAll(directory, "main change");
        var merge = RunGitRaw(directory, "merge", "--no-edit", "other");
        Assert.That(merge.ExitCode, Is.EqualTo(1), "premise: the merge stops on a conflict. " + merge.StdOut + merge.StdErr);
        return other;
    }

    /// <summary>
    /// A repository whose branch tracks <c>origin/&lt;branch&gt;</c> (a bare clone of it as the remote, the standard fetch refspec) with HEAD equal to the upstream tip.
    /// It has two commits so a test can step back to a HEAD that is behind the upstream.
    /// </summary>
    private string NewRepoWithUpstream(string name)
    {
        var repo = NewRepo(name);
        Write(repo, "a.txt", "second\n");
        CommitAll(repo, "second commit");
        var remote = Path.Combine(_root, name + "-remote.git");
        RunGit(_root, "clone", "--quiet", "--bare", repo, remote);
        RunGit(repo, "remote", "add", "origin", remote);
        RunGit(repo, "fetch", "--quiet", "origin");
        var branch = BranchName(repo);
        RunGit(repo, "branch", "--quiet", "--set-upstream-to=origin/" + branch, branch);
        return repo;
    }

    /// <summary>The composite served exactly <paramref name="calls"/> reads from libgit2: no decline, no recorded failure.</summary>
    private static void AssertServedByLibgit2(FallbackGitReader composite, long calls = 1)
    {
        Assert.Multiple(() =>
        {
            Assert.That(composite.Libgit2Count, Is.EqualTo(calls), "libgit2 should have answered (last decline: " + composite.LastDeclineReason + ", first failure: " + composite.FirstFallbackReason + ")");
            Assert.That(composite.DeclineCount, Is.Zero, "last decline: " + composite.LastDeclineReason);
            Assert.That(composite.CliFallbackCount, Is.Zero, "first failure: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Libgit2));
        });
    }

    /// <summary>The composite declined <paramref name="calls"/> reads (planned declines, not failures) and the CLI answered them.</summary>
    private static void AssertDeclinedToTheCli(FallbackGitReader composite, long calls = 1)
    {
        Assert.Multiple(() =>
        {
            Assert.That(composite.DeclineCount, Is.EqualTo(calls), "last decline: " + composite.LastDeclineReason);
            Assert.That(composite.Libgit2Count, Is.Zero);
            Assert.That(composite.CliFallbackCount, Is.Zero, "a decline is not a failure");
            Assert.That(composite.FirstFallbackReason, Is.Null);
            Assert.That(composite.LastBackend, Is.EqualTo(GitReaderBackend.Cli));
        });
    }

    // ---- ResolveCommitAbbreviation ----

    [Test]
    public void ResolveCommitAbbreviation_UniquePrefixesAndTheFullHash_BothReadersAgreeWithGit_AndLibgit2Serves()
    {
        var repo = NewRepo("abbrev-unique");
        var head = Head(repo);
        var cli = new CliGitReader();
        var lib = new Libgit2GitReader();
        var composite = Composite();
        // Lengths of 7 and more: a 4-digit prefix could by chance match another object of the repository.
        var lengths = new[] { 7, 12, 40 };

        Assert.Multiple(() =>
        {
            foreach (var length in lengths)
            {
                var abbreviation = head[..length];
                Assert.That(cli.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(head), "cli " + length);
                Assert.That(lib.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(head), "libgit2 " + length);
                Assert.That(composite.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(head), "composite " + length);
            }
        });
        AssertServedByLibgit2(composite, lengths.Length);
    }

    [Test]
    public void ResolveCommitAbbreviation_NoObjectHasThePrefix_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("abbrev-unknown");
        const string unknown = "0123456789abcdef";
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().ResolveCommitAbbreviation(repo, unknown, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().ResolveCommitAbbreviation(repo, unknown, Timeout), Is.Null);
            Assert.That(composite.ResolveCommitAbbreviation(repo, unknown, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void ResolveCommitAbbreviation_NotAnAbbreviation_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("abbrev-notahash");
        var head = Head(repo);
        var composite = Composite();

        // "HEAD^{commit}" is a legal rev-parse expression: git answers it, so the decline must hand it over rather than report "no such commit".
        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, "HEAD", Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, head[..7].ToUpperInvariant(), Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.ResolveCommitAbbreviation(repo, "HEAD", Timeout), Is.EqualTo(head));
        });
        AssertDeclinedToTheCli(composite);
    }

    /// <summary>Writes loose commits (same tree, same parent, same author, different message) until two share the first four hex digits; the unreachable objects are enough for an ambiguous prefix.</summary>
    private static (string First, string Second) AddCollidingCommits(string repoDirectory)
    {
        using var repo = new LibGit2Sharp.Repository(repoDirectory);
        var tip = repo.Head.Tip;
        var signature = new LibGit2Sharp.Signature("Test", "test@example.com", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 20000; i++)
        {
            var sha = repo.ObjectDatabase.CreateCommit(signature, signature, "collide " + i, tip.Tree, [tip], prettifyMessage: false).Sha;
            if (seen.TryGetValue(sha[..4], out var other))
            {
                return (other, sha);
            }

            seen[sha[..4]] = sha;
        }

        throw new InvalidOperationException("no two of 20000 commits shared a four-digit prefix");
    }

    [Test]
    public void ResolveCommitAbbreviation_AmbiguousPrefix_IsNullFromGit_Libgit2DeclinesInsteadOfPicking_AndLongerPrefixesStillResolve()
    {
        var repo = NewRepo("abbrev-ambiguous");
        var (first, second) = AddCollidingCommits(repo);
        var ambiguous = first[..4];
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().ResolveCommitAbbreviation(repo, ambiguous, Timeout), Is.Null, "git refuses an ambiguous abbreviation");
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, ambiguous, Timeout), Throws.TypeOf<NotSupportedException>().With.Message.Contains("more than one"));
            Assert.That(composite.ResolveCommitAbbreviation(repo, ambiguous, Timeout), Is.Null, "the composite never picks one of the two commits");
        });
        AssertDeclinedToTheCli(composite);

        // The same two commits are told apart as soon as the abbreviation is long enough, by both readers.
        var lib = new Libgit2GitReader();
        var cli = new CliGitReader();
        Assert.Multiple(() =>
        {
            Assert.That(lib.ResolveCommitAbbreviation(repo, first[..12], Timeout), Is.EqualTo(first));
            Assert.That(lib.ResolveCommitAbbreviation(repo, second[..12], Timeout), Is.EqualTo(second));
            Assert.That(cli.ResolveCommitAbbreviation(repo, first[..12], Timeout), Is.EqualTo(first));
            Assert.That(cli.ResolveCommitAbbreviation(repo, second[..12], Timeout), Is.EqualTo(second));
        });
    }

    [Test]
    public void ResolveCommitAbbreviation_ABranchIsNamedLikeTheAbbreviation_GitPrefersTheRef_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("abbrev-shadowed");
        var initial = Head(repo);
        Write(repo, "a.txt", "second\n");
        var second = CommitAll(repo, "second");
        var abbreviation = second[..7];
        // A branch named like the second commit's abbreviation but pointing at the first commit: git reads ref names before hex object names.
        RunGit(repo, "branch", "--quiet", abbreviation, initial);
        var cli = new CliGitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(cli, Is.EqualTo(initial), "premise: git resolves the name to the branch, not to the object");
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(cli));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void ResolveCommitAbbreviation_PrefixOfAnAnnotatedTag_GitPeelsToTheCommit_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("abbrev-tag");
        var head = Head(repo);
        RunGit(repo, "tag", "-a", "v1", "-m", "annotated");
        var tagObject = RunGit(repo, "rev-parse", "v1");
        var abbreviation = tagObject[..12];
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(tagObject, Is.Not.EqualTo(head), "premise: the tag is its own object");
            Assert.That(new CliGitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(head), "git peels the tag to its commit");
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.EqualTo(head));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void ResolveCommitAbbreviation_PrefixOfATree_IsNullFromGit_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("abbrev-tree");
        var tree = RunGit(repo, "rev-parse", "HEAD^{tree}");
        var abbreviation = tree[..12];
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.Null);
            Assert.That(() => new Libgit2GitReader().ResolveCommitAbbreviation(repo, abbreviation, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.ResolveCommitAbbreviation(repo, abbreviation, Timeout), Is.Null);
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void ResolveCommitAbbreviation_InALinkedWorktree_BothReadersAgree_AndLibgit2Serves()
    {
        var main = NewRepo("abbrev-wt-main");
        var worktree = AddLinkedWorktree(main, "abbrev-wt");
        Write(worktree, "a.txt", "in the worktree\n");
        var commit = CommitAll(worktree, "worktree commit");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().ResolveCommitAbbreviation(worktree, commit[..9], Timeout), Is.EqualTo(commit));
            Assert.That(new Libgit2GitReader().ResolveCommitAbbreviation(worktree, commit[..9], Timeout), Is.EqualTo(commit));
            Assert.That(composite.ResolveCommitAbbreviation(worktree, commit[..9], Timeout), Is.EqualTo(commit));
        });
        AssertServedByLibgit2(composite);
    }

    // ---- GetCommitMessage ----

    private static readonly Dictionary<string, string> Messages = new()
    {
        ["subject-and-body"] = "subject\n\nbody line one\nbody line two\n",
        ["trailing-blank-lines"] = "subject\n\n\nthree blank lines follow\n\n\n",
        ["crlf"] = "crlf subject\r\n\r\nbody\r\nmore\r\n",
        ["lone-cr"] = "lone cr\rinside\n",
        ["unicode"] = "ünïcödé – ✓ subject\n\nbody 日本語\n",
        ["no-final-newline"] = "no newline at the end",
        ["trailers"] = "subject\n\nbody\n\nCo-Authored-By: Pat <pat@example.com>\nTool-Call-Id: abc\n",
    };

    private static IEnumerable<string> MessageNames => Messages.Keys;

    /// <summary>A commit whose message is exactly <paramref name="text"/> (verbatim cleanup, written as UTF-8 bytes, so no stripping, no wrapping, no platform line-ending change).</summary>
    private string CommitWithMessage(string repo, string text)
    {
        Write(repo, "a.txt", Guid.NewGuid().ToString("N") + "\n");
        RunGit(repo, "add", "-A");
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".msg");
        File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes(text));
        RunGit(repo, "commit", "--quiet", "--allow-empty", "--cleanup=verbatim", "-F", file);
        return Head(repo);
    }

    [TestCaseSource(nameof(MessageNames))]
    public void GetCommitMessage_RawMessageIsByteIdenticalFromBothReaders_AndLibgit2Serves(string name)
    {
        var repo = NewRepo("message-" + name);
        var text = Messages[name];
        var head = CommitWithMessage(repo, text);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetCommitMessage(repo, head, Timeout), Is.EqualTo(text), "cli");
            Assert.That(new Libgit2GitReader().GetCommitMessage(repo, head, Timeout), Is.EqualTo(text), "libgit2");
            Assert.That(composite.GetCommitMessage(repo, head, Timeout), Is.EqualTo(text), "composite");
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetCommitMessage_UnknownRevision_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("message-unknown");
        var unknown = new string('1', 40);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetCommitMessage(repo, unknown, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetCommitMessage(repo, unknown, Timeout), Is.Null);
            Assert.That(composite.GetCommitMessage(repo, unknown, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetCommitMessage_DeclaredLatin1Encoding_Libgit2Declines_AndTheCliReturnsItAsUtf8()
    {
        var repo = NewRepo("message-latin1");
        Write(repo, "a.txt", "latin1\n");
        RunGit(repo, "add", "-A");
        var file = Path.Combine(_root, "latin1.msg");
        File.WriteAllBytes(file, Encoding.Latin1.GetBytes("café latin1\n"));
        RunGit(repo, "-c", "i18n.commitEncoding=ISO-8859-1", "commit", "--quiet", "--cleanup=verbatim", "-F", file);
        var head = Head(repo);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetCommitMessage(repo, head, Timeout), Is.EqualTo("café latin1\n"), "git re-encodes to UTF-8");
            Assert.That(() => new Libgit2GitReader().GetCommitMessage(repo, head, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetCommitMessage(repo, head, Timeout), Is.EqualTo("café latin1\n"));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetCommitMessage_ShowSignatureConfigured_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("message-showsignature");
        RunGit(repo, "config", "log.showSignature", "true");
        var head = Head(repo);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().GetCommitMessage(repo, head, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetCommitMessage(repo, head, Timeout), Is.EqualTo(new CliGitReader().GetCommitMessage(repo, head, Timeout)));
        });
        AssertDeclinedToTheCli(composite);
    }

    // ---- HasStagedChanges ----

    [Test]
    public void HasStagedChanges_NothingStaged_IsFalseFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("staged-none");
        // An unstaged edit does not count: only the index against HEAD does.
        Write(repo, "a.txt", "unstaged edit\n");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().HasStagedChanges(repo, null, Timeout), Is.False);
            Assert.That(new Libgit2GitReader().HasStagedChanges(repo, null, Timeout), Is.False);
            Assert.That(composite.HasStagedChanges(repo, null, Timeout), Is.False);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void HasStagedChanges_StagedFile_WholeIndexAndLimitedToPaths_BothReadersAgree_AndLibgit2Serves()
    {
        var repo = NewRepo("staged-some");
        Write(repo, "a.txt", "staged edit\n");
        RunGit(repo, "add", "a.txt");
        var composite = Composite();
        var cli = new CliGitReader();
        var lib = new Libgit2GitReader();
        (IReadOnlyList<string>? Paths, bool Expected)[] cases =
        [
            (null, true), (["a.txt"], true), (["README.md"], false), (["README.md", "a.txt"], true), ([], true),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (paths, expected) in cases)
            {
                var label = paths is null ? "no paths" : $"[{string.Join(',', paths)}]";
                Assert.That(cli.HasStagedChanges(repo, paths, Timeout), Is.EqualTo(expected), "cli " + label);
                Assert.That(lib.HasStagedChanges(repo, paths, Timeout), Is.EqualTo(expected), "libgit2 " + label);
                Assert.That(composite.HasStagedChanges(repo, paths, Timeout), Is.EqualTo(expected), "composite " + label);
            }
        });
        AssertServedByLibgit2(composite, cases.Length);
    }

    [Test]
    public void HasStagedChanges_GlobAndMagicPaths_AreLiteralForGit_TheCompositeEqualsTheCli()
    {
        var repo = NewRepo("staged-literal");
        Write(repo, "a.txt", "staged edit\n");
        RunGit(repo, "add", "a.txt");
        var composite = Composite();
        var cli = new CliGitReader();

        // --literal-pathspecs: "*.txt" is a file name, not a glob, so nothing is staged under it; ":(top)a.txt" is not magic either.
        Assert.Multiple(() =>
        {
            foreach (var path in new[] { "*.txt", ":(top)a.txt", "a.t?t" })
            {
                Assert.That(cli.HasStagedChanges(repo, [path], Timeout), Is.False, "cli " + path);
                Assert.That(composite.HasStagedChanges(repo, [path], Timeout), Is.False, "composite " + path);
            }
        });
    }

    [Test]
    public void HasStagedChanges_UnbornBranchWithAStagedFile_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("staged-unborn", initialCommit: false);
        Write(repo, "a.txt", "first\n");
        RunGit(repo, "add", "a.txt");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().HasStagedChanges(repo, null, Timeout), Is.True);
            Assert.That(() => new Libgit2GitReader().HasStagedChanges(repo, null, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.HasStagedChanges(repo, null, Timeout), Is.True);
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void HasStagedChanges_IntentToAddEntry_TheCompositeEqualsTheCli()
    {
        var repo = NewRepo("staged-ita");
        Write(repo, "new.txt", "new\n");
        RunGit(repo, "add", "-N", "new.txt");
        var composite = Composite();

        // git leaves an intent-to-add entry out of the cached diff; libgit2 lists it as added, so the reader declines and git answers.
        Assert.That(composite.HasStagedChanges(repo, null, Timeout), Is.EqualTo(new CliGitReader().HasStagedChanges(repo, null, Timeout)));
        Assert.That(composite.CliFallbackCount, Is.Zero, composite.FirstFallbackReason);
    }

    [Test]
    public void HasStagedChanges_NotARepository_IsNullFromBothReaders()
    {
        var directory = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(directory);

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().HasStagedChanges(directory, null, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().HasStagedChanges(directory, null, Timeout), Is.Null);
        });
    }

    // ---- GetInProgressOperation ----

    [Test]
    public void GetInProgressOperation_NothingInProgress_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("progress-none");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetInProgressOperation(repo, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetInProgressOperation(repo, Timeout), Is.Null);
            Assert.That(composite.GetInProgressOperation(repo, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetInProgressOperation_ConflictedMerge_IsMerge_BothReadersAgree_AndLibgit2Serves()
    {
        var repo = NewRepo("progress-merge");
        StartConflictedMerge(repo);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetInProgressOperation(repo, Timeout), Is.EqualTo("merge"));
            Assert.That(new Libgit2GitReader().GetInProgressOperation(repo, Timeout), Is.EqualTo("merge"));
            Assert.That(composite.GetInProgressOperation(repo, Timeout), Is.EqualTo("merge"));
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetInProgressOperation_StateFilesOfEachKind_BothReadersAgree()
    {
        var composite = Composite();
        var cli = new CliGitReader();
        var lib = new Libgit2GitReader();
        (string Name, Action<string> Arrange)[] cases =
        [
            ("cherry-pick", gitDir => File.WriteAllText(Path.Combine(gitDir, "CHERRY_PICK_HEAD"), new string('a', 40) + "\n")),
            ("revert", gitDir => File.WriteAllText(Path.Combine(gitDir, "REVERT_HEAD"), new string('a', 40) + "\n")),
            ("rebase", gitDir => Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"))),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (name, arrange) in cases)
            {
                var repo = NewRepo("progress-" + name);
                arrange(Path.Combine(repo, ".git"));
                Assert.That(cli.GetInProgressOperation(repo, Timeout), Is.EqualTo(name), "cli " + name);
                Assert.That(lib.GetInProgressOperation(repo, Timeout), Is.EqualTo(name), "libgit2 " + name);
                Assert.That(composite.GetInProgressOperation(repo, Timeout), Is.EqualTo(name), "composite " + name);
            }
        });
        AssertServedByLibgit2(composite, 3);
    }

    [Test]
    public void GetInProgressOperation_LinkedWorktreeMergeIsPerWorktree_BothReadersAgree_AndLibgit2Serves()
    {
        var main = NewRepo("progress-wt-main");
        var worktree = AddLinkedWorktree(main, "progress-wt");
        StartConflictedMerge(worktree);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetInProgressOperation(worktree, Timeout), Is.EqualTo("merge"));
            Assert.That(composite.GetInProgressOperation(worktree, Timeout), Is.EqualTo("merge"));
            Assert.That(new CliGitReader().GetInProgressOperation(main, Timeout), Is.Null, "the main working tree is not mid-merge");
            Assert.That(composite.GetInProgressOperation(main, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite, 2);
    }

    [Test]
    public void GetInProgressOperation_NotARepository_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var directory = Path.Combine(_root, "progress-not-a-repo");
        Directory.CreateDirectory(directory);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetInProgressOperation(directory, Timeout), Is.Null);
            Assert.That(composite.GetInProgressOperation(directory, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    // ---- GetUpstreamContainingHead ----

    [Test]
    public void GetUpstreamContainingHead_NoUpstreamConfigured_IsNullFromBothReaders_AndLibgit2Serves()
    {
        var repo = NewRepo("upstream-none");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(repo, Timeout), Is.Null);
            Assert.That(new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Is.Null);
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.Null);
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_UnbornBranch_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("upstream-unborn", initialCommit: false);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(new CliGitReader().GetUpstreamContainingHead(repo, Timeout)));
        });
        AssertDeclinedToTheCli(composite);
    }

    /// <summary>Each way HEAD can relate to its upstream: the same, ahead, behind, diverged.</summary>
    [TestCase("same", true)]
    [TestCase("ahead", false)]
    [TestCase("behind", true)]
    [TestCase("diverged", false)]
    public void GetUpstreamContainingHead_HeadAgainstTheUpstream_BothReadersAgreeWithGit_AndLibgit2Serves(string relation, bool contained)
    {
        var repo = NewRepoWithUpstream("upstream-" + relation);
        var expectedName = "origin/" + BranchName(repo);
        switch (relation)
        {
            case "ahead":
                Write(repo, "a.txt", "local only\n");
                CommitAll(repo, "local only");
                break;
            case "behind":
                RunGit(repo, "reset", "--quiet", "--hard", "HEAD~1");
                break;
            case "diverged":
                RunGit(repo, "reset", "--quiet", "--hard", "HEAD~1");
                Write(repo, "a.txt", "diverged\n");
                CommitAll(repo, "diverged local commit");
                break;
        }

        var expected = contained ? expectedName : null;
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(expected), "cli");
            Assert.That(new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(expected), "libgit2");
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(expected), "composite");
        });
        AssertServedByLibgit2(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_DetachedHead_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepoWithUpstream("upstream-detached");
        RunGit(repo, "checkout", "--quiet", "--detach");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(repo, Timeout), Is.Null, "git has no branch to read an upstream from");
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.Null);
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_TrackingRefDeleted_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepoWithUpstream("upstream-gone");
        var branch = BranchName(repo);
        RunGit(repo, "update-ref", "-d", "refs/remotes/origin/" + branch);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(repo, Timeout), Is.Null);
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.Null);
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_UpstreamIsALocalBranch_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepo("upstream-local");
        var branch = BranchName(repo);
        RunGit(repo, "branch", "--quiet", "base");
        RunGit(repo, "branch", "--quiet", "--set-upstream-to=base", branch);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(repo, Timeout), Is.EqualTo("base"));
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo("base"));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_MoreThanOneFetchRefspec_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepoWithUpstream("upstream-refspecs");
        RunGit(repo, "config", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(new CliGitReader().GetUpstreamContainingHead(repo, Timeout)));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_HalfConfiguredUpstream_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepoWithUpstream("upstream-half");
        var branch = BranchName(repo);
        RunGit(repo, "config", "--unset", $"branch.{branch}.remote");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(new CliGitReader().GetUpstreamContainingHead(repo, Timeout)));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_ABranchSharesTheShortName_Libgit2Declines_AndTheCompositeEqualsTheCli()
    {
        var repo = NewRepoWithUpstream("upstream-ambiguous");
        var branch = BranchName(repo);
        // A local branch named "origin/<branch>" makes git print the longer "remotes/origin/<branch>".
        RunGit(repo, "branch", "--quiet", "origin/" + branch);
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(() => new Libgit2GitReader().GetUpstreamContainingHead(repo, Timeout), Throws.TypeOf<NotSupportedException>());
            Assert.That(composite.GetUpstreamContainingHead(repo, Timeout), Is.EqualTo(new CliGitReader().GetUpstreamContainingHead(repo, Timeout)));
        });
        AssertDeclinedToTheCli(composite);
    }

    [Test]
    public void GetUpstreamContainingHead_LinkedWorktreeWithAndWithoutUpstream_BothReadersAgree_AndLibgit2Serves()
    {
        var main = NewRepoWithUpstream("upstream-wt-main");
        var branch = BranchName(main);
        var tracking = AddLinkedWorktree(main, "upstream-wt-tracking");
        RunGit(tracking, "branch", "--quiet", "--set-upstream-to=origin/" + branch, "upstream-wt-tracking");
        var plain = AddLinkedWorktree(main, "upstream-wt-plain");
        var composite = Composite();

        Assert.Multiple(() =>
        {
            Assert.That(new CliGitReader().GetUpstreamContainingHead(tracking, Timeout), Is.EqualTo("origin/" + branch), "cli, tracking worktree");
            Assert.That(composite.GetUpstreamContainingHead(tracking, Timeout), Is.EqualTo("origin/" + branch), "composite, tracking worktree");
            Assert.That(new CliGitReader().GetUpstreamContainingHead(plain, Timeout), Is.Null, "cli, worktree without upstream");
            Assert.That(composite.GetUpstreamContainingHead(plain, Timeout), Is.Null, "composite, worktree without upstream");
        });
        AssertServedByLibgit2(composite, 2);
    }

    // ---- tool level ----

    /// <summary>Plays one scenario against a fresh repository with <paramref name="reader"/> behind a <see cref="GitImpl"/>; returns the normalized DTO and the facts about the repository afterwards.</summary>
    private async Task<(string Json, string Facts)> RunToolScenarioAsync(string scenario, IGitReader reader)
    {
        var impl = new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);
        var name = scenario + (reader is FallbackGitReader ? "-composite" : "-cli");
        string directory;
        GitCommitResult result;
        switch (scenario)
        {
            case "amend-published":
                directory = NewRepoWithUpstream(name);
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-behind-upstream":
                directory = NewRepoWithUpstream(name);
                RunGit(directory, "reset", "--quiet", "--hard", "HEAD~1");
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-ahead-of-upstream":
                directory = NewRepoWithUpstream(name);
                Write(directory, "a.txt", "local only\n");
                CommitAll(directory, "local only");
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-diverged-from-upstream":
                directory = NewRepoWithUpstream(name);
                RunGit(directory, "reset", "--quiet", "--hard", "HEAD~1");
                Write(directory, "a.txt", "diverged\n");
                CommitAll(directory, "diverged local commit");
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, null, GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-published-in-linked-worktree":
                // Short names on purpose: libgit2 fails (a real failure, handled by the composite's CLI fallback) on a git dir path past Windows' MAX_PATH, which the long scenario name would reach.
                // The branch name is the same for both readers so the facts compare.
                var suffix = reader is FallbackGitReader ? "c" : "x";
                var main = NewRepoWithUpstream("wtmain-" + suffix);
                directory = AddLinkedWorktree(main, "wtp-" + suffix, "wt-branch");
                RunGit(directory, "branch", "--quiet", "--set-upstream-to=origin/" + BranchName(main), "wt-branch");
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-detached":
                directory = NewRepoWithUpstream(name);
                RunGit(directory, "checkout", "--quiet", "--detach");
                Write(directory, "a.txt", "amended\n");
                result = await impl.CommitAsync(directory, "amended subject", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "commit-hook-fails-after-staging":
                directory = NewRepo(name);
                File.WriteAllText(Path.Combine(directory, ".git", "hooks", "pre-commit"), "#!/bin/sh\necho the hook says no >&2\nexit 1\n");
                Write(directory, "new.txt", "new\n");
                result = await impl.CommitAsync(directory, "add a file", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
                break;
            case "nothing-staged":
                directory = NewRepo(name);
                result = await impl.CommitAsync(directory, "nothing to commit", null, null, amend: false, CancellationToken.None);
                break;
            case "unresolved-merge":
                directory = NewRepo(name);
                StartConflictedMerge(directory);
                result = await impl.CommitAsync(directory, "commit with conflicts left", null, null, amend: false, CancellationToken.None);
                break;
            case "leaves-other-files-staged":
                directory = NewRepo(name);
                Write(directory, "a.txt", "committed\n");
                Write(directory, "README.md", "also staged, not committed\n");
                RunGit(directory, "add", "a.txt", "README.md");
                result = await impl.CommitAsync(directory, "only a.txt", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            default:
                throw new ArgumentException("no scenario " + scenario);
        }

        // ErrorKind and ErrorDetail are not part of the serialized DTO, so they are compared as facts.
        return (GitCommitReadsParityTests.CommitJson(result), Facts(directory, result) + $"|kind={result.ErrorKind}|detail={result.ErrorDetail}");
    }

    private static string Facts(string directory, GitCommitResult result)
    {
        var branch = BranchName(directory);
        var staged = RunGit(directory, "-c", "core.quotePath=false", "diff", "--cached", "--name-only").ReplaceLineEndings(",");
        var log = RunGit(directory, "log", "--format=%s", "-n", "5");
        var message = result.CommitHash.Length == 0 ? "no hash" : GitCommitReadsParityTests.MaskHashes(RunGit(directory, "log", "-1", "--format=%B", result.CommitHash));
        return $"branch={branch}|staged={staged}|log={log.ReplaceLineEndings(",")}|message={message}";
    }

    /// <summary>Scenario, whether libgit2 declines something in it (the amend on a detached HEAD, the commit with unmerged paths).</summary>
    private static readonly (string Scenario, bool Declines)[] ToolScenarios =
    [
        ("amend-published", false), ("amend-behind-upstream", false), ("amend-ahead-of-upstream", false), ("amend-diverged-from-upstream", false),
        ("amend-published-in-linked-worktree", false), ("amend-detached", true), ("commit-hook-fails-after-staging", false), ("nothing-staged", false),
        ("unresolved-merge", true), ("leaves-other-files-staged", false),
    ];

    private static IEnumerable<TestCaseData> ToolScenarioCases() =>
        ToolScenarios.Select(entry => new TestCaseData(entry.Scenario, entry.Declines).SetName("GitTool_Commit_" + entry.Scenario + "_CompositeEqualsCliOnly"));

    [TestCaseSource(nameof(ToolScenarioCases))]
    public async Task GitTool_Commit_CompositeReaderEqualsTheCliOnlyReader_ForTheNewSeamReadsAsync(string scenario, bool declines)
    {
        var viaCli = await RunToolScenarioAsync(scenario, new CliGitReader());
        var composite = Composite();
        var viaComposite = await RunToolScenarioAsync(scenario, composite);

        Assert.Multiple(() =>
        {
            Assert.That(viaComposite.Json, Is.EqualTo(viaCli.Json), "tool DTO differs between the composite and the CLI-only reader");
            Assert.That(viaComposite.Facts, Is.EqualTo(viaCli.Facts), "the repository state differs");
            Assert.That(composite.Libgit2Count, Is.GreaterThan(0), "libgit2 served part of the flow");
            Assert.That(composite.CliFallbackCount, Is.Zero, "no real libgit2 failure: " + composite.FirstFallbackReason);
            Assert.That(composite.FirstFallbackReason, Is.Null);
            if (declines)
            {
                Assert.That(composite.DeclineCount, Is.GreaterThan(0), "premise: this scenario hits a read libgit2 declines");
            }
            else
            {
                Assert.That(composite.DeclineCount, Is.Zero, "nothing in this ordinary repository is declined: " + composite.LastDeclineReason);
            }
        });
    }

    [Test]
    public async Task GitTool_AmendOfAPublishedCommit_IsRefusedWithTheUpstreamNameAndChangesNothingAsync()
    {
        var result = await RunToolScenarioAsync("amend-published", Composite());

        Assert.Multiple(() =>
        {
            // System.Text.Json escapes the apostrophe as \u0027.
            Assert.That(result.Json, Does.Contain("HEAD is already contained in its upstream \\u0027origin/").And.Contain("Nothing was amended."));
            Assert.That(result.Facts, Does.Contain("log=second commit,initial commit"), "HEAD was not rewritten");
        });
    }

    [Test]
    public async Task GitTool_CommitAfterAFailedCommit_NamesTheStagedCountAndTheNothingToCommitKindAsync()
    {
        var failed = await RunToolScenarioAsync("commit-hook-fails-after-staging", Composite());
        var nothing = await RunToolScenarioAsync("nothing-staged", Composite());
        var merge = await RunToolScenarioAsync("unresolved-merge", Composite());

        Assert.Multiple(() =>
        {
            Assert.That(failed.Json, Does.Contain("the index now holds 1 staged paths"));
            Assert.That(nothing.Facts, Does.Contain("kind=" + GitErrorCodes.NothingToCommit));
            Assert.That(merge.Json, Does.Contain("mid-merge"));
            Assert.That(merge.Facts, Does.Contain("kind=" + GitErrorCodes.OperationInProgress));
        });
    }

    [Test]
    public async Task GitTool_CommitWithOtherStagedFiles_ReportsThemAsRemainingStagedAsync()
    {
        var result = await RunToolScenarioAsync("leaves-other-files-staged", Composite());

        Assert.That(result.Json, Does.Contain("\"RemainingStaged\":[\"README.md\"]"));
    }

    // ---- spawn level ----

    /// <summary>
    /// Runs <paramref name="run"/> with git's own trace2 event log on (<c>GIT_TRACE2_EVENT</c>, inherited by every git process the call starts) and returns the command name of every
    /// TOP-LEVEL git process that ran in <paramref name="repoDirectory"/>, in start order. A process a git command starts itself (a hook's git, a maintenance run) has a nested session
    /// id ("parent/child") and is not counted: the question is which git processes the server starts. Processes of other tests that run in parallel log to the same file; they
    /// are told apart by the repository's working tree path (the directory name is a unique GUID).
    /// </summary>
    private static readonly SemaphoreSlim TraceGate = new(1, 1);

    private async Task<List<string>> SpawnedGitCommandsAsync(string repoDirectory, Func<Task> run)
    {
        var traceFile = Path.Combine(Path.GetTempPath(), "rs-commitseam-" + Guid.NewGuid().ToString("N") + ".trace2");
        _traceFiles.Add(traceFile);
        // The variable is process-wide, so two of these windows must not overlap (the second would overwrite and then clear it under the first).
        await TraceGate.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", traceFile);
            await run();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", null);
            TraceGate.Release();
        }

        // git writes the file when its first process starts: no file means no git process ran at all.
        if (!File.Exists(traceFile))
        {
            return [];
        }

        var directoryName = Path.GetFileName(repoDirectory);
        var order = new List<string>();
        var worktrees = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        // Shared access: a git process of a test running in parallel (it inherited the variable while it was set) may still hold the file open.
        using var stream = new FileStream(traceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("sid", out var sidElement) || !root.TryGetProperty("event", out var eventElement))
                {
                    continue;
                }

                var sid = sidElement.GetString()!;
                switch (eventElement.GetString())
                {
                    case "start":
                        order.Add(sid);
                        break;
                    case "def_repo" when root.TryGetProperty("worktree", out var worktree):
                        worktrees[sid] = worktree.GetString()!.Replace('\\', '/');
                        break;
                    case "cmd_name":
                        names[sid] = root.GetProperty("name").GetString()!;
                        break;
                }
            }
        }

        return [.. order
            .Where(sid => !sid.Contains('/') && worktrees.TryGetValue(sid, out var worktree) && worktree.EndsWith("/" + directoryName, StringComparison.OrdinalIgnoreCase))
            .Select(sid => names.GetValueOrDefault(sid, "?"))];
    }

    private async Task<List<string>> RunSpawnScenarioAsync(string scenario)
    {
        var impl = new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, GitReaders.Default);
        var name = "spawn-" + scenario;
        string directory;
        Func<Task<GitCommitResult>> commit;
        switch (scenario)
        {
            case "plain":
                directory = NewRepo(name);
                Write(directory, "a.txt", "changed\n");
                commit = () => impl.CommitAsync(directory, "plain subject\n\nplain body", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "plain-trailers":
                directory = NewRepo(name, trailers: true);
                Write(directory, "a.txt", "changed\n");
                commit = () => impl.CommitAsync(directory, "plain subject", GitStageScope.listed, "a.txt", amend: false, CancellationToken.None);
                break;
            case "untracked":
                directory = NewRepo(name);
                Write(directory, "new.txt", "new\n");
                commit = () => impl.CommitAsync(directory, "add a file", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
                break;
            case "scope-all":
                directory = NewRepo(name);
                Write(directory, "a.txt", "changed\n");
                Write(directory, "new.txt", "new\n");
                commit = () => impl.CommitAsync(directory, "everything", GitStageScope.all, null, amend: false, CancellationToken.None);
                break;
            case "amend":
                directory = NewRepo(name);
                Write(directory, "a.txt", "changed\n");
                commit = () => impl.CommitAsync(directory, "amended", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-trailers":
                directory = NewRepo(name, trailers: true);
                Write(directory, "a.txt", "changed\n");
                commit = () => impl.CommitAsync(directory, null, GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-ahead-of-upstream":
                directory = NewRepoWithUpstream(name);
                Write(directory, "a.txt", "local only\n");
                CommitAll(directory, "local only");
                Write(directory, "a.txt", "amended\n");
                commit = () => impl.CommitAsync(directory, "amended", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "amend-published-is-refused":
                directory = NewRepoWithUpstream(name);
                Write(directory, "a.txt", "amended\n");
                commit = () => impl.CommitAsync(directory, "amended", GitStageScope.all, null, amend: true, CancellationToken.None);
                break;
            case "commit-fails":
                directory = NewRepo(name);
                File.WriteAllText(Path.Combine(directory, ".git", "hooks", "pre-commit"), "#!/bin/sh\nexit 1\n");
                Write(directory, "new.txt", "new\n");
                commit = () => impl.CommitAsync(directory, "add a file", GitStageScope.listed, "new.txt", amend: false, CancellationToken.None);
                break;
            default:
                throw new ArgumentException("no scenario " + scenario);
        }

        GitCommitResult? result = null;
        var spawned = await SpawnedGitCommandsAsync(directory, async () => result = await commit());
        var expectError = scenario is "amend-published-is-refused" or "commit-fails";
        Assert.That(result!.IsError, Is.EqualTo(expectError), result.Error);
        return spawned;
    }

    [TestCase("plain")]
    [TestCase("plain-trailers")]
    [TestCase("untracked")]
    [TestCase("scope-all")]
    [TestCase("amend")]
    [TestCase("amend-trailers")]
    [TestCase("amend-ahead-of-upstream")]
    [TestCase("commit-fails")]
    public async Task Commit_StartsOnlyGitCommitItself_NoOtherGitProcessAsync(string scenario)
    {
        var spawned = await RunSpawnScenarioAsync(scenario);

        Assert.That(spawned, Is.EqualTo(new[] { "commit" }), "every read and check around git commit goes through the in-process seam");
    }

    [Test]
    public async Task Commit_AmendOfAPublishedCommit_IsRefusedWithoutStartingAnyGitProcessAsync()
    {
        var spawned = await RunSpawnScenarioAsync("amend-published-is-refused");

        Assert.That(spawned, Is.Empty, "the published-history check is answered in process and nothing is committed");
    }
}

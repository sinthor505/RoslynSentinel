// AdvancedStructuralEngine.PreviewInstanceMoveCallSitesAsync -> Decision 3 of
// docs/current/plans/plan_scoped_operation_ledger.md (proposal_movemember_instance_callsite_resolution.md
// sections 1, 5, 6). Reporting-only dry-run: does not write anything, classifies each call site of the
// member(s) being moved by compiling a trial change set (member removed, call site untouched) and asking
// the compiler's own diagnostics whether that site breaks, then explaining broken sites via an in-scope
// candidate scan (SemanticModel.LookupSymbols).

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class PreviewInstanceMoveCallSitesTests
{
    private TestSolutionFixture _fixture;
    private PersistentWorkspaceManager _workspaceManager;
    private AdvancedStructuralEngine _engine;

    [SetUp]
    public async Task SetUpAsync()
    {
        _fixture = new TestSolutionFixture();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);

        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine);
        _engine = new AdvancedStructuralEngine(_workspaceManager, validationEngine);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    private const string ClassAWithFooSource = """
        namespace ContosoOrders.Core;

        public class PreviewMoveClassA
        {
            public void Foo()
            {
            }

            public void Bar()
            {
            }
        }
        """;

    private const string ClassBSource = """
        namespace ContosoOrders.Core;

        public class PreviewMoveClassB
        {
        }
        """;

    [Test]
    public async Task UnambiguousSingleCandidate_ClassifiesAsValidWithSuggestedFixAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassA.cs"), ClassAWithFooSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassB.cs"), ClassBSource, reloadSolution: false);

        const string callerSource = """
            namespace ContosoOrders.Core;

            public class PreviewMoveCallerUnambiguous
            {
                private readonly PreviewMoveClassB _classB = new PreviewMoveClassB();

                public void Do()
                {
                    var a = new PreviewMoveClassA();
                    a.Foo();
                }
            }
            """;
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveCallerUnambiguous.cs"), callerSource);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "PreviewMoveClassA.cs"));

        var results = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Valid));
            Assert.That(fooSite.SuggestedFix, Is.EqualTo("_classB.Foo"));
            Assert.That(fooSite.Candidates, Does.Contain("_classB"));
        });
    }

    [Test]
    public async Task MultipleCandidates_ClassifiesAsAmbiguousAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassA.cs"), ClassAWithFooSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassB.cs"), ClassBSource, reloadSolution: false);

        const string callerSource = """
            namespace ContosoOrders.Core;

            public class PreviewMoveCallerAmbiguous
            {
                private readonly PreviewMoveClassB _classB1 = new PreviewMoveClassB();
                private readonly PreviewMoveClassB _classB2 = new PreviewMoveClassB();

                public void Do()
                {
                    var a = new PreviewMoveClassA();
                    a.Foo();
                }
            }
            """;
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveCallerAmbiguous.cs"), callerSource);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "PreviewMoveClassA.cs"));

        var results = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Ambiguous));
            Assert.That(fooSite.Candidates, Is.EquivalentTo(new[] { "_classB1", "_classB2" }));
            Assert.That(fooSite.BlockReason, Does.Contain("callSiteFixups"));
        });
    }

    [Test]
    public async Task NoInScopeCandidate_ClassifiesAsNoCandidateIntroducibleAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassA.cs"), ClassAWithFooSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassB.cs"), ClassBSource, reloadSolution: false);

        const string callerSource = """
            namespace ContosoOrders.Core;

            public class PreviewMoveCallerNoCandidate
            {
                public void Do()
                {
                    var a = new PreviewMoveClassA();
                    a.Foo();
                }
            }
            """;
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveCallerNoCandidate.cs"), callerSource);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "PreviewMoveClassA.cs"));

        var results = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
            Assert.That(fooSite.Candidates, Is.Empty);
            Assert.That(fooSite.BlockReason, Does.Contain("PreviewMoveClassB"));
        });
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task MoveMemberAsync_UnambiguousInstanceMember_AppliesAutomaticallyAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassA.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassA
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassB.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassB
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceCallerUnambiguous.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceCallerUnambiguous
            {
                private readonly MoveInstanceClassB _classB = new MoveInstanceClassB();

                public void Do()
                {
                    var a = new MoveInstanceClassA();
                    a.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveInstanceClassA.cs"));

        var result = await _engine.MoveMemberAsync(filePath, "MoveInstanceClassA", ["Foo"], "MoveInstanceClassB");

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            var callerChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MoveInstanceCallerUnambiguous"));
            Assert.That(callerChange.Value, Does.Contain("_classB.Foo"));
            var targetChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MoveInstanceClassB"));
            Assert.That(targetChange.Value, Does.Contain("public void Foo()"));
        });
    }


    // Added by AddMember (expected - used for diagnostics)
    [Test]
    public async Task MoveMemberAsync_AmbiguousInstanceMemberNoFixup_RejectsWithSpecificErrorAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassC.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassC
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassD.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassD
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceCallerAmbiguous2.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceCallerAmbiguous2
            {
                private readonly MoveInstanceClassD _d1 = new MoveInstanceClassD();
                private readonly MoveInstanceClassD _d2 = new MoveInstanceClassD();

                public void Do()
                {
                    var c = new MoveInstanceClassC();
                    c.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveInstanceClassC.cs"));

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _engine.MoveMemberAsync(filePath, "MoveInstanceClassC", ["Foo"], "MoveInstanceClassD"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("MoveInstanceCallerAmbiguous2"));
            Assert.That(ex.Message, Does.Contain("callSiteFixups"));
        });
    }

    // NOTE: no test here for MoveOrderDependent (a call site inside a member that's itself being
    // moved in the same batch). Tried the obvious fixture -- Foo() called unqualified from Baz(),
    // both in memberNames -- and it classifies as Valid, not MoveOrderDependent: when both members
    // move together, the call site's line is removed from the trial compile along with its container,
    // so no diagnostic ever fires there and brokenHere is false. isMoveOrderDependent is checked only
    // after brokenHere is true (see PreviewInstanceMoveCallSitesAsync), so this path may be
    // unreachable for a same-batch caller+callee pair as currently written -- flagged in the Decision 3
    // plan update rather than forcing a fixture that doesn't match the real semantics.
}

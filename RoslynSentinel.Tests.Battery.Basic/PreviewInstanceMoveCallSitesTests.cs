// AdvancedStructuralEngine.PreviewInstanceMoveCallSitesAsync -> Decision 3 of
// docs/current/plans/plan_scoped_operation_ledger.md (proposal_movemember_instance_callsite_resolution.md
// sections 1, 5, 6). Reporting-only dry-run: does not write anything, classifies each call site of the
// member(s) being moved by compiling a trial change set (member removed, call site untouched) and asking
// the compiler's own diagnostics whether that site breaks, then explaining broken sites via an in-scope
// candidate scan (SemanticModel.LookupSymbols).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PreviewInstanceMoveCallSitesTests
{
    private TestSolutionFixture _fixture;
    private PersistentWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _engine;

    [SetUp]
    public async Task SetUpAsync()
    {
        _fixture = new TestSolutionFixture();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);

        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(_workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _engine = new MemberRefactoringEngine(_workspaceManager, symbolNav, validationEngine);
    }

    [TearDown]
    public void TearDown()
    {
        // Keep SetUp/TearDown for disk-tier test: CrossProjectSiblingField_ClassifiesAsValidNotNoCandidateIntroducibleAsync
        // requires TestSolutionBuilder.CreateTwoProjectSolution for cross-project compilation checks, which cannot be
        // simulated in InMemoryWorkspace.
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine) CreateInMemoryTestFixture(
        params (string relativePath, string content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = new MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine);
        return (workspace, engine);
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
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", """
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
                """),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassB
                {
                }
                """),
            ("ContosoOrders.Core/PreviewMoveCallerUnambiguous.cs", """
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
                """));

        var results = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs"),
            "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Valid));
            // SuggestedFix is receiver-only: MoveInstanceMembersAsync's rewrite calls
            // original.WithExpression(...) on the existing member-access node, which keeps its
            // own .Name segment - a value that already included ".Foo" here produced a doubled
            // method name on apply (e.g. "_classB.Foo.Foo").
            Assert.That(fooSite.SuggestedFix, Is.EqualTo("_classB"));
            Assert.That(fooSite.Candidates, Does.Contain("_classB"));
        });
    }

    [Test]
    public async Task MultipleCandidates_ClassifiesAsAmbiguousAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", """
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
                """),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassB
                {
                }
                """),
            ("ContosoOrders.Core/PreviewMoveCallerAmbiguous.cs", """
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
                """));

        var results = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs"),
            "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Ambiguous));
            Assert.That(fooSite.Candidates, Is.EquivalentTo(new[] { "_classB1", "_classB2" }));
            Assert.That(fooSite.BlockReason, Does.Contain("callSiteFixups"));
        });
    }


    [Test]
    public async Task ExistingNonEmptyDestinationClass_ClassifiesAsValidWithSuggestedFixAsync()
    {
        // Regression test for docs/current/blockers/blocking_error_movemember_instance_callsite_not_rewritten.md:
        // PreviewInstanceMoveCallSitesAsync built its trial changeset from the source-file edit only
        // (member removed), never including the destination-side edit (member added). Against an
        // EXISTING destination class with real pre-existing members (not a fresh near-empty class),
        // that incomplete changeset let every genuinely-unambiguous call site get misclassified as
        // already-Valid with no SuggestedFix, so autoResolveCallSites silently skipped rewriting it.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/PreviewMoveClassExistingA.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassExistingA
                {
                    public void Foo()
                    {
                    }

                    public void Bar()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/PreviewMoveClassExistingB.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassExistingB
                {
                    public void Baz()
                    {
                    }

                    public void Qux()
                    {
                    }

                    private int _counter;
                }
                """),
            ("ContosoOrders.Core/PreviewMoveCallerExistingDestination.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveCallerExistingDestination
                {
                    private readonly PreviewMoveClassExistingB _classB = new PreviewMoveClassExistingB();

                    public void Do()
                    {
                        var a = new PreviewMoveClassExistingA();
                        a.Foo();
                    }
                }
                """));

        var results = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/PreviewMoveClassExistingA.cs"),
            "PreviewMoveClassExistingA", ["Foo"], "PreviewMoveClassExistingB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Valid));
            // SuggestedFix is receiver-only: MoveInstanceMembersAsync's rewrite calls
            // original.WithExpression(...) on the existing member-access node, which keeps its
            // own .Name segment - a value that already included ".Foo" here produced a doubled
            // method name on apply (e.g. "_classB.Foo.Foo").
            Assert.That(fooSite.SuggestedFix, Is.EqualTo("_classB"));
            Assert.That(fooSite.Candidates, Does.Contain("_classB"));
        });
    }


    [Test]
    public async Task NoInScopeCandidate_ClassifiesAsNoCandidateIntroducibleAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", """
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
                """),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassB
                {
                }
                """),
            ("ContosoOrders.Core/PreviewMoveCallerNoCandidate.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveCallerNoCandidate
                {
                    public void Do()
                    {
                        var a = new PreviewMoveClassA();
                        a.Foo();
                    }
                }
                """));

        var results = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs"),
            "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
            Assert.That(fooSite.Candidates, Is.Empty);
            Assert.That(fooSite.BlockReason, Does.Contain("PreviewMoveClassB"));
            // Prep-step prose, not a receiver expression: names the field to add, where, and which
            // existing receiver's construction to mirror (so configuration isn't silently dropped).
            Assert.That(fooSite.SuggestedFix, Does.Contain("Add a field of type PreviewMoveClassB to PreviewMoveCallerNoCandidate"));
            Assert.That(fooSite.SuggestedFix, Does.Contain("'a'"));
        });
    }

    [Test]
    public async Task OuterClassFieldOfDestinationType_NotTreatedAsUnqualifiedCandidateForNestedCallSiteAsync()
    {
        // Regression test for docs/current/blockers/blocking_error_movemember_batch_false_cs0120_cross_nested_class.md:
        // an outer class field of the destination type must never be offered as a candidate receiver
        // for a call site inside one of its nested classes - C# nested classes hold no implicit
        // outer-instance reference, so an unqualified reference to it is always CS0120, never a valid
        // rewrite. The nested class's OWN field of the source type is the only legitimate receiver,
        // and that call site is not broken by the move at all (it never used the destination type),
        // so it must be left alone / reported honestly, not silently pointed at the unreachable field.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/PreviewMoveClassA.cs", """
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
                """),
            ("ContosoOrders.Core/PreviewMoveClassB.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveClassB
                {
                }
                """),
            ("ContosoOrders.Core/PreviewMoveOuterFixture.cs", """
                namespace ContosoOrders.Core;

                public class PreviewMoveOuterFixture
                {
                    private PreviewMoveClassB _outerFieldOfDestinationType;

                    public class PreviewMoveNestedFixture
                    {
                        private PreviewMoveClassA _innerFieldOfSourceType;

                        public void Do()
                        {
                            _innerFieldOfSourceType = new PreviewMoveClassA();
                            _innerFieldOfSourceType.Foo();
                        }
                    }
                }
                """));

        var results = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/PreviewMoveClassA.cs"),
            "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            // Must NOT be Valid with the outer field as SuggestedFix - that would be the defect
            // (a receiver unreachable from the nested call site, producing CS0120 on apply).
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
            Assert.That(fooSite.Candidates, Is.Empty);
            Assert.That(fooSite.SuggestedFix, Does.Not.Contain("_outerFieldOfDestinationType"));
        });
    }

    [Test]
    public async Task MoveMemberAsync_UnambiguousInstanceMember_AppliesAutomaticallyAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveInstanceClassA.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassA
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveInstanceClassB.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassB
                {
                }
                """),
            ("ContosoOrders.Core/MoveInstanceCallerUnambiguous.cs", """
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
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MoveInstanceClassA.cs"),
            "MoveInstanceClassA", ["Foo"], "MoveInstanceClassB");

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            var callerChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MoveInstanceCallerUnambiguous"));
            Assert.That(callerChange.Value, Does.Contain("_classB.Foo"));
            var targetChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MoveInstanceClassB"));
            Assert.That(targetChange.Value, Does.Contain("public void Foo()"));
        });
    }

    [Test]
    public async Task MoveMemberAsync_FieldDependencyAlreadySatisfiedOnTarget_MovesWithoutDuplicatingFieldAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/SatisfiedFieldSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class SatisfiedFieldSourceClass
                {
                    private readonly string _shared = "source";

                    public string ReadShared()
                    {
                        return _shared;
                    }
                }
                """),
            ("ContosoOrders.Core/SatisfiedFieldTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class SatisfiedFieldTargetClass
                {
                    private readonly string _shared = "target";
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/SatisfiedFieldSourceClass.cs"),
            "SatisfiedFieldSourceClass", ["ReadShared"], "SatisfiedFieldTargetClass");

        Assert.Multiple(() =>
        {
            var targetChange = result.Changes.Single(kv => kv.Key.ToString().Contains("SatisfiedFieldTargetClass"));
            Assert.That(targetChange.Value, Does.Contain("public string ReadShared()"));

            // The field must NOT be duplicated onto the target - exactly one declaration of "_shared" should remain.
            var declarationCount = System.Text.RegularExpressions.Regex.Matches(targetChange.Value, @"_shared\s*=").Count;
            Assert.That(declarationCount, Is.EqualTo(1), "the target's pre-existing '_shared' field must not be duplicated by the move");
        });
    }

    [Test]
    public async Task MoveMemberAsync_FieldDependencyIncompatibleTypeOnTarget_FailsWithCollisionMessageAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/IncompatibleFieldSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class IncompatibleFieldSourceClass
                {
                    private readonly string _shared = "source";

                    public string ReadShared()
                    {
                        return _shared;
                    }
                }
                """),
            ("ContosoOrders.Core/IncompatibleFieldTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class IncompatibleFieldTargetClass
                {
                    private readonly int _shared = 42;
                }
                """));

        var ex = Assert.ThrowsAsync<ToolInvalidArgumentException>(async () =>
            await engine.MoveMemberAsync(
                workspace.PathOf("ContosoOrders.Core/IncompatibleFieldSourceClass.cs"),
                "IncompatibleFieldSourceClass", ["ReadShared"], "IncompatibleFieldTargetClass"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("incompatible type"));
            Assert.That(ex.Message, Does.Contain("_shared"));
        });
    }

    [Test]
    public async Task MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveInstanceClassC.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassC
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveInstanceClassD.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassD
                {
                }
                """),
            ("ContosoOrders.Core/MoveInstanceCallerAmbiguous2.cs", """
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
                """));

        // Decision 5: MoveMemberAsync itself no longer rejects an unresolved instance-move call
        // site. It returns a proposed change set (the move still happens) plus a pending ledger
        // entry describing the site that couldn't be auto-resolved - opening the actual ledger is
        // the MoveMember MCP tool's job, done only after the change set is atomically applied (see
        // AdvancedStructuralEngine.MoveInstanceMembersAsync's comment on why TryOpen can't happen here).
        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MoveInstanceClassC.cs"),
            "MoveInstanceClassC", ["Foo"], "MoveInstanceClassD");

        Assert.Multiple(() =>
        {
            Assert.That(result.PendingLedgerEntries, Is.Not.Null.And.Count.EqualTo(1));
            var entry = result.PendingLedgerEntries!.Single();
            Assert.That(entry.Status, Is.EqualTo(CallSiteStatus.Ambiguous));
            Assert.That(entry.FilePath, Does.Contain("MoveInstanceCallerAmbiguous2"));
            Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveInstanceClassE.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassE
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveInstanceClassF.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceClassF
                {
                }
                """),
            ("ContosoOrders.Core/MoveInstanceCallerAmbiguous3.cs", """
                namespace ContosoOrders.Core;

                public class MoveInstanceCallerAmbiguous3
                {
                    private readonly MoveInstanceClassF _f1 = new MoveInstanceClassF();
                    private readonly MoveInstanceClassF _f2 = new MoveInstanceClassF();

                    public void Do()
                    {
                        var e = new MoveInstanceClassE();
                        e.Foo();
                    }
                }
                """));

        var filePath = workspace.PathOf("ContosoOrders.Core/MoveInstanceClassE.cs");

        var result = await engine.MoveMemberAsync(filePath, "MoveInstanceClassE", ["Foo"], "MoveInstanceClassF");
        Assume.That(result.PendingLedgerEntries, Is.Not.Null.And.Count.EqualTo(1));

        var applyResult = await workspace.Manager.ApplyProposedChangesAsync(result.Changes, validateChanges: false);
        Assert.That(applyResult.Success, Is.True, applyResult.Summary);

        var opened = ((IScopedOperationLedger)workspace.Manager).TryOpen(
            "MoveMember_Test_Decision5", result.PendingLedgerEntries!, out var rejectionReason);
        Assert.That(opened, Is.True, rejectionReason);

        // An unrelated file - not the ledger's own tracked call-site file - must be refused while
        // the ledger's entry is unresolved, per Decision 2's IsBlocked gate.
        var otherPath = workspace.PathOf("ContosoOrders.Core/MoveInstanceClassF.cs");
        var otherText = workspace.ReadText("ContosoOrders.Core/MoveInstanceClassF.cs");
        var unrelatedChange = new Dictionary<FilePathWrapper, string>
        {
            [otherPath] = otherText + "\n// unrelated edit\n"
        };
        var blockedResult = await workspace.Manager.ApplyProposedChangesAsync(unrelatedChange, validateChanges: false);

        Assert.Multiple(() =>
        {
            Assert.That(blockedResult.Success, Is.False);
            Assert.That(blockedResult.Summary, Does.Contain("scoped operation ledger"));
        });

        // The ledger's own tracked file (the unresolved call site) must still be writable -> that's
        // where RecordFix's resolving edit needs to land.
        var entry = result.PendingLedgerEntries!.Single();
        var fixupPath = workspace.PathOf(entry.FilePath);
        var fixupText = workspace.ReadText(entry.FilePath);
        var fixupChange = new Dictionary<FilePathWrapper, string>
        {
            [fixupPath] = fixupText.Replace("_f1.Foo", "_f1.Foo")
        };
        var fixupApply = await workspace.Manager.ApplyProposedChangesAsync(fixupChange, validateChanges: false);
        Assert.That(fixupApply.Success, Is.True, fixupApply.Summary);
    }

    // docs/current/blockers/resolved/blocking_error_movemember_analysisengine_antipatternengine_friction.md:
    // "new" is a zero-argument constructor call only. Against a target with no zero-arg constructor it
    // used to build 'new Target()' anyway and surface one undifferentiated CS7036 per site from the
    // compile gate; it must now be refused before any change set is built, naming the key and the
    // real constructor signature.
    [Test]
    public async Task MoveMemberAsync_NewFixupTargetWithoutZeroArgCtor_RefusedWithSignatureAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveNewSourceA.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewSourceA
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveNewTargetB.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewTargetB
                {
                    public MoveNewTargetB(string name)
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveNewCallerB.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewCallerB
                {
                    public void Do()
                    {
                        var a = new MoveNewSourceA();
                        a.Foo();
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/MoveNewSourceA.cs"),
            "MoveNewSourceA", ["Foo"], "MoveNewTargetB");
        var row = rows.Single(r => r.CallExpression.Contains("Foo"));
        Assume.That(row.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
        var key = $"{row.FilePath}:{row.Line}";

        var ex = Assert.ThrowsAsync<ToolInvalidArgumentException>(() =>
            engine.MoveMemberAsync(
                workspace.PathOf("ContosoOrders.Core/MoveNewSourceA.cs"),
                "MoveNewSourceA", ["Foo"], "MoveNewTargetB", null, default, true,
                new Dictionary<string, string> { [key] = "new" }));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(ex.Message, Does.Contain(key));
            Assert.That(ex.Message, Does.Contain("MoveNewTargetB(string name)"));
            Assert.That(ex.Message, Does.Contain("zero-argument"));
            Assert.That(ex.Message, Does.Contain("No changes were made"));
        });
    }

    [Test]
    public async Task MoveMemberAsync_NewFixupParameterlessTarget_RewritesToNewTargetAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveNewSourceC.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewSourceC
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveNewTargetD.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewTargetD
                {
                }
                """),
            ("ContosoOrders.Core/MoveNewCallerD.cs", """
                namespace ContosoOrders.Core;

                public class MoveNewCallerD
                {
                    public void Do()
                    {
                        var c = new MoveNewSourceC();
                        c.Foo();
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/MoveNewSourceC.cs"),
            "MoveNewSourceC", ["Foo"], "MoveNewTargetD");
        var row = rows.Single(r => r.CallExpression.Contains("Foo"));
        var key = $"{row.FilePath}:{row.Line}";

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MoveNewSourceC.cs"),
            "MoveNewSourceC", ["Foo"], "MoveNewTargetD", null, default, true,
            new Dictionary<string, string> { [key] = "new" });

        Assert.Multiple(() =>
        {
            Assert.That(result.PendingLedgerEntries, Is.Null.Or.Empty);
            var callerChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MoveNewCallerD"));
            Assert.That(callerChange.Value, Does.Contain("new MoveNewTargetD().Foo"));
            Assert.That(result.AppliedFixups, Has.Count.EqualTo(1));
            Assert.That(result.AppliedFixups![0].FixupKey, Is.EqualTo(key));
            Assert.That(result.AppliedFixups[0].FixupValue, Is.EqualTo("new"));
        });
    }

    // Bulk keys: one "*" entry stands in for every unresolved "FilePath:Line" key (86 identical
    // entries in the originating blocker). Wildcard-resolved rows count as resolved - no ledger entry.
    [Test]
    public async Task MoveMemberAsync_GlobalWildcardFixup_ResolvesEveryUnresolvedSiteAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveWildSourceA.cs", """
                namespace ContosoOrders.Core;

                public class MoveWildSourceA
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MoveWildTargetB.cs", """
                namespace ContosoOrders.Core;

                public class MoveWildTargetB
                {
                }
                """),
            ("ContosoOrders.Core/MoveWildCaller1.cs", """
                namespace ContosoOrders.Core;

                public class MoveWildCaller1
                {
                    public void Do()
                    {
                        var a = new MoveWildSourceA();
                        a.Foo();
                    }
                }
                """),
            ("ContosoOrders.Core/MoveWildCaller2.cs", """
                namespace ContosoOrders.Core;

                public class MoveWildCaller2
                {
                    public void Do()
                    {
                        var a = new MoveWildSourceA();
                        a.Foo();
                    }
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MoveWildSourceA.cs"),
            "MoveWildSourceA", ["Foo"], "MoveWildTargetB", null, default, true,
            new Dictionary<string, string> { ["*"] = "new" });

        Assert.Multiple(() =>
        {
            Assert.That(result.PendingLedgerEntries, Is.Null.Or.Empty);
            Assert.That(result.SkippedCallSites, Is.Empty);
            Assert.That(result.Changes.Single(kv => kv.Key.ToString().Contains("MoveWildCaller1")).Value, Does.Contain("new MoveWildTargetB().Foo"));
            Assert.That(result.Changes.Single(kv => kv.Key.ToString().Contains("MoveWildCaller2")).Value, Does.Contain("new MoveWildTargetB().Foo"));
            Assert.That(result.AppliedFixups, Has.Count.EqualTo(2));
            Assert.That(result.AppliedFixups!.Select(f => f.FixupKey), Is.All.EqualTo("*"));
        });
    }

    // Precedence: exact "FilePath:Line" > "FilePath:*" > "*". The file wildcard is given as a
    // solution-relative, upper-cased path to also pin that file keys are normalized (relative paths
    // resolved against the solution root) and matched case-insensitively.
    [Test]
    public async Task MoveMemberAsync_FixupKeyPrecedence_ExactBeatsFileWildcardBeatsGlobalAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MovePrecSourceA.cs", """
                namespace ContosoOrders.Core;

                public class MovePrecSourceA
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/MovePrecTargetB.cs", """
                namespace ContosoOrders.Core;

                public class MovePrecTargetB
                {
                }
                """),
            ("ContosoOrders.Core/MovePrecCaller1.cs", """
                namespace ContosoOrders.Core;

                public class MovePrecCaller1
                {
                    public void First()
                    {
                        var a = new MovePrecSourceA();
                        a.Foo();
                    }

                    public void Second()
                    {
                        var a = new MovePrecSourceA();
                        a.Foo();
                    }
                }
                """),
            ("ContosoOrders.Core/MovePrecCaller2.cs", """
                namespace ContosoOrders.Core;

                public class MovePrecCaller2
                {
                    public void Do()
                    {
                        var a = new MovePrecSourceA();
                        a.Foo();
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/MovePrecSourceA.cs"),
            "MovePrecSourceA", ["Foo"], "MovePrecTargetB");
        var caller1Rows = rows.Where(r => r.FilePath.ToString().Contains("MovePrecCaller1")).OrderBy(r => r.Line).ToList();
        Assume.That(caller1Rows, Has.Count.EqualTo(2));
        var exactKey = $"{caller1Rows[0].FilePath}:{caller1Rows[0].Line}";
        var solutionRoot = workspace.Manager.GetSolutionRoot();
        var fileKey = (solutionRoot != null ? Path.GetRelativePath(solutionRoot, caller1Rows[0].FilePath).ToUpperInvariant() : caller1Rows[0].FilePath.ToString()) + ":*";

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MovePrecSourceA.cs"),
            "MovePrecSourceA", ["Foo"], "MovePrecTargetB", null, default, true,
            new Dictionary<string, string>
            {
                [exactKey] = "_exactValue",
                [fileKey] = "_fileValue",
                ["*"] = "_globalValue",
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.PendingLedgerEntries, Is.Null.Or.Empty);
            var caller1 = result.Changes.Single(kv => kv.Key.ToString().Contains("MovePrecCaller1")).Value;
            Assert.That(caller1, Does.Contain("_exactValue.Foo"));
            Assert.That(caller1, Does.Contain("_fileValue.Foo"));
            Assert.That(caller1, Does.Not.Contain("_globalValue"));
            Assert.That(caller1.IndexOf("_exactValue.Foo", StringComparison.Ordinal), Is.LessThan(caller1.IndexOf("_fileValue.Foo", StringComparison.Ordinal)));
            var caller2 = result.Changes.Single(kv => kv.Key.ToString().Contains("MovePrecCaller2")).Value;
            Assert.That(caller2, Does.Contain("_globalValue.Foo"));
            Assert.That(result.AppliedFixups!.Select(f => f.FixupKey), Is.EquivalentTo(new[] { exactKey, fileKey, "*" }));
        });
    }

    // Regression test for docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md.
    // Root cause: destinationType was resolved from the destination class's own (upstream)
    // compilation, but each call site's in-scope candidate fields were resolved from the
    // *calling* document's (downstream) compilation. SymbolEqualityComparer.Default.Equals
    // never treats an ITypeSymbol from one Compilation as equal to "the same" type as seen
    // from a different Compilation, even across a direct ProjectReference - so a call site
    // whose containing class already had a correctly-typed, correctly-initialized sibling
    // field of the destination type was still reported NoCandidateIntroducible with an empty
    // candidate list. This fixture uses two real AdhocWorkspace projects (not one), which is
    // the minimum shape that can actually exercise the bug - a single-project fixture can
    // never fail this way, since every symbol in it already shares one Compilation.
    [Test]
    public async Task CrossProjectSiblingField_ClassifiesAsValidNotNoCandidateIntroducibleAsync()
    {
        const string upstreamSource = """
            namespace UpstreamLib;

            public class UpstreamServiceA
            {
                public void Foo()
                {
                }
            }

            public class UpstreamServiceB
            {
            }
            """;

        const string downstreamSource = """
            using UpstreamLib;

            namespace DownstreamApp;

            public class Caller
            {
                private readonly UpstreamServiceB _serviceB = new UpstreamServiceB();

                public void Do()
                {
                    var a = new UpstreamServiceA();
                    a.Foo();
                }
            }
            """;

        var solution = TestSolutionBuilder.CreateTwoProjectSolution(
            "UpstreamLib",
            [("UpstreamServiceA.cs", upstreamSource)],
            "DownstreamApp",
            [("Caller.cs", downstreamSource)]);

        _workspaceManager.SetTestSolution(solution);

        var results = await _engine.PreviewInstanceMoveCallSitesAsync("UpstreamServiceA.cs", "UpstreamServiceA", ["Foo"], "UpstreamServiceB");

        var fooSite = results.Single(r => r.CallExpression.Contains("Foo"));
        Assert.Multiple(() =>
        {
            Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.Valid),
                $"status={fooSite.Status} candidates=[{string.Join(",", fooSite.Candidates)}] reason={fooSite.BlockReason}");
            Assert.That(fooSite.Candidates, Does.Contain("_serviceB"));
        });
    }

    // Regression test for the wildcard callSiteFixup guard: line 3906 in MemberRefactoringEngine.cs.
    // When a single line has two call sites - one Valid (resolved to a specific receiver) and one
    // Unresolved (matched by wildcard) - the wildcard fixup must NOT overwrite the Valid receiver.
    [Test]
    public async Task MoveMemberAsync_WildcardFixup_DoesNotOverwriteValidReceiverOnSameLineAsync()
    {
        const string callerSource = """
            namespace ContosoOrders.Core;

            public class MixedLineCaller
            {
                private readonly MixedLineTarget _target = new MixedLineTarget();

                public void Run(MixedLineSource source)
                {
                    source.Foo(); Invoke((MixedLineTarget t) => source.Bar());
                }

                private static void Invoke(System.Action<MixedLineTarget> action)
                {
                }
            }
            """;

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MixedLineSource.cs", """
                namespace ContosoOrders.Core;

                public class MixedLineSource
                {
                    public void Foo() { }

                    public void Bar() { }
                }
                """),
            ("ContosoOrders.Core/MixedLineTarget.cs", """
                namespace ContosoOrders.Core;

                public class MixedLineTarget
                {
                }
                """),
            ("ContosoOrders.Core/MixedLineCaller.cs", callerSource));

        // Pin the fixture's preconditions: same line, Valid(Foo) then Ambiguous(Bar). If this drifts the
        // guard is no longer exercised and the test must say so rather than pass vacuously.
        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/MixedLineSource.cs"),
            "MixedLineSource", ["Foo", "Bar"], "MixedLineTarget");
        var callerRows = rows.Where(r => r.FilePath.ToString().Contains("MixedLineCaller")).ToList();
        Assert.That(callerRows, Has.Count.EqualTo(2));
        Assert.That(callerRows.Select(r => r.Line).Distinct().Count(), Is.EqualTo(1), "Both call sites must be on one line.");
        Assert.That(callerRows[0].Status, Is.EqualTo(CallSiteStatus.Valid));
        Assert.That(callerRows[0].SuggestedFix, Is.EqualTo("_target"));
        Assert.That(callerRows[1].Status, Is.EqualTo(CallSiteStatus.Ambiguous));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MixedLineSource.cs"),
            "MixedLineSource", ["Foo", "Bar"], "MixedLineTarget", null, default, true,
            new Dictionary<string, string> { ["*"] = "new" });

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            Assert.That(result.PendingLedgerEntries, Is.Null.Or.Empty);
            Assert.That(result.AppliedFixups, Is.Null.Or.Empty, "The wildcard must not be applied to a line whose receiver the Valid row already resolved.");
            var callerContent = result.Changes.Single(kv => kv.Key.ToString().Contains("MixedLineCaller")).Value;
            var expected = callerSource.Replace("source.Foo()", "_target.Foo()").Replace("source.Bar()", "_target.Bar()");
            Assert.That(callerContent, Is.EqualTo(expected));
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

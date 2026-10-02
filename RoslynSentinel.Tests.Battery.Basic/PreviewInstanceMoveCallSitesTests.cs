// AdvancedStructuralEngine.PreviewInstanceMoveCallSitesAsync -> Decision 3 of
// docs/current/plans/plan_scoped_operation_ledger.md (proposal_movemember_instance_callsite_resolution.md
// sections 1, 5, 6). Reporting-only dry-run: does not write anything, classifies each call site of the
// member(s) being moved by compiling a trial change set (member removed, call site untouched) and asking
// the compiler's own diagnostics whether that site breaks, then explaining broken sites via an in-scope
// candidate scan (SemanticModel.LookupSymbols).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

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
    public async Task ExistingNonEmptyDestinationClass_ClassifiesAsValidWithSuggestedFixAsync()
    {
        // Regression test for docs/current/blockers/blocking_error_movemember_instance_callsite_not_rewritten.md:
        // PreviewInstanceMoveCallSitesAsync built its trial changeset from the source-file edit only
        // (member removed), never including the destination-side edit (member added). Against an
        // EXISTING destination class with real pre-existing members (not a fresh near-empty class),
        // that incomplete changeset let every genuinely-unambiguous call site get misclassified as
        // already-Valid with no SuggestedFix, so autoResolveCallSites silently skipped rewriting it.
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassExistingA.cs"), """
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
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassExistingB.cs"), """
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
            """, reloadSolution: false);

        const string callerSource = """
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
            """;
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveCallerExistingDestination.cs"), callerSource);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "PreviewMoveClassExistingA.cs"));

        var results = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "PreviewMoveClassExistingA", ["Foo"], "PreviewMoveClassExistingB");

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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassA.cs"), ClassAWithFooSource, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveClassB.cs"), ClassBSource, reloadSolution: false);

        const string callerSource = """
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
        """;
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "PreviewMoveOuterFixture.cs"), callerSource);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "PreviewMoveClassA.cs"));

        var results = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "PreviewMoveClassA", ["Foo"], "PreviewMoveClassB");

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

    [Test]
    public async Task MoveMemberAsync_FieldDependencyAlreadySatisfiedOnTarget_MovesWithoutDuplicatingFieldAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "SatisfiedFieldSourceClass.cs"), """
            namespace ContosoOrders.Core;

            public class SatisfiedFieldSourceClass
            {
                private readonly string _shared = "source";

                public string ReadShared()
                {
                    return _shared;
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "SatisfiedFieldTargetClass.cs"), """
            namespace ContosoOrders.Core;

            public class SatisfiedFieldTargetClass
            {
                private readonly string _shared = "target";
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "SatisfiedFieldSourceClass.cs"));

        var result = await _engine.MoveMemberAsync(filePath, "SatisfiedFieldSourceClass", ["ReadShared"], "SatisfiedFieldTargetClass");

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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "IncompatibleFieldSourceClass.cs"), """
            namespace ContosoOrders.Core;

            public class IncompatibleFieldSourceClass
            {
                private readonly string _shared = "source";

                public string ReadShared()
                {
                    return _shared;
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "IncompatibleFieldTargetClass.cs"), """
            namespace ContosoOrders.Core;

            public class IncompatibleFieldTargetClass
            {
                private readonly int _shared = 42;
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "IncompatibleFieldSourceClass.cs"));

        var ex = Assert.ThrowsAsync<ToolInvalidArgumentException>(async () =>
            await _engine.MoveMemberAsync(filePath, "IncompatibleFieldSourceClass", ["ReadShared"], "IncompatibleFieldTargetClass"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("incompatible type"));
            Assert.That(ex.Message, Does.Contain("_shared"));
        });
    }

    [Test]
    public async Task MoveMemberAsync_AmbiguousInstanceMemberNoFixup_ReturnsPendingLedgerEntryAsync()
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

        // Decision 5: MoveMemberAsync itself no longer rejects an unresolved instance-move call
        // site. It returns a proposed change set (the move still happens) plus a pending ledger
        // entry describing the site that couldn't be auto-resolved - opening the actual ledger is
        // the MoveMember MCP tool's job, done only after the change set is atomically applied (see
        // AdvancedStructuralEngine.MoveInstanceMembersAsync's comment on why TryOpen can't happen here).
        var result = await _engine.MoveMemberAsync(filePath, "MoveInstanceClassC", ["Foo"], "MoveInstanceClassD");

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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassE.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassE
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceClassF.cs"), """
            namespace ContosoOrders.Core;

            public class MoveInstanceClassF
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveInstanceCallerAmbiguous3.cs"), """
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
            """);
        var unrelatedFile = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveInstanceClassE.cs");
        var unrelatedPath = _workspaceManager.SetFilePath(unrelatedFile);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveInstanceClassE.cs"));

        var result = await _engine.MoveMemberAsync(filePath, "MoveInstanceClassE", ["Foo"], "MoveInstanceClassF");
        Assume.That(result.PendingLedgerEntries, Is.Not.Null.And.Count.EqualTo(1));

        var applyResult = await _workspaceManager.ApplyProposedChangesAsync(result.Changes, validateChanges: false);
        Assert.That(applyResult.Success, Is.True, applyResult.Summary);

        var opened = ((IScopedOperationLedger)_workspaceManager).TryOpen(
            "MoveMember_Test_Decision5", result.PendingLedgerEntries!, out var rejectionReason);
        Assert.That(opened, Is.True, rejectionReason);

        // An unrelated file - not the ledger's own tracked call-site file - must be refused while
        // the ledger's entry is unresolved, per Decision 2's IsBlocked gate.
        var otherFile = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveInstanceClassF.cs");
        var otherPath = _workspaceManager.SetFilePath(otherFile);
        var unrelatedChange = new Dictionary<FilePathWrapper, string>
        {
            [otherPath] = await File.ReadAllTextAsync(otherFile) + "\n// unrelated edit\n"
        };
        var blockedResult = await _workspaceManager.ApplyProposedChangesAsync(unrelatedChange, validateChanges: false);

        Assert.Multiple(() =>
        {
            Assert.That(blockedResult.Success, Is.False);
            Assert.That(blockedResult.Summary, Does.Contain("scoped operation ledger"));
        });

        // The ledger's own tracked file (the unresolved call site) must still be writable -> that's
        // where RecordFix's resolving edit needs to land.
        var entry = result.PendingLedgerEntries!.Single();
        var fixupPath = _workspaceManager.SetFilePath(entry.FilePath);
        var fixupChange = new Dictionary<FilePathWrapper, string>
        {
            [fixupPath] = (await File.ReadAllTextAsync(entry.FilePath)).Replace("_f1.Foo", "_f1.Foo")
        };
        var fixupApply = await _workspaceManager.ApplyProposedChangesAsync(fixupChange, validateChanges: false);
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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewSourceA.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewSourceA
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewTargetB.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewTargetB
            {
                public MoveNewTargetB(string name)
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewCallerB.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewCallerB
            {
                public void Do()
                {
                    var a = new MoveNewSourceA();
                    a.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveNewSourceA.cs"));
        var callerFile = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveNewCallerB.cs");
        var callerBefore = await File.ReadAllTextAsync(callerFile);

        var rows = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "MoveNewSourceA", ["Foo"], "MoveNewTargetB");
        var row = rows.Single(r => r.CallExpression.Contains("Foo"));
        Assume.That(row.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
        var key = $"{row.FilePath}:{row.Line}";

        var ex = Assert.ThrowsAsync<ToolInvalidArgumentException>(() =>
            _engine.MoveMemberAsync(filePath, "MoveNewSourceA", ["Foo"], "MoveNewTargetB", null, default, true, new Dictionary<string, string> { [key] = "new" }));

        var callerAfter = await File.ReadAllTextAsync(callerFile);

        Assert.Multiple(() =>
        {
            Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(ex.Message, Does.Contain(key));
            Assert.That(ex.Message, Does.Contain("MoveNewTargetB(string name)"));
            Assert.That(ex.Message, Does.Contain("zero-argument"));
            Assert.That(ex.Message, Does.Contain("No changes were made"));
            Assert.That(callerAfter, Is.EqualTo(callerBefore));
        });
    }

    [Test]
    public async Task MoveMemberAsync_NewFixupParameterlessTarget_RewritesToNewTargetAsync()
    {
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewSourceC.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewSourceC
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewTargetD.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewTargetD
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveNewCallerD.cs"), """
            namespace ContosoOrders.Core;

            public class MoveNewCallerD
            {
                public void Do()
                {
                    var c = new MoveNewSourceC();
                    c.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveNewSourceC.cs"));
        var rows = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "MoveNewSourceC", ["Foo"], "MoveNewTargetD");
        var row = rows.Single(r => r.CallExpression.Contains("Foo"));
        var key = $"{row.FilePath}:{row.Line}";

        var result = await _engine.MoveMemberAsync(filePath, "MoveNewSourceC", ["Foo"], "MoveNewTargetD", null, default, true, new Dictionary<string, string> { [key] = "new" });

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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveWildSourceA.cs"), """
            namespace ContosoOrders.Core;

            public class MoveWildSourceA
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveWildTargetB.cs"), """
            namespace ContosoOrders.Core;

            public class MoveWildTargetB
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveWildCaller1.cs"), """
            namespace ContosoOrders.Core;

            public class MoveWildCaller1
            {
                public void Do()
                {
                    var a = new MoveWildSourceA();
                    a.Foo();
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MoveWildCaller2.cs"), """
            namespace ContosoOrders.Core;

            public class MoveWildCaller2
            {
                public void Do()
                {
                    var a = new MoveWildSourceA();
                    a.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MoveWildSourceA.cs"));

        var result = await _engine.MoveMemberAsync(filePath, "MoveWildSourceA", ["Foo"], "MoveWildTargetB", null, default, true, new Dictionary<string, string> { ["*"] = "new" });

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
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MovePrecSourceA.cs"), """
            namespace ContosoOrders.Core;

            public class MovePrecSourceA
            {
                public void Foo()
                {
                }
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MovePrecTargetB.cs"), """
            namespace ContosoOrders.Core;

            public class MovePrecTargetB
            {
            }
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MovePrecCaller1.cs"), """
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
            """, reloadSolution: false);
        await _fixture.AddFileToSolution(_workspaceManager, Path.Combine("ContosoOrders.Core", "MovePrecCaller2.cs"), """
            namespace ContosoOrders.Core;

            public class MovePrecCaller2
            {
                public void Do()
                {
                    var a = new MovePrecSourceA();
                    a.Foo();
                }
            }
            """);

        var filePath = _workspaceManager.SetFilePath(Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "MovePrecSourceA.cs"));
        var rows = await _engine.PreviewInstanceMoveCallSitesAsync(filePath, "MovePrecSourceA", ["Foo"], "MovePrecTargetB");
        var caller1Rows = rows.Where(r => r.FilePath.ToString().Contains("MovePrecCaller1")).OrderBy(r => r.Line).ToList();
        Assume.That(caller1Rows, Has.Count.EqualTo(2));
        var exactKey = $"{caller1Rows[0].FilePath}:{caller1Rows[0].Line}";
        var fileKey = Path.GetRelativePath(_workspaceManager.GetSolutionRoot()!, caller1Rows[0].FilePath).ToUpperInvariant() + ":*";

        var result = await _engine.MoveMemberAsync(filePath, "MovePrecSourceA", ["Foo"], "MovePrecTargetB", null, default, true, new Dictionary<string, string>
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

    // NOTE: no test here for MoveOrderDependent (a call site inside a member that's itself being
    // moved in the same batch). Tried the obvious fixture -- Foo() called unqualified from Baz(),
    // both in memberNames -- and it classifies as Valid, not MoveOrderDependent: when both members
    // move together, the call site's line is removed from the trial compile along with its container,
    // so no diagnostic ever fires there and brokenHere is false. isMoveOrderDependent is checked only
    // after brokenHere is true (see PreviewInstanceMoveCallSitesAsync), so this path may be
    // unreachable for a same-batch caller+callee pair as currently written -- flagged in the Decision 3
    // plan update rather than forcing a fixture that doesn't match the real semantics.
}

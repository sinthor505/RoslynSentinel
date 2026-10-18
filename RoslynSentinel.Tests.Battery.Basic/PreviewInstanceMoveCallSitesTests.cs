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
[Category("MemberRefactoringEngine")] // sentinel:auto-category
[Category("MoveMemberResult")] // sentinel:auto-category
[Category("PreviewCallSite")] // sentinel:auto-category
public class PreviewInstanceMoveCallSitesTests
{
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
    public async Task MoveMemberAsync_FieldMapSatisfiesSourceFieldDependency_DoesNotRefuseAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/FieldMapSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapSourceClass
                {
                    private readonly string _a = "source";

                    public string ReadA()
                    {
                        return _a;
                    }
                }
                """),
            ("ContosoOrders.Core/FieldMapTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapTargetClass
                {
                    private readonly string _b = "target";
                }
                """));

        var fieldMap = new Dictionary<string, string> { ["_a"] = "_b" };
        try
        {
            await engine.MoveMemberAsync(
                workspace.PathOf("ContosoOrders.Core/FieldMapSourceClass.cs"),
                "FieldMapSourceClass", ["ReadA"], "FieldMapTargetClass",
                null, CancellationToken.None, true, null, fieldMap);
        }
        catch (RoslynSentinel.Common.ToolInvalidArgumentException ex) when (ex.Message.Contains("private/internal"))
        {
            Assert.Fail($"fieldMap should satisfy the '_a' dependency, but the move still refused it: {ex.Message}");
        }
    }

    [Test]
    public async Task MoveMemberAsync_FieldMapRewritesMappedFieldReferencesInMovedBody_TargetCompilesAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/FieldMapRewriteSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapRewriteSourceClass
                {
                    private readonly string _a = "source";

                    public string M()
                    {
                        var first = _a;
                        return first + this._a;
                    }
                }
                """),
            ("ContosoOrders.Core/FieldMapRewriteTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapRewriteTargetClass
                {
                    private readonly string _b = "target";
                }
                """));

        var fieldMap = new Dictionary<string, string> { ["_a"] = "_b" };
        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/FieldMapRewriteSourceClass.cs"),
            "FieldMapRewriteSourceClass", ["M"], "FieldMapRewriteTargetClass",
            null, CancellationToken.None, true, null, fieldMap);

        var sourceText = result.Changes.Single(kv => kv.Key.ToString().Contains("FieldMapRewriteSourceClass")).Value;
        var targetText = result.Changes.Single(kv => kv.Key.ToString().Contains("FieldMapRewriteTargetClass")).Value;

        // The moved body is compiled together with the (unmoved) source class, exactly as the compile gate would see it.
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "FieldMapRewriteCheck",
            new[] { sourceText, targetText }.Select(t => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(t)),
            trustedAssemblies.Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(targetText, Does.Contain("var first = _b;"), "the bare reference in the moved body must bind to the target field");
            Assert.That(targetText, Does.Contain("this._b"), "the this-qualified reference in the moved body must bind to the target field");
            Assert.That(targetText, Does.Not.Contain("_a"), "no reference to the source field may remain in the target");
            Assert.That(sourceText, Does.Contain("private readonly string _a"), "the source class keeps its field");
            Assert.That(sourceText, Does.Not.Contain("string M()"), "the method left the source class");
            Assert.That(errors, Is.Empty, "source and target must compile together after the move");
        });
    }

    [Test]
    public async Task MoveMemberAsync_FieldMapRefusesMovedLocalNamedLikeMappedTargetFieldAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/FieldMapShadowSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapShadowSourceClass
                {
                    private readonly string _a = "source";

                    public string M()
                    {
                        var _b = "local";
                        return _a + _b;
                    }
                }
                """),
            ("ContosoOrders.Core/FieldMapShadowTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class FieldMapShadowTargetClass
                {
                    private readonly string _b = "target";
                }
                """));

        var fieldMap = new Dictionary<string, string> { ["_a"] = "_b" };
        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/FieldMapShadowSourceClass.cs"),
            "FieldMapShadowSourceClass", ["M"], "FieldMapShadowTargetClass",
            null, CancellationToken.None, true, null, fieldMap));

        Assert.That(ex!.Message, Does.Contain("_b"), "the refusal must name the colliding local and the mapped target field");
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

        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () =>
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
    public async Task MoveMemberAsync_FieldAssignedInSourceConstructor_RefusedWithConstructorMessageAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/CtorAssignedSourceClass.cs", """
                namespace ContosoOrders.Core;

                public class CtorAssignedSourceClass
                {
                    private string _name;

                    public CtorAssignedSourceClass(string name)
                    {
                        _name = name;
                    }

                    public string ReadName()
                    {
                        return _name;
                    }
                }
                """),
            ("ContosoOrders.Core/CtorAssignedTargetClass.cs", """
                namespace ContosoOrders.Core;

                public class CtorAssignedTargetClass
                {
                }
                """));

        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () =>
            await engine.MoveMemberAsync(
                workspace.PathOf("ContosoOrders.Core/CtorAssignedSourceClass.cs"),
                "CtorAssignedSourceClass", ["ReadName", "_name"], "CtorAssignedTargetClass"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("_name"));
            Assert.That(ex.Message, Does.Contain("constructor"));
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
        Assert.That(!applyResult.IsError, Is.True, applyResult.Summary);

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
            Assert.That(!blockedResult.IsError, Is.False);
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
        Assert.That(!fixupApply.IsError, Is.True, fixupApply.Summary);
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

        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() =>
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
    [Category("AppliedCallSiteFixup")] // sentinel:auto-category
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
    [Category("AppliedCallSiteFixup")] // sentinel:auto-category
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
    [Category("AppliedCallSiteFixup")] // sentinel:auto-category
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

        // Disk-tier only: this test needs the real disk fixture and its two-project compilation (see the
        // comment above). Every other test in this fixture runs on InMemoryWorkspace and creates no disk state.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = new MemberRefactoringEngine(workspaceManager, symbolNav, validationEngine);

        workspaceManager.SetTestSolution(solution);

        var results = await engine.PreviewInstanceMoveCallSitesAsync("UpstreamServiceA.cs", "UpstreamServiceA", ["Foo"], "UpstreamServiceB");

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

    [Test]
    public async Task MoveMemberAsync_ReceiverOnPreviousLine_RewritesReceiverAsync()
    {
        // The call is wrapped so the receiver sits on the line BEFORE the member name:
        //     a
        //         .Foo();
        // The preview row's line is the member-name line; the receiver rewrite must find the same site.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/WrapSource.cs", """
                namespace ContosoOrders.Core;

                public class WrapSource
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/WrapTarget.cs", """
                namespace ContosoOrders.Core;

                public class WrapTarget
                {
                }
                """),
            ("ContosoOrders.Core/WrapCaller.cs", """
                namespace ContosoOrders.Core;

                public class WrapCaller
                {
                    private readonly WrapTarget _target = new WrapTarget();

                    public void Do()
                    {
                        var a = new WrapSource();
                        a
                            .Foo();
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/WrapSource.cs"),
            "WrapSource", ["Foo"], "WrapTarget");
        var row = rows.Single(r => r.FilePath.ToString().Contains("WrapCaller"));
        Assert.That(row.Status, Is.EqualTo(CallSiteStatus.Valid));
        Assert.That(row.SuggestedFix, Is.EqualTo("_target"));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/WrapSource.cs"),
            "WrapSource", ["Foo"], "WrapTarget");

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            var callerContent = result.Changes.Single(kv => kv.Key.ToString().Contains("WrapCaller")).Value;
            Assert.That(callerContent, Does.Match(@"_target\s*\.Foo\(\);"), "The receiver on the previous line must be rewritten, keeping the wrapped layout.");
            Assert.That(callerContent, Does.Not.Match(@"\ba\s*\.Foo"), "The old receiver 'a' must be replaced.");
        });
    }

    [Test]
    public async Task PreviewInstanceMoveCallSites_QualifiedCallInsideMovedBody_IsMoveOrderDependentAsync()
    {
        // Baz is moved together with Foo and reaches Foo through a typed receiver (a.Foo()). After the move that receiver
        // (type MoveOrderSource) no longer has Foo, and the original-scope receiver lookup would be wrong for a body that now lives
        // on the destination, so the row is MoveOrderDependent rather than Valid or a guessed candidate.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MoveOrderSource.cs", """
                namespace ContosoOrders.Core;

                public class MoveOrderSource
                {
                    public void Foo()
                    {
                    }

                    public void Baz(MoveOrderSource a)
                    {
                        a.Foo();
                    }
                }
                """),
            ("ContosoOrders.Core/MoveOrderTarget.cs", """
                namespace ContosoOrders.Core;

                public class MoveOrderTarget
                {
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/MoveOrderSource.cs"),
            "MoveOrderSource", ["Foo", "Baz"], "MoveOrderTarget");

        var fooSite = rows.Single(r => r.CallExpression == "a.Foo");
        Assert.That(fooSite.Status, Is.EqualTo(CallSiteStatus.MoveOrderDependent));
    }

    [Test]
    public async Task PreviewInstanceMoveCallSites_DocCommentCref_IsNotClassifiedAsBrokenCallSiteAsync()
    {
        // A cref to the moved member is not a call site: it must never become a blocking Ambiguous/NoCandidate row.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/CrefSource.cs", """
                namespace ContosoOrders.Core;

                public class CrefSource
                {
                    public void Foo()
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/CrefTarget.cs", """
                namespace ContosoOrders.Core;

                public class CrefTarget
                {
                }
                """),
            ("ContosoOrders.Core/CrefCaller.cs", """
                namespace ContosoOrders.Core;

                public class CrefCaller
                {
                    private readonly CrefTarget _target = new CrefTarget();

                    /// <summary>Calls <see cref="CrefSource.Foo"/>.</summary>
                    public void Do()
                    {
                        new CrefSource().Foo();
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/CrefSource.cs"),
            "CrefSource", ["Foo"], "CrefTarget");

        // SymbolFinder reports the cref as a reference (line 7) alongside the real call (line 10).
        Assert.That(rows, Has.Count.EqualTo(2));
        var crefRow = rows.Single(r => r.Line == 7);
        Assert.That(crefRow.Status, Is.EqualTo(CallSiteStatus.Valid));
        Assert.That(crefRow.SuggestedFix, Is.Null, "A cref is never rewritten.");
        Assert.That(crefRow.BlockReason, Does.Contain("cref"));
        var callRow = rows.Single(r => r.Line == 10);
        Assert.That(callRow.Status, Is.EqualTo(CallSiteStatus.Valid));
        Assert.That(callRow.SuggestedFix, Is.EqualTo("_target"));
    }

    [Test]
    public async Task PreviewInstanceMoveCallSites_WrappedCallWithNamedArgumentNoReceiverInScope_IsNotValidAsync()
    {
        // The invocation is wrapped across lines with a named argument on its own line. The caller has no WrapNamedTarget
        // in scope, so after the move the site must be explained by binding/candidate scan, never Valid with a null fix.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/WrapNamedSource.cs", """
                namespace ContosoOrders.Core;

                public class WrapNamedSource
                {
                    public void Foo(int x)
                    {
                    }
                }
                """),
            ("ContosoOrders.Core/WrapNamedTarget.cs", """
                namespace ContosoOrders.Core;

                public class WrapNamedTarget
                {
                }
                """),
            ("ContosoOrders.Core/WrapNamedCaller.cs", """
                namespace ContosoOrders.Core;

                public class WrapNamedCaller
                {
                    public void Do()
                    {
                        new WrapNamedSource()
                            .Foo(
                                x: 1);
                    }
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/WrapNamedSource.cs"),
            "WrapNamedSource", ["Foo"], "WrapNamedTarget");
        var row = rows.Single(r => r.FilePath.ToString().Contains("WrapNamedCaller"));
        Assert.That(row.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
        Assert.That(row.SuggestedFix, Does.Contain("Add a field of type WrapNamedTarget"));
    }

    [Test]
    public async Task PreviewInstanceMoveCallSites_ReferenceBelowMovedMemberInSameFile_ClassifiesWithOriginalLineAsync()
    {
        // The reference sits BELOW Foo in the same file, so removing Foo shifts its line. The row must still classify as
        // Valid with the receiver fix, and its Line must be the original-text line (17), not the post-removal line.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/ShiftSource.cs", """
                namespace ContosoOrders.Core;

                public class ShiftSource
                {
                    public void Foo()
                    {
                    }
                }

                public class ShiftCaller
                {
                    private readonly ShiftTarget _target = new ShiftTarget();

                    public void Do()
                    {
                        var a = new ShiftSource();
                        a.Foo();
                    }
                }
                """),
            ("ContosoOrders.Core/ShiftTarget.cs", """
                namespace ContosoOrders.Core;

                public class ShiftTarget
                {
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/ShiftSource.cs"),
            "ShiftSource", ["Foo"], "ShiftTarget");

        var row = rows.Single(r => r.CallExpression == "a.Foo");
        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo(CallSiteStatus.Valid));
            Assert.That(row.SuggestedFix, Is.EqualTo("_target"));
            Assert.That(row.Line, Is.EqualTo(17));
        });
    }

    [Test]
    public async Task PreviewInstanceMoveCallSites_RecursiveSelfCall_ClassifiesAsValidNotAmbiguousAsync()
    {
        // Count calls itself unqualified inside its own body. After the move that call binds to the moved member on the
        // destination, so the row must be Valid and never Ambiguous.
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/RecurSource.cs", """
                namespace ContosoOrders.Core;

                public class RecurSource
                {
                    public int Count(int n)
                    {
                        return n == 0 ? 0 : Count(n - 1);
                    }
                }
                """),
            ("ContosoOrders.Core/RecurTarget.cs", """
                namespace ContosoOrders.Core;

                public class RecurTarget
                {
                }
                """));

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(
            workspace.PathOf("ContosoOrders.Core/RecurSource.cs"),
            "RecurSource", ["Count"], "RecurTarget");

        Assert.That(rows.Select(r => r.Status), Does.Not.Contain(CallSiteStatus.Ambiguous));
        var selfRow = rows.Single(r => r.Line == 7);
        Assert.That(selfRow.Status, Is.EqualTo(CallSiteStatus.Valid));
    }

    // NOTE: MoveOrderDependent (a call site inside a member that is itself being moved in the same batch) is
    // reached when the site no longer binds in the trial text, e.g. a typed receiver `a.Foo()` inside the moved
    // Baz (PreviewInstanceMoveCallSites_QualifiedCallInsideMovedBody_IsMoveOrderDependentAsync). An unqualified
    // Foo() or this.Foo() inside the moved Baz binds to the moved Foo on the destination and stays Valid.

    // Characterisation (TODO.md same-class gap; slice 3 of proposal_movemember_keep_delegator_and_same_class_calls.md).
    // Hypothesis (a) HELD in part: the bare Foo(2) is left untouched (no MemberAccessExpression to rewrite), so the source
    // would not compile (CS0103; this test does not compile it). NOT held: the site is not silent - it is reported as a
    // pending ledger entry with status NoCandidateIntroducible; the move is not refused.
    [Test]
    public async Task KnownLimitation_InstanceMove_BareSameClassCall_IsNotRewrittenAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/KnownLimitSrcA.cs", """
                namespace ContosoOrders.Core;

                public class KnownLimitSrcA
                {
                    public int Foo(int x)
                    {
                        return x + 1;
                    }

                    public int Caller()
                    {
                        return Foo(2);
                    }
                }
                """),
            ("ContosoOrders.Core/KnownLimitTgtB.cs", """
                namespace ContosoOrders.Core;

                public class KnownLimitTgtB
                {
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/KnownLimitSrcA.cs"),
            "KnownLimitSrcA", ["Foo"], "KnownLimitTgtB");

        var sourceChange = result.Changes.Single(kv => kv.Key.ToString().Contains("KnownLimitSrcA")).Value;
        Assert.Multiple(() =>
        {
            Assert.That(sourceChange, Does.Contain("return Foo(2);"));
            Assert.That(sourceChange, Does.Not.Contain("this."));
            Assert.That(result.PendingLedgerEntries, Is.Not.Null.And.Count.EqualTo(1));
            var entry = result.PendingLedgerEntries!.Single();
            Assert.That(entry.FilePath, Does.Contain("KnownLimitSrcA"));
            Assert.That(entry.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible));
        });
    }

    // Characterisation (slice 3 (b)). Hypothesis: a STATIC move into an existing class inserts the destination type as
    // a qualifier on the bare Foo(2) (CollectStaticMoveReferenceEditsAsync), so the source reads KnownLimitTgtS.Foo(2).
    // HELD: the source change contains KnownLimitTgtS.Foo(2). Compile of the result is not checked by this test.
    [Test]
    public async Task KnownLimitation_StaticMove_BareSameClassCall_IsQualifiedWithTargetTypeAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/KnownLimitSrcS.cs", """
                namespace ContosoOrders.Core;

                public static class KnownLimitSrcS
                {
                    public static int Foo(int x)
                    {
                        return x + 1;
                    }

                    public static int Caller()
                    {
                        return Foo(2);
                    }
                }
                """),
            ("ContosoOrders.Core/KnownLimitTgtS.cs", """
                namespace ContosoOrders.Core;

                public static class KnownLimitTgtS
                {
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/KnownLimitSrcS.cs"),
            "KnownLimitSrcS", ["Foo"], "KnownLimitTgtS");

        var sourceChange = result.Changes.Single(kv => kv.Key.ToString().Contains("KnownLimitSrcS")).Value;
        Assert.That(sourceChange, Does.Contain("KnownLimitTgtS.Foo(2)"));
    }

    // Rewrite behaviour (remove mode): the source holds exactly one field of the destination type, so the bare
    // Foo(2) in Caller becomes this._tgt.Foo(2), is reported in RewrittenCallSites, and leaves no unresolved row.
    [Test]
    public async Task InstanceMove_BareSameClassCall_WithSingleTargetField_IsRewrittenAndReportedAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/KnownLimitSrcC.cs", """
                namespace ContosoOrders.Core;

                public class KnownLimitSrcC
                {
                    private readonly KnownLimitTgtC _tgt = new KnownLimitTgtC();

                    public int Foo(int x)
                    {
                        return x + 1;
                    }

                    public int Caller()
                    {
                        return Foo(2);
                    }
                }
                """),
            ("ContosoOrders.Core/KnownLimitTgtC.cs", """
                namespace ContosoOrders.Core;

                public class KnownLimitTgtC
                {
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/KnownLimitSrcC.cs"),
            "KnownLimitSrcC", ["Foo"], "KnownLimitTgtC");

        var sourceChange = result.Changes.Single(kv => kv.Key.ToString().Contains("KnownLimitSrcC")).Value;
        Assert.Multiple(() =>
        {
            Assert.That(sourceChange, Does.Contain("return this._tgt.Foo(2);"));
            Assert.That(sourceChange, Does.Not.Contain("return Foo(2);"));
            Assert.That(result.RewrittenCallSites, Has.Count.EqualTo(1));
            var row = result.RewrittenCallSites.Single();
            Assert.That(row.Line, Is.EqualTo(14));
            Assert.That(row.OldText, Is.EqualTo("Foo(2)"));
            Assert.That(row.NewText, Is.EqualTo("this._tgt.Foo(2)"));
            Assert.That(result.PendingLedgerEntries?.Any(e => e.Status == CallSiteStatus.NoCandidateIntroducible) ?? false, Is.False);
        });
    }

    // Two target-typed fields: the receiver is ambiguous, so the bare call is not rewritten. It stays an unresolved row
    // whose BlockReason names both candidate fields.
    [Test]
    public async Task InstanceMove_BareSameClassCall_TwoTargetFields_LeftUnresolvedNamingBothAsync()
    {
        var (workspace, engine) = CreateInMemoryTestFixture(
            ("ContosoOrders.Core/MultiFieldSrc.cs", """
                namespace ContosoOrders.Core;

                public class MultiFieldSrc
                {
                    private readonly MultiFieldTgt _first = new MultiFieldTgt();
                    private readonly MultiFieldTgt _second = new MultiFieldTgt();

                    public int Foo(int x)
                    {
                        return x + 1;
                    }

                    public int Caller()
                    {
                        return Foo(2);
                    }
                }
                """),
            ("ContosoOrders.Core/MultiFieldTgt.cs", """
                namespace ContosoOrders.Core;

                public class MultiFieldTgt
                {
                }
                """));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("ContosoOrders.Core/MultiFieldSrc.cs"),
            "MultiFieldSrc", ["Foo"], "MultiFieldTgt");

        var sourceChange = result.Changes.Single(kv => kv.Key.ToString().Contains("MultiFieldSrc")).Value;
        var entry = result.PendingLedgerEntries!.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.RewrittenCallSites, Is.Empty);
            Assert.That(sourceChange, Does.Contain("return Foo(2);"));
            Assert.That(sourceChange, Does.Not.Contain("this._first"));
            Assert.That(sourceChange, Does.Not.Contain("this._second"));
            Assert.That(entry.FilePath, Does.Contain("MultiFieldSrc"));
            Assert.That(entry.BlockReason, Does.Contain("_first"));
            Assert.That(entry.BlockReason, Does.Contain("_second"));
        });
    }
}

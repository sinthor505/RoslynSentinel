using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("MemberRefactoringEngine")]
public class MoveMemberOverloadFallbackTests
{
    private static readonly string[] AllFiles = ["Source.cs", "Target.cs", "Caller.cs", "Types.cs"];

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine) CreateFixture(params (string relativePath, string content)[] files)
    {
        var workspace = InMemoryWorkspace.Create(files);
        var diffEngine = TestEngines.DiffEngine();
        var validationEngine = TestEngines.ValidationEngine(workspace.Manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validationEngine);
        return (workspace, engine);
    }

    private static Task<MoveMemberResult> MoveAsync(
        InMemoryWorkspace workspace,
        MemberRefactoringEngine engine,
        string[] memberNames,
        string className = "Source",
        string targetClassName = "Target",
        Dictionary<string, string>? callSiteFixups = null,
        MoveMemberOldMember oldMember = MoveMemberOldMember.remove,
        string? forwarderReceiver = null,
        bool autoResolveCallSites = true) =>
        engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            className,
            memberNames,
            targetClassName,
            null,
            CancellationToken.None,
            autoResolveCallSites: autoResolveCallSites,
            callSiteFixups: callSiteFixups,
            oldMember: oldMember,
            forwarderReceiver: forwarderReceiver);

    private static string TextOf(MoveMemberResult result, string fileName, string original)
    {
        var keys = result.Changes.Keys.Where(k => Path.GetFileName(k.ToString()) == fileName).ToList();
        return keys.Count == 0 ? original : result.Changes[keys[0]];
    }

    private static string SourceText(string header, string movedParameters, string stayingMembers) => Lines(
        "using System.Collections.Generic;",
        "",
        "namespace Example;",
        "",
        header,
        "{",
        $"    public int Save({movedParameters}) => 1;",
        stayingMembers,
        "}");

    private static string TargetText() => Lines(
        "using System.Collections.Generic;",
        "",
        "namespace Example;",
        "",
        "public class Target",
        "{",
        "}");

    private static string TypesText(string extraTypes) => Lines(
        "using System.Collections.Generic;",
        "",
        "namespace Example;",
        "",
        extraTypes);

    private static string CallerText(string receiverFields, params string[] bodyLines)
    {
        var lines = new List<string>
        {
            "using System.Collections.Generic;",
            "",
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    private readonly Source a = new Source();",
        };
        lines.Add(receiverFields);
        lines.Add("");
        lines.Add("    public int Run()");
        lines.Add("    {");
        lines.AddRange(bodyLines);
        lines.Add("    }");
        lines.Add("}");
        return Lines(lines.ToArray());
    }

    private const string TargetField = "    private readonly Target _target = new Target();";

    /// <summary>Compiles the given texts together and returns the error diagnostics plus the compilation, so a test can also bind a call.</summary>
    private static (List<string> Errors, CSharpCompilation Compilation) Compile(IEnumerable<string> texts)
    {
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(
            "OverloadFallbackCheck",
            texts.Select(t => CSharpSyntaxTree.ParseText(t)),
            trustedAssemblies.Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        return (errors, compilation);
    }

    /// <summary>The containing type name of the symbol each invocation of <paramref name="methodName"/> on <paramref name="receiverText"/> binds to, in source order.</summary>
    private static List<string> BoundContainers(CSharpCompilation compilation, string methodName, string receiverText)
    {
        var bound = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                if (invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access
                    && access.Name.Identifier.Text == methodName
                    && access.Expression.ToString() == receiverText)
                {
                    var symbol = model.GetSymbolInfo(invocation).Symbol as Microsoft.CodeAnalysis.IMethodSymbol;
                    bound.Add(((symbol?.ReducedFrom ?? symbol)?.ContainingType.Name) ?? "<unbound>");
                }
            }
        }

        return bound;
    }

    [TestCase("public class Source", "int a", "    public int Save(long a) => 2;", "", "1", "Save(int)", TestName = "Widening_SaveLongStays")]
    [TestCase("public class Source", "int a", "    public int Save(int? a) => 2;", "", "1", "Save(int)", TestName = "Nullable_SaveNullableIntStays")]
    [TestCase("public class Source", "Dog d", "    public int Save(Animal an) => 2;", "public class Animal { }\npublic class Dog : Animal { }", "new Dog()", "Save(Dog)", TestName = "BaseType_SaveAnimalStays")]
    [TestCase("public class Source", "List<int> l", "    public int Save(IEnumerable<int> e) => 2;", "", "new List<int>()", "Save(List<int>)", TestName = "Interface_SaveEnumerableStays")]
    [TestCase("public class Source", "string s", "    public int Save<T>(T t) => 2;", "", "\"x\"", "Save(string)", TestName = "Generic_SaveGenericStays")]
    [TestCase("public class Source", "int a", "    public int Save(params int[] a) => 2;", "", "1", "Save(int)", TestName = "Params_SaveParamsStays")]
    [TestCase("public class Source", "int a", "    public int Save(int a, bool flush = false) => 2;", "", "1", "Save(int)", TestName = "Optional_SaveWithOptionalStays")]
    [TestCase("public class Source", "int a", "    public int Save(Meters m) => 2;", "public struct Meters { public static implicit operator Meters(int i) => default; }", "1", "Save(int)", TestName = "UserDefinedImplicitConversion_SaveMetersStays")]
    [TestCase("public class Source", "int a", "", "public static class SourceExtensions { public static int Save(this Source s, int a) => 2; }", "1", "Save(int)", TestName = "ExtensionMethod_SameNamedExtensionTakesOver")]
    [TestCase("public class Source : BaseSource", "int a", "", "public class BaseSource { public int Save(long a) => 2; }", "1", "Save(int)", TestName = "InheritedBaseOverload_BaseSaveLongTakesOver")]
    public async Task InstanceMove_CallThatFellBackToStayingOverload_IsRewrittenToMovedOverload(string header, string movedParameters, string stayingMembers, string extraTypes, string callArgument, string movedKey)
    {
        var sourceText = SourceText(header, movedParameters, stayingMembers);
        var callerText = CallerText(TargetField, $"        return a.Save({callArgument});");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText(extraTypes)));

        var result = await MoveAsync(workspace, engine, [movedKey]);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var caller = newTexts[2];
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty, "The call must be rewritten, not skipped.");
            Assert.That(caller, Does.Contain($"return _target.Save({callArgument});"));
            Assert.That(newTexts[1], Does.Contain($"public int Save({movedParameters})"), "The moved overload sits on Target.");
            Assert.That(errors, Is.Empty, "The moved code must compile.");
            Assert.That(BoundContainers(compilation, "Save", "_target"), Is.EqualTo(new[] { "Target" }), "The call binds to Target's member.");
        });
    }

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine, string callerText) WideningFixture(string receiverFields, params string[] callerBody)
    {
        var callerText = CallerText(receiverFields, callerBody);
        var (workspace, engine) = CreateFixture(
            ("Source.cs", SourceText("public class Source", "int a", "    public int Save(long a) => 2;")),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("")));
        return (workspace, engine, callerText);
    }

    private static void AssertNothingWritten(InMemoryWorkspace workspace, string callerText)
    {
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(SourceText("public class Source", "int a", "    public int Save(long a) => 2;")), "A refused move must not write.");
            Assert.That(workspace.ReadText("Target.cs"), Is.EqualTo(TargetText()), "A refused move must not write.");
            Assert.That(workspace.ReadText("Caller.cs"), Is.EqualTo(callerText), "A refused move must not write.");
        });
    }

    private static int LineOf(string text, string fragment) => text.Split('\n').ToList().FindIndex(l => l.Contains(fragment)) + 1;

    [Test]
    public async Task InstanceMove_FallbackCallWithNoReceiver_RefusesWholeMoveAndWritesNothing()
    {
        var (workspace, engine, callerText) = WideningFixture("", "        return a.Save(1);");

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"]));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain($"Caller.cs:{LineOf(callerText, "return a.Save(1);")}"), "Names the file and line.");
            Assert.That(refusal.Message, Does.Contain("Example.Source.Save(long)"), "Names the method the call would silently bind to.");
            Assert.That(refusal.Message, Does.Contain("callSiteFixups"), "Names the next step.");
            Assert.That(refusal.Message, Does.Contain("also move the other overload"), "Names the alternative next step.");
            Assert.That(refusal.Message, Does.Contain("MoveMember"), "The hint is a tool call.");
            Assert.That(refusal.Message, Does.Contain("Alternative that leaves every caller untouched"), "Names the keep-mode alternative.");
            Assert.That(refusal.Message, Does.Contain("[Obsolete] forwarder"), "Says what the keep-mode alternative leaves behind.");
            Assert.That(refusal.Message, Does.Contain("forwarderReceiver"), "Names the receiver parameter the keep mode may need.");
        });
        AssertNothingWritten(workspace, callerText);
    }

    [Test]
    public async Task InstanceMove_FallbackCallWithNoReceiver_KeepAsDeprecatedWrapperAlternative_LeavesCallerCompilingAndBoundToSource()
    {
        // The same fixture the refusal above is raised for: the moved Save(int) has a staying overload Save(long) and the
        // caller has no Target receiver. The refusal's alternative (oldMember keepAsDeprecatedWrapper) must really work here.
        var (workspace, engine, callerText) = WideningFixture("", "        return a.Save(1);");

        var result = await MoveAsync(workspace, engine, ["Save(int)"], oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper, forwarderReceiver: "new Target()");

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty, "A keep mode skips no caller.");
            Assert.That(newTexts[2], Is.EqualTo(callerText), "The caller is left untouched.");
            Assert.That(newTexts[0], Does.Contain("System.Obsolete("), "The moved overload stays behind as a deprecated forwarder.");
            Assert.That(newTexts[0], Does.Contain("public int Save(long a) => 2;"), "The staying overload is untouched.");
            Assert.That(newTexts[1], Does.Contain("public int Save(int a)"), "The moved overload sits on Target.");
            Assert.That(errors, Is.Empty, "The kept forwarder code must compile.");
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Source" }), "The untouched caller still binds to Source.Save(int), now the forwarder.");
        });
    }

    [Test]
    public async Task InstanceMove_FallbackCallWithTwoCandidateReceivers_RefusesWholeMoveAndWritesNothing()
    {
        var (workspace, engine, callerText) = WideningFixture(TargetField + "\n    private readonly Target _other = new Target();", "        return a.Save(1);");

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"]));

        Assert.That(refusal!.Message, Does.Contain("would bind to 'Example.Source.Save(long)'"));
        AssertNothingWritten(workspace, callerText);
    }

    [Test]
    public async Task InstanceMove_AutoResolveOff_RefusesInstanceMoveAndWritesNothing()
    {
        var (workspace, engine, callerText) = WideningFixture(TargetField, "        return a.Save(1);");

        await Assert.ThrowsAsync<ToolNotFoundException>(() => MoveAsync(workspace, engine, ["Save(int)"], autoResolveCallSites: false));

        AssertNothingWritten(workspace, callerText);
    }

    [Test]
    public async Task InstanceMove_FallbackCallWithFixup_MovesAndRewritesWithTheFixupReceiver()
    {
        var (workspace, engine, callerText) = WideningFixture("", "        return a.Save(1);");
        var fixups = new Dictionary<string, string> { [$"{workspace.PathOf("Caller.cs")}:{LineOf(callerText, "return a.Save(1);")}"] = "new" };

        var result = await MoveAsync(workspace, engine, ["Save(int)"], callSiteFixups: fixups);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("return new Target().Save(1);"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "new Target()"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task InstanceMove_FallbackCallWithTwoCandidateReceivers_FixupPicksOne()
    {
        var (workspace, engine, callerText) = WideningFixture(TargetField + "\n    private readonly Target _other = new Target();", "        return a.Save(1);");
        var fixups = new Dictionary<string, string> { [$"{workspace.PathOf("Caller.cs")}:{LineOf(callerText, "return a.Save(1);")}"] = "_other" };

        var result = await MoveAsync(workspace, engine, ["Save(int)"], callSiteFixups: fixups);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("return _other.Save(1);"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "_other"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task InstanceMove_PreviewAgreesWithTheMove_RewriteCase()
    {
        var (workspace, engine, callerText) = WideningFixture(TargetField, "        return a.Save(1);");

        var rows = await engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("Source.cs"), "Source", ["Save(int)"], "Target");
        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        var row = rows.Single(r => r.CallExpression.Contains("Save"));
        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo(CallSiteStatus.Valid), "The dry-run row is resolvable, as the move treats it.");
            Assert.That(row.SuggestedFix, Is.EqualTo("_target"));
            Assert.That(row.Line, Is.EqualTo(LineOf(callerText, "return a.Save(1);")));
            Assert.That(TextOf(result, "Caller.cs", callerText), Does.Contain("return _target.Save(1);"));
        });
    }

    [Test]
    public async Task InstanceMove_PreviewAgreesWithTheMove_RefusalCase()
    {
        var (workspace, engine, callerText) = WideningFixture("", "        return a.Save(1);");

        var silentFallbacks = new Dictionary<(string FilePath, int Line), string>();
        var rows = await engine.PreviewInstanceMoveCallSitesAsync(workspace.PathOf("Source.cs"), "Source", ["Save(int)"], "Target", null, CancellationToken.None, silentFallbacks);
        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"]));

        var row = rows.Single(r => r.CallExpression.Contains("Save"));
        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo(CallSiteStatus.NoCandidateIntroducible), "The dry-run row is unresolved, as the move treats it.");
            Assert.That(row.BlockReason, Does.Contain("silently bind to 'Example.Source.Save(long)'"), "The dry run says why.");
            Assert.That(silentFallbacks.Values, Is.EqualTo(new[] { "Example.Source.Save(long)" }));
            Assert.That(refusal!.Message, Does.Contain("Example.Source.Save(long)"));
        });
        AssertNothingWritten(workspace, callerText);
    }

    [Test]
    public async Task InstanceMove_CallToStayingOverload_IsLeftUntouchedBesideTheRewrittenCall()
    {
        var (workspace, engine, _) = WideningFixture(TargetField, "        var x = a.Save(2L);", "        return x + a.Save(1);");

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("var x = a.Save(2L);"), "A call that binds to the staying overload is not touched.");
            Assert.That(newTexts[2], Does.Contain("return x + _target.Save(1);"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Source" }));
            Assert.That(BoundContainers(compilation, "Save", "_target"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task InstanceMove_OnlyStayingOverloadIsCalled_NeedsNoReceiverAndIsNotRefused()
    {
        var (workspace, engine, callerText) = WideningFixture("", "        return a.Save(2L);");

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            Assert.That(TextOf(result, "Caller.cs", callerText), Is.EqualTo(callerText));
            Assert.That(TextOf(result, "Target.cs", TargetText()), Does.Contain("public int Save(int a)"));
        });
    }

    [Test]
    public async Task InstanceMove_DifferentlyNamedStayingMember_KeepsTodaysSkippedCallSiteBehaviour()
    {
        // The staying member has a different name, so the moved call does not fall back to a same-named method: it simply no longer
        // binds, which is the existing BindFailed route (skipped and ledgered when no receiver exists), not a refusal.
        var callerText = CallerText("", "        return a.Save(1);");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", SourceText("public class Source", "int a", "    public int Keep(long a) => 2;")),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("")));

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1));
    }

    private static string SourceWithImplText(string runBody) => Lines(
        "namespace Example;",
        "",
        "public class Source",
        "{",
        "    private readonly Target _impl = new Target();",
        "",
        "    public int Save(int a) => 1;",
        "    public int Save(long a) => 2;",
        $"    public int Run() => {runBody};",
        "}");

    [Test]
    public async Task InstanceMove_SameClassBareCallThatFellBack_IsRewrittenThroughTheTargetField()
    {
        var (workspace, engine) = CreateFixture(
            ("Source.cs", SourceWithImplText("Save(1) + Save(2L)")),
            ("Target.cs", TargetText()),
            ("Caller.cs", CallerText("", "        return 0;")),
            ("Types.cs", TypesText("")));

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[0], Does.Contain("public int Run() => this._impl.Save(1) + Save(2L);"), "Only the call that bound to the moved overload is rewritten.");
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "this._impl"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task InstanceMove_SameClassBareCallThatFellBackWithNoTargetField_RefusesWholeMoveAndWritesNothing()
    {
        var sourceText = Lines(
            "namespace Example;",
            "",
            "public class Source",
            "{",
            "    public int Save(int a) => 1;",
            "    public int Save(long a) => 2;",
            "    public int Run() => Save(1);",
            "}");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", TargetText()),
            ("Caller.cs", CallerText("", "        return 0;")),
            ("Types.cs", TypesText("")));

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"]));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain($"Source.cs:{LineOf(sourceText, "Save(1)")}"));
            Assert.That(refusal.Message, Does.Contain("Example.Source.Save(long)"));
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
            Assert.That(workspace.ReadText("Target.cs"), Is.EqualTo(TargetText()), "A refused move must not write.");
        });
    }

    [TestCase(MoveMemberOldMember.keepDelegator, TestName = "KeepDelegator_CallersAreLeftAlone")]
    [TestCase(MoveMemberOldMember.keepAsDeprecatedWrapper, TestName = "KeepAsDeprecatedWrapper_CallersAreLeftAlone")]
    public async Task InstanceMove_KeepModes_ForwarderKeepsTheOldOverloadSoNothingFallsBack(MoveMemberOldMember oldMember)
    {
        var callerText = CallerText("", "        return a.Save(1);");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", SourceWithImplText("Save(1)")),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("")));

        var result = await MoveAsync(workspace, engine, ["Save(int)"], oldMember: oldMember, forwarderReceiver: "_impl");

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(result.SkippedCallSites, Is.Empty);
            Assert.That(newTexts[2], Is.EqualTo(callerText), "Keep modes do not rewrite callers.");
            Assert.That(newTexts[0], Does.Contain("this._impl.Save(a)"), "The forwarder keeps the old overload.");
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Source" }), "The caller still binds to the kept overload.");
        });
    }

    private static string DerivedSourceText(string stayingMembers, string runBody = "0") => Lines(
        "namespace Example;",
        "",
        "public class Source : Base",
        "{",
        "    public int Save(int a) => 1;",
        stayingMembers,
        $"    public int Run() => {runBody};",
        "}");

    private static (InMemoryWorkspace workspace, MemberRefactoringEngine engine, string sourceText, string callerText) PullUpFixture(string stayingMembers, string callerCall, string runBody = "0")
    {
        var sourceText = DerivedSourceText(stayingMembers, runBody);
        var callerText = CallerText("", $"        return {callerCall};");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("public class Base { }")));
        return (workspace, engine, sourceText, callerText);
    }

    [Test]
    public async Task BaseTypePullUp_CallWouldBindToStayingMoreDerivedOverload_RefusesWholeMoveAndWritesNothing()
    {
        var (workspace, engine, sourceText, callerText) = PullUpFixture("    public int Save(object o) => 2;", "a.Save(1)");

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"], targetClassName: "Base"));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain($"Caller.cs:{LineOf(callerText, "a.Save(1)")}"), "Names the file and line.");
            Assert.That(refusal.Message, Does.Contain("Example.Source.Save(object)"), "Names the method the call would silently bind to.");
            Assert.That(refusal.Message, Does.Contain("every same-named overload"), "Names the next step.");
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
            Assert.That(workspace.ReadText("Caller.cs"), Is.EqualTo(callerText), "A refused move must not write.");
            Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(TypesText("public class Base { }")), "A refused move must not write.");
        });
    }

    [Test]
    public async Task BaseTypePullUp_BareSameClassCallWouldBindToStayingOverload_RefusesWholeMoveAndWritesNothing()
    {
        var (workspace, engine, sourceText, _) = PullUpFixture("    public int Save(object o) => 2;", "0", runBody: "Save(1)");

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"], targetClassName: "Base"));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain($"Source.cs:{LineOf(sourceText, "Save(1)")}"));
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
        });
    }

    [Test]
    public async Task BaseTypePullUp_MovingBothOverloads_IsAllowed()
    {
        var (workspace, engine, _, callerText) = PullUpFixture("    public int Save(object o) => 2;", "a.Save(1)");

        var result = await MoveAsync(workspace, engine, ["Save(int)", "Save(object)"], targetClassName: "Base");

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Is.EqualTo(callerText), "A pull-up leaves callers alone.");
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Base" }));
        });
    }

    [Test]
    public async Task BaseTypePullUp_NoStayingOverload_IsAllowedAndCallsBindToBase()
    {
        var (workspace, engine, _, callerText) = PullUpFixture("", "a.Save(1)");

        var result = await MoveAsync(workspace, engine, ["Save(int)"], targetClassName: "Base");

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Is.EqualTo(callerText), "A pull-up leaves callers alone.");
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Base" }));
        });
    }

    [Test]
    public async Task BaseTypePullUp_StayingOverloadButNoCallUsesTheMovedOne_IsAllowed()
    {
        var (workspace, engine, _, callerText) = PullUpFixture("    public int Save(object o) => 2;", "a.Save(new object())");

        var result = await MoveAsync(workspace, engine, ["Save(int)"], targetClassName: "Base");

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Is.EqualTo(callerText));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "a"), Is.EqualTo(new[] { "Source" }));
        });
    }

    private static string StaticSourceText() => Lines(
        "namespace Example;",
        "",
        "public static class Source",
        "{",
        "    public static int Save(int a) => 1;",
        "    public static int Save(long a) => 2;",
        "}");

    private static string StaticTargetText() => Lines(
        "namespace Example;",
        "",
        "public static class Target",
        "{",
        "}");

    private static string StaticCallerText() => Lines(
        "namespace Example;",
        "",
        "public static class Caller",
        "{",
        "    public static int Run()",
        "    {",
        "        var x = Source.Save(2L);",
        "        return x + Source.Save(1);",
        "    }",
        "}");

    [Test]
    public async Task StaticMove_ExistingTarget_CallThatWouldFallBackIsRewrittenBySymbol()
    {
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSourceText()),
            ("Target.cs", StaticTargetText()),
            ("Caller.cs", StaticCallerText()),
            ("Types.cs", TypesText("")));

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        var newTexts = AllFiles.Select(f => TextOf(result, f, workspace.ReadText(f))).ToList();
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("var x = Source.Save(2L);"));
            Assert.That(newTexts[2], Does.Contain("return x + Target.Save(1);"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "Source"), Is.EqualTo(new[] { "Source" }));
            Assert.That(BoundContainers(compilation, "Save", "Target"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task StaticMove_NewTargetClass_CallThatWouldFallBackIsRewrittenBySymbol()
    {
        var (workspace, engine) = CreateFixture(
            ("Source.cs", StaticSourceText()),
            ("Caller.cs", StaticCallerText()),
            ("Types.cs", TypesText("")));

        var result = await MoveAsync(workspace, engine, ["Save(int)"]);

        var targetKey = result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Target.cs");
        var newTexts = new List<string> { TextOf(result, "Source.cs", workspace.ReadText("Source.cs")), result.Changes[targetKey], TextOf(result, "Caller.cs", workspace.ReadText("Caller.cs")), workspace.ReadText("Types.cs") };
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("var x = Source.Save(2L);"));
            Assert.That(newTexts[2], Does.Contain("Target.Save(1)"));
            Assert.That(newTexts[2], Does.Not.Contain("Source.Save(1)"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "Source"), Is.EqualTo(new[] { "Source" }));
            Assert.That(BoundContainers(compilation, "Save", "Target"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task InstanceMove_NewTargetClass_CallThatWouldFallBackIsNotLeftSilentlyRebound()
    {
        var callerText = CallerText("", "        return a.Save(1);");
        var sourceText = SourceText("public class Source", "int a", "    public int Save(long a) => 2;");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("")));

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"]));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain($"Caller.cs:{LineOf(callerText, "a.Save(1)")}"));
            Assert.That(refusal.Message, Does.Contain("Example.Source.Save(long)"));
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
            Assert.That(workspace.ReadText("Caller.cs"), Is.EqualTo(callerText), "A refused move must not write.");
        });
    }

    [Test]
    public async Task InstanceMove_NewTargetClass_FallbackCallWithFixup_MovesAndRewritesWithTheFixupReceiver()
    {
        var callerText = CallerText("", "        return a.Save(1);");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", SourceText("public class Source", "int a", "    public int Save(long a) => 2;")),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("")));
        var fixups = new Dictionary<string, string> { [$"{workspace.PathOf("Caller.cs")}:{LineOf(callerText, "return a.Save(1);")}"] = "new" };

        var result = await MoveAsync(workspace, engine, ["Save(int)"], callSiteFixups: fixups);

        var targetKey = result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == "Target.cs");
        var newTexts = new List<string> { TextOf(result, "Source.cs", workspace.ReadText("Source.cs")), result.Changes[targetKey], TextOf(result, "Caller.cs", workspace.ReadText("Caller.cs")), workspace.ReadText("Types.cs") };
        var (errors, compilation) = Compile(newTexts);
        Assert.Multiple(() =>
        {
            Assert.That(newTexts[2], Does.Contain("return new Target().Save(1);"));
            Assert.That(errors, Is.Empty);
            Assert.That(BoundContainers(compilation, "Save", "new Target()"), Is.EqualTo(new[] { "Target" }));
        });
    }

    [Test]
    public async Task BaseTypePullUp_StayingOverloadAndTrialCannotRun_RefusesWholeMoveAndWritesNothing()
    {
        // A second class named Base makes the trial's destination lookup ambiguous, so the preview throws before binding anything.
        // With a same-named overload staying, an unchecked pull-up could silently rebind a call, so the move is refused.
        var sourceText = DerivedSourceText("    public int Save(object o) => 2;");
        var callerText = CallerText("", "        return a.Save(1);");
        var otherBaseText = Lines("namespace Other;", "", "public class Base { }");
        var (workspace, engine) = CreateFixture(
            ("Source.cs", sourceText),
            ("Target.cs", TargetText()),
            ("Caller.cs", callerText),
            ("Types.cs", TypesText("public class Base { }")),
            ("Other.cs", otherBaseText));

        var refusal = await Assert.ThrowsAsync<ToolInvalidArgumentException>(() => MoveAsync(workspace, engine, ["Save(int)"], targetClassName: "Base"));

        Assert.Multiple(() =>
        {
            Assert.That(refusal!.Message, Does.Contain("could not be checked"));
            Assert.That(refusal.Message, Does.Contain("'Source'").And.Contain("'Base'"), "Names the class and the base type.");
            Assert.That(refusal.Message, Does.Contain("Nothing was written"));
            Assert.That(refusal.Message, Does.Contain("every same-named overload"), "Names the next step.");
            Assert.That(refusal.Message, Does.Not.Contain(Path.GetTempPath()), "No internal paths.");
            Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(sourceText), "A refused move must not write.");
            Assert.That(workspace.ReadText("Types.cs"), Is.EqualTo(TypesText("public class Base { }")), "A refused move must not write.");
            Assert.That(workspace.ReadText("Other.cs"), Is.EqualTo(otherBaseText), "A refused move must not write.");
        });
    }
}

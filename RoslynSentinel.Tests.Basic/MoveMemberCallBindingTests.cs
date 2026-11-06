using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MoveMember's W20 call-binding gate end to end: a move that would silently re-point an existing call at a different method is
/// refused before anything is written, and the moves that keep every binding (or keep them through a forwarder) still succeed.
/// Each refusal asserts the message names the call and both bindings, and that the files are unchanged.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("MoveMemberCallBinding")]
public class MoveMemberCallBindingTests
{
    private const string Eol = "\r\n";

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    private static (MemberRefactoringEngine Engine, ValidationEngine Validation) CreateEngine(InMemoryWorkspace workspace)
    {
        var validation = Battery.Basic.TestEngines.ValidationEngine(workspace.Manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (Battery.Basic.TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation), validation);
    }

    private static async Task<string> RefusalMessageAsync(MemberRefactoringEngine engine, InMemoryWorkspace workspace, string sourceFile, string className, string[] memberNames, string targetClassName, string targetFile, MoveMemberOldMember oldMember = MoveMemberOldMember.remove)
    {
        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.MoveMemberAsync(workspace.PathOf(sourceFile), className, memberNames, targetClassName, workspace.PathOf(targetFile), CancellationToken.None, oldMember: oldMember));
        return error!.Message;
    }

    private static Task<MoveMemberResult> MoveAsync(MemberRefactoringEngine engine, InMemoryWorkspace workspace, string sourceFile, string className, string[] memberNames, string targetClassName, string targetFile, MoveMemberOldMember oldMember = MoveMemberOldMember.remove)
    => engine.MoveMemberAsync(workspace.PathOf(sourceFile), className, memberNames, targetClassName, workspace.PathOf(targetFile), CancellationToken.None, oldMember: oldMember);

    private static string FileName(FilePathWrapper path) => Path.GetFileName(path.ToString());

    /// <summary>Replays the recorded edits on the originals, then runs the real apply through the compile gate and collateral guard.</summary>
    private static async Task AssertAppliesCleanlyAsync(InMemoryWorkspace workspace, ValidationEngine validation, (string Name, string Content)[] originals, MoveMemberResult result)
    {
        Assert.That(result.AppliedEdits, Is.Not.Null, "The route must report the edits it applied");
        foreach (var (path, edits) in result.AppliedEdits!)
        {
            var original = originals.Single(f => f.Name == FileName(path)).Content;
            Assert.That(CollateralChangeGuard.ApplyEdits(original, edits), Is.EqualTo(result.Changes[path]), $"Replaying the recorded edits on {FileName(path)} must reproduce the engine's text");
        }

        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(validation, workspace.Manager, NullLogger.Instance, result.Changes, "MoveMember", dryRun: false, expectedEdits: result.AppliedEdits);
        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
    }

    private static void AssertNothingWritten(InMemoryWorkspace workspace, (string Name, string Content)[] originals)
    {
        foreach (var (name, content) in originals)
        {
            Assert.That(workspace.ReadText(name), Is.EqualTo(content), $"{name} must be unchanged after a refused move");
        }
    }

    private static string TextOf(MoveMemberResult result, string fileName) => result.Changes.Single(c => FileName(c.Key) == fileName).Value;

    private static async Task<IMethodSymbol> MethodSymbolAsync(Solution solution, string path, string methodName)
    {
        var document = solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).Single()!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        var declaration = root!.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == methodName);
        return (IMethodSymbol)model!.GetDeclaredSymbol(declaration)!;
    }

    private static async Task<INamedTypeSymbol> NamedTypeSymbolAsync(Solution solution, string path, string typeName)
    {
        var document = solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).Single()!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        var declaration = root!.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.Text == typeName);
        return (INamedTypeSymbol)model!.GetDeclaredSymbol(declaration)!;
    }

    private static async Task AssertAppliesCleanlyAsync(FakeWorkspaceManager manager, ValidationEngine validation, (string Name, string Content)[] originals, MoveMemberResult result)
    {
        Assert.That(result.AppliedEdits, Is.Not.Null, "The route must report the edits it applied");
        foreach (var (path, edits) in result.AppliedEdits!)
        {
            var original = originals.Single(f => f.Name == FileName(path)).Content;
            Assert.That(CollateralChangeGuard.ApplyEdits(original, edits), Is.EqualTo(result.Changes[path]), $"Replaying the recorded edits on {FileName(path)} must reproduce the engine's text");
        }

        var outcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(validation, manager, NullLogger.Instance, result.Changes, "MoveMember", dryRun: false, expectedEdits: result.AppliedEdits);
        Assert.That(outcome.Error, Is.Null, outcome.Error?.Message);
    }

    private static (MemberRefactoringEngine Engine, ValidationEngine Validation) CreateEngine(FakeWorkspaceManager manager)
    {
        var validation = Battery.Basic.TestEngines.ValidationEngine(manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (Battery.Basic.TestEngines.MemberRefactoringEngine(manager, symbolNav, validation), validation);
    }

    [Test]
    public async Task Rebinding_MovedOverloadWouldCaptureExistingCall_RefusedNamingCallAndBothBindingsAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "    public static void Save(long x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Target.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(message, Does.StartWith("Cannot move [Save(int)] from 'Source' to 'Target'"));
        Assert.That(message, Does.Contain("Caller.cs:"), "The site is reported at its original location");
        Assert.That(message, Does.Contain("`Target.Save(1)`"), "The refusal quotes the call");
        Assert.That(message, Does.Contain("binds to 'Example.Target.Save(long)' today but would bind to 'Example.Target.Save(int)'"), "Both bindings are named");
        Assert.That(message, Does.Contain("cast the argument"), "The fix hint offers a cast");
        Assert.That(message, Does.Contain("use a different name"), "The fix hint offers a different name");
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task RepeatedRefusal_ProducesIdenticalMessageAndWritesNothingAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "    public static void Save(long x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Target.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        // The engine has no dryRun switch; the tool-layer dryRun is not reachable from this project, so the
        // equivalence is checked by running the same refused move twice and comparing the text.
        var first = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");
        var second = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(second, Is.EqualTo(first), "A repeated refused move must produce the same refusal text");
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task TwoCallsInOneMember_BothSitesReportedAndNamedAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "    public static void Save(long x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run()", "    {", "        Target.Save(1);", "        Target.Save(2);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(message, Does.Contain("2 call site(s)"), "Both calls count toward the refusal");
        Assert.That(message, Does.Contain("Caller.cs:7"), "The first call is reported at its line");
        Assert.That(message, Does.Contain("Caller.cs:8"), "The second call is reported at its line");
        Assert.That(message, Does.Contain("`Target.Save(1)`"));
        Assert.That(message, Does.Contain("`Target.Save(2)`"));
        Assert.That(message, Does.Contain("binds to 'Example.Target.Save(long)' today but would bind to 'Example.Target.Save(int)'"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task RecursiveSelfCall_FollowsTheMovedMember_AppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x)", "    {", "        if (x > 0) Save(x - 1);", "    }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("Save(x - 1)"), "The self-call moves with the member");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task UnrelatedSameNameMethod_IsNotFlagged_AppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        ("Other.cs", Lines("namespace Example;", "", "public static class Other", "{", "    public static void Save(int x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Other.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("Save(int x)"), "The member lands in Target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task KeepDelegator_CallersStayBoundThroughForwarder_AppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Source.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs", MoveMemberOldMember.keepDelegator);

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("Save(int x)"), "The member lands in Target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task KeepDelegator_StillRefusesRebindingOfExistingCall_NamingBothBindingsAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "    public static void Save(long x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Target.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs", MoveMemberOldMember.keepDelegator);

        Assert.That(message, Does.StartWith("Cannot move [Save(int)] from 'Source' to 'Target'"));
        Assert.That(message, Does.Contain("`Target.Save(1)`"));
        Assert.That(message, Does.Contain("binds to 'Example.Target.Save(long)' today but would bind to 'Example.Target.Save(int)'"));
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task RebindingInsideLambda_IsRefusedAndNamedAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "    public static void Save(long x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run()", "    {", "        System.Action act = () => Target.Save(1);", "        act();", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(message, Does.Contain("Caller.cs:7"), "The lambda call site is reported at its line");
        Assert.That(message, Does.Contain("`Target.Save(1)`"), "The refusal quotes the lambda call");
        Assert.That(message, Does.Contain("binds to 'Example.Target.Save(long)' today but would bind to 'Example.Target.Save(int)'"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task LocalFunctionSelfCall_FollowsTheMovedMember_AppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x)", "    {", "        Inner();", "        void Inner() => Save(x - 1);", "    }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public static class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save(int)" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("void Inner()"), "The local function moves with the member");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task GenericMethod_MovesAndAppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save<T>(T value) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public static class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("Save<T>(T value)"), "The generic member lands in Target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task ExtensionMethodWithReducedCallSite_IsRefusedNamingTheReasonAndWritesNothingAsync()
    {
        // Owner ruling D7: extension methods are refused. A reduced call ("a".Foo(1)) has no class qualifier to retarget, so the static-move
        // call-site rewrite used to turn it into Target.Foo(1), drop the receiver and fail the compile gate with CS7036.
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Foo(this string s, int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public static class Target", "{", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => \"a\".Foo(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var error = await Assert.ThrowsAsync<ToolNotImplementedException>(async () => await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Foo" }, "Target", "Target.cs"));

        Assert.That(error!.Message, Does.Contain("extension method 'Foo'"), "The refusal names the method");
        Assert.That(error.Message, Does.Contain("drop the receiver"), "The refusal names the reason");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task PullUpToBaseType_CallersStillBindAndAppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Base.cs", Lines("namespace Example;", "", "public class Base", "{", "}")),
        ("Derived.cs", Lines("namespace Example;", "", "public class Derived : Base", "{", "    public void Save(int x) { }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => new Derived().Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Derived.cs", "Derived", new[] { "Save(int)" }, "Base", "Base.cs");

        Assert.That(TextOf(result, "Base.cs"), Does.Contain("Save(int x)"), "The member is pulled up into Base");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task AddedUsingRebindsAnotherCallOfTheSameName_IsRefusedAsync()
    {
        // MoveMember adds "using Example.Lib;" above the file-scoped namespace in Target.cs. The existing "x".Bar() there binds to
        // NsA.ObjExt.Bar(this object) through a using at the same (compilation-unit) level, so StrExt.Bar(this string) outranks it.
        var files = new (string Name, string Content)[]
        {
        ("NsA.cs", Lines("namespace Example.NsA;", "", "public static class ObjExt", "{", "    public static void Bar(this object o) { }", "}")),
        ("Lib.cs", Lines("namespace Example.Lib;", "", "public class Widget { }", "", "public static class StrExt", "{", "    public static void Bar(this string s) { }", "}")),
        ("Source.cs", Lines("namespace Example.Src;", "", "using Example.Lib;", "", "public static class Source", "{", "    public static Widget Make() => new Widget();", "}")),
        ("Target.cs", Lines("using Example.NsA;", "", "namespace Example.Other;", "", "public static class Target", "{", "    public static void Run() => \"x\".Bar();", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Make" }, "Target", "Target.cs");

        Assert.That(message, Does.StartWith("Cannot move [Make] from 'Source' to 'Target'"));
        Assert.That(message, Does.Contain("Target.cs:"), "The existing call in the target file is reported");
        Assert.That(message, Does.Contain("`\"x\".Bar()`"), "The refusal quotes the existing call");
        Assert.That(message, Does.Contain("would bind to"), "The rebinding is named");
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task UnresolvableTargetType_FailsClosedWithReasonAsync()
    {
        // MoveMember cannot reach a CheckFailed result (the target is resolved first), so this drives the check directly.
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = workspace.Manager.CurrentSolution;
        var moved = await MethodSymbolAsync(solution, workspace.PathOf("Source.cs").ToString(), "Save");

        var result = await MoveMemberCallBindingCheck.CheckAsync(solution, solution, new ISymbol[] { moved }, "Missing", new[] { workspace.PathOf("Target.cs").ToString() }, keepsOldMember: false);

        Assert.That(result.FailureReason, Does.Contain("the target type 'Missing' was not found in the files the move changes"));
        Assert.That(result.Discrepancies, Is.Empty, "A failed check reports no discrepancies, it does not pass");
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task NestedTypeMove_CallersRetargetToTheNewOwner_AppliesCleanlyAsync()
    {
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public class Inner", "    {", "        public static void Save(int x) { }", "    }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Source.Inner.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Inner" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("class Inner"), "The nested type lands in Target");
        Assert.That(TextOf(result, "Caller.cs"), Does.Contain("Target.Inner.Save(1)"), "The caller's qualified path is rewritten to the new owner");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task NestedTypeMove_BareCallInOldOwnerIsQualifiedToTheNewOwner_DoesNotFallThroughToAUsingAsync()
    {
        // Source's bare Inner.Save(1) binds to its nested Inner today. Once Inner leaves Source the bare name would resolve through
        // the using to Example.Lib.Inner.Save(long); the engine avoids that by qualifying the call to the new owner, so no rebind occurs.
        var files = new (string Name, string Content)[]
        {
        ("Lib.cs", Lines("namespace Example.Lib;", "", "public class Inner", "{", "    public static void Save(long x) { }", "}")),
        ("Source.cs", Lines("using Example.Lib;", "", "namespace Example.Src;", "", "public static class Source", "{", "    public class Inner", "    {", "        public static void Save(int x) { }", "    }", "", "    public static void Run() => Inner.Save(1);", "}")),
        ("Target.cs", Lines("namespace Example.Src;", "", "public class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Inner" }, "Target", "Target.cs");

        Assert.That(TextOf(result, "Source.cs"), Does.Contain("Run() => Target.Inner.Save(1);"), "The bare call is retargeted to the nested type's new owner");
        Assert.That(TextOf(result, "Target.cs"), Does.Contain("class Inner"), "The nested type lands in Target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task NestedTypeMove_ExistingBareCallInTargetWouldBindToTheMovedType_IsRefusedAndNamedAsync()
    {
        // Target's bare Inner.Save(1) binds through the using to Example.Lib.Inner.Save(long) today. Once Source.Inner is nested in
        // Target, the nested type shadows the using-imported one, so the existing call would silently rebind to Save(int).
        var files = new (string Name, string Content)[]
        {
        ("Lib.cs", Lines("namespace Example.Lib;", "", "public class Inner", "{", "    public static void Save(long x) { }", "}")),
        ("Source.cs", Lines("namespace Example.Src;", "", "public static class Source", "{", "    public class Inner", "    {", "        public static void Save(int x) { }", "    }", "}")),
        ("Target.cs", Lines("using Example.Lib;", "", "namespace Example.Src;", "", "public class Target", "{", "    public static void Run() => Inner.Save(1);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, _) = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Source.cs", "Source", new[] { "Inner" }, "Target", "Target.cs");

        Assert.That(message, Does.StartWith("Cannot move [Inner] from 'Source' to 'Target'"));
        Assert.That(message, Does.Contain("Target.cs:"), "The existing call in the target file is reported at its original location");
        Assert.That(message, Does.Contain("would bind to"), "The rebinding is named");
        Assert.That(message, Does.Contain("`Inner` binds to 'Example.Lib.Inner' today but would bind to 'Example.Src.Target.Inner'"), "Both type bindings are named");
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task MovedTypeMissingAMemberInTheTrial_FailsClosedNamingTheMemberAsync()
    {
        // The trial's copy of Inner has no Save, so MapTypeTree cannot place the moved member. The check must refuse, never pass.
        var files = new (string Name, string Content)[]
        {
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public class Inner", "    {", "        public static void Save(int x) { }", "    }", "}")),
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var original = workspace.Manager.CurrentSolution!;
        var targetDocumentId = original.GetDocumentIdsWithFilePath(workspace.PathOf("Target.cs")).Single();
        var trial = original.WithDocumentText(targetDocumentId, Microsoft.CodeAnalysis.Text.SourceText.From(Lines("namespace Example;", "", "public class Target", "{", "    public class Inner", "    {", "    }", "}")));
        var moved = await NamedTypeSymbolAsync(original, workspace.PathOf("Source.cs"), "Inner");

        var result = await MoveMemberCallBindingCheck.CheckAsync(original, trial, new ISymbol[] { moved }, "Target", new[] { workspace.PathOf("Target.cs") }, keepsOldMember: false);

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.CheckFailed));
        Assert.That(result.FailureReason, Does.Contain("the moved type 'Inner' could not be mapped member by member"));
        Assert.That(result.FailureReason, Does.Contain("has no symbol in the target after the move"));
        Assert.That(result.Discrepancies, Is.Empty, "A failed check reports no discrepancies, it does not pass");
    }

    [Test]
    public async Task CrossProjectMove_DownstreamToUpstream_RecursiveMemberAppliesCleanlyAsync()
    {
        // Upstream holds Target; downstream (which references upstream) holds Source and its caller. The moved member's
        // binding must be re-keyed from the downstream project to the upstream one, or the check reports it as missing.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RoslynSentinelCrossProject"));
        var upstreamPath = Path.Combine(root, "Upstream", "Target.cs");
        var sourcePath = Path.Combine(root, "Downstream", "Source.cs");
        var files = new (string Name, string Content)[]
        {
        ("Target.cs", Lines("namespace Example;", "", "public class Target", "{", "}")),
        ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x)", "    {", "        if (x > 0) Save(x - 1);", "    }", "}")),
        ("Caller.cs", Lines("namespace Example;", "", "public static class Caller", "{", "    public static void Run() => Source.Save(1);", "}")),
        };
        var upstream = TestSolutionBuilder.CreateSolutionWithProject("Upstream", Path.Combine(root, "Upstream", "Upstream.csproj"), new[] { (files[0].Name, files[0].Content, upstreamPath) });
        var upstreamProject = upstream.Projects.Single();
        var downstreamId = ProjectId.CreateNewId();
        var solution = upstream.AddProject(ProjectInfo.Create(downstreamId, VersionStamp.Default, "Downstream", "Downstream", LanguageNames.CSharp)
            .WithMetadataReferences(upstreamProject.MetadataReferences)
            .WithCompilationOptions(new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(Path.Combine(root, "Downstream", "Downstream.csproj"))
            .WithDefaultNamespace("Downstream")
            .WithProjectReferences(new[] { new ProjectReference(upstreamProject.Id) }));
        foreach (var (name, content) in files.Skip(1))
        {
            solution = solution.AddDocument(DocumentId.CreateNewId(downstreamId, name), name, Microsoft.CodeAnalysis.Text.SourceText.From(content), filePath: Path.Combine(root, "Downstream", name));
        }

        using var manager = new FakeWorkspaceManager(WorkspaceAccess.ReadWrite);
        manager.SetTestSolution(solution);
        var (engine, validation) = CreateEngine(manager);

        var result = await engine.MoveMemberAsync(sourcePath, "Source", new[] { "Save(int)" }, "Target", upstreamPath, CancellationToken.None);

        Assert.That(TextOf(result, "Target.cs"), Does.Contain("Save(int x)"), "The member lands in the upstream class");
        Assert.That(TextOf(result, "Caller.cs"), Does.Contain("Target.Save(1)"), "The downstream caller binds to the upstream class");
        await AssertAppliesCleanlyAsync(manager, validation, files, result);
    }

    [Test]
    public async Task NewClassFileOutsideAnyProject_IsRefusedNamingTheProjectDirectoryAsync()
    {
        // The project lives under Proj\ while the source file sits under Src\, so a new class file next to the source is not compiled.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RoslynSentinelNewClass"));
        var sourcePath = Path.Combine(root, "Src", "Source.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Proj", Path.Combine(root, "Proj", "Proj.csproj"), new[] { ("Source.cs", Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}"), sourcePath) });
        using var manager = new FakeWorkspaceManager(WorkspaceAccess.ReadWrite);
        manager.SetTestSolution(solution);
        var (engine, _) = CreateEngine(manager);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.MoveMemberAsync(sourcePath, "Source", new[] { "Save" }, "NewClass", null, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("is not under any project's directory"), "The refusal names the cause");
        Assert.That(error.Message, Does.Contain("Create the class inside a project folder first"), "The refusal names the fix");
        Assert.That(error.Message, Does.Contain("Nothing was written."));
    }

    [Test]
    public async Task NewClassFileUnderProjectDirectory_MovesAndAppliesCleanlyAsync()
    {
        var source = Lines("namespace Example;", "", "public static class Source", "{", "    public static void Save(int x) { }", "}");
        var files = new (string Name, string Content)[] { ("Source.cs", source) };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await MoveAsync(engine, workspace, "Source.cs", "Source", new[] { "Save" }, "NewClass", "NewClass.cs");

        Assert.That(TextOf(result, "NewClass.cs"), Does.Contain("class NewClass"), "The new class is created in the new file");
        Assert.That(TextOf(result, "NewClass.cs"), Does.Contain("Save(int x)"), "The member lands in the new class");
        // The new file has no original text; an empty original is the pre-image the replay starts from.
        await AssertAppliesCleanlyAsync(workspace, validation, files.Append(("NewClass.cs", string.Empty)).ToArray(), result);
    }
}

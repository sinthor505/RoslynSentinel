using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// ChangeSignature's W20 call-binding gate end to end (ChangeSignatureCallBindingCheck wired into BasicRefactoringEngine):
/// a signature change that would silently re-point an existing call at a different overload is refused before anything is
/// written, and the changes that keep every binding (migrated calls, calls left on the wrapper, recursion, nameof, local
/// functions, lambdas, constructors) still succeed. Each refusal asserts the message names the call and both bindings and
/// that the files are unchanged.
/// </summary>
[TestFixture]
[Category("BasicRefactoringEngine")]
[Category("ChangeSignatureCallBinding")]
public class ChangeSignatureCallBindingTests
{
    private const string Eol = "\r\n";

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    private static BasicRefactoringEngine CreateEngine(InMemoryWorkspace workspace) =>
        Battery.Basic.TestEngines.BasicRefactoringEngine(workspace.Manager, NullLogger<BasicRefactoringEngine>.Instance, Battery.Basic.TestEngines.SentinelConfiguration());

    private static Task<ChangeSignatureResult> ChangeAsync(BasicRefactoringEngine engine, InMemoryWorkspace workspace, string fileName, string methodName, string? contextSnippet, ChangeSignatureOldSignature oldSignature, params SignatureParameterSpec[] parameters) =>
        engine.ChangeSignatureAsync(workspace.PathOf(fileName), methodName, parameters, contextSnippet, null, null, CancellationToken.None, oldSignature);

    private static async Task<string> RefusalMessageAsync(BasicRefactoringEngine engine, InMemoryWorkspace workspace, string fileName, string methodName, string? contextSnippet, ChangeSignatureOldSignature oldSignature, params SignatureParameterSpec[] parameters)
    {
        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await ChangeAsync(engine, workspace, fileName, methodName, contextSnippet, oldSignature, parameters));
        return error!.Message;
    }

    private static void AssertNothingWritten(InMemoryWorkspace workspace, (string Name, string Content)[] originals)
    {
        foreach (var (name, content) in originals)
        {
            Assert.That(workspace.ReadText(name), Is.EqualTo(content), $"{name} must be unchanged after a refused change");
        }
    }

    // The engine keys Changes by full document path, so the changed text of one file is found by its file name.
    private static string ChangedText(ChangeSignatureResult result, string fileName) =>
        result.Changes.Single(kv => kv.Key.ToString().EndsWith(fileName, StringComparison.Ordinal)).Value;

    // The project after the change: each original file replaced by its rewritten text when the engine changed it.
    private static List<(string Name, string Text)> AfterChange(ChangeSignatureResult result, params (string Name, string Text)[] originals)
    {
        var files = new List<(string Name, string Text)>();
        foreach (var (name, text) in originals)
        {
            var changed = result.Changes.FirstOrDefault(kv => kv.Key.ToString().EndsWith(name, StringComparison.Ordinal));
            files.Add((name, changed.Value ?? text));
        }

        return files;
    }

    // Compiles the files against the core framework assemblies, so a success is not vacuous (the check is skipped on compile errors).
    private static void AssertCompiles(IReadOnlyList<(string Name, string Text)> files)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Text, path: file.Name)).ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureCallBinding", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The changed files must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    // Saver has Save(int a, int b) (the one being reduced) and Save(int a, long c = 0); Caller.Run(Saver s) holds the given body lines.
    private static (string Name, string Content)[] OverloadFixture(params string[] bodyLines) => new[]
    {
        ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public void Save(int a, int b) { }", "    public void Save(int a, long c = 0) { }", "}")),
        ("Caller.cs", Lines(new[] { "namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s)", "    {" }.Concat(bodyLines).Concat(new[] { "    }", "}" }).ToArray())),
    };

    private static async Task<IMethodSymbol> MethodSymbolAsync(Solution solution, string path, string methodName)
    {
        var document = solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).Single()!;
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        var declaration = root!.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == methodName);
        return (IMethodSymbol)model!.GetDeclaredSymbol(declaration)!;
    }

    [Test]
    public async Task RemoveMode_UntouchedCallRebindsToReducedOverload_RefusedNamingCallAndBothBindings()
    {
        var files = OverloadFixture("        s.Save(5, 6);", "        s.Save(5);");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Saver.cs", "Save", "Save(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(message, Does.StartWith("Cannot change the signature of 'Save': 1 call site(s) would silently change which method they bind to"));
        Assert.That(message, Does.Contain("Caller.cs:8"), "The untouched call is reported at its original line");
        Assert.That(message, Does.Contain("`s.Save(5)`"), "The refusal quotes the call");
        Assert.That(message, Does.Contain("binds to 'Example.Saver.Save(int, long)' today but would bind to 'Example.Saver.Save(int)'"), "Both bindings are named");
        Assert.That(message, Does.Contain("Fix for this change:"), "The refusal ends with a fix sentence");
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task RepeatedRefusal_ProducesIdenticalMessageAndWritesNothing()
    {
        // The engine has no dryRun switch, so the dry-run/apply equivalence is checked by running the same refused change twice.
        var files = OverloadFixture("        s.Save(5, 6);", "        s.Save(5);");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var first = await RefusalMessageAsync(engine, workspace, "Saver.cs", "Save", "Save(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));
        var second = await RefusalMessageAsync(engine, workspace, "Saver.cs", "Save", "Save(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(second, Is.EqualTo(first), "A repeated refused change must produce the same refusal text");
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task RemoveMode_TwoUntouchedSitesInOneMember_BothReportedAndNamed()
    {
        var files = OverloadFixture("        s.Save(5);", "        s.Save(6);");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Saver.cs", "Save", "Save(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(message, Does.Contain("2 call site(s)"), "Both calls count toward the refusal");
        Assert.That(message, Does.Contain("Caller.cs:7"));
        Assert.That(message, Does.Contain("Caller.cs:8"));
        Assert.That(message, Does.Contain("`s.Save(5)`"));
        Assert.That(message, Does.Contain("`s.Save(6)`"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task WrapperMode_PartialMigration_LeftOnWrapperAcceptedOnOldIdMigratedOnNewId_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(string name, int id) => id + name.Length;", "    public int Run() => Save(\"x\", 1);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Use(Saver s)", "    {", "        System.Func<string, int, int> f = s.Save;", "        var r = s.Save(\"a\", 2);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.keepAsDeprecatedWrapper, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1), "The method-group reference is left on the wrapper");
        Assert.That(result.MigratedCallSites, Has.Count.EqualTo(2), "The Run call and the Caller invocation are migrated");
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(2, \"a\")"), "The migrated invocation uses the new order");
        Assert.That(ChangedText(result, "Saver.cs"), Does.Contain("[global::System.Obsolete(\"Use Save(int, string)\")]"));
        Assert.That(ChangedText(result, "Saver.cs"), Does.Contain("this.Save(id, name);"), "The wrapper's forwarding call is added code and is not flagged");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task OldSignatureAuto_MethodGroupCaller_FallsBackToWrapper_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(string name, int id) => id + name.Length;", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s) { System.Func<string, int, int> f = s.Save; }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.auto, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.keepAsDeprecatedWrapper), "A method-group caller cannot migrate, so the wrapper is kept");
        Assert.That(ChangedText(result, "Saver.cs"), Does.Contain("[global::System.Obsolete(\"Use Save(int, string)\")]"));
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task OldSignatureAuto_EveryCallerMigrates_RemovesOldSignature_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(int id, string name) => id + name.Length;", "    public int Run() => Save(1, \"x\");", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public int Use(Saver s) => s.Save(1, \"a\");", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.auto, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove), "Every caller migrates, so the old signature is removed");
        Assert.That(ChangedText(result, "Saver.cs"), Does.Not.Contain("Obsolete"), "No wrapper is kept");
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(\"a\", 1)"), "The invocation is migrated to the new order");
        AssertCompiles(AfterChange(result, files));
    }

    [TestCase(ChangeSignatureOldSignature.remove)]
    [TestCase(ChangeSignatureOldSignature.keepAsDeprecatedWrapper)]
    public async Task RecursiveSelfCall_PairsThroughMemberIdMap_Succeeds(ChangeSignatureOldSignature oldSignature)
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(int a, string b) => a > 0 ? Save(a - 1, b) : b.Length;", "    public int Run() => Save(3, \"x\");", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, oldSignature, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task RemoveMode_NameofTarget_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public void Save(int a, int b) { }", "    public string Label => nameof(Save);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s) { s.Save(1, 2); }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(1)"));
        Assert.That(ChangedText(result, "Saver.cs"), Does.Contain("nameof(Save)"));
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task WrapperMode_NameofAndMethodGroupTargets_Succeed()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(string name, int id) => id + name.Length;", "    public string Label => nameof(Save);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s)", "    {", "        System.Func<string, int, int> f = s.Save;", "        var r = s.Save(\"a\", 2);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.keepAsDeprecatedWrapper, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Saver.cs"), Does.Contain("nameof(Save)"));
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task RemoveMode_SameNameOnUnrelatedType_IsNotFlagged_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public void Save(int a, int b) { }", "}")),
            ("Other.cs", Lines("namespace Example;", "", "public class Other", "{", "    public void Save(int a, int b) { }", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s, Other o)", "    {", "        s.Save(1, 2);", "        o.Save(1, 2);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(1)"), "The Saver call is migrated");
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("o.Save(1, 2);"), "The unrelated call is untouched");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task RemoveMode_LocalFunctionAndLambdaCallers_MigratedCorrectly_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public void Save(int a, int b) { }", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s)", "    {", "        void Local() => s.Save(1, 2);", "        System.Action act = () => s.Save(3, 4);", "        Local();", "        act();", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(1)"), "The local function's call is migrated");
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(3)"), "The lambda's call is migrated");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task RemoveMode_RebindingInsideLambda_RefusedNamingCallAndBindings()
    {
        var files = OverloadFixture("        System.Action act = () => s.Save(5);");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Saver.cs", "Save", "Save(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(message, Does.Contain("Caller.cs:7"), "The lambda's call is reported at its line");
        Assert.That(message, Does.Contain("`s.Save(5)`"));
        Assert.That(message, Does.Contain("binds to 'Example.Saver.Save(int, long)' today but would bind to 'Example.Saver.Save(int)'"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task ConstructorReorder_NewCallSitesMigrated_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    public Widget(int a, string b) { }", "}")),
            ("Maker.cs", Lines("namespace Example;", "", "public static class Maker", "{", "    public static Widget Make() => new Widget(1, \"x\");", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Maker.cs"), Does.Contain("new Widget(\"x\", 1)"), "The constructor call is migrated to the new order");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task ConstructorOverloadRebinding_RefusedNamingCallAndBindings()
    {
        var files = new (string Name, string Content)[]
        {
            ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    public Widget(int a, int b) { }", "    public Widget(int a, long c = 0) { }", "}")),
            ("Maker.cs", Lines("namespace Example;", "", "public static class Maker", "{", "    public static Widget Make() => new Widget(5);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Widget.cs", "Widget", "public Widget(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(message, Does.Contain("Maker.cs:5"), "The constructor call is reported at its line");
        Assert.That(message, Does.Contain("`new Widget(5)`"), "The refusal quotes the constructor call");
        Assert.That(message, Does.Contain("binds to 'Example.Widget.Widget(int, long)' today but would bind to 'Example.Widget.Widget(int)'"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task ConstructorOptionalParameterAdded_UntouchedCallRebindsToChangedConstructor_RefusedNamingCallAndBindings()
    {
        // new Widget(5) binds Widget(long, int) today (Widget(int, int) needs two arguments). Giving Widget(int, int) an optional
        // second parameter makes it applicable and a better match for the int argument, so the untouched call would move to it.
        var files = new (string Name, string Content)[]
        {
            ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    public Widget(int a, int b) { }", "    public Widget(long a, int c = 0) { }", "}")),
            ("Maker.cs", Lines("namespace Example;", "", "public static class Maker", "{", "    public static Widget Make() => new Widget(5);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var message = await RefusalMessageAsync(engine, workspace, "Widget.cs", "Widget", "public Widget(int a, int b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0), new NewParameterSpec("d", "int", "0"));

        Assert.That(message, Does.Contain("Maker.cs:5"), "The untouched constructor call is reported at its line");
        Assert.That(message, Does.Contain("`new Widget(5)`"), "The refusal quotes the constructor call");
        Assert.That(message, Does.Contain("binds to 'Example.Widget.Widget(long, int)' today but would bind to 'Example.Widget.Widget(int, int)'"), "Both constructor bindings are named");
        Assert.That(message, Does.Contain("Nothing was written."));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task FailClosed_ChangedMemberNotFoundInTrial_ReturnsCheckFailedWithoutPathsOrStackTrace()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public void Save(int a, int b) { }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = workspace.Manager.CurrentSolution!;
        var declarationPath = workspace.PathOf("Saver.cs");
        var method = await MethodSymbolAsync(solution, declarationPath, "Save");

        // The trial is the original solution, so no member with the declared new parameter list exists and the check cannot pair the changed member.
        var result = await ChangeSignatureCallBindingCheck.CheckAsync(solution, solution, method, "Save", SyntaxFactory.ParseParameterList("(int z)"), declarationPath, false, Array.Empty<(string FilePath, int Line)>(), Array.Empty<(string FilePath, int Line)>());

        Assert.That(result.Status, Is.EqualTo(CallBindingCheckStatus.CheckFailed));
        Assert.That(result.IsRefusal, Is.True, "A check that cannot run must refuse, never pass");
        Assert.That(result.FailureReason, Does.Contain("was not found with its new parameter list"));
        Assert.That(result.FailureReason, Does.Not.Contain("\\").And.Not.Contain(".cs").And.Not.Contain("   at "), "The reason carries no path or stack trace");

        var text = CallBindingCheck.FormatRefusal(result, "change the signature of 'Save'");
        Assert.That(text, Does.StartWith("Cannot change the signature of 'Save': the call bindings could not be verified ("));
        Assert.That(text, Does.Contain("Nothing was written."));
        Assert.That(text, Does.Not.Contain("\\").And.Not.Contain(".cs"));
        AssertNothingWritten(workspace, files);
    }

    [Test]
    public async Task NameofTarget_AutoMode_NoSkippedSites_ResolvesToRemove_NameofUnchanged_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(int a, int b) => a;", "    public string Label => nameof(Save);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s) { s.Save(1, 2); }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.auto, new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(ChangedText(result, "Caller.cs"), Does.Contain("s.Save(1);"), "The call is migrated to the reduced signature");
        var saver = ChangedText(result, "Saver.cs");
        Assert.That(saver, Does.Contain("nameof(Save)"), "The nameof text is not rewritten");
        Assert.That(saver, Does.Not.Contain("Obsolete"), "Auto with no skipped sites resolves to remove, so no wrapper is kept");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task NameofTarget_WrapperMode_NameofTextUnchanged_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(int a, int b) => a;", "    public string Label => nameof(Save);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s)", "    {", "        var r = s.Save(1, 2);", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.keepAsDeprecatedWrapper, new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "0"));

        Assert.That(result.Error, Is.Null, result.Error);
        var saver = ChangedText(result, "Saver.cs");
        Assert.That(saver, Does.Contain("nameof(Save)"), "The nameof text is not rewritten");
        Assert.That(saver, Does.Contain("public string Label => nameof(Save);"), "The nameof declaration line is unchanged");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task NameofMemberAccess_AsArgumentOfAnotherCall_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save(int a, int b) => a;", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public string Run(Saver s) => Describe(nameof(s.Save), s.Save(1, 2));", "    private static string Describe(string name, int value) => name + value;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var caller = ChangedText(result, "Caller.cs");
        Assert.That(caller, Does.Contain("Describe(nameof(s.Save), s.Save(1));"), "The nameof argument is unchanged and the sibling call is migrated");
        AssertCompiles(AfterChange(result, files));
    }

    // Maker holds the four target-typed forms: a field initializer, a return, a local initializer and a method argument.
    private static (string Name, string Content)[] TargetTypedFixture(string widgetConstructor, string arguments) => new[]
    {
        ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    " + widgetConstructor, "}")),
        ("Maker.cs", Lines("namespace Example;", "", "public class Maker", "{",
            "    public Widget Field = new(" + arguments + ");",
            "    public void Use(Widget w) { }",
            "    public Widget Ret() { return new(" + arguments + "); }",
            "    public void Local() { Widget w = new(" + arguments + "); Use(w); }",
            "    public void Arg() { Use(new(" + arguments + ")); }",
            "}")),
    };

    [Test]
    public async Task TargetTypedNew_ReorderOfConstructor_RewritesEveryForm_Succeeds()
    {
        var files = TargetTypedFixture("public Widget(int a, string b) { }", "1, \"a\"");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var maker = ChangedText(result, "Maker.cs");
        Assert.That(maker, Does.Contain("public Widget Field = new(\"a\", 1);"), "The field initializer is rewritten");
        Assert.That(maker, Does.Contain("return new(\"a\", 1);"), "The return is rewritten");
        Assert.That(maker, Does.Contain("Widget w = new(\"a\", 1);"), "The local initializer is rewritten");
        Assert.That(maker, Does.Contain("Use(new(\"a\", 1));"), "The argument is rewritten");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task TargetTypedNew_AddParameterWithDefault_RewritesEveryForm_Succeeds()
    {
        var files = TargetTypedFixture("public Widget(int a, string b) { }", "1, \"a\"");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "7"));

        Assert.That(result.Error, Is.Null, result.Error);
        var maker = ChangedText(result, "Maker.cs");
        Assert.That(maker, Does.Contain("public Widget Field = new(1, \"a\", 7);"), "The field initializer carries the default");
        Assert.That(maker, Does.Contain("return new(1, \"a\", 7);"), "The return carries the default");
        Assert.That(maker, Does.Contain("Widget w = new(1, \"a\", 7);"), "The local initializer carries the default");
        Assert.That(maker, Does.Contain("Use(new(1, \"a\", 7));"), "The argument carries the default");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task TargetTypedNew_RemoveParameter_RewritesEveryForm_Succeeds()
    {
        var files = TargetTypedFixture("public Widget(int a, string b, long c) { }", "1, \"a\", 2L");
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        var maker = ChangedText(result, "Maker.cs");
        Assert.That(maker, Does.Contain("public Widget Field = new(1, \"a\");"), "The field initializer drops the removed argument");
        Assert.That(maker, Does.Contain("return new(1, \"a\");"), "The return drops the removed argument");
        Assert.That(maker, Does.Contain("Widget w = new(1, \"a\");"), "The local initializer drops the removed argument");
        Assert.That(maker, Does.Contain("Use(new(1, \"a\"));"), "The argument drops the removed argument");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task NestedNewInsideCall_ConstructorReorder_RewritesInnerNewOnly_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    public Widget(int a, string b) { }", "}")),
            ("Maker.cs", Lines("namespace Example;", "", "public class Maker", "{", "    public static void Use(int x, Widget w) { }", "    public void Run() { Use(5, new Widget(1, \"a\")); }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Maker.cs"), Does.Contain("Use(5, new Widget(\"a\", 1));"), "Only the inner new is rewritten; the outer call keeps its arguments");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task BaseInitializer_ReorderOfBaseConstructor_RewritesEveryBaseCall_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Types.cs", Lines("namespace Example;", "", "public class Base", "{", "    public Base(int a, string b) { }", "}", "", "public class Derived : Base", "{", "    public Derived() : base(1, \"a\") { }", "    public Derived(int x) : base(x, \"b\") { }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Types.cs", "Base", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var types = ChangedText(result, "Types.cs");
        Assert.That(types, Does.Contain(": base(\"a\", 1) { }"), "The parameterless base call is rewritten");
        Assert.That(types, Does.Contain(": base(\"b\", x) { }"), "The base call with an argument is rewritten");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task BaseInitializer_RemoveOfBaseConstructorParameter_RewritesEveryBaseCall_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Types.cs", Lines("namespace Example;", "", "public class Base", "{", "    public Base(int a, string b, long c) { }", "}", "", "public class Derived : Base", "{", "    public Derived() : base(1, \"a\", 2L) { }", "    public Derived(int x) : base(x, \"b\", 3L) { }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Types.cs", "Base", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        var types = ChangedText(result, "Types.cs");
        Assert.That(types, Does.Contain(": base(1, \"a\") { }"), "The parameterless base call drops the removed argument");
        Assert.That(types, Does.Contain(": base(x, \"b\") { }"), "The base call with an argument drops the removed argument");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task ThisInitializer_RemoveOfChainedConstructorParameter_RewritesThisCall_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Types.cs", Lines("namespace Example;", "", "public class Derived", "{", "    public Derived() : this(1, \"a\", 2L) { }", "    public Derived(int x, string y, long z) { }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Types.cs", "Derived", "public Derived(int x, string y, long z)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(ChangedText(result, "Types.cs"), Does.Contain(": this(1, \"a\") { }"), "The this initializer drops the removed argument");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task PrimaryConstructorBaseCall_ReorderOfBaseConstructor_RewritesBaseArgument_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Types.cs", Lines("namespace Example;", "", "public class B", "{", "    public B(int a, string b) { }", "}", "", "public class D(int x) : B(x, \"b\") { }")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Types.cs", "B", null, ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var types = ChangedText(result, "Types.cs");
        Assert.That(types, Does.Contain("public class D(int x) : B(\"b\", x) { }"), "The primary-constructor base call is rewritten to the new order");
        AssertCompiles(AfterChange(result, files));
    }

    [Test]
    public async Task MethodGroupArguments_WrapperMode_NotRewrittenAsCalls_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Saver.cs", Lines("namespace Example;", "", "public class Saver", "{", "    public int Save() => 0;", "    public int Save(int a, int b) => a;", "    public System.Func<int> Getter() => new System.Func<int>(Save);", "}")),
            ("Caller.cs", Lines("namespace Example;", "", "public class Caller", "{", "    public void Run(Saver s)", "    {", "        Take(s.Save);", "    }", "", "    public void Take(System.Func<int, int, int> f) { }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Saver.cs", "Save", "public int Save(int a, int b)", ChangeSignatureOldSignature.keepAsDeprecatedWrapper, new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "0"));

        Assert.That(result.Error, Is.Null, result.Error);
        var after = AfterChange(result, files);
        Assert.That(after.Single(f => f.Name == "Caller.cs").Text, Is.EqualTo(files[1].Content), "Method groups are not call sites: the caller text is identical");
        AssertCompiles(after);
    }

    [Test]
    public async Task TargetTypedNew_ToDifferentConstructor_IsUntouched_Succeeds()
    {
        var files = new (string Name, string Content)[]
        {
            ("Widget.cs", Lines("namespace Example;", "", "public class Widget", "{", "    public Widget(int a, string b) { }", "    public Widget(long z) { }", "}")),
            ("Maker.cs", Lines("namespace Example;", "", "public class Maker", "{", "    public Widget Make()", "    {", "        Widget other = new(5L);", "        return new(1, \"a\");", "    }", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var engine = CreateEngine(workspace);

        var result = await ChangeAsync(engine, workspace, "Widget.cs", "Widget", "public Widget(int a, string b)", ChangeSignatureOldSignature.remove, new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, result.Error);
        var maker = ChangedText(result, "Maker.cs");
        Assert.That(maker, Does.Contain("Widget other = new(5L);"), "The target-typed new of the other constructor is untouched");
        Assert.That(maker, Does.Contain("return new(\"a\", 1);"), "The changed constructor's target-typed new is rewritten");
        AssertCompiles(AfterChange(result, files));
    }
}

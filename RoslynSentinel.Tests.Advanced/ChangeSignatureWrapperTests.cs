using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature with oldSignature keepAsDeprecatedWrapper: the rewritten code must compile, and the generated
/// [Obsolete] wrapper's call must bind to the NEW member (not to itself or another overload).
/// </summary>
[TestFixture]
public class ChangeSignatureWrapperTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SentinelConfiguration _config = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = Battery.Basic.TestEngines.SentinelConfiguration();
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, _config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // TODO: no wrapper test covers an 'in' parameter (ref/out/params are covered).
    // TODO: question for the engine owner - NewParameterSpec always emits a default, so a defaulted parameter in a non-trailing position may produce invalid C# (CS1737); unverified, not a confirmed bug.
    private Task<ChangeSignatureResult> KeepWrapper(string filePath, string methodName, params SignatureParameterSpec[] parameters) =>
    _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters, oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);

    // The project after the change: each original file replaced by its rewritten text when the engine changed it.
    private static List<(string Name, string Text)> AfterChange(IReadOnlyDictionary<FilePathWrapper, string> changes, params (string Name, string Text)[] originals)
    {
        var files = new List<(string Name, string Text)>();
        foreach (var (name, text) in originals)
        {
            var changed = changes.FirstOrDefault(kv => kv.Key.ToString().Contains(name));
            files.Add((name, changed.Value ?? text));
        }

        return files;
    }

    // Compiles the files against the core framework assemblies (same directory as System.Private.CoreLib).
    private static CSharpCompilation CompileFiles(IReadOnlyList<(string Name, string Text)> files)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Text, path: file.Name)).ToList();
        return CSharpCompilation.Create("ChangeSignatureWrapperProof", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static void AssertCompiles(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    private static bool HasObsoleteAttribute(MethodDeclarationSyntax method) =>
    method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString().Contains("Obsolete"));

    // "ref int", "out int", "params int[]", "string": passing mode and declared type, as the binder sees them.
    private static string DescribeParameter(IParameterSymbol parameter)
    {
        var mode = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => string.Empty,
        };
        return (parameter.IsParams ? "params " : string.Empty) + mode + parameter.Type.ToDisplayString();
    }

    // Proves the generated wrapper binds to the new member: its call (inside the [Obsolete] method) resolves to the new
    // member's definition, never to the wrapper itself, and the bound parameters are the NEW signature.
    private static void AssertWrapperBindsToNewMember(CSharpCompilation compilation, string fileName, string methodName, params string[] expectedNewParameters)
    {
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath == fileName);
        var semanticModel = compilation.GetSemanticModel(tree);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == methodName).ToList();
        var wrapper = methods.Single(HasObsoleteAttribute);
        var newMember = methods.Single(m => !HasObsoleteAttribute(m));

        var call = wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var bound = semanticModel.GetSymbolInfo(call).Symbol as IMethodSymbol;
        Assert.That(bound, Is.Not.Null, "The wrapper's call must bind to exactly one method");

        var wrapperSymbol = semanticModel.GetDeclaredSymbol(wrapper);
        var newSymbol = semanticModel.GetDeclaredSymbol(newMember);
        Assert.That(bound!.OriginalDefinition, Is.Not.SameAs(wrapperSymbol), "The wrapper must not call itself");
        Assert.That(SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, newSymbol!.OriginalDefinition), Is.True, "The wrapper's call must bind to the new member");
        Assert.That(SymbolEqualityComparer.Default.Equals(bound.ContainingType.OriginalDefinition, newSymbol.ContainingType.OriginalDefinition), Is.True, "Same containing type");
        Assert.That(bound.Parameters.Select(DescribeParameter).ToList(), Is.EqualTo(expectedNewParameters), "Bound parameters must equal the NEW signature");
    }

    private void SetFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_Reorder_MigratesCallersAndKeepsMethodGroupOnWrapper()
    {
        const string saverSource = @"public class Saver
{
    public int Save(string name, int id) => id + name.Length;
    public int Run() => Save(""x"", 1);
}";
        const string callerSource = @"public class Caller
{
    public void Use(Saver s)
    {
        System.Func<string, int, int> f = s.Save;
        var r = s.Save(""a"", 2);
    }
}";
        SetFiles(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        var result = await KeepWrapper("Saver.cs", "Save", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null, "Wrapper mode must not refuse a reorder with a method-group caller");
        Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1), "The method-group reference stays on the wrapper");
        Assert.That(result.SkippedCallSites[0].Reason, Does.Contain("Reference is not a simple invocation"));
        Assert.That(result.MigratedCallSites, Has.Count.EqualTo(2), "The two invocations are migrated to the new order");
        Assert.That(result.WrapperSignature, Is.EqualTo("Save(string, int)"));
        Assert.That(result.ReplacementSignature, Is.EqualTo("Save(int, string)"));

        var saverAfter = result.Changes["Saver.cs"];
        Assert.That(saverAfter, Does.Contain("[global::System.Obsolete(\"Use Save(int, string)\")]"));
        Assert.That(saverAfter, Does.Contain("this.Save(id, name);"));
        Assert.That(result.Changes["Caller.cs"], Does.Contain("s.Save(2, \"a\")"), "The invocation uses the new argument order");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Saver.cs", saverSource), ("Caller.cs", callerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Saver.cs", "Save", "int", "string");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_NewParameterWithDefault_WrapperPassesDefaultLiteral()
    {
        const string saverSource = @"public class Saver
{
    public int Save(string name, int id) => id + name.Length;
    public int Run() => Save(""x"", 1);
}";
        SetFiles(("Saver.cs", saverSource));
        var result = await KeepWrapper("Saver.cs", "Save", new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("scale", "int", "2"));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.ReplacementSignature, Is.EqualTo("Save(int, string, int)"));
        var saverAfter = result.Changes["Saver.cs"];
        Assert.That(saverAfter, Does.Contain("int id, string name, int scale)"), "The new member declares the new parameter required");
        Assert.That(saverAfter, Does.Not.Contain("scale = 2"), "The default is only the value passed, never a declared default (it would compete with the wrapper)");
        Assert.That(saverAfter, Does.Contain("this.Save(id, name, 2);"), "The wrapper passes the default literal explicitly");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Saver.cs", saverSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Saver.cs", "Save", "int", "string", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_OptionalParameter_PassesDefaultExplicitly()
    {
        // Wrapper mode declares every new parameter required, so the new member's c has no default. The wrapper must pass 0
        // explicitly, and the migrated caller gets the 0 added.
        const string calcSource = @"public class Calc
{
    public int Add(int a, int b) => a + b;
    public int Run() => Add(1, 2);
}";
        const string callerSource = @"public class Caller
{
    public int Use(Calc calc) => calc.Add(3, 4);
}";
        SetFiles(("Calc.cs", calcSource), ("Caller.cs", callerSource));
        var result = await KeepWrapper("Calc.cs", "Add", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "0"));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var calcAfter = result.Changes["Calc.cs"];
        Assert.That(calcAfter, Does.Contain("int a, int b, int c)"), "The new member declares the new parameter required");
        Assert.That(calcAfter, Does.Not.Contain("c = 0"), "No declared default that would compete with the wrapper");
        Assert.That(calcAfter, Does.Contain("this.Add(a, b, 0);"), "The wrapper passes the default explicitly");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("calc.Add(3, 4, 0)"), "The migrated caller passes the default explicitly");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Calc.cs", calcSource), ("Caller.cs", callerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Calc.cs", "Add", "int", "int", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_RemoveParameter_WrapperDropsTheArgument()
    {
        const string calcSource = @"public class Calc
{
    public int Add(int a, int b, int c) => a + b;
    public int Run() => Add(1, 2, 3);
}";
        SetFiles(("Calc.cs", calcSource));
        var result = await KeepWrapper("Calc.cs", "Add", new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.ReplacementSignature, Is.EqualTo("Add(int, int)"));
        var calcAfter = result.Changes["Calc.cs"];
        Assert.That(calcAfter, Does.Contain("Obsolete(\"Use Add(int, int)\")"));
        Assert.That(calcAfter, Does.Contain("this.Add(a, b);"), "The wrapper forwards only the surviving arguments");
        Assert.That(calcAfter, Does.Contain("Add(1, 2);"), "The migrated call drops the removed argument");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Calc.cs", calcSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Calc.cs", "Add", "int", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_RefParameter_KeepsRefOnWrapperAndNewMember()
    {
        const string counterSource = @"public class Counter
{
    public void Bump(ref int counter, string label) { counter++; }
    public void Run()
    {
        int n = 0;
        Bump(ref n, ""a"");
    }
}";
        SetFiles(("Counter.cs", counterSource));
        var result = await KeepWrapper("Counter.cs", "Bump", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.ReplacementSignature, Is.EqualTo("Bump(string, ref int)"));
        var counterAfter = result.Changes["Counter.cs"];
        Assert.That(counterAfter, Does.Contain("Bump(string label, ref int counter)"), "The new member takes the ref parameter second");
        Assert.That(counterAfter, Does.Contain("this.Bump(label, ref counter);"), "The wrapper forwards the ref argument");
        Assert.That(counterAfter, Does.Contain("Bump(\"a\", ref n);"), "The migrated call keeps its ref argument");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Counter.cs", counterSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Counter.cs", "Bump", "string", "ref int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_OutParameter_KeepsOutOnWrapperAndNewMember()
    {
        const string readerSource = @"public class Reader
{
    public bool TryRead(string key, out int value) { value = key.Length; return true; }
    public int Run() => TryRead(""abc"", out var got) ? got : 0;
}";
        SetFiles(("Reader.cs", readerSource));
        var result = await KeepWrapper("Reader.cs", "TryRead", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.ReplacementSignature, Is.EqualTo("TryRead(out int, string)"));
        var readerAfter = result.Changes["Reader.cs"];
        Assert.That(readerAfter, Does.Contain("this.TryRead(out value, key);"), "The wrapper forwards the out argument");
        Assert.That(readerAfter, Does.Contain("TryRead(out var got, \"abc\")"), "The migrated call keeps its out argument");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Reader.cs", readerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Reader.cs", "TryRead", "out int", "string");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_ParamsParameter_ExpandedCallMigratesWithEveryArgument()
    {
        const string totalsSource = @"public class Totals
{
    public int Total(params int[] values) => values.Length;
    public int Run() => Total(1, 2, 3);
}";
        const string callerSource = @"public class Caller
{
    public int Use(Totals t) => t.Total(new[] { 4, 5 });
}";
        SetFiles(("Totals.cs", totalsSource), ("Caller.cs", callerSource));
        var result = await KeepWrapper("Totals.cs", "Total", new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty, "The expanded params call is migrated, not left on the wrapper");
        Assert.That(result.MigratedCallSites, Has.Count.EqualTo(2));
        var totalsAfter = result.Changes["Totals.cs"];
        Assert.That(totalsAfter, Does.Contain("this.Total(\"x\", values);"), "The wrapper passes the default for the new leading parameter");
        Assert.That(totalsAfter, Does.Contain("Total(\"x\", 1, 2, 3)"), "The expanded call keeps all three params arguments after the new parameter");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("t.Total(\"x\", new[] { 4, 5 })"), "The array-form call is migrated");
        var expanded = result.MigratedCallSites.Single(site => site.OldText.Contains("Total(1, 2, 3)"));
        Assert.That(expanded.NewText, Does.Contain("Total(\"x\", 1, 2, 3)"), "The reported migrated call shows every argument");
        Assert.That(expanded.Line, Is.EqualTo(4));

        var compilation = CompileFiles(AfterChange(result.Changes, ("Totals.cs", totalsSource), ("Caller.cs", callerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Totals.cs", "Total", "string", "params int[]");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_GenericMethod_MirrorsConstraintsAndBindsToNewMember()
    {
        const string pickerSource = @"public class Picker
{
    public T Pick<T>(T first, T second) where T : class => first ?? second;
    public string Run() => Pick(""a"", ""b"");
}";
        SetFiles(("Picker.cs", pickerSource));
        var result = await KeepWrapper("Picker.cs", "Pick", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("id", "int", "0"));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var pickerAfter = result.Changes["Picker.cs"];
        Assert.That(pickerAfter, Does.Contain("where T : class"), "The wrapper mirrors the constraint");
        Assert.That(pickerAfter, Does.Contain("this.Pick<T>(first, second, 0);"), "The wrapper passes explicit type arguments and the default");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Picker.cs", pickerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Picker.cs", "Pick", "T", "T", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_StaticMethod_WrapperCallsThroughTypeName()
    {
        const string calcSource = @"public static class Calc
{
    public static int Add(int a, int b) => a + b;
}";
        const string consumerSource = @"public class Consumer
{
    public int Run() => Calc.Add(1, 2);
}";
        SetFiles(("Calc.cs", calcSource), ("Consumer.cs", consumerSource));
        var result = await KeepWrapper("Calc.cs", "Add", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", "0"));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var calcAfter = result.Changes["Calc.cs"];
        Assert.That(calcAfter, Does.Contain("public static int Add(int a, int b) => Calc.Add(a, b, 0);"), "A static wrapper calls through the type name, not this");
        Assert.That(result.Changes["Consumer.cs"], Does.Contain("Calc.Add(1, 2, 0)"), "The static call site is migrated");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Calc.cs", calcSource), ("Consumer.cs", consumerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Calc.cs", "Add", "int", "int", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_NoSkippedCallSite_WrapperIsStillKept()
    {
        const string loneSource = @"public class Lone
{
    public int Add(int a, string b) => a + b.Length;
}";
        SetFiles(("Lone.cs", loneSource));
        var result = await KeepWrapper("Lone.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Null);
        Assert.That(result.SkippedCallSites, Is.Empty, "No call site is skipped");
        Assert.That(result.MigratedCallSites, Is.Empty, "There are no call sites to migrate");
        Assert.That(result.WrapperSignature, Is.EqualTo("Add(int, string)"));
        Assert.That(result.Changes["Lone.cs"], Does.Contain("[global::System.Obsolete(\"Use Add(string, int)\")]"), "The wrapper is still written");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Lone.cs", loneSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Lone.cs", "Add", "string", "int");
    }

    private static void AssertRefused(ChangeSignatureResult result, string reason)
    {
        Assert.That(result.Error, Does.Contain("oldSignature keepAsDeprecatedWrapper"), "The refusal must name the mode");
        Assert.That(result.Error, Does.Contain(reason), "The refusal must name the reason");
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_IdentityChange_IsRefusedAsSignatureCollision()
    {
        // Keeping (int a, string b) in place: the old and new signature are the same overload, so the wrapper would collide.
        SetFiles(("Pair.cs", "public class Pair { public void Set(int a, string b) {} }"));
        var result = await KeepWrapper("Pair.cs", "Set", new ExistingParameterSpec(0), new ExistingParameterSpec(1));

        AssertRefused(result, "cannot keep 'Set'");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_SwapOfSameTypedParameters_IsRefusedAsSignatureCollision()
    {
        // Swapping two int parameters changes names and positions only: the type list is the same, so the overloads collide.
        SetFiles(("Pair.cs", "public class Pair { public void Set(int a, int b) {} }"));
        var result = await KeepWrapper("Pair.cs", "Set", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        AssertRefused(result, "cannot keep 'Set'");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_NewParameterWithoutDefault_IsRefused()
    {
        SetFiles(("Pair.cs", "public class Pair { public void Set(int a, int b) {} }"));
        var result = await KeepWrapper("Pair.cs", "Set", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("c", "int", string.Empty));

        AssertRefused(result, "needs a defaultValue for every new parameter");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_VirtualMethod_IsRefused()
    {
        SetFiles(("Base.cs", "public class Base { public virtual int Add(int a, int b) => a + b; }"));
        var result = await KeepWrapper("Base.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        AssertRefused(result, "cannot wrap 'Add': it is virtual, abstract, override or an interface member");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_OverrideMethod_IsRefused()
    {
        SetFiles(
            ("Base.cs", "public class Base { public virtual int Add(int a, int b) => a + b; }"),
            ("Derived.cs", "public class Derived : Base { public override int Add(int a, int b) => a - b; }"));
        var result = await KeepWrapper("Derived.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        AssertRefused(result, "cannot wrap 'Add': it is virtual, abstract, override or an interface member");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_AbstractMethod_IsRefused()
    {
        SetFiles(("Shape.cs", "public abstract class Shape { public abstract int Add(int a, int b); }"));
        var result = await KeepWrapper("Shape.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        AssertRefused(result, "cannot wrap 'Add': it is virtual, abstract, override or an interface member");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_Constructor_IsRefused()
    {
        SetFiles(("Box.cs", "public class Box { public Box(int a, int b) { } }"));
        var result = await KeepWrapper("Box.cs", "Box", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        AssertRefused(result, "does not support constructors yet");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_InterfaceMemberWithImplementer_IsRefusedOutright()
    {
        SetFiles(
            ("IGreeter.cs", "public interface IGreeter { int Add(int a, int b); }"),
            ("Greeter.cs", "public class Greeter : IGreeter { public int Add(int a, int b) => a + b; }"));
        var result = await KeepWrapper("IGreeter.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Error, Does.Contain("refuses this outright"));
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_RemoveMode_MethodGroupCaller_IsRefusedWhenOldSignatureOmitted()
    {
        SetFiles(
            ("Saver.cs", "public class Saver { public int Save(int id, string name) => id; }"),
            ("Caller.cs", "public class Caller { public void Run(Saver s) { System.Func<int, string, int> f = s.Save; } }"));
        var result = await _refactoringEngine.ChangeSignatureAsync("Saver.cs", "Save", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) }, oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Error, Does.Contain("Could not rewrite 1 call site(s)"));
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_RemoveModeExplicit_MethodGroupCaller_IsRefused()
    {
        SetFiles(
            ("Saver.cs", "public class Saver { public int Save(int id, string name) => id; }"),
            ("Caller.cs", "public class Caller { public void Run(Saver s) { System.Func<int, string, int> f = s.Save; } }"));
        var result = await _refactoringEngine.ChangeSignatureAsync("Saver.cs", "Save", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) }, oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.Error, Does.Contain("Could not rewrite 1 call site(s)"));
        Assert.That(result.Changes, Is.Empty);
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_RemoveMode_CleanReorder_LeavesWrapperFieldsNull()
    {
        SetFiles(
            ("Saver.cs", "public class Saver { public int Save(int id, string name) => id; }"),
            ("Caller.cs", "public class Caller { public void Run(Saver s) { s.Save(1, \"a\"); } }"));
        var result = await _refactoringEngine.ChangeSignatureAsync("Saver.cs", "Save", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) }, oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.WrapperSignature, Is.Null);
        Assert.That(result.ReplacementSignature, Is.Null);
        Assert.That(result.MigratedCallSites, Is.Null);
    }

    private Task<ChangeSignatureResult> Auto(string filePath, string methodName, params SignatureParameterSpec[] parameters) =>
    _refactoringEngine.ChangeSignatureAsync(filePath, methodName, parameters, oldSignature: ChangeSignatureOldSignature.auto);

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_Auto_AllCallersMigrate_RemovesOldSignatureAndLeavesWrapperFieldsNull()
    {
        const string saverSource = @"public class Saver
{
    public int Save(int id, string name) => id + name.Length;
    public int Run() => Save(1, ""x"");
}";
        const string callerSource = @"public class Caller
{
    public int Use(Saver s) => s.Save(1, ""a"");
}";
        SetFiles(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        // No oldSignature argument: auto is the default.
        var result = await _refactoringEngine.ChangeSignatureAsync("Saver.cs", "Save", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove), "Every caller migrates, so the old signature is removed");
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.WrapperSignature, Is.Null);
        Assert.That(result.ReplacementSignature, Is.Null);
        Assert.That(result.MigratedCallSites, Is.Null);
        Assert.That(result.Changes["Saver.cs"], Does.Not.Contain("Obsolete"), "No wrapper is kept");
        Assert.That(result.Changes["Saver.cs"], Does.Contain("Save(string name, int id)"), "The member has the new order");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("s.Save(\"a\", 1)"), "The invocation is migrated to the new order");

        AssertCompiles(CompileFiles(AfterChange(result.Changes, ("Saver.cs", saverSource), ("Caller.cs", callerSource))));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_Auto_MethodGroupCaller_KeepsWrapperAndListsSkippedCallSite()
    {
        const string saverSource = @"public class Saver
{
    public int Save(string name, int id) => id + name.Length;
}";
        const string callerSource = @"public class Caller { public void Run(Saver s) { System.Func<string, int, int> f = s.Save; } }";
        SetFiles(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        var result = await Auto("Saver.cs", "Save", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.keepAsDeprecatedWrapper), "A method-group caller cannot migrate, so the eligible method keeps a wrapper");
        Assert.That(result.WrapperSignature, Is.EqualTo("Save(string, int)"));
        Assert.That(result.ReplacementSignature, Is.EqualTo("Save(int, string)"));
        Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1));
        Assert.That(result.SkippedCallSites[0].FilePath.ToString(), Does.EndWith("Caller.cs"), "The skipped call site names its file");
        Assert.That(result.SkippedCallSites[0].LineNumber, Is.EqualTo(1), "The skipped call site names its line");
        Assert.That(result.Changes["Saver.cs"], Does.Contain("[global::System.Obsolete(\"Use Save(int, string)\")]"), "The wrapper is kept");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Saver.cs", saverSource), ("Caller.cs", callerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Saver.cs", "Save", "int", "string");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_Auto_VirtualMethodWithUnmigratedCaller_IsRefusedAndNamesCallerAndReason()
    {
        SetFiles(
            ("Base.cs", "public class Base { public virtual int Add(int a, int b) => a + b; }"),
            ("Caller.cs", "public class Caller { public void Run(Base b) { System.Func<int, int, int> f = b.Add; } }"));
        var result = await Auto("Base.cs", "Add", new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.IsError, Is.True, "Auto must refuse: the caller cannot migrate and no wrapper is possible");
        Assert.That(result.Error, Does.Contain("Nothing was changed. Could not rewrite 1 call site(s)"));
        Assert.That(result.Error, Does.Contain("Caller.cs:1"), "The refusal names the caller file and line");
        Assert.That(result.Error, Does.Contain("cannot wrap 'Add': it is virtual, abstract, override or an interface member"), "The refusal names why a wrapper is not possible");
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
        Assert.That(result.OldSignatureOutcome, Is.Null, "No outcome on an error");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_Auto_GenericMethod_MigratesEveryCallerAndCompiles()
    {
        const string pickerSource = @"public class Picker
{
    public T Pick<T>(T first, T second) where T : class => first ?? second;
    public string Run() => Pick(""a"", ""b"");
}";
        const string consumerSource = @"public class Consumer
{
    public string Use(Picker p) => p.Pick<string>(""a"", ""b"");
}";
        SetFiles(("Picker.cs", pickerSource), ("Consumer.cs", consumerSource));
        var result = await Auto("Picker.cs", "Pick", new ExistingParameterSpec(0), new ExistingParameterSpec(1), new NewParameterSpec("id", "int", "0"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove));
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.Changes["Picker.cs"], Does.Contain("where T : class"), "The constraint is kept");
        Assert.That(result.Changes["Picker.cs"], Does.Contain("Pick(\"a\", \"b\", 0)"), "The in-file call passes the default");
        Assert.That(result.Changes["Consumer.cs"], Does.Contain("p.Pick<string>(\"a\", \"b\", 0)"), "The cross-file generic call is migrated");

        AssertCompiles(CompileFiles(AfterChange(result.Changes, ("Picker.cs", pickerSource), ("Consumer.cs", consumerSource))));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_KeepAsDeprecatedWrapper_NewParameterWithDefault_IsRequiredOnMemberAndOldShapedCallerStaysOnWrapper()
    {
        const string saverSource = @"public class Saver
{
    public int Save(string name, int id) => id + name.Length;
}";
        const string callerSource = @"public class Caller { public void Run(Saver s) { System.Func<string, int, int> f = s.Save; } }";
        SetFiles(("Saver.cs", saverSource), ("Caller.cs", callerSource));
        var result = await KeepWrapper("Saver.cs", "Save", new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("scale", "int", "2"));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.keepAsDeprecatedWrapper));
        Assert.That(result.SkippedCallSites, Has.Count.EqualTo(1), "The method-group caller is left on the wrapper");
        Assert.That(result.Changes["Saver.cs"], Does.Contain("int id, string name, int scale)"), "The new member declares the added parameter");
        Assert.That(result.Changes["Saver.cs"], Does.Not.Contain("scale ="), "The new member has no default: a defaulted overload would compete with the wrapper");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("System.Func<string, int, int> f = s.Save;"), "The old-shaped caller text is unchanged");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Saver.cs", saverSource), ("Caller.cs", callerSource)));
        AssertCompiles(compilation);
        AssertWrapperBindsToNewMember(compilation, "Saver.cs", "Save", "int", "string", "int");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_Auto_ParamsExpandedCall_MigratesWithEveryArgumentAndCompiles()
    {
        const string totalsSource = @"public class Totals
{
    public int Total(params int[] values) => values.Length;
    public int Run() => Total(1, 2, 3);
}";
        const string callerSource = @"public class Caller
{
    public int Use(Totals t) => t.Total(new[] { 4, 5 });
}";
        SetFiles(("Totals.cs", totalsSource), ("Caller.cs", callerSource));
        var result = await Auto("Totals.cs", "Total", new NewParameterSpec("label", "string", "\"x\""), new ExistingParameterSpec(0));

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove), "Every call site migrates, so the old signature is removed");
        Assert.That(result.SkippedCallSites, Is.Empty);
        Assert.That(result.Changes["Totals.cs"], Does.Contain("Total(\"x\", 1, 2, 3)"), "The expanded call keeps all three params arguments after the new parameter");
        Assert.That(result.Changes["Totals.cs"], Does.Not.Contain("Obsolete"), "No wrapper is kept");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("t.Total(\"x\", new[] { 4, 5 })"), "The array-form call is migrated");

        AssertCompiles(CompileFiles(AfterChange(result.Changes, ("Totals.cs", totalsSource), ("Caller.cs", callerSource))));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task ChangeSignature_RemoveMode_NonTrailingNewParameter_IsRequiredAndCompilesWithoutCS1737()
    {
        const string pairSource = @"public class Pair
{
    public int M(int a, int b) => a + b;
    public int Run() => M(1, 2);
}";
        const string callerSource = @"public class Caller
{
    public int Use(Pair p) => p.M(3, 4);
}";
        SetFiles(("Pair.cs", pairSource), ("Caller.cs", callerSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Pair.cs", "M", new SignatureParameterSpec[] { new ExistingParameterSpec(0), new NewParameterSpec("c", "int", "0"), new ExistingParameterSpec(1) }, oldSignature: ChangeSignatureOldSignature.remove);

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove));
        Assert.That(result.Changes["Pair.cs"], Does.Contain("int a, int c, int b)"), "The added parameter is declared required, so the later required parameter is legal");
        Assert.That(result.Changes["Pair.cs"], Does.Not.Contain("int c = 0"), "No default on the added parameter in front of a required one");
        Assert.That(result.Changes["Pair.cs"], Does.Contain("M(1, 0, 2)"), "The declaration's call site receives the default in its new position");
        Assert.That(result.Changes["Caller.cs"], Does.Contain("p.M(3, 0, 4)"), "The cross-file call receives the default in its new position");

        var compilation = CompileFiles(AfterChange(result.Changes, ("Pair.cs", pairSource), ("Caller.cs", callerSource)));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Id == "CS1737"), Is.Empty, "An optional parameter before a required one is CS1737");
        AssertCompiles(compilation);
    }
}

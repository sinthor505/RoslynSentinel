using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// ChangeSignature on a constructor (target = the class name) with oldSignature keepAsDeprecatedWrapper or auto: the declaration
/// is rewritten, an [Obsolete] wrapper keeps the old constructor shape and forwards to the new one, call sites are migrated or
/// left on the wrapper, and constructors that cannot carry a wrapper are refused with a named reason.
/// </summary>
[TestFixture]
public class ChangeSignatureConstructorWrapperTests
{
    // Lines: 1 class, 2 brace, 3 Flag, 4 constructor, 5 Make (new Widget(1, "x")), 6 Target (new(2, "y")), 7 Inner, 8 brace.
    private const string WidgetSource = """
        class Widget
        {
            const bool Flag = true;
            public Widget(int a, string b) { }
            public static Widget Make() => new Widget(1, "x");
            public static Widget Target() { Widget w = new(2, "y"); return w; }
            public void Inner() { var w = new Widget(3, "z"); }
        }
        """;

    // Line 3 is the foreign call. Other cannot see Widget's private Flag, so the new parameter's default does not bind there.
    private const string OtherSource = """
        class Other
        {
            void Go() { var w = new Widget(1, "x"); }
        }
        """;

    // Line 3 is the private constructor; Make is the in-class call.
    private const string GadgetSource = """
        class Gadget
        {
            private Gadget(int a, string b = "q") { }
            public static Gadget Make() => new Gadget(1, "x");
        }
        """;

    // Lines: 1 partial class, 2 brace, 3 Node(int), 4 Node(int, string) with a this(a) initializer, 5 brace.
    private const string NodeSource = """
        public partial class Node
        {
            public Node(int a) { }
            public Node(int a, string b) : this(a) { }
        }
        """;

    // Line 3 is the derived constructor's base(...) initializer, a call that binds to Node(int, string).
    private const string DerivedSource = """
        public class Derived : Node
        {
            public Derived() : base(1, "s") { }
        }
        """;

    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        _refactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    private Task<ChangeSignatureResult> ChangeConstructor(string filePath, string className, ChangeSignatureOldSignature oldSignature, params SignatureParameterSpec[] parameters) =>
        _refactoringEngine.ChangeSignatureAsync(filePath, className, parameters, oldSignature: oldSignature);

    // The project after the change: each original file replaced by its rewritten text when the engine changed it.
    private static (string Name, string Text)[] AfterChange(IReadOnlyDictionary<FilePathWrapper, string> changes, params (string Name, string Text)[] originals) =>
        originals.Select(file => (file.Name, changes.FirstOrDefault(kv => kv.Key.ToString().Contains(file.Name)).Value ?? file.Text)).ToArray();

    // Compiles the files against the core framework assemblies (same directory as System.Private.CoreLib).
    private static void AssertCompiles(params (string Name, string Text)[] files)
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll" }
            .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(frameworkDir, assembly)))
            .ToList();
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Text, path: file.Name)).ToList();
        var compilation = CSharpCompilation.Create("ChangeSignatureConstructorWrapperProof", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.That(errors, Is.Empty, "The rewritten code must compile with zero errors: " + string.Join(Environment.NewLine, errors));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_CtorReorderAndAddParameter_RewritesDeclarationWrapsAndMigratesEverySite()
    {
        SetFiles(("Widget.cs", WidgetSource));
        var result = await ChangeConstructor("Widget.cs", "Widget", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("c", "bool", "Flag"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.SkippedCallSites, Is.Empty);
        var after = result.Changes["Widget.cs"];
        Assert.That(after, Does.Contain("public Widget(string b, int a, bool c)"), "The declaration takes the new signature and keeps its body");
        Assert.That(after, Does.Contain("[global::System.Obsolete(\"Use Widget(string, int, bool)\")]"), "The old shape is kept as an [Obsolete] wrapper");
        Assert.That(after, Does.Contain("public Widget(int a, string b) : this(b, a, Flag)"), "The wrapper forwards to the new constructor");

        Assert.That(result.MigratedCallSites, Has.Count.EqualTo(3), "The two new-expression callers and the in-class call are migrated");
        var inClass = result.MigratedCallSites!.Single(site => site.OldText.Contains("new Widget(1, \"x\")"));
        Assert.That(inClass.NewText, Does.Contain("new Widget(\"x\", 1, Flag)"));
        var targetTyped = result.MigratedCallSites.Single(site => site.OldText.Contains("new(2, \"y\")"));
        Assert.That(targetTyped.NewText, Does.Contain("\"y\", 2, Flag"), "A target-typed new is migrated too");

        AssertCompiles(("Widget.cs", after));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_PrivateConstructor_WrapperKeepsAccessibilityAndOldParameterDefaults()
    {
        SetFiles(("Gadget.cs", GadgetSource));
        var result = await ChangeConstructor("Gadget.cs", "Gadget", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("c", "bool", "true"));

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Gadget.cs"];
        Assert.That(after, Does.Contain("private Gadget(string b"), "The new declaration keeps the private accessibility");
        Assert.That(after, Does.Contain("private Gadget(int a, string b = \"q\") : this(b, a, true)"), "The wrapper keeps the private modifier and the old default for b");
        Assert.That(result.MigratedCallSites, Has.Count.EqualTo(1));
        AssertCompiles(("Gadget.cs", after));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AutoMode_ForeignSiteDoesNotBind_InClassSitesMigrated_ForeignSiteStaysOnWrapper()
    {
        SetFiles(("Widget.cs", WidgetSource), ("Other.cs", OtherSource));
        var result = await ChangeConstructor("Widget.cs", "Widget", ChangeSignatureOldSignature.auto,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("c", "bool", "Flag"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.keepAsDeprecatedWrapper), "A skipped site keeps the wrapper");
        var after = result.Changes["Widget.cs"];
        Assert.That(after, Does.Contain("[global::System.Obsolete(\"Use Widget(string, int, bool)\")]"));
        Assert.That(after, Does.Contain("public Widget(int a, string b) : this(b, a, Flag)"));
        Assert.That(after, Does.Contain("new Widget(\"x\", 1, Flag)"), "The in-class call binds Flag and is migrated");
        Assert.That(result.Changes.Keys.Select(k => k.ToString()).Any(k => k.Contains("Other.cs")), Is.False, "The foreign file is not rewritten");

        var foreign = result.SkippedCallSites.Where(s => s.FilePath.ToString().Contains("Other.cs")).ToList();
        Assert.That(foreign, Has.Count.EqualTo(1), "The foreign site is listed as skipped");
        Assert.That(foreign[0].LineNumber, Is.EqualTo(3));
        Assert.That(foreign[0].Reason, Does.Contain("defaultValue 'Flag' of new parameter 'c' does not bind in this scope"));

        AssertCompiles(AfterChange(result.Changes, ("Widget.cs", WidgetSource), ("Other.cs", OtherSource)));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task AutoMode_EveryCallerMigrates_RemovesOldSignature_NoObsoleteWrapper()
    {
        SetFiles(("Widget.cs", WidgetSource));
        var result = await ChangeConstructor("Widget.cs", "Widget", ChangeSignatureOldSignature.auto,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("c", "bool", "true"));

        Assert.That(result.Error, Is.Null, result.Error);
        Assert.That(result.OldSignatureOutcome, Is.EqualTo(ChangeSignatureOldSignature.remove), "Every call site migrates, so the old signature is removed");
        var after = result.Changes["Widget.cs"];
        Assert.That(after, Does.Contain("public Widget(string b, int a, bool c = true)"));
        Assert.That(after, Does.Not.Contain("Obsolete"), "No wrapper is written when the old signature is removed");
        Assert.That(after, Does.Not.Contain(": this(b, a"));
        AssertCompiles(("Widget.cs", after));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_PartialClassInitializer_StaysOnNewConstructor_WrapperForwardsWithZero_DerivedCallerMigrated()
    {
        SetFiles(("Node.cs", NodeSource), ("Derived.cs", DerivedSource));
        var result = await _refactoringEngine.ChangeSignatureAsync("Node.cs", "Node",
            new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0), new NewParameterSpec("c", "int", "0") },
            contextSnippet: "public Node(int a, string b)",
            oldSignature: ChangeSignatureOldSignature.keepAsDeprecatedWrapper);

        Assert.That(result.Error, Is.Null, result.Error);
        var after = result.Changes["Node.cs"];
        Assert.That(after, Does.Contain("public Node(string b, int a, int c) : this(a)"), "The existing initializer stays on the new constructor; in wrapper mode the new parameter is required on the member");
        Assert.That(after, Does.Contain(": this(b, a, 0)"), "The wrapper forwards with the new default");

        var derived = result.Changes.Keys.Any(k => k.ToString().Contains("Derived.cs"));
        Assert.That(derived, Is.True, "The derived base(...) caller is migrated");
        Assert.That(result.Changes.First(kv => kv.Key.ToString().Contains("Derived.cs")).Value, Does.Contain("base(\"s\", 1, 0)"));

        AssertCompiles(AfterChange(result.Changes, ("Node.cs", NodeSource), ("Derived.cs", DerivedSource)));
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_SameParameterTypesSwapped_RefusesWithCollisionMessage()
    {
        const string boxSource = """
            class Box
            {
                public Box(int a, int b) { }
            }
            """;
        SetFiles(("Box.cs", boxSource));
        var result = await ChangeConstructor("Box.cs", "Box", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Not.Null, "A wrapper cannot tell the old and new constructors apart");
        Assert.That(result.Error, Does.Contain("cannot keep 'Box': the old and the new signature both take (int, int)"));
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_StaticConstructor_Refuses()
    {
        const string staticSource = """
            class Sc
            {
                static Sc() { }
            }
            """;
        SetFiles(("Sc.cs", staticSource));
        var result = await ChangeConstructor("Sc.cs", "Sc", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Not.Null, "A static constructor has no signature to change");
        Assert.That(result.Error, Does.Contain("static constructor"));
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_PrimaryConstructor_Refuses()
    {
        const string primarySource = """
            class P(int a)
            {
            }
            """;
        SetFiles(("P.cs", primarySource));
        var result = await ChangeConstructor("P.cs", "P", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(0), new NewParameterSpec("b", "int", "0"));

        Assert.That(result.Error, Is.Not.Null, "A primary constructor has no declaration to wrap");
        Assert.That(result.Error, Does.Contain("primary constructor"));
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_ExternConstructor_Refuses()
    {
        const string externSource = """
            class Ext
            {
                public extern Ext(int a, string b);
            }
            """;
        SetFiles(("Ext.cs", externSource));
        var result = await ChangeConstructor("Ext.cs", "Ext", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Not.Null, "An extern constructor has no body to generate a wrapper beside");
        Assert.That(result.Error, Does.Contain("cannot wrap 'Ext': a partial, static or extern constructor has no safe generated wrapper"));
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }

    [Test]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    public async Task KeepWrapper_PartialConstructor_Refuses()
    {
        const string partialSource = """
            public partial class Pc
            {
                public partial Pc(int a, string b);
            }
            """;
        SetFiles(("Pc.cs", partialSource));
        var result = await ChangeConstructor("Pc.cs", "Pc", ChangeSignatureOldSignature.keepAsDeprecatedWrapper,
            new ExistingParameterSpec(1), new ExistingParameterSpec(0));

        Assert.That(result.Error, Is.Not.Null, "A partial constructor declaration has no safe generated wrapper");
        Assert.That(result.Error, Does.Contain("cannot wrap 'Pc': a partial, static or extern constructor has no safe generated wrapper"));
        Assert.That(result.Changes, Is.Empty, "A refused change writes nothing");
    }
}

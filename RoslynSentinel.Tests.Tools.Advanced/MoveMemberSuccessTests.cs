using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using static RoslynSentinel.Tests.Support.Sources;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (InMemoryWorkspace) success tests for MoveMemberTool, split out of BatteryTwentyFourTests so they cannot
// leak files into the bin dir. Sources are written with CRLF; text read back is normalized to LF before comparing.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberSuccessTests
{

    private static string Before(string text, string marker) => text.Substring(0, text.IndexOf(marker, StringComparison.Ordinal));

    private static string From(string text, string marker) => text.Substring(text.IndexOf(marker, StringComparison.Ordinal));

    private static async Task<Dictionary<string, string>> ReadAllDocumentsAsync(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            texts[document.Name] = (await document.GetTextAsync(CancellationToken.None)).ToString();
        }
        return texts;
    }

    // Compiler errors and warnings of the in-memory solution, one "Severity Id File" line each.
    private static async Task<List<string>> GetDiagnosticsAsync(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var lines = new List<string>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(CancellationToken.None);
            foreach (var diagnostic in compilation!.GetDiagnostics().Where(d => d.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning))
            {
                var file = System.IO.Path.GetFileName(diagnostic.Location.SourceTree?.FilePath ?? string.Empty);
                lines.Add($"{diagnostic.Severity} {diagnostic.Id} {file}");
            }
        }
        return lines;
    }

    private static void AssertDocumentsUnchanged(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        Assert.That(after.Keys, Is.EquivalentTo(before.Keys), "A dry run must not add or remove documents.");
        foreach (var (name, text) in before)
        {
            Assert.That(after[name], Is.EqualTo(text), $"A dry run must not write '{name}'.");
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_InstanceSingleField_BareSameClassCall_ReportsRewriteFinding()
    {
        var original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x)\n    {\n        return x + 1;\n    }\n\n    public int Caller()\n    {\n        return Foo(2);\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Target.cs", "public class Target\n{\n}\n".ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(Lf(workspace.ReadText("Refactor.cs")), Does.Contain("return this._impl.Foo(2);"));
        Assert.That(result.Findings.Any(f => f.Message.StartsWith("Rewrote same-class call Foo(2) -> this._impl.Foo(2) (", StringComparison.Ordinal)), Is.True, "A Caution finding must report the rewrite with old text, new text and location.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapValidInstanceMove_RewritesBodyToTargetField()
    {
        var source = "public class Source\n{\n    private string _a = \"source\";\n    public string M() { var first = _a; return first + this._a; }\n}\n\npublic class Target\n{\n    private string _b = \"target\";\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", source.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["M"], "Target", fieldMap: new Dictionary<string, string> { ["_a"] = "_b" });
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        var targetPart = From(text, "class Target");
        Assert.That(targetPart, Does.Contain("var first = _b"), "The moved body must reference the target field.");
        Assert.That(targetPart, Does.Not.Contain("_a"), "No reference to the source field may remain in the moved body.");
        Assert.That(text, Does.Contain("private string _a"), "The source field stays in place.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_FieldMapDryRun_AppliesMappingWithoutWriting()
    {
        var source = "public class Source\n{\n    private string _a = \"source\";\n    public string M() { var first = _a; return first + this._a; }\n}\n\npublic class Target\n{\n    private string _b = \"target\";\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", source.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["M"], "Target", dryRun: true, returnDiff: true, fieldMap: new Dictionary<string, string> { ["_a"] = "_b" });
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("var first = _b"), "The dry-run diff must show the mapped field in the moved body.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_DefaultOldMember_RemovesSourceMemberAndMovesBodyToTarget()
    {
        var source = "public class Source\n{\n    public int Get() => 1;\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", source.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Get"], "Target");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(Before(text, "class Target"), Does.Not.Contain("Get()"), "Default remove mode deletes the source member.");
        Assert.That(From(text, "class Target"), Does.Contain("public int Get() => 1;"), "Default remove mode moves the body to the target.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_LeavesForwarder()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(Before(text, "class Target"), Does.Contain("public static int Foo(int x) => global::Target.Foo(x);"), "The source keeps a plain forwarder to the moved member.");
        Assert.That(From(text, "class Target"), Does.Contain("public static int Foo(int x) { return x + 1; }"), "The target holds the full moved method.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_StaticToExistingClass_AddsObsoleteAttribute()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call Target.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public static int Foo(int x) => global::Target.Foo(x);"), "The Obsolete attribute must precede the forwarder.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        // Recorded outcome (open question 6/7): a caller in another class receives an RSDEPRECATED warning from the
        // Obsolete attribute, and the solution has no errors.
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build with the deprecated wrapper.");
        Assert.That(diagnostics, Does.Contain("Warning RSDEPRECATED Caller.cs"), "Recorded outcome: the deprecated wrapper warns at the outside caller.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToNewClass_LeavesForwarder()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n";
        var caller = "public class Caller\n{\n    public int Call() => Src.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("public static int Foo(int x) => global::NewTarget.Foo(x);"), "The source keeps a plain forwarder to the new class.");
        var newText = Lf(workspace.ReadText("NewTarget.cs"));
        Assert.That(newText, Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
        Assert.That(newText, Does.Contain("public static int Foo(int x) { return x + 1; }"), "The new class holds the full moved method.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_StaticToNewClass_AddsObsoleteAttribute()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call NewTarget.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public static int Foo(int x) => global::NewTarget.Foo(x);"), "The Obsolete attribute must precede the forwarder to the new class.");
        Assert.That(Lf(workspace.ReadText("NewTarget.cs")), Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_StaticToExistingClass_DryRun_WritesNothing()
    {
        var original = "public class Src\n{\n    public static int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Foo"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("global::Target.Foo(x)"), "The dry-run diff must show the forwarder.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToExistingClass_SingleField_LeavesForwarder()
    {
        var original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(Before(text, "class Target"), Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The source keeps a forwarder through the single target-typed field.");
        Assert.That(From(text, "class Target"), Does.Contain("public int Foo(int x) { return x + 1; }"), "The target holds the full moved method.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceTwoCandidateFields_WithForwarderReceiver_UsesGivenReceiver()
    {
        var original = "public class Source\n{\n    private readonly Target _a = new Target();\n    private readonly Target _b = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, forwarderReceiver: "_b");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("public int Foo(int x) => this._b.Foo(x);"), "The forwarder must call through the given receiver.");
        Assert.That(text, Does.Not.Contain("this._a.Foo(x)"), "The other candidate must not be used.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_InstanceToExistingClass_AddsObsoleteAttribute()
    {
        var original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call Target.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public int Foo(int x) => this._impl.Foo(x);"), "The Obsolete attribute must precede the forwarder.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build with the deprecated wrapper.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceWithFieldMap_RewritesBodyAndLeavesForwarder()
    {
        var original = "public class Source\n{\n    private int _base = 1;\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + _base; }\n}\n\npublic class Target\n{\n    private int _b = 0;\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, fieldMap: new Dictionary<string, string> { ["_base"] = "_b" });
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        var targetPart = From(text, "class Target");
        Assert.That(targetPart, Does.Contain("return x + _b;"), "The moved body must reference the mapped target field.");
        Assert.That(targetPart, Does.Not.Contain("_base"), "No reference to the source field may remain in the moved body.");
        Assert.That(Before(text, "class Target"), Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder is still written beside the mapped move.");
        Assert.That(Before(text, "class Target"), Does.Contain("private int _base"), "The source field stays in place.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceAutoResolveCallSitesFalse_LeavesForwarderAndCallerUntouched()
    {
        var original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n}\n\npublic class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator, autoResolveCallSites: false);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        Assert.That(Lf(workspace.ReadText("Refactor.cs")), Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder is written with nothing to resolve.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceSameClassBareCall_LeftValid()
    {
        var original = "public class Source\n{\n    private readonly Target _impl = new Target();\n    public int Foo(int x) { return x + 1; }\n    public int Bar() => Foo(1);\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        var sourcePart = Before(text, "class Target");
        Assert.That(sourcePart, Does.Contain("public int Bar() => Foo(1);"), "The same-class bare call is left as written.");
        Assert.That(sourcePart, Does.Contain("public int Foo(int x) => this._impl.Foo(x);"), "The forwarder keeps the bare call valid.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The bare call must still compile through the forwarder.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_ToBaseClass_PullsMemberUpIntoBaseClass()
    {
        // Self-contained source (replaces BatteryTwentyFourTests RefactorSource, whose Sound override conflicts with
        // the abstract base member, so its outcome was not a clean pull-up).
        var original = "public class Animal\n{\n}\n\npublic class Dog : Animal\n{\n    public string Bark() => \"woof\";\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Dog", ["Bark"], "Animal");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        // TODO Pull-up to an existing base class emits the member as virtual; confirm that is intended.
        Assert.That(Before(text, "class Dog"), Does.Contain("public virtual string Bark() => \"woof\";"), "The pulled-up member must land in the base class, made virtual.");
        Assert.That(From(text, "class Dog"), Does.Not.Contain("Bark()"), "The member must be removed from the derived class.");
    }
}

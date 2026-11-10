using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using static RoslynSentinel.Tests.Support.Sources;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (InMemoryWorkspace) success tests for MoveMemberTool, split out of BatteryTwentyFourTests so they cannot
// leak files into the bin dir. Sources are written with CRLF; text read back is normalized to LF before comparing.
// TODO move this whole file (542 lines, MoveMemberTool only) to Tests.Tools.Basic: needs a MoveFile tool or cross-project MoveType (MoveType is a no-op for a sole type)
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
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
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Dog", ["Bark"], "Animal");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        // Intended: pull-up emits virtual so the moved member stays overridable and call sites keep working (MoveMemberTextEdits.BuildPullUpModifierEdits).
        Assert.That(Before(text, "class Dog"), Does.Contain("public virtual string Bark() => \"woof\";"), "The pulled-up member must land in the base class, made virtual.");
        Assert.That(From(text, "class Dog"), Does.Not.Contain("Bark()"), "The member must be removed from the derived class.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToStaticClass_LeavesForwarder()
    {
        var original = "public class Source\n{\n    public int Foo(int x) { return x + 1; }\n}\n\npublic static class Target\n{\n}\n";
        var caller = "public class Caller\n{\n    public int Call(Source s) => s.Foo(1);\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(Before(text, "class Target"), Does.Contain("public int Foo(int x) => global::Target.Foo(x);"), "The source keeps an instance forwarder to the static target.");
        Assert.That(From(text, "class Target"), Does.Contain("public static int Foo(int x) { return x + 1; }"), "The static target holds the moved method, now static.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_InstanceToStaticClass_AddsObsoleteAttribute()
    {
        var original = "public class Source\n{\n    public int Foo(int x) { return x + 1; }\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "Target", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("[global::System.Obsolete(\"Deprecated wrapper: call Target.Foo instead.\", DiagnosticId = \"RSDEPRECATED\")]\n    public int Foo(int x) => global::Target.Foo(x);"), "The Obsolete attribute must precede the instance forwarder to the static target.");
        Assert.That(From(text, "class Target"), Does.Contain("public static int Foo(int x) { return x + 1; }"), "The static target holds the moved method, now static.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_InstanceToNewClass_LeavesNewInstanceForwarder()
    {
        var original = "namespace App\n{\n    public class Source\n    {\n        public int Foo(int x) { return x + 1; }\n    }\n}\n";
        var caller = "namespace App\n{\n    public class Caller\n    {\n        public int Call(Source s) => s.Foo(1);\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")), ("Caller.cs", caller.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("public int Foo(int x) => new global::App.NewTarget().Foo(x);"), "The source keeps an instance forwarder that news the new class.");
        var newText = Lf(workspace.ReadText("NewTarget.cs"));
        Assert.That(newText, Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
        Assert.That(newText, Does.Contain("public int Foo(int x) { return x + 1; }"), "The new class holds the full moved instance method.");
        Assert.That(Lf(workspace.ReadText("Caller.cs")), Is.EqualTo(caller), "Keep modes leave outside callers untouched.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The solution must build after the forwarder is written.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_InstanceToNewClass_AddsObsoleteAttribute()
    {
        var original = "namespace App\n{\n    public class Source\n    {\n        public int Foo(int x) { return x + 1; }\n    }\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Foo"], "NewTarget", oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(text, Does.Contain("[global::System.Obsolete(").And.Contain("DiagnosticId = \"RSDEPRECATED\")]"), "The Obsolete attribute must be added to the forwarder.");
        Assert.That(text, Does.Contain("public int Foo(int x) => new global::App.NewTarget().Foo(x);"), "The forwarder must new the new class.");
        Assert.That(Lf(workspace.ReadText("NewTarget.cs")), Does.Contain("class NewTarget"), "The new class is created in the new file beside the source.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericInstanceMethodSingleConstraint_DryRun_ShowsForwarderAndWritesNothing()
    {
        var original = "public class Src\n{\n    private readonly Target _impl = new Target();\n    public T Get<T>(int id) where T : class => default;\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Get"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("Get<T>(int id) where T : class"), "The forwarder must mirror the generic signature and its constraint.");
        Assert.That(summary.Diff, Does.Contain(".Get<T>(id)"), "The forwarder must call the target with explicit type arguments.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericStaticMethodIntoStaticTarget_DryRun_ShowsQualifiedForwarder()
    {
        var original = "public static class Src\n{\n    public static T Make<T>(int id) where T : class => default;\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Make"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("Make<T>(int id) where T : class"), "The static forwarder must mirror the generic signature and its constraint.");
        Assert.That(summary.Diff, Does.Contain("global::Target.Make<T>(id)"), "The static forwarder must call the qualified generic target.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericMultipleTypeParamsWithConstraints_DryRun_ShowsAllConstraints()
    {
        var original = "public static class Src\n{\n    public static TValue Pair<TKey, TValue>(TKey key, TValue value) where TKey : notnull where TValue : struct => value;\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Pair"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("where TKey : notnull"), "The forwarder must keep the notnull constraint on TKey.");
        Assert.That(summary.Diff, Does.Contain("where TValue : struct"), "The forwarder must keep the struct constraint on TValue.");
        Assert.That(summary.Diff, Does.Contain("global::Target.Pair<TKey, TValue>(key, value)"), "The forwarder must pass both type arguments explicitly.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericNewInterfaceAndUnmanagedConstraints_DryRun_ShowsAllConstraints()
    {
        var original = "using System;\n\npublic static class Src\n{\n    public static T Build<T>() where T : IDisposable, new() => new T();\n    public static T Raw<T>(T value) where T : unmanaged => value;\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Build", "Raw"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("where T : IDisposable, new()"), "The forwarder must keep the interface and new() constraints.");
        Assert.That(summary.Diff, Does.Contain("where T : unmanaged"), "The forwarder must keep the unmanaged constraint.");
        Assert.That(summary.Diff, Does.Contain("global::Target.Build<T>()"), "The forwarder must call the qualified target with explicit type arguments.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericRefOutInParamsDefaults_DryRun_ShowsForwarderWithModifiers()
    {
        var original = "public static class Src\n{\n    public static int Mix<T>(ref T target, out int count, in T probe, int offset = 0, string label = null, params T[] items) where T : struct\n    {\n        count = items.Length + offset + (label == null ? 0 : label.Length);\n        target = probe;\n        return items.Length;\n    }\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Mix"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("ref T target, out int count, in T probe"), "The forwarder must keep ref, out and in modifiers.");
        Assert.That(summary.Diff, Does.Contain("int offset = 0, string label = null, params T[] items"), "The forwarder must keep default values and params.");
        Assert.That(summary.Diff, Does.Contain("global::Target.Mix<T>(ref target, out count, in probe, offset, label, items)"), "The forwarder must pass ref, out and in arguments through.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericAsyncTaskOfT_DryRun_ShowsForwarder()
    {
        var original = "using System.Threading.Tasks;\n\npublic static class Src\n{\n    public static async Task<T> LoadAsync<T>(int id) where T : class\n    {\n        await Task.Yield();\n        return default;\n    }\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["LoadAsync"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("LoadAsync<T>(int id) where T : class"), "The async forwarder must mirror the generic signature and constraint.");
        Assert.That(summary.Diff, Does.Contain("global::Target.LoadAsync<T>(id)"), "The async forwarder must call the qualified generic target.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepAsDeprecatedWrapper_GenericMethod_DryRun_ShowsObsoleteGenericForwarder()
    {
        var original = "public static class Src\n{\n    public static T Fetch<T>(int id) where T : class => default;\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var before = await ReadAllDocumentsAsync(workspace);
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Fetch"], "Target", dryRun: true, returnDiff: true, oldMember: MoveMemberOldMember.keepAsDeprecatedWrapper);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Diff, Does.Contain("[global::System.Obsolete("), "The generic wrapper must carry the Obsolete attribute.");
        Assert.That(summary.Diff, Does.Contain("RSDEPRECATED"), "The generic wrapper must carry the RSDEPRECATED diagnostic id.");
        Assert.That(summary.Diff, Does.Contain("Fetch<T>(int id) where T : class"), "The deprecated wrapper must mirror the generic signature and constraint.");
        AssertDocumentsUnchanged(before, await ReadAllDocumentsAsync(workspace));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericStaticMethods_AllConstraintKinds_SolutionCompilesAfterMove()
    {
        var original = "using System;\nusing System.Threading.Tasks;\n\npublic static class Src\n{\n    public static T Build<T>() where T : IDisposable, new() => new T();\n    public static T Raw<T>(T value) where T : unmanaged => value;\n    public static int Mix<T>(ref T target, out int count, in T probe, int offset = 0, string label = null, params T[] items) where T : struct\n    {\n        count = items.Length + offset + (label == null ? 0 : label.Length);\n        target = probe;\n        return items.Length;\n    }\n    public static TValue Pair<TKey, TValue>(TKey key, TValue value) where TKey : notnull where TValue : struct => value;\n    public static async Task<T> LoadAsync<T>(int id) where T : class\n    {\n        await Task.Yield();\n        return default;\n    }\n}\n\npublic static class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Build", "Raw", "Mix", "Pair", "LoadAsync"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "Every generated generic forwarder must compile.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_KeepDelegator_GenericInstanceMethod_SolutionCompilesAfterMove()
    {
        var original = "public class Src\n{\n    private readonly Target _impl = new Target();\n    public T Get<T>(int id) where T : class => default;\n}\n\npublic class Target\n{\n}\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateCoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Src", ["Get"], "Target", oldMember: MoveMemberOldMember.keepDelegator);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var text = Lf(workspace.ReadText("Refactor.cs"));
        Assert.That(Before(text, "class Target"), Does.Contain("Get<T>(int id) where T : class"), "The source keeps a generic forwarder.");
        var diagnostics = await GetDiagnosticsAsync(workspace);
        Assert.That(diagnostics.Where(d => d.StartsWith("Error", StringComparison.Ordinal)), Is.Empty, "The generated generic instance forwarder must compile.");
    }
}

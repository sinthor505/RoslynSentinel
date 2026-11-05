using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MoveMember carries the using directives the moved code needs into the target file (TODO item 39, slice 1):
/// static members moved into an existing class in another file, and members pulled up into a base type's file.
/// Each positive case runs the real apply through the compile gate, so a missing using would be rejected as a new error.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("MoveMemberUsings")]
public class MoveMemberCarriesUsingsTests
{
    private const string Eol = "\r\n";
    private const string RegexUsing = "using System.Text.RegularExpressions;";

    private const string RegexSource = "using System.Text.RegularExpressions;" + Eol + Eol + "namespace Example;" + Eol + Eol
        + "public static class A" + Eol + "{" + Eol
        + "    public static string Clean(string input) => Regex.Replace(input, \"a+\", \"b\");" + Eol + Eol
        + "    public static int Other(string x) => x.Length;" + Eol + "}" + Eol;

    [Test]
    public async Task StaticMethodToExistingClass_TargetLacksUsing_UsingIsAddedAboveTheNamespaceAndCompilesAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", RegexSource),
            ("Target.cs", Lines("namespace Example;", "", "public static class B", "{", "    public static int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Clean" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        var target = TextOf(result, "Target.cs");
        Assert.That(target, Does.StartWith(RegexUsing + Eol + Eol + "namespace Example;"), "The using goes to the top of a target that has none, followed by a blank line");
        Assert.That(CountOf(target, RegexUsing), Is.EqualTo(1));
        Assert.That(target, Does.Contain("Regex.Replace(input"));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToExistingClass_TargetHasOtherUsings_UsingIsAppendedAfterTheLastOneAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", RegexSource),
            ("Target.cs", Lines("using System;", "", "namespace Example;", "", "public static class B", "{", "    public static int Count() => Math.Abs(0);", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Clean" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(TextOf(result, "Target.cs"), Does.StartWith("using System;" + Eol + RegexUsing + Eol + Eol + "namespace Example;"));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToExistingClass_TargetAlreadyHasUsing_ExactlyOneOccurrenceAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", RegexSource),
            ("Target.cs", Lines(RegexUsing, "", "namespace Example;", "", "public static class B", "{", "    public static int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Clean" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(CountOf(TextOf(result, "Target.cs"), RegexUsing), Is.EqualTo(1));
        Assert.That(EditCount(result, "Target.cs"), Is.EqualTo(1), "Only the member insertion edits the target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToExistingClass_OnlyTypesTheTargetAlreadyImports_UsingsAreUntouchedAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("using System;", "", "namespace Example;", "", "public static class A", "{",
                "    public static int Pick(int a) => Math.Max(a, 1);", "", "    public static int Other(string x) => x.Length;", "}")),
            ("Target.cs", Lines("using System;", "", "namespace Example;", "", "public static class B", "{", "    public static int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Pick" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(CountOf(TextOf(result, "Target.cs"), "using "), Is.EqualTo(1), "No using is added");
        Assert.That(EditCount(result, "Target.cs"), Is.EqualTo(1), "Only the member insertion edits the target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToExistingClass_ExtensionMethodCall_CarriesTheExtensionNamespaceUsingAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Ext.cs", Lines("namespace Example.Ext;", "", "public static class StrExt", "{", "    public static string Shout(this string s) => s.ToUpper();", "}")),
            ("Source.cs", Lines("using Example.Ext;", "", "namespace Example;", "", "public static class A", "{",
                "    public static string Loud(string s) => s.Shout();", "", "    public static int Other(string x) => x.Length;", "}")),
            ("Target.cs", Lines("namespace Other;", "", "public static class B", "{", "    public static int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Loud" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(TextOf(result, "Target.cs"), Does.StartWith("using Example.Ext;" + Eol + Eol + "namespace Other;"));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task StaticMethodToExistingClass_TargetIsInsideTheNeededNamespace_NoUsingIsAddedAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Ext.cs", Lines("namespace Example.Ext;", "", "public static class StrExt", "{", "    public static string Shout(this string s) => s.ToUpper();", "}")),
            ("Source.cs", Lines("using Example.Ext;", "", "namespace Example;", "", "public static class A", "{",
                "    public static string Loud(string s) => s.Shout();", "", "    public static int Other(string x) => x.Length;", "}")),
            ("Target.cs", Lines("namespace Example.Ext;", "", "public static class B", "{", "    public static int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Loud" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(CountOf(TextOf(result, "Target.cs"), "using "), Is.EqualTo(0));
        Assert.That(EditCount(result, "Target.cs"), Is.EqualTo(1));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task MethodToBaseType_BaseFileLacksUsing_UsingIsAddedAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("using System.Text.RegularExpressions;", "", "namespace Example;", "", "public class Derived : Base",
                "{", "    public string Clean(string input) => Regex.Replace(input, \"a+\", \"b\");", "", "    public int Process(string x) => x.Length;", "}")),
            ("Base.cs", Lines("namespace Example;", "", "public class Base", "{", "    public int Count() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "Derived", new[] { "Clean" }, "Base", workspace.PathOf("Base.cs"), CancellationToken.None);

        Assert.That(TextOf(result, "Base.cs"), Does.StartWith(RegexUsing + Eol + Eol + "namespace Example;"));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task InstanceMethodToExistingClass_TargetLacksUsing_UsingIsAddedAndCompilesAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("using System.Text.RegularExpressions;", "", "namespace Example;", "", "public class A",
                "{", "    public string Clean(string input) => Regex.Replace(input, \"a+\", \"b\");", "", "    public int Process(string x) => x.Length;", "}")),
            ("Target.cs", Lines("namespace Example;", "", "public class B", "{", "    public int Size() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Clean" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        var target = TextOf(result, "Target.cs");
        Assert.That(target, Does.StartWith(RegexUsing + Eol + Eol + "namespace Example;"));
        Assert.That(CountOf(target, RegexUsing), Is.EqualTo(1));
        Assert.That(target, Does.Contain("Regex.Replace(input"));
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

    [Test]
    public async Task InstanceMethodToExistingClass_TargetAlreadyHasUsing_ExactlyOneOccurrenceAsync()
    {
        var files = new (string Name, string Content)[]
        {
            ("Source.cs", Lines("using System.Text.RegularExpressions;", "", "namespace Example;", "", "public class A",
                "{", "    public string Clean(string input) => Regex.Replace(input, \"a+\", \"b\");", "", "    public int Process(string x) => x.Length;", "}")),
            ("Target.cs", Lines(RegexUsing, "", "namespace Example;", "", "public class B", "{", "    public int Size() => 0;", "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var (engine, validation) = CreateEngine(workspace);

        var result = await engine.MoveMemberAsync(workspace.PathOf("Source.cs"), "A", new[] { "Clean" }, "B", workspace.PathOf("Target.cs"), CancellationToken.None);

        Assert.That(CountOf(TextOf(result, "Target.cs"), RegexUsing), Is.EqualTo(1));
        Assert.That(EditCount(result, "Target.cs"), Is.EqualTo(1), "Only the member insertion edits the target");
        await AssertAppliesCleanlyAsync(workspace, validation, files, result);
    }

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

    private static string TextOf(MoveMemberResult result, string fileName) => result.Changes.Single(c => FileName(c.Key) == fileName).Value;

    private static int EditCount(MoveMemberResult result, string fileName) => result.AppliedEdits!.Single(c => FileName(c.Key) == fileName).Value.Count;

    private static int CountOf(string text, string needle) => text.Split(needle).Length - 1;

    private static string FileName(FilePathWrapper path) => Path.GetFileName(path.ToString());

    private static string Lines(params string[] lines) => string.Join(Eol, lines) + Eol;

    private static (MemberRefactoringEngine Engine, ValidationEngine Validation) CreateEngine(InMemoryWorkspace workspace)
    {
        var validation = Battery.Basic.TestEngines.ValidationEngine(workspace.Manager, Battery.Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var symbolNav = Battery.Basic.TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return (Battery.Basic.TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, validation), validation);
    }
}

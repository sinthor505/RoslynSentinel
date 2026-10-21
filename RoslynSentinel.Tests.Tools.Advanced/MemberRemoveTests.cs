using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) Member add/remove tests split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MemberRemoveTests
{
    private static readonly string SimpleSource = Sources.Order;

    private static string Crlf(string source) => source.ReplaceLineEndings("\r\n");

    // Concatenates every document's text so a test can assert a declaration is gone from the whole solution.
    private static string AllText(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution!;
        return string.Join("\n", solution.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.GetTextAsync().GetAwaiter().GetResult().ToString()));
    }

    // --- AddMemberToClass ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task AddMemberToClass_ValidClass_AddsTagPropertyToOrder()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", SimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.addMember, "Order", newMemberSource: "public string Tag { get; set; }");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("public string Tag { get; set; }"));
    }

    // --- RemoveMember ---
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_ValidMember_RemovesGetLabelAndKeepsSibling()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", SimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "GetLabel");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Not.Contain("public string GetLabel()"));
        Assert.That(orderText, Does.Contain("public string GetStatus()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_ZeroReferences_SucceedsAsBefore()
    {
        // GetLabel has no callers in SimpleSource, so the default precheck must let it through.
        using var workspace = InMemoryWorkspace.Create(("Order.cs", SimpleSource));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Order.cs"), MemberAction.remove, memberName: "GetLabel");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var orderText = workspace.ReadText("Order.cs");
        Assert.That(orderText, Does.Not.Contain("public string GetLabel()"));
        Assert.That(orderText, Does.Contain("public string GetStatus()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasCaller_RefusedByDefault_ListsCaller()
    {
        var source = Crlf("""
            namespace TestProj;

            public class Helper
            {
                public string GetName() => "Test";

                public void UseHelper()
                {
                    var name = GetName();
                }
            }
            """);
        using var workspace = InMemoryWorkspace.Create(("Helper.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Helper.cs"), MemberAction.remove, memberName: "GetName");
        Assert.That(result.IsError, Is.True, "A member with a real caller must be refused by default.");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("caller"));
        Assert.That(workspace.ReadText("Helper.cs"), Does.Contain("public string GetName()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasCaller_SkipPrecheckTrue_StillRefusedByEngineCallerCheck()
    {
        // skipPrecheck: true bypasses only the tool-level (callers+implementations) precheck ->
        // BasicRefactoringEngine.RemoveMemberAsync's own pre-existing, unconditional caller check
        // (SymbolFinder-based, no bypass) still applies underneath, so a member with a real caller
        // is never truly force-removable. This matches the existing engine-level contract
        // (BUG_60_RemoveMember_ErrorsWhenMemberIsUsed) rather than superseding it.
        var source = Crlf("""
            namespace TestProj;

            public class Helper
            {
                public string GetName() => "Test";

                public void UseHelper()
                {
                    var name = GetName();
                }
            }
            """);
        using var workspace = InMemoryWorkspace.Create(("Helper.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Helper.cs"), MemberAction.remove, memberName: "GetName", skipPrecheck: true);
        Assert.That(result.IsError, Is.True, "The engine's own caller check still applies even with skipPrecheck: true.");
        Assert.That(workspace.ReadText("Helper.cs"), Does.Contain("public string GetName()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_HasImplementationOnly_SkipPrecheckTrue_BypassesToolLevelCheck()
    {
        // An interface member's implementation isn't caught by the engine's caller-only
        // SymbolFinder check, so the default (skipPrecheck: false) refusal here can only be coming
        // from the tool-level precheck. With skipPrecheck: true that precheck is bypassed ->
        // removal still fails, but for a different reason (the compile-validation safety net catching
        // the now-unimplemented interface member), demonstrating skipPrecheck actually skips the precheck.
        var source = Crlf("""
            namespace TestProj;

            public interface IGreeter
            {
                string Greet();
            }

            public class Greeter : IGreeter
            {
                public string Greet() => "hello";
            }
            """);
        using var workspace = InMemoryWorkspace.Create(("Greeter.cs", source));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var refused = await tools.MemberTool(reason: "test message", workspace.PathOf("Greeter.cs"), MemberAction.remove, memberName: "Greet");
        Assert.That(refused.IsError, Is.True, "An interface member's implementation must be caught by the default precheck.");
        Assert.That(refused.ErrorData!.Message, Does.Contain("implementation"), "Default refusal must come from the tool-level precheck, listing the implementation.");
        Assert.That(workspace.ReadText("Greeter.cs"), Does.Contain("public string Greet()"));

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Greeter.cs"), MemberAction.remove, memberName: "Greet", skipPrecheck: true);
        Assert.That(result.IsError, Is.True, "Removing an interface's sole implementation still breaks compilation - the separate compile-validation safety net catches it.");
        Assert.That(result.ErrorData!.Detail, Does.Contain("does not implement interface member"), "With skipPrecheck: true, the refusal reason must shift from the precheck to compile validation, proving the precheck itself was skipped. The diagnostic text lives in .Detail, not .Message.");
        Assert.That(workspace.ReadText("Greeter.cs"), Does.Contain("public string Greet()"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnNamespaceLevelRecord_SkipPrecheckTrue_ReferencedRecord_ReturnsSafeDeleteRefusal()
    {
        // Regression test for blocking_error_member_remove_skipprecheck_targetnotfound_ambiguous_symbol.md.
        // MemberTool(remove) routes type-level declarations to StructuralRefinementEngine.SafeDeleteSymbolAsync.
        // Without skipPrecheck, a record with callers is refused first by the tool-level precheck
        // (FindCallersAsync) with a caller list. With skipPrecheck: true, that precheck is bypassed and
        // SafeDeleteSymbolAsync's own usage check refuses the still-referenced record.
        using var workspace = InMemoryWorkspace.Create(
            ("Chain.cs", Crlf("""
                namespace TestProj;

                public record CircularDependencyChain(string Name);
                """)),
            ("Caller.cs", Crlf("""
                namespace TestProj;

                public class Caller
                {
                    public CircularDependencyChain Make() => new CircularDependencyChain("x");
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var withPrecheck = await tools.MemberTool(reason: "test message", workspace.PathOf("Chain.cs"), MemberAction.remove, memberName: "CircularDependencyChain");
        Assert.That(withPrecheck.IsError, Is.True, "A record with a real caller must be refused by the default precheck.");
        Assert.That(withPrecheck.ErrorData!.Message, Does.Contain("caller"), "Default refusal must come from the tool-level precheck, listing the caller.");
        Assert.That(workspace.ReadText("Chain.cs"), Does.Contain("record CircularDependencyChain"));

        var skipPrecheck = await tools.MemberTool(reason: "test message", workspace.PathOf("Chain.cs"), MemberAction.remove, memberName: "CircularDependencyChain", skipPrecheck: true);
        Assert.That(skipPrecheck.IsError, Is.True, "Member(remove) with skipPrecheck must still refuse a referenced type declaration.");
        Assert.That(skipPrecheck.ErrorData!.Message, Does.Contain("usages").Or.Contain("cannot be safely deleted"), "The refusal must come from SafeDeleteSymbolAsync's usage check, not a generic 'Member not found'.");
        Assert.That(workspace.ReadText("Chain.cs"), Does.Contain("record CircularDependencyChain"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnUnreferencedRecord_RemovesRecord()
    {
        // Regression test for docs/current/blockers/blocking_error_member_remove_refuses_record_types.md.
        // MemberTool(remove) routes type-level declarations to StructuralRefinementEngine.SafeDeleteSymbolAsync,
        // so an unreferenced record is removed rather than refused.
        using var workspace = InMemoryWorkspace.Create(
            ("Orphan.cs", Crlf("""
                namespace TestProj;

                public record OrphanRecord(string Name);
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Orphan.cs"), MemberAction.remove, memberName: "OrphanRecord");
        Assert.That(result.IsError, Is.False, "Member(remove) must remove an unreferenced type-level declaration.");
        Assert.That(AllText(workspace), Does.Not.Contain("OrphanRecord"), "The resulting solution text must no longer contain the record.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OnUnreferencedInterface_RemovesInterface()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("IOrphan.cs", Crlf("""
                namespace TestProj;

                public interface IOrphan { void M(); }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("IOrphan.cs"), MemberAction.remove, memberName: "IOrphan");
        Assert.That(result.IsError, Is.False, "Member(remove) must remove an unreferenced interface declaration.");
        Assert.That(AllText(workspace), Does.Not.Contain("IOrphan"), "The resulting solution text must no longer contain the interface.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OverrideWithNoCallersOrImplementations_SucceedsByDefault()
    {
        // An override with no callers of its own and nothing further overriding it is flagged by neither the
        // tool-level precheck nor the engine's caller check, so the precheck is not over-broad.
        using var workspace = InMemoryWorkspace.Create(
            ("AnimalBase.cs", Crlf("""
                namespace TestProj;

                public class AnimalBase
                {
                    public virtual string Speak() => "...";
                }
                """)),
            ("Dog.cs", Crlf("""
                namespace TestProj;

                public class Dog : AnimalBase
                {
                    public override string Speak() => "woof";
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Dog.cs"), MemberAction.remove, memberName: "Speak");
        Assert.That(result.IsError, Is.False, "An override with no callers and nothing overriding it in turn must succeed under the default precheck.");
        Assert.That(workspace.ReadText("Dog.cs"), Does.Not.Contain("Speak()"));
        Assert.That(workspace.ReadText("AnimalBase.cs"), Does.Contain("public virtual string Speak()"));
    }

    // Regression coverage for docs/current/blockers/blocking_error_member_replace_interface_notfound.md:
    // replace/remove must be able to target an interface's own property declaration (excludeInterfaceMembers: false).
    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_InterfaceProperty_NoImplementerInFile_Succeeds()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("IGreeter.cs", Crlf("""
                namespace TestProj;

                public interface IGreeter
                {
                    string Greeting { get; }
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("IGreeter.cs"), MemberAction.remove, memberName: "Greeting");
        Assert.That(result.IsError, Is.False, "Member(remove) must be able to target a property declared directly on an interface.");
        Assert.That(workspace.ReadText("IGreeter.cs"), Does.Not.Contain("Greeting"));
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_UnreferencedMethod_ReportsVerifiedRemovalAndKeepsSibling()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("Greeter.cs", Crlf("""
                namespace TestProj;

                public class Greeter
                {
                    public string Hello() => "hello";
                    public string Goodbye() => "bye";
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Greeter.cs"), MemberAction.remove, memberName: "Hello");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("Verified"), "A successful non-dry-run removal must report that the repeat lookup found no declaration.");
        Assert.That(summary.Description, Does.Contain("'Hello'"), "The message must name the removed method.");
        var text = workspace.ReadText("Greeter.cs");
        Assert.That(text, Does.Not.Contain("Hello()"), "The removed method must no longer be in the file text.");
        Assert.That(text, Does.Contain("Goodbye()"), "The sibling method must still be in the file text.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_OverloadSelectedByContextSnippet_RemovesOnlyThatOverload()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("Printer.cs", Crlf("""
                namespace TestProj;

                public class Printer
                {
                    public void Print(string text) { }
                    public void Print(int number) { }
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Printer.cs"), MemberAction.remove, memberName: "Print", contextSnippet: "public void Print(int number)");
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.Description, Does.Contain("'Print'"), "The message must name the removed member.");
        Assert.That(summary.Description, Does.Contain("Verified"), "The snippet no longer matches after removal, so the repeat lookup must find no declaration.");
        var text = workspace.ReadText("Printer.cs");
        Assert.That(text, Does.Not.Contain("Print(int number)"), "The overload selected by the snippet must be removed.");
        Assert.That(text, Does.Contain("Print(string text)"), "The other overload must still be in the file text.");
    }

    [Test]
    [Category("RefactoringStructuralTools")] // sentinel:auto-category
    public async Task RemoveMember_DryRun_DoesNotClaimVerified()
    {
        using var workspace = InMemoryWorkspace.Create(
            ("Greeter.cs", Crlf("""
                namespace TestProj;

                public class Greeter
                {
                    public string Hello() => "hello";
                    public string Goodbye() => "bye";
                }
                """)));
        var tools = BatteryToolFactory.CreateRefactoringStructuralTools(workspace.Manager);
        var before = AllText(workspace);

        var result = await tools.MemberTool(reason: "test message", workspace.PathOf("Greeter.cs"), MemberAction.remove, memberName: "Hello", dryRun: true);
        Assert.That(result.IsError, Is.False, $"{result.ErrorData?.ErrorCode}: {result.ErrorData?.Message}");
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.That(summary.DryRun, Is.True);
        Assert.That(summary.Description, Does.Not.Contain("Verified"), "A dry run writes nothing, so it must not claim the removal was verified.");
        Assert.That(summary.Description, Is.EqualTo("Removed 'Hello' from Greeter.cs."));
        Assert.That(AllText(workspace), Is.EqualTo(before), "A dry run must leave every document text unchanged.");
    }
}

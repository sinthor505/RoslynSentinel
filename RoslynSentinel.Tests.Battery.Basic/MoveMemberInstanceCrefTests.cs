using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// End-to-end tests for the cref RETARGET behaviour of MemberRefactoringEngine.MoveMemberAsync on the instance-move
/// route. Doc-comment crefs elsewhere that name a moved member are retargeted from the source class to the target
/// class; bare crefs inside the moved members are left alone; ambiguous (candidate) crefs are counted, not edited.
/// Notes strings are asserted exactly, including the informational notes the move emits for a cross-file target.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("InstanceMoveCrefRetarget")]
public class MoveMemberInstanceCrefTests
{
    private const string DifferentFileNote =
        "The target class is in a different file; doc-comment crefs were not re-verified against its namespace and using directives.";

    private const string BareInheritedNote =
        "Bare doc-comment crefs in the moved member(s) could not be verified against inherited members of the source class; check any that name one.";

    [Test]
    public async Task QualifiedCrefInSourceAndCallerDocs_IsRetargetedToTargetClass()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method, see <see cref=\"A.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");
        var caller = Lines(eol,
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Documents <see cref=\"A.Count\"/>.</summary>",
            "    public int Run() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target),
            ("Caller.cs", caller));

        // Vacuity guard: the qualified crefs exist in the input before the move.
        Assert.That(source, Does.Contain("<see cref=\"A.Count\"/>"));
        Assert.That(caller, Does.Contain("<see cref=\"A.Count\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other method, see <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}")));
        Assert.That(NewText(result, "Target.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(string input) => input.Length;",
            "}")));
        Assert.That(NewText(result, "Caller.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Documents <see cref=\"B.Count\"/>.</summary>",
            "    public int Run() => 0;",
            "}")));
        Assert.That(result.Notes, Is.EqualTo(new[]
        {
            "Retargeted 2 doc-comment cref(s) from 'A' to 'B'.",
            DifferentFileNote,
        }));
    }

    [Test]
    public async Task BareCrefInOtherMemberOfSourceClass_IsRetargeted()
    {
        const string eol = "\n";
        // Bare crefs from derived classes are not retargeted: they do not bind to inherited members
        // (known limitation, TODO.md), so this test uses a sibling member of A itself instead.
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other member, see <see cref=\"Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target));

        // Vacuity guard: the bare cref sits in Other, outside the moved member.
        Assert.That(source, Does.Contain("    /// <summary>Other member, see <see cref=\"Count\"/>.</summary>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        Assert.That(result.Notes, Is.EqualTo(new[]
        {
            "Retargeted 1 doc-comment cref(s) from 'A' to 'B'.",
            DifferentFileNote,
        }));
        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other member, see <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}")));
    }

    [Test]
    public async Task BareCrefInsideMovedMemberOwnDoc_IsNotRetargetedAndNotCounted()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target));

        // Vacuity guard: the bare cref is inside the member that moves.
        Assert.That(source, Does.Contain("    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other method.</summary>",
            "    public int Process(string x) => x.Length;",
            "}")));
        // The moved text keeps the bare cref unchanged: the member moves together with it.
        Assert.That(NewText(result, "Target.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "}")));
        // Neither a retarget nor an ambiguous-candidate note: only the informational notes for this move.
        Assert.That(result.Notes, Is.EqualTo(new[] { DifferentFileNote, BareInheritedNote }),
            "A bare cref inside the moved member is neither retargeted nor counted");
    }

    [Test]
    public async Task OverloadGroupBareCref_IsNotRetargetedAndIsCountedAsCandidate_WhileQualifiedControlIsRetargeted()
    {
        const string eol = "\n";
        // Both overloads are named Count, so MoveMember moves both. The bare cref in Process is ambiguous
        // between them and must not be edited; the signature-qualified cref is the positive control.
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts strings.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Counts ints.</summary>",
            "    public int Count(int input) => input;",
            "",
            "    /// <summary>See <see cref=\"Count\"/> and <see cref=\"A.Count(string)\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target));

        // Vacuity guard: both the ambiguous bare cref and the positive-control qualified cref exist in the input.
        Assert.That(source, Does.Contain("<see cref=\"Count\"/>"));
        Assert.That(source, Does.Contain("<see cref=\"A.Count(string)\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        // Two adjacent members leave one blank line after the class brace: the existing member-removal whitespace.
        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "",
            "    /// <summary>See <see cref=\"Count\"/> and <see cref=\"B.Count(string)\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}")));
        Assert.That(result.Notes, Is.EqualTo(new[]
        {
            "Retargeted 1 doc-comment cref(s) from 'A' to 'B'.",
            "1 ambiguous (candidate) cref(s) were not retargeted.",
            DifferentFileNote,
        }));
    }

    [Test]
    public async Task MultiVariableField_BothVariableCrefsAreRetargeted()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>First.</summary>",
            "    public int x, y;",
            "",
            "    /// <summary>See <see cref=\"A.x\"/> and <see cref=\"A.y\"/>.</summary>",
            "    public int Process(string s) => s.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target));

        // Vacuity guard: both qualified field crefs exist in the input.
        Assert.That(source, Does.Contain("<see cref=\"A.x\"/>"));
        Assert.That(source, Does.Contain("<see cref=\"A.y\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "x", "y" });

        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>See <see cref=\"B.x\"/> and <see cref=\"B.y\"/>.</summary>",
            "    public int Process(string s) => s.Length;",
            "}")));
        Assert.That(NewText(result, "Target.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>First.</summary>",
            "    public int x, y;",
            "}")));
        Assert.That(result.Notes, Is.EqualTo(new[]
        {
            "Retargeted 2 doc-comment cref(s) from 'A' to 'B'.",
            DifferentFileNote,
        }));
    }

    [Test]
    public async Task NoCrefsAnywhere_NoRetargetOrAmbiguousNoteAndUntouchedFileIsIdentical()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts the characters.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");
        var caller = Lines(eol,
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Plain caller.</summary>",
            "    public int Run() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target),
            ("Caller.cs", caller));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        // Only the cross-file informational note: no retarget note and no ambiguous note.
        Assert.That(result.Notes, Is.EqualTo(new[] { DifferentFileNote }));
        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other method.</summary>",
            "    public int Process(string x) => x.Length;",
            "}")));
        Assert.That(TextOrOriginal(result, "Caller.cs", caller), Is.EqualTo(caller));
    }

    [TestCase("\r\n")]
    [TestCase("\n")]
    public async Task QualifiedCrefRetarget_PreservesSourceLineEndings(string eol)
    {
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>",
            "    /// Counts the characters.",
            "    /// </summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method, see <see cref=\"A.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");
        var caller = Lines(eol,
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Documents <see cref=\"A.Count\"/>.</summary>",
            "    public int Run() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target),
            ("Caller.cs", caller));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        var expectedSource = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other method, see <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var expectedCaller = Lines(eol,
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>Documents <see cref=\"B.Count\"/>.</summary>",
            "    public int Run() => 0;",
            "}");

        var sourceNew = NewText(result, "Source.cs");
        var callerNew = NewText(result, "Caller.cs");
        Assert.That(sourceNew, Is.EqualTo(expectedSource));
        Assert.That(callerNew, Is.EqualTo(expectedCaller));
        Assert.That(sourceNew.EndsWith(eol), Is.True, "Final newline must be kept");
        Assert.That(callerNew.EndsWith(eol), Is.True, "Final newline must be kept");

        if (eol == "\r\n")
        {
            Assert.That(sourceNew.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "No bare LF may appear in a CRLF file");
            Assert.That(callerNew.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "No bare LF may appear in a CRLF file");
        }
        else
        {
            Assert.That(sourceNew, Does.Not.Contain("\r"), "No CR may appear in an LF file");
            Assert.That(callerNew, Does.Not.Contain("\r"), "No CR may appear in an LF file");
        }

        Assert.That(result.Notes, Is.EqualTo(new[]
        {
            "Retargeted 2 doc-comment cref(s) from 'A' to 'B'.",
            DifferentFileNote,
        }));
    }

    private static async Task<MoveMemberResult> MoveInstanceAsync(
        InMemoryWorkspace workspace,
        MemberRefactoringEngine engine,
        string[] memberNames) =>
        await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"),
            "A",
            memberNames,
            "B",
            null,
            CancellationToken.None);

    private static string NewText(MoveMemberResult result, string fileName) =>
        result.Changes[result.Changes.Keys.Single(k => Path.GetFileName(k.ToString()) == fileName)];

    private static string TextOrOriginal(MoveMemberResult result, string fileName, string original)
    {
        var keys = result.Changes.Keys.Where(k => Path.GetFileName(k.ToString()) == fileName).ToList();
        return keys.Count == 0 ? original : result.Changes[keys[0]];
    }

    /// <summary>Joins lines with <paramref name="eol"/> and terminates the last line with it too.</summary>
    private static string Lines(string eol, params string[] lines) => string.Join(eol, lines) + eol;

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
}

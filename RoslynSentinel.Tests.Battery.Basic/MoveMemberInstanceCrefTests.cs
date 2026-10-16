using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Common;
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

    [Test]
    public async Task BareCrefToNonMovedSourceMember_IsQualifiedInMovedDocAndNoted()
    {
        const string eol = "\n";
        const string qualifiedNote = "Qualified 1 doc-comment cref(s) in the moved member(s) with the source class name so they still resolve at 'B'.";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
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

        // Vacuity guard: the bare cref names a member that stays in A.
        Assert.That(source, Does.Contain("<see cref=\"Other\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        Assert.That(NewText(result, "Source.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
            "}")));
        Assert.That(NewText(result, "Target.cs"), Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>Counts, see <see cref=\"A.Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "}")));

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes.Count(n => n == qualifiedNote), Is.EqualTo(1), "Exactly one Qualified note for the one qualified cref");
        Assert.That(notes, Does.Contain(DifferentFileNote));
    }

    [Test]
    public async Task BareCrefToMovedSiblingMember_StaysBareAndIsNotQualified()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Helper\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Helper.</summary>",
            "    public int Helper() => 1;",
            "",
            "    /// <summary>Keeps.</summary>",
            "    public int Keep() => 0;",
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

        // Vacuity guard: the bare cref names a member that is moved together with the doc owner.
        Assert.That(source, Does.Contain("<see cref=\"Helper\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count", "Helper" });

        var sourceNew = NewText(result, "Source.cs");
        Assert.That(sourceNew, Does.Contain("    public int Keep() => 0;"), "The non-moved member must stay in A");
        Assert.That(sourceNew, Does.Not.Contain("Count("), "Count must leave A");
        Assert.That(sourceNew, Does.Not.Contain("Helper("), "Helper must leave A");
        var targetNew = NewText(result, "Target.cs");
        Assert.That(targetNew, Does.Contain("    /// <summary>Counts, see <see cref=\"Helper\"/>.</summary>"));
        Assert.That(targetNew, Does.Not.Contain("A.Helper"), "A cref to a moved sibling must not be qualified with the source class");

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes.Any(n => n.StartsWith("Qualified", StringComparison.Ordinal)), Is.False, "No Qualified note when no cref names a non-moved member");
        Assert.That(notes, Does.Contain(BareInheritedNote));
    }

    [Test]
    public async Task BareCrefToNonMovedMember_SameFileTarget_IsQualifiedWithoutDifferentFileNote()
    {
        const string eol = "\n";
        const string qualifiedNote = "Qualified 1 doc-comment cref(s) in the moved member(s) with the source class name so they still resolve at 'B'.";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
            "}",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source));

        // Vacuity guard: the bare cref names a member that stays in A.
        Assert.That(source, Does.Contain("<see cref=\"Other\"/>"));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "B", workspace.PathOf("Source.cs"), CancellationToken.None);

        var text = NewText(result, "Source.cs");
        var classBStart = text.IndexOf("public class B", StringComparison.Ordinal);
        Assert.That(classBStart, Is.GreaterThan(0), "Class B must remain in the same file");
        var classA = text[..classBStart];
        var classB = text[classBStart..];
        Assert.That(classA, Does.Not.Contain("Count("), "The member must leave A");
        Assert.That(classB, Does.Contain("    /// <summary>Counts, see <see cref=\"A.Other\"/>.</summary>"));
        Assert.That(classB, Does.Contain("    public int Count(string input) => input.Length;"));

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes.Count(n => n == qualifiedNote), Is.EqualTo(1), "Exactly one Qualified note for the one qualified cref");
        Assert.That(notes, Does.Not.Contain(DifferentFileNote), "Same-file target must not carry the different-file note");
    }

    [Test]
    public async Task BareCrefToNonMovedMember_NewClassTarget_IsQualifiedInNewFile()
    {
        const string eol = "\n";
        const string qualifiedNote = "Qualified 1 doc-comment cref(s) in the moved member(s) with the source class name so they still resolve at 'NewHelper'.";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
            "}");

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source));

        // Vacuity guard: the bare cref names a member that stays in A.
        Assert.That(source, Does.Contain("<see cref=\"Other\"/>"));

        var result = await engine.MoveMemberAsync(
            workspace.PathOf("Source.cs"), "A", new[] { "Count" }, "NewHelper", null, CancellationToken.None);

        var newFile = NewText(result, "NewHelper.cs");
        Assert.That(newFile, Does.Contain("public class NewHelper"));
        Assert.That(newFile, Does.Contain("    /// <summary>Counts, see <see cref=\"A.Other\"/>.</summary>"));
        Assert.That(newFile, Does.Contain("    public int Count(string input) => input.Length;"));

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes.Count(n => n == qualifiedNote), Is.EqualTo(1), "Exactly one Qualified note for the one qualified cref");
    }

    [Test]
    public async Task BareCrefToNonMovedMember_GenericSourceClass_UsesGenericPrefix()
    {
        const string eol = "\n";
        const string qualifiedNote = "Qualified 1 doc-comment cref(s) in the moved member(s) with the source class name so they still resolve at 'B'.";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A<T>",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
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

        // Vacuity guard: the bare cref names a member that stays in A<T>.
        Assert.That(source, Does.Contain("<see cref=\"Other\"/>"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        var targetNew = NewText(result, "Target.cs");
        Assert.That(targetNew, Does.Contain("    /// <summary>Counts, see <see cref=\"A{T}.Other\"/>.</summary>"));
        Assert.That(targetNew, Does.Not.Contain("<see cref=\"A.Other\"/>"));

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes.Count(n => n == qualifiedNote), Is.EqualTo(1), "Exactly one Qualified note for the one qualified cref");
    }

    [Test]
    public async Task QualifiedCrefRetarget_AppliedEditsReplayToEngineTextOnEveryEditedFile()
    {
        const string eol = "\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
            "}");
        var target = Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "}");
        var originals = new Dictionary<string, string>
        {
            ["Source.cs"] = source,
            ["Target.cs"] = target,
        };

        var (workspace, engine) = CreateInMemoryTestFixture(
            ("Source.cs", source),
            ("Target.cs", target));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        Assert.That(result.AppliedEdits, Is.Not.Null, "The route must report the edits it applied");
        var editedNames = result.AppliedEdits!.Keys.Select(k => Path.GetFileName(k.ToString())).ToArray();
        Assert.That(editedNames, Is.EquivalentTo(new[] { "Source.cs", "Target.cs" }));

        foreach (var (path, edits) in result.AppliedEdits!)
        {
            var name = Path.GetFileName(path.ToString());
            Assert.That(edits.Count, Is.GreaterThan(0), $"{name} must carry at least one recorded edit");
            Assert.That(CollateralChangeGuard.ApplyEdits(originals[name], edits), Is.EqualTo(result.Changes[path]),
                $"Replaying the recorded edits on the original {name} must reproduce the engine's text");
        }
    }

    [Test]
    public async Task QualifiedCrefRetarget_CrlfSourceAndTarget_StayCrlf()
    {
        const string eol = "\r\n";
        var source = Lines(eol,
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
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

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

        var targetNew = NewText(result, "Target.cs");
        Assert.That(targetNew, Is.EqualTo(Lines(eol,
            "namespace Example;",
            "",
            "public class B",
            "{",
            "    /// <summary>Existing member on target.</summary>",
            "    public int Size() => 0;",
            "",
            "    /// <summary>Counts, see <see cref=\"A.Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "}")));
        Assert.That(targetNew.EndsWith(eol), Is.True, "Final newline must be kept");
        Assert.That(targetNew.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "No bare LF may appear in a CRLF target");
        Assert.That(NewText(result, "Source.cs").Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "No bare LF may appear in a CRLF source");
    }

    [Test]
    public async Task NoBareCrefInMovedDoc_HasNoVerificationOrQualifiedNote()
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
            "    /// <summary>Other method.</summary>",
            "    public int Other() => 0;",
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

        // Vacuity guard: the moved doc carries no cref at all.
        Assert.That(source, Does.Not.Contain("<see cref"));

        var result = await MoveInstanceAsync(workspace, engine, new[] { "Count" });

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

        var notes = result.Notes ?? Array.Empty<string>();
        Assert.That(notes, Does.Not.Contain(BareInheritedNote), "No bare cref means no could-not-verify note");
        Assert.That(notes.Any(n => n.StartsWith("Qualified", StringComparison.Ordinal)), Is.False, "No Qualified note when nothing was qualified");
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

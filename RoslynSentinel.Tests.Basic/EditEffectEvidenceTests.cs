using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

/// <summary>Tests for the edit effect not-found note: lookup, chains, cancellation, identity scope and wording.</summary>
[TestFixture]
public class EditEffectEvidenceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "rs-effect-root");

    private const string FooSource = "namespace N;\r\npublic class Foo\r\n{\r\n    public void Baz()\r\n    {\r\n    }\r\n}\r\n";

    [TearDown]
    public void ResetContext()
    {
        ToolCallContext.Set(null);
    }

    private static EditEffect Effect(
        EditEffectKind kind,
        string display,
        string? newName = null,
        string? newContainer = null,
        string? newFile = null,
        string? callId = "a76e9f1c",
        string tool = "RenameSymbol",
        string? session = "s1",
        string instance = "inst-1",
        string? agent = "agent-1",
        int secondsAgo = 180)
    {
        return new EditEffect(kind, display, null, EditEffectNames.SimpleName(display), newName, newContainer, newFile, callId, tool, null, session, instance, agent, Now.AddSeconds(-secondsAgo));
    }

    private static EditEffectIndex IndexOf(params EditEffect[] effects)
    {
        var index = new EditEffectIndex();
        index.Append(effects);
        return index;
    }

    private static string? Note(EditEffectIndex index, string term, string? session = "s1", string instance = "inst-1", string? agent = "agent-1", Solution? solution = null)
    {
        return EditEffectEvidence.Find(index, term, session, instance, agent, solution, Root, Now);
    }

    private static Solution SolutionWith(string source)
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var info = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            "Foo.cs",
            filePath: Path.Combine(Root, "Foo.cs"),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())));
        return workspace.AddDocument(info).Project.Solution;
    }

    [Test]
    public void Rename_NoteNamesCallAndNewName_WithDeclaredLocation()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", newFile: "Foo.cs"));

        var note = Note(index, "Foo.Bar", solution: SolutionWith(FooSource));

        Assert.That(note, Is.EqualTo("`Foo.Bar` was not found. Call `a76e9f1c` (`RenameSymbol`, this agent, 3m ago) renamed `N.Foo.Bar` to `N.Foo.Baz`, declared at `Foo.cs:4`."));
        Assert.That(note, Does.Not.Contain("stop").And.Not.Contain("must").And.Not.Contain("do not").And.Not.Contain("because"));
    }

    [Test]
    public void Rename_ChainBarBazQux_FollowsToTheNewestName()
    {
        var index = IndexOf(
            Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", newFile: "Foo.cs", callId: "c1", secondsAgo: 300),
            Effect(EditEffectKind.Renamed, "N.Foo.Baz", newName: "Qux", newFile: "Foo.cs", callId: "c2", secondsAgo: 120));
        var source = FooSource.Replace("Baz", "Qux", StringComparison.Ordinal);

        var note = Note(index, "Bar", solution: SolutionWith(source));

        Assert.That(note, Is.EqualTo("`Bar` was not found. Call `c1` (`RenameSymbol`, this agent, 5m ago) renamed `N.Foo.Bar` to `N.Foo.Baz`; call `c2` (`RenameSymbol`, this agent, 2m ago) renamed `N.Foo.Baz` to `N.Foo.Qux`, declared at `Foo.cs:4`."));
    }

    [Test]
    public void Rename_ChainEndNoLongerDeclared_SaysSoInsteadOfGuessing()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", newFile: "Foo.cs"));
        var source = FooSource.Replace("Baz", "Other", StringComparison.Ordinal);

        var note = Note(index, "Bar", solution: SolutionWith(source));

        Assert.That(note, Does.EndWith("renamed `N.Foo.Bar` to `N.Foo.Baz`; no declaration of `N.Foo.Baz` was found in the loaded solution."));
    }

    [Test]
    public void Rename_WithNoSolution_StatesNoDeclarationFact()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", newFile: "Foo.cs"));

        var note = Note(index, "Bar");

        Assert.That(note, Does.EndWith("renamed `N.Foo.Bar` to `N.Foo.Baz`."));
    }

    [Test]
    public void Rename_WithNoToolCallId_OmitsTheCallIdWording()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", callId: null));

        var note = Note(index, "Bar");

        Assert.That(note, Is.EqualTo("`Bar` was not found. A `RenameSymbol` call (this agent, 3m ago) renamed `N.Foo.Bar` to `N.Foo.Baz`."));
    }

    [Test]
    public void Removed_NoteNamesTheFile_AndOtherAgentIsNamed()
    {
        var index = IndexOf(Effect(EditEffectKind.Removed, "N.Foo.Bar", newFile: "Foo.cs", tool: "Member", agent: "agent-2"));

        var note = Note(index, "Bar");

        Assert.That(note, Is.EqualTo("`Bar` was not found. Call `a76e9f1c` (`Member`, agent agent-2, 3m ago) removed `N.Foo.Bar` from `Foo.cs`."));
    }

    [Test]
    public void Moved_NoteNamesContainerAndFile()
    {
        var index = IndexOf(Effect(EditEffectKind.Moved, "N.Foo.Bar", newContainer: "N.Util", newFile: "Util.cs", tool: "MoveMember"));

        var note = Note(index, "Bar");

        Assert.That(note, Is.EqualTo("`Bar` was not found. Call `a76e9f1c` (`MoveMember`, this agent, 3m ago) moved `N.Foo.Bar` to `N.Util` in `Util.cs`."));
    }

    [Test]
    public void FileDeleted_AndSignatureChanged_AreHandledWithoutThrowing()
    {
        var deleted = IndexOf(Effect(EditEffectKind.FileDeleted, "Foo.cs", tool: "DeleteFile") with { SimpleName = "Foo" });
        var changed = IndexOf(Effect(EditEffectKind.SignatureChanged, "N.Foo.Bar", tool: "ChangeSignature"));

        Assert.That(Note(deleted, "Foo"), Does.Contain("deleted `Foo.cs`"));
        Assert.That(Note(changed, "Bar"), Does.Contain("changed the signature of `N.Foo.Bar`"));
    }

    [Test]
    public void LaterAddedEffect_CancelsTheNote()
    {
        var index = IndexOf(
            Effect(EditEffectKind.Removed, "N.Foo.Bar", newFile: "Foo.cs", secondsAgo: 300),
            Effect(EditEffectKind.Added, "N.Foo.Bar", newContainer: "N.Foo", newFile: "Foo.cs", tool: "Member", secondsAgo: 60));

        Assert.That(Note(index, "Bar"), Is.Null);
    }

    [Test]
    public void LaterRenameToTheMissingName_CancelsTheNote()
    {
        var index = IndexOf(
            Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", secondsAgo: 300),
            Effect(EditEffectKind.Renamed, "N.Foo.Other", newName: "Bar", secondsAgo: 60));

        Assert.That(Note(index, "Bar"), Is.Null);
    }

    [Test]
    public void EffectOfAnotherSession_IsExcluded_AndOwnSessionIsIncluded()
    {
        var other = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: "s2"));
        var own = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: "s1"));

        Assert.That(Note(other, "Bar"), Is.Null);
        Assert.That(Note(own, "Bar"), Does.Contain("this agent"));
    }

    [Test]
    public void SameSession_DifferentServerInstance_IsIncluded()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: "s1", instance: "inst-0"));

        Assert.That(Note(index, "Bar", session: "s1", instance: "inst-1"), Is.Not.Null);
    }

    [Test]
    public void CallWithoutSessionId_FallsBackToServerInstance()
    {
        var sameInstance = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: "s1", instance: "inst-1"));
        var otherInstance = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: "s1", instance: "inst-2"));

        Assert.That(Note(sameInstance, "Bar", session: null, instance: "inst-1"), Is.Not.Null);
        Assert.That(Note(otherInstance, "Bar", session: null, instance: "inst-1"), Is.Null);
    }

    [Test]
    public void EffectWithoutSessionId_FallsBackToServerInstance()
    {
        var sameInstance = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: null, instance: "inst-1"));
        var otherInstance = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz", session: null, instance: "inst-2"));

        Assert.That(Note(sameInstance, "Bar", session: "s1", instance: "inst-1"), Is.Not.Null);
        Assert.That(Note(otherInstance, "Bar", session: "s1", instance: "inst-1"), Is.Null);
    }

    [Test]
    public void RevertedEffect_GivesNoNote()
    {
        var effect = Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz");
        var index = IndexOf(effect);
        index.AssignChangeId([effect], "c200384e");
        index.MarkReverted("c200384e");

        Assert.That(Note(index, "Bar"), Is.Null);
    }

    [Test]
    public void QualifiedTerm_MustMatchTheEffectContainer()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz"));

        Assert.That(Note(index, "Other.Bar"), Is.Null);
        Assert.That(Note(index, "Foo.Bar"), Is.Not.Null);
        Assert.That(Note(index, "N.Foo.Bar"), Is.Not.Null);
        Assert.That(Note(index, "Bar"), Is.Not.Null);
    }

    [Test]
    public void DocCommentIdAndParameterListTerms_ReduceToTheSameName()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz"));

        Assert.That(Note(index, "M:N.Foo.Bar(System.Int32)"), Does.StartWith("`N.Foo.Bar` was not found."));
        Assert.That(Note(index, "Bar(int, string)"), Does.StartWith("`Bar` was not found."));
        Assert.That(Note(index, "Bar<T>"), Does.StartWith("`Bar` was not found."));
    }

    [Test]
    public void TermThatIsNotAName_GivesNoNote()
    {
        var index = IndexOf(Effect(EditEffectKind.Renamed, "N.Foo.Bar", newName: "Baz"));

        Assert.That(Note(index, "var x = Bar;\r\nreturn x;"), Is.Null);
        Assert.That(Note(index, " "), Is.Null);
        Assert.That(Note(index, "Unrelated"), Is.Null);
    }

    [Test]
    public void ForCurrentCall_NotFoundCode_ReturnsFinding_OtherCodeAndNoIndexReturnNothing()
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("agent-1", null, "RenameSymbol", "rename", 0) { SessionId = "s1" };
        writer.AssignToolCallId("a76e9f1c");
        writer.RecordEffect(EditEffectKind.Renamed, "N.Foo.Bar", "M:N.Foo.Bar", "Bar", "Baz", null, "Foo.cs");
        writer.CommitPendingEffects(index);
        var solution = SolutionWith(FooSource);
        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0)
        {
            SessionId = "s1",
            MissingTerm = "Bar",
            SolutionRoot = Root,
            EditEffects = index,
            CurrentSolution = () => solution,
        });

        var notes = EditEffectEvidence.ForCurrentCall("NotFound");

        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Source, Is.EqualTo(EditEffectEvidence.FindingSource));
        Assert.That(notes[0].Severity, Is.EqualTo(FindingSeverity.Info));
        Assert.That(notes[0].Message, Does.Contain("Call `a76e9f1c` (`RenameSymbol`, this agent, "));
        Assert.That(notes[0].Message, Does.EndWith(" ago) renamed `N.Foo.Bar` to `N.Foo.Baz`, declared at `Foo.cs:4`."));
        Assert.That(EditEffectEvidence.ForCurrentCall("InvalidArgument"), Is.Empty);

        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0) { SessionId = "s1", MissingTerm = "Bar" });
        Assert.That(EditEffectEvidence.ForCurrentCall("NotFound"), Is.Empty);
        ToolCallContext.Set(null);
        Assert.That(EditEffectEvidence.ForCurrentCall("NotFound"), Is.Empty);
    }

    [Test]
    public void ForCurrentCall_SolutionAccessorThrows_StillReturnsTheNoteWithoutLocation()
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("agent-1", null, "RenameSymbol", "rename", 0) { SessionId = "s1" };
        writer.RecordEffect(EditEffectKind.Renamed, "N.Foo.Bar", null, "Bar", "Baz", null, "Foo.cs");
        writer.CommitPendingEffects(index);
        ToolCallContext.Set(new ToolCallContext("agent-1", null, "LocateSymbol", "look", 0)
        {
            SessionId = "s1",
            MissingTerm = "Bar",
            EditEffects = index,
            CurrentSolution = () => throw new InvalidOperationException("workspace busy"),
        });

        var notes = EditEffectEvidence.ForCurrentCall("NotFound");

        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Message, Does.EndWith("renamed `N.Foo.Bar` to `N.Foo.Baz`."));
    }
}
